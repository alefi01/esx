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
    private readonly TimeProvider _time;
    private readonly ILogger<CreateModel> _logger;

    public CreateModel(PortalDbContext db, TimeProvider time, ILogger<CreateModel> logger)
    {
        _db = db;
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

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
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
}
