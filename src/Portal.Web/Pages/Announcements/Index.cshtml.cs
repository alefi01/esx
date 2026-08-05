using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;
using Portal.Web.Services.Notifications;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Лента объявлений. Читать может любой, у кого есть доступ к порталу.
/// </summary>
public class IndexModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly DatabaseOptions _databaseOptions;
    private readonly NotificationService _notifications;
    private readonly Portal.Web.Services.Announcements.AnnouncementStorage _storage;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        PortalDbContext db,
        IOptions<ActiveDirectoryOptions> adOptions,
        IOptions<DatabaseOptions> databaseOptions,
        NotificationService notifications,
        Portal.Web.Services.Announcements.AnnouncementStorage storage,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _adOptions = adOptions.Value;
        _databaseOptions = databaseOptions.Value;
        _notifications = notifications;
        _storage = storage;
        _logger = logger;
    }

    public IReadOnlyList<Announcement> Items { get; private set; } = [];

    /// <summary>Номер текущей страницы, начиная с 1.</summary>
    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    public int TotalPages { get; private set; } = 1;

    /// <summary>true — ленту прочитать не удалось. Сообщение видят ВСЕ пользователи.</summary>
    public bool DatabaseUnavailable { get; private set; }

    /// <summary>
    /// Техническая подробность ошибки. Показывается только администраторам:
    /// в тексте исключения бывают имена серверов, пользователей базы и путей.
    /// </summary>
    public string? DatabaseErrorDetail { get; private set; }

    public bool CanPublish =>
        User.IsInRole(_adOptions.PublisherGroup) || User.IsInRole(_adOptions.AdminGroup);

    public bool IsAdmin => User.IsInRole(_adOptions.AdminGroup);

    public bool CanModify(Announcement announcement) =>
        AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup);

    /// <summary>Успешное действие с предыдущей страницы («Объявление опубликовано» и т.п.).</summary>
    [TempData]
    public string? StatusMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        var pageSize = Math.Max(1, _databaseOptions.AnnouncementsPageSize);

        try
        {
            var total = await _db.Announcements.CountAsync(cancellationToken);

            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

            if (PageNumber > TotalPages)
            {
                PageNumber = TotalPages;
            }

            // Закреплённые идут первыми независимо от даты — в этом весь
            // смысл закрепления. Сортировка именно в запросе, а не в памяти:
            // страницы нарезаются базой, и переставлять записи после Take
            // означало бы менять порядок только внутри одной страницы.
            Items = await _db.Announcements
                .Include(a => a.Files)
                .OrderByDescending(a => a.IsPinned)
                .ThenByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)   // на случай совпадения времени до микросекунды
                .Skip((PageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);

            // Человек открыл ленту — значит всё новое он увидел.
            // Отметку ставим только на первой странице: на второй и дальше
            // лежит старое, и объявлять его прочитанным было бы неправдой.
            if (PageNumber == 1)
            {
                var userName = User.Identity?.Name;

                if (!string.IsNullOrEmpty(userName))
                {
                    await _notifications.MarkAllSeenAsync(userName, cancellationToken);
                }
            }
        }
        catch (Exception ex)
        {
            // База может быть недоступна: остановлен PostgreSQL, сеть, пароль сменили.
            // Портал при этом продолжает работать. Важно не молчать: без сообщения
            // пустая лента выглядит как «объявлений нет», и человек уходит,
            // не зная, что часть портала сломана.
            _logger.LogError(ex, "Не удалось прочитать ленту объявлений.");

            DatabaseUnavailable = true;
            DatabaseErrorDetail = IsAdmin ? ex.Message : null;
        }

        return Page();
    }

    /// <summary>
    /// Закрепить объявление наверху ленты или снять закрепление.
    ///
    /// Право то же, что и на правку: кто может исправить объявление,
    /// тот может и решить, висеть ему наверху или нет. Заводить отдельное
    /// право ради одной галочки — лишняя сущность.
    /// </summary>
    public Task<IActionResult> OnPostPinAsync(int id, CancellationToken cancellationToken) =>
        SwitchAsync(id, a => a.IsPinned = !a.IsPinned, cancellationToken);

    /// <summary>Пометить объявление важным или снять пометку.</summary>
    public Task<IActionResult> OnPostImportantAsync(int id, CancellationToken cancellationToken) =>
        SwitchAsync(id, a => a.IsImportant = !a.IsImportant, cancellationToken);

    /// <summary>
    /// Общая часть обоих переключателей: найти, проверить право, поменять.
    ///
    /// Права проверяются ЗДЕСЬ, по данным из базы, а не по тому, показали ли
    /// мы кнопку. Скрытая кнопка — это удобство интерфейса, а не защита:
    /// такой POST можно отправить и без неё.
    /// </summary>
    private async Task<IActionResult> SwitchAsync(
        int id, Action<Announcement> change, CancellationToken cancellationToken)
    {
        var announcement = await _db.Announcements.AsTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (announcement is null)
        {
            return NotFound();
        }

        if (!CanModify(announcement))
        {
            return Forbid();
        }

        change(announcement);

        // Дату правки НЕ трогаем: закрепление не меняет текст объявления,
        // и подпись «изменено» после него сбивала бы с толку.
        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToPage(new { PageNumber });
    }

    /// <summary>
    /// Отдаёт вложение объявления.
    ///
    /// Отдельной проверки прав здесь нет намеренно: объявление видно всем,
    /// у кого есть доступ к порталу, а доступ к порталу проверяется общей
    /// политикой ещё до входа в этот метод.
    /// </summary>
    public async Task<IActionResult> OnGetAttachmentAsync(int fileId, CancellationToken cancellationToken)
    {
        var file = await _db.AnnouncementFiles
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file is null || !_storage.Exists(file.AnnouncementId, file.StorageName))
        {
            return NotFound();
        }

        var stream = _storage.OpenRead(file.AnnouncementId, file.StorageName);

        // Картинки показываем прямо в ленте, поэтому их отдаём «на просмотр»,
        // а не «на сохранение». Тип содержимого при этом берём по расширению,
        // а заголовок nosniff (см. Program.cs) запрещает браузеру
        // «додумывать» его самому.
        if (file.IsImage)
        {
            return new FileStreamResult(stream, file.ContentType) { EnableRangeProcessing = true };
        }

        return new FileStreamResult(stream, file.ContentType)
        {
            FileDownloadName = file.OriginalName,
            EnableRangeProcessing = true
        };
    }
}
