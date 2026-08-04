using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;
using Portal.Web.Services.Notifications;

namespace Portal.Web.Pages;

/// <summary>
/// Главная страница: приветствие и лента объявлений.
///
/// Раньше здесь была проверочная страница со списком групп Active Directory —
/// она была нужна на первом этапе, чтобы убедиться, что вход работает.
/// Сотруднику эти сведения не нужны и только пугают, поэтому они переехали
/// на страницу «Диагностика», где им и место.
///
/// Объявления показываются прямо здесь: это то, ради чего человек открывает
/// портал утром. Отдельный раздел «Объявления» остался — там вся история
/// с постраничным листанием.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>Сколько последних объявлений показывать на главной.</summary>
    private const int FeedSize = 10;

    private readonly ActiveDirectoryOptions _adOptions;
    private readonly PortalDbContext _db;
    private readonly NotificationService _notifications;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IOptions<ActiveDirectoryOptions> adOptions,
        PortalDbContext db,
        NotificationService notifications,
        ILogger<IndexModel> logger)
    {
        _adOptions = adOptions.Value;
        _db = db;
        _notifications = notifications;
        _logger = logger;
    }

    public string UserName => User.Identity?.Name ?? "—";

    public string DisplayName =>
        User.FindFirstValue(ClaimTypes.GivenName) is { Length: > 0 } value ? value : UserName;

    public string OfficeName =>
        User.FindFirstValue(PortalClaimTypes.OfficeName) is { Length: > 0 } value ? value : "";

    public bool IsAdmin => User.IsInRole(_adOptions.AdminGroup);

    public bool CanPublish =>
        User.IsInRole(_adOptions.PublisherGroup) || IsAdmin;

    public IReadOnlyList<Announcement> Items { get; private set; } = [];

    public bool DatabaseUnavailable { get; private set; }

    /// <summary>Есть ли объявления помимо показанных — тогда нужна ссылка «все объявления».</summary>
    public bool HasMore { get; private set; }

    public bool CanModify(Announcement announcement) =>
        AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup);

    /// <summary>Приветствие по времени суток — мелочь, а портал сразу выглядит живым.</summary>
    public string Greeting => DateTime.Now.Hour switch
    {
        >= 5 and < 12 => "Доброе утро",
        >= 12 and < 18 => "Добрый день",
        >= 18 and < 23 => "Добрый вечер",
        _ => "Доброй ночи"
    };

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var total = await _db.Announcements.CountAsync(cancellationToken);

            Items = await _db.Announcements
                .Include(a => a.Files)
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(FeedSize)
                .ToListAsync(cancellationToken);

            HasMore = total > Items.Count;

            // Главная — это и есть лента объявлений, поэтому открыв её,
            // человек всё новое увидел. Счётчик на колокольчике обнуляем.
            var userName = User.Identity?.Name;

            if (!string.IsNullOrEmpty(userName))
            {
                await _notifications.MarkAllSeenAsync(userName, cancellationToken);
            }
        }
        catch (Exception ex)
        {
            // База может быть недоступна. Портал при этом продолжает работать:
            // файлы и переписки живут своей жизнью. Молчать нельзя —
            // пустая лента выглядит как «объявлений нет».
            _logger.LogError(ex, "Не удалось прочитать ленту объявлений для главной страницы.");

            DatabaseUnavailable = true;
        }
    }
}
