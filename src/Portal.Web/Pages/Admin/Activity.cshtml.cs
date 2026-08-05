using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Лента активности: что происходило в портале за последнее время.
///
/// Отличие от журнала действий (страница Audit) — не в данных, а в вопросе,
/// на который страница отвечает. Журнал отвечает на «кто скачал этот документ
/// в марте»: там фильтры, страницы и таблица. Лента отвечает на «что вообще
/// сейчас происходит»: последние события подряд, лентой, без фильтров.
///
/// Раздел виден ТОЛЬКО администратору — как и журнал. Причина та же:
/// по такой ленте видно, кто чем занимается, а это сведения, которые
/// сотрудникам друг о друге знать незачем. Пункт меню остальным
/// не показывается вовсе, а доступ закрыт политикой на всю папку /Admin.
/// </summary>
public class ActivityModel : PageModel
{
    /// <summary>Сколько последних событий показывать. Лента, а не архив.</summary>
    private const int Take = 120;

    private readonly PortalDbContext _db;
    private readonly ILogger<ActivityModel> _logger;

    public ActivityModel(PortalDbContext db, ILogger<ActivityModel> logger)
    {
        _db = db;
        _logger = logger;
    }

    /// <summary>События одного дня.</summary>
    /// <param name="Day">День в местном времени.</param>
    /// <param name="Entries">События этого дня, свежие сверху.</param>
    public sealed record DayGroup(DateTime Day, IReadOnlyList<AuditEntry> Entries);

    public IReadOnlyList<DayGroup> Days { get; private set; } = [];

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            var entries = await _db.AuditEntries
                .OrderByDescending(a => a.At)
                .Take(Take)
                .ToListAsync(cancellationToken);

            // Группируем по дню УЖЕ после выборки, в памяти. Группировать
            // запросом пришлось бы по местному времени, а в базе всё в UTC:
            // событие в 2 часа ночи попало бы не в тот день.
            Days = entries
                .GroupBy(a => a.At.ToLocalTime().Date)
                .OrderByDescending(g => g.Key)
                .Select(g => new DayGroup(g.Key, g.ToList()))
                .ToList();
        }
        catch (Exception ex)
        {
            // База может быть недоступна. Молчать нельзя: пустая лента
            // выглядит как «ничего не происходило».
            _logger.LogError(ex, "Не удалось прочитать ленту активности.");

            DatabaseError = ex.Message;
        }
    }

    /// <summary>Название действия по-русски — то же, что и в журнале.</summary>
    public static string Describe(AuditAction action) => AuditModel.Describe(action);

    /// <summary>
    /// Значок для точки на ленте. Подбирается по смыслу действия,
    /// чтобы событие узнавалось, не читая подпись.
    /// </summary>
    public static string IconOf(AuditAction action) => action switch
    {
        AuditAction.Upload => "i-upload",
        AuditAction.Download => "i-download",
        AuditAction.MoveToTrash or AuditAction.DeleteFolder => "i-trash",
        AuditAction.RestoreFromTrash => "i-restore",
        AuditAction.Purge or AuditAction.RetentionCleanup or AuditAction.PurgeAuditLog => "i-trash",
        AuditAction.CreateFolder => "i-new-folder",
        AuditAction.ChangeFolderSettings => "i-settings",
        AuditAction.ChangePermissions => "i-shield",
        AuditAction.Move or AuditAction.Copy => "i-copy",
        AuditAction.Rename => "i-rename",
        AuditAction.Preview => "i-eye",
        _ => "i-file"
    };

    /// <summary>
    /// Цвет точки: обычное действие, создание или удаление.
    /// Три состояния, а не десять, — иначе лента превращается в радугу.
    /// </summary>
    public static string ToneOf(AuditAction action) => action switch
    {
        AuditAction.Upload or AuditAction.CreateFolder or AuditAction.RestoreFromTrash => "ok",

        AuditAction.MoveToTrash or AuditAction.DeleteFolder or AuditAction.Purge
            or AuditAction.RetentionCleanup or AuditAction.PurgeAuditLog => "danger",

        _ => ""
    };

    /// <summary>«Сегодня», «вчера» или дата — заголовок группы.</summary>
    public static string DayTitle(DateTime day)
    {
        var today = DateTime.Now.Date;

        if (day == today)
        {
            return "Сегодня";
        }

        return day == today.AddDays(-1) ? "Вчера" : day.ToString("dd.MM.yyyy");
    }
}
