using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Data;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Создание объявления. Доступ ограничен политикой PublishAnnouncements —
/// она навешана на страницу в Program.cs, отдельной проверки здесь не нужно.
/// </summary>
public class CreateModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly Portal.Web.Services.Announcements.AnnouncementStorage _storage;
    private readonly Portal.Web.Services.Storage.UploadValidator _validator;
    private readonly TimeProvider _time;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(
        PortalDbContext db,
        Portal.Web.Services.Announcements.AnnouncementStorage storage,
        Portal.Web.Services.Storage.UploadValidator validator,
        TimeProvider time,
        ILogger<CreateModel> logger)
    {
        _db = db;
        _storage = storage;
        _validator = validator;
        _time = time;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Отдельная модель для формы, а не сама сущность Announcement.
    ///
    /// Так сделано намеренно: если привязывать форму прямо к сущности,
    /// злоумышленник может дописать в отправляемые данные поля, которых
    /// в форме нет — например, AuthorUserName — и опубликовать объявление
    /// от чужого имени. Здесь же в модели есть ровно два поля, и подделать
    /// автора невозможно: он берётся из cookie, а не из формы.
    /// </summary>
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Введите заголовок")]
        [MaxLength(200, ErrorMessage = "Заголовок не длиннее 200 символов")]
        [Display(Name = "Заголовок")]
        public string Title { get; set; } = "";

        [Required(ErrorMessage = "Введите текст объявления")]
        [MaxLength(10000, ErrorMessage = "Текст не длиннее 10000 символов")]
        [Display(Name = "Текст")]
        public string Body { get; set; } = "";
    }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync(
        List<IFormFile> attachments, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var announcement = new Announcement
        {
            Title = Input.Title.Trim(),
            Body = Input.Body.Trim(),
            AuthorUserName = User.Identity?.Name ?? "",
            AuthorDisplayName = User.FindFirstValue(ClaimTypes.GivenName) ?? User.Identity?.Name ?? "",
            CreatedAt = _time.GetUtcNow().UtcDateTime
        };

        try
        {
            _db.Announcements.Add(announcement);
            await _db.SaveChangesAsync(cancellationToken);

            // Вложения сохраняем ПОСЛЕ объявления: до сохранения у него
            // нет номера, а номер нужен для папки на диске.
            var problems = await AttachAsync(announcement, attachments, cancellationToken);

            if (problems.Count > 0)
            {
                // Объявление уже опубликовано, поэтому не ошибка, а предупреждение:
                // текст на месте, не приложились только некоторые файлы.
                TempData["ErrorMessage"] = "Не приложены: " + string.Join("; ", problems);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить объявление пользователя {User}.", announcement.AuthorUserName);

            ErrorMessage = "Не удалось сохранить объявление: база данных недоступна. " +
                           "Скопируйте текст, чтобы не потерять, и сообщите администратору.";

            return Page();
        }

        _logger.LogInformation(
            "Пользователь {User} опубликовал объявление {Id}.", announcement.AuthorUserName, announcement.Id);

        // TempData переживает перенаправление и очищается после первого показа —
        // ровно то, что нужно для сообщения «готово».
        TempData["StatusMessage"] = "Объявление опубликовано.";

        return RedirectToPage("Index");
    }

    /// <summary>
    /// Сохраняет вложения объявления. Возвращает список того, что не удалось,
    /// с причинами.
    ///
    /// Проверки те же, что и в файловом хранилище: запрет опасных расширений
    /// и предел размера. Объявление видно всем сотрудникам сразу, поэтому
    /// послаблений здесь быть не может.
    /// </summary>
    private async Task<List<string>> AttachAsync(
        Announcement announcement, List<IFormFile>? uploads, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        if (uploads is null || uploads.Count == 0)
        {
            return problems;
        }

        if (!_storage.IsConfigured)
        {
            problems.Add("хранилище не настроено (Storage:RootPath)");
            return problems;
        }

        var added = false;

        foreach (var upload in uploads)
        {
            if (upload.Length == 0)
            {
                continue;
            }

            var rejection = _validator.Validate(upload.FileName, upload.Length, 0, null, 0);

            if (rejection is not null)
            {
                problems.Add($"«{rejection.FileName}» — {rejection.Reason}");
                continue;
            }

            var safeName = Portal.Web.Services.Storage.UploadValidator.SanitizeName(upload.FileName);

            try
            {
                await using var content = upload.OpenReadStream();

                var storageName = await _storage.SaveAsync(announcement.Id, content, cancellationToken);

                _db.AnnouncementFiles.Add(new AnnouncementFile
                {
                    AnnouncementId = announcement.Id,
                    OriginalName = safeName,
                    StorageName = storageName,
                    SizeBytes = upload.Length,
                    ContentType = Portal.Web.Services.Storage.UploadValidator.ResolveContentType(safeName),
                    IsImage = Portal.Web.Services.Announcements.AnnouncementStorage.LooksLikeImage(safeName)
                });

                added = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось сохранить вложение {Name} к объявлению.", safeName);

                problems.Add($"«{safeName}» — не удалось сохранить");
            }
        }

        if (added)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return problems;
    }
}
