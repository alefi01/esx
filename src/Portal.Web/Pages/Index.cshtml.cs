using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;
using Portal.Web.Services.Notifications;

namespace Portal.Web.Pages;

/// <summary>
/// Главная страница: часы, поиск и непрочитанные объявления.
///
/// Раньше здесь была проверочная страница со списком групп Active Directory —
/// она была нужна на первом этапе, чтобы убедиться, что вход работает.
/// Сотруднику эти сведения не нужны и только пугают, поэтому они переехали
/// на страницу «Диагностика», где им и место.
///
/// ЧЕМ ОНА ОТЛИЧАЕТСЯ ОТ РАЗДЕЛА «ОБЪЯВЛЕНИЯ»
///
/// Раньше главная была той же лентой объявлений, только короче, и потому
/// не отвечала ни на один вопрос, на который не отвечал бы раздел.
/// Теперь она отвечает на один: «что появилось, пока меня не было».
/// Поэтому здесь ТОЛЬКО НЕПРОЧИТАННОЕ, а вся история с листанием
/// осталась в разделе «Объявления».
///
/// Отметка «прочитано» здесь НЕ ставится сама. Раньше ставилась — и это
/// было безобидно, пока главная показывала всё подряд. Теперь молчаливая
/// отметка означала бы, что список исчезает от одного захода на страницу,
/// в том числе случайного. Отмечает человек, кнопкой.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>Сколько непрочитанных объявлений показывать на главной.</summary>
    private const int FeedSize = 10;

    /// <summary>Сколько закреплённых объявлений помещается в правый столбец.</summary>
    private const int PinnedSize = 6;

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

    /// <summary>Непрочитанные объявления, свежие сверху.</summary>
    public IReadOnlyList<Announcement> Items { get; private set; } = [];

    /// <summary>Сколько всего непрочитанных — их может быть больше, чем показано.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Сколько всего объявлений в портале — для ссылки «вся лента».</summary>
    public int TotalCount { get; private set; }

    /// <summary>
    /// Закреплённые объявления — правым столбцом.
    ///
    /// Показываются НЕЗАВИСИМО от того, прочитаны они или нет: закрепляют
    /// как раз то, что должно висеть перед глазами постоянно — режим работы,
    /// телефон охраны, порядок в праздники. Такое читают один раз, а нужно
    /// оно потом месяцами.
    /// </summary>
    public IReadOnlyList<Announcement> Pinned { get; private set; } = [];

    /// <summary>
    /// Совсем пусто: ни закреплённого, ни непрочитанного. Тогда на странице
    /// остаются только часы и поиск, и поиску отдаётся весь экран.
    /// </summary>
    public bool NothingToShow => Pinned.Count == 0 && Items.Count == 0 && !DatabaseUnavailable;

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
            TotalCount = await _db.Announcements.CountAsync(cancellationToken);

            var userName = User.Identity?.Name;

            // Граница «прочитанного» — номер последнего объявления, которое
            // человек отметил. Он же используется колокольчиком, поэтому
            // число на главной и число на колокольчике не разъезжаются.
            var lastSeen = string.IsNullOrEmpty(userName)
                ? 0
                : await _notifications.LastSeenAnnouncementIdAsync(userName, cancellationToken);

            var unread = _db.Announcements.Where(a => a.Id > lastSeen);

            UnreadCount = await unread.CountAsync(cancellationToken);

            // Закреплённые первыми — так же, как в ленте объявлений.
            // Иначе главная и раздел «Объявления» показывали бы разное.
            Items = await unread
                .Include(a => a.Files)
                .OrderByDescending(a => a.IsPinned)
                .ThenByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(FeedSize)
                .ToListAsync(cancellationToken);

            HasMore = UnreadCount > Items.Count;

            // Закреплённые берутся отдельным запросом, а не выбираются
            // из непрочитанных: закреплённое показывается и прочитанным.
            Pinned = await _db.Announcements
                .Where(a => a.IsPinned)
                .OrderByDescending(a => a.CreatedAt)
                .ThenByDescending(a => a.Id)
                .Take(PinnedSize)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // База может быть недоступна. Портал при этом продолжает работать:
            // файлы и переписки живут своей жизнью. Молчать нельзя —
            // пустая лента выглядит как «объявлений нет».
            _logger.LogError(ex, "Не удалось прочитать объявления для главной страницы.");

            DatabaseUnavailable = true;
        }
    }

    /// <summary>
    /// «Я всё прочитал». Отмечает текущее состояние ленты и возвращает
    /// человека на главную — она становится пустой и спокойной.
    /// </summary>
    public async Task<IActionResult> OnPostMarkReadAsync(CancellationToken cancellationToken)
    {
        var userName = User.Identity?.Name;

        if (!string.IsNullOrEmpty(userName))
        {
            await _notifications.MarkAllSeenAsync(userName, cancellationToken);
        }

        return RedirectToPage();
    }
}
