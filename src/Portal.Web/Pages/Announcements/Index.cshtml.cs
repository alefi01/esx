using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Лента объявлений. Читать может любой, у кого есть доступ к порталу.
/// </summary>
public class IndexModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly DatabaseOptions _databaseOptions;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        PortalDbContext db,
        IOptions<ActiveDirectoryOptions> adOptions,
        IOptions<DatabaseOptions> databaseOptions,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _adOptions = adOptions.Value;
        _databaseOptions = databaseOptions.Value;
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

            Items = await _db.Announcements
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)   // на случай совпадения времени до микросекунды
                .Skip((PageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
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
}
