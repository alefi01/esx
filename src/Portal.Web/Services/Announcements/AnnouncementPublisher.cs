using System.Security.Claims;
using Portal.Web.Data;
using Portal.Web.Services.Notifications;
using Portal.Web.Services.Storage;

namespace Portal.Web.Services.Announcements;

/// <summary>
/// Публикация объявления.
///
/// ЗАЧЕМ ОТДЕЛЬНОЙ СЛУЖБОЙ
///
/// Объявление пишут из двух мест: окном прямо в папке (так быстрее —
/// человек не уходит со страницы и видит, куда вешает) и отдельной
/// страницей, которая остаётся работать там, где JavaScript выключен.
/// Повторять в обоих местах сохранение, вложения и оповещение значило бы
/// однажды поправить одно и забыть другое.
/// </summary>
public sealed class AnnouncementPublisher
{
    private readonly PortalDbContext _db;
    private readonly AnnouncementStorage _storage;
    private readonly UploadValidator _validator;
    private readonly NotificationHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<AnnouncementPublisher> _logger;

    public AnnouncementPublisher(
        PortalDbContext db,
        AnnouncementStorage storage,
        UploadValidator validator,
        NotificationHub hub,
        TimeProvider time,
        ILogger<AnnouncementPublisher> logger)
    {
        _db = db;
        _storage = storage;
        _validator = validator;
        _hub = hub;
        _time = time;
        _logger = logger;
    }

    /// <summary>Что получилось: само объявление и список того, что не приложилось.</summary>
    public sealed record Result(Announcement? Announcement, List<string> Problems, string? Error);

    /// <summary>
    /// Опубликовать объявление в папке. Право на публикацию проверяет
    /// вызывающий: он знает, откуда пришёл запрос.
    /// </summary>
    public async Task<Result> PublishAsync(
        int folderId,
        string title,
        string body,
        ClaimsPrincipal user,
        List<IFormFile>? attachments,
        CancellationToken cancellationToken)
    {
        var announcement = new Announcement
        {
            Title = title.Trim(),
            Body = body.Trim(),
            AuthorUserName = user.Identity?.Name ?? "",
            AuthorDisplayName = user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name ?? "",
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            FolderId = folderId
        };

        try
        {
            _db.Announcements.Add(announcement);

            await _db.SaveChangesAsync(cancellationToken);

            // Будим все открытые страницы: кому объявление видно и ново,
            // тот и услышит — остальным сервер в ответ на проверку скажет,
            // что нового для них нет.
            _hub.NotifyAll();

            // Вложения сохраняем ПОСЛЕ объявления: до сохранения у него
            // нет номера, а номер нужен для папки на диске.
            var problems = await AttachAsync(announcement, attachments, cancellationToken);

            _logger.LogInformation(
                "Пользователь {User} опубликовал объявление {Id}.",
                announcement.AuthorUserName, announcement.Id);

            return new Result(announcement, problems, null);
        }
        catch (Exception ex)
        {
            _logger.LogError(
                ex, "Не удалось сохранить объявление пользователя {User}.", announcement.AuthorUserName);

            return new Result(
                null,
                [],
                "Не удалось сохранить объявление: база данных недоступна. "
                + "Скопируйте текст, чтобы не потерять, и сообщите администратору.");
        }
    }

    /// <summary>
    /// Сохраняет вложения объявления. Возвращает список того, что не удалось,
    /// с причинами.
    ///
    /// Проверки те же, что и в файловом хранилище: запрет опасных расширений
    /// и предел размера. Объявление видно всему отделу сразу, поэтому
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

            var safeName = UploadValidator.SanitizeName(upload.FileName);

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
                    ContentType = UploadValidator.ResolveContentType(safeName),
                    IsImage = AnnouncementStorage.LooksLikeImage(safeName)
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
