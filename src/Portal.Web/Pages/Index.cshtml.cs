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
/// ГЛАВНАЯ И ЕСТЬ ЛЕНТА
///
/// Отдельного раздела «Объявления» больше нет: объявление висит на папке
/// и читается там же, где лежат документы отдела. Но место, где видно
/// «что нового вообще», всё равно нужно — им и стала главная. Она
/// показывает непрочитанные объявления ИЗ ВСЕХ ПАПОК, куда человек
/// допущен, а карточка ведёт в папку.
///
/// Отметка «прочитано» здесь НЕ ставится: иначе список исчезал бы
/// от одного захода на страницу, в том числе случайного. Прочитанным
/// объявление становится, когда человек открывает папку с ним.
/// </summary>
public class IndexModel : PageModel
{
    /// <summary>Сколько непрочитанных объявлений показывать на главной.</summary>
    private const int FeedSize = 10;

    /// <summary>Сколько закреплённых объявлений помещается в правый столбец.</summary>
    private const int PinnedSize = 6;

    private readonly ActiveDirectoryOptions _adOptions;
    private readonly PortalDbContext _db;
    private readonly Portal.Web.Services.Announcements.AnnouncementFeed _feed;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IOptions<ActiveDirectoryOptions> adOptions,
        PortalDbContext db,
        Portal.Web.Services.Announcements.AnnouncementFeed feed,
        ILogger<IndexModel> logger)
    {
        _adOptions = adOptions.Value;
        _db = db;
        _feed = feed;
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

    /// <summary>Непрочитанные объявления вместе с папками, свежие сверху.</summary>
    public IReadOnlyList<Portal.Web.Services.Announcements.FeedItem> Items { get; private set; } = [];

    /// <summary>Сколько всего непрочитанных — их может быть больше, чем показано.</summary>
    public int UnreadCount { get; private set; }

    /// <summary>Сколько всего объявлений человеку видно.</summary>
    public int TotalCount { get; private set; }

    /// <summary>
    /// Закреплённые объявления — правым столбцом.
    ///
    /// Показываются НЕЗАВИСИМО от того, прочитаны они или нет: закрепляют
    /// как раз то, что должно висеть перед глазами постоянно — режим работы,
    /// телефон охраны, порядок в праздники. Такое читают один раз, а нужно
    /// оно потом месяцами.
    /// </summary>
    public IReadOnlyList<Portal.Web.Services.Announcements.FeedItem> Pinned { get; private set; } = [];

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
            var readable = await _feed.ReadableFolderIdsAsync(User, cancellationToken);

            TotalCount = await _db.Announcements
                .CountAsync(a => a.FolderId == null || readable.Contains(a.FolderId.Value), cancellationToken);

            // Непрочитанное считает та же служба, что и колокольчик, —
            // иначе число в шапке и список на странице разъехались бы.
            var unread = await _feed.UnreadAsync(User, FeedSize + 1, cancellationToken);

            // Закреплённое из «нового» исключено: оно и так стоит в правом
            // столбце, и одна и та же карточка в обоих столбцах выглядела
            // как ошибка вёрстки — да и была бесполезна.
            var fresh = unread.Where(entry => !entry.Item.IsPinned).ToList();

            Items = fresh.Take(FeedSize).ToList();
            UnreadCount = fresh.Count;
            HasMore = fresh.Count > Items.Count;

            // Закреплённые берутся отдельно: закреплённое показывается
            // и прочитанным — на то его и закрепляли.
            Pinned = await _feed.PinnedAsync(User, PinnedSize, cancellationToken);
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

    // Кнопки «прочитано» на главной больше нет: прочитанным объявление
    // становится там, где его читают, — в ленте. Кнопка при этом означала
    // «спрятать, не читая», и нажимали её именно так.
}
