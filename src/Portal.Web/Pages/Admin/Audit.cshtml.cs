using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Журнал действий с файлами. Только для администраторов портала:
/// по нему видно, кто что скачивал, а это сведения чувствительные сами по себе.
/// </summary>
public class AuditModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly StorageOptions _options;

    public AuditModel(PortalDbContext db, IOptions<StorageOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public IReadOnlyList<AuditEntry> Entries { get; private set; } = [];

    public int TotalPages { get; private set; } = 1;

    [BindProperty(SupportsGet = true)]
    public int PageNumber { get; set; } = 1;

    /// <summary>Фильтр по логину. Пусто — все.</summary>
    [BindProperty(SupportsGet = true)]
    public string? UserFilter { get; set; }

    /// <summary>Фильтр по действию. null — все.</summary>
    [BindProperty(SupportsGet = true)]
    public AuditAction? ActionFilter { get; set; }

    /// <summary>Фильтр по имени файла или папки.</summary>
    [BindProperty(SupportsGet = true)]
    public string? TargetFilter { get; set; }

    public string? DatabaseError { get; private set; }

    /// <summary>Сколько всего записей в журнале и сколько из них старые.</summary>
    public int TotalEntries { get; private set; }
    public int OldEntries { get; private set; }
    public DateTime? OldestEntryAt { get; private set; }

    /// <summary>Действующий срок хранения, дней. 0 — хранить вечно.</summary>
    public int RetentionDays => _options.AuditRetentionDays;

    /// <summary>Как часто просыпается фоновая уборка — для подсказки на странице.</summary>
    public int CleanupHours => _options.CleanupIntervalHours;

    [TempData]
    public string? StatusMessage { get; set; }

    /// <summary>
    /// Убрать записи старше срока хранения.
    ///
    /// То же самое делает фоновая уборка раз в несколько часов; кнопка нужна,
    /// чтобы не ждать её — например, сразу после того как срок поменяли.
    /// </summary>
    public async Task<IActionResult> OnPostPurgeOldAsync(CancellationToken cancellationToken)
    {
        if (_options.AuditRetentionDays <= 0)
        {
            StatusMessage = "Срок хранения журнала не задан — удалять нечего. " +
                            "Задайте Storage:AuditRetentionDays в appsettings.Production.json.";

            return RedirectToPage();
        }

        var threshold = DateTime.UtcNow.AddDays(-_options.AuditRetentionDays);

        var removed = await _db.AuditEntries
            .Where(a => a.At < threshold)
            .ExecuteDeleteAsync(cancellationToken);

        StatusMessage = removed == 0
            ? "Записей старше срока хранения не нашлось."
            : $"Убрано записей: {removed}.";

        return RedirectToPage();
    }

    /// <summary>
    /// Стереть журнал целиком.
    ///
    /// Действие необратимое и заметное, поэтому требует подтверждения
    /// в интерфейсе, а сам факт очистки записывается в журнал первой же
    /// строкой — иначе очистка стала бы способом скрыть свои следы.
    /// </summary>
    public async Task<IActionResult> OnPostPurgeAllAsync(CancellationToken cancellationToken)
    {
        var removed = await _db.AuditEntries.ExecuteDeleteAsync(cancellationToken);

        _db.AuditEntries.Add(new AuditEntry
        {
            At = DateTime.UtcNow,
            UserName = User.Identity?.Name ?? "",
            Action = AuditAction.PurgeAuditLog,
            Target = "журнал действий",
            Details = $"журнал очищен полностью, удалено записей: {removed}"
        });

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Журнал очищен. Удалено записей: {removed}.";

        return RedirectToPage();
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var pageSize = Math.Max(10, _options.AuditPageSize);

        if (PageNumber < 1)
        {
            PageNumber = 1;
        }

        try
        {
            var query = _db.AuditEntries.AsQueryable();

            if (!string.IsNullOrWhiteSpace(UserFilter))
            {
                var user = UserFilter.Trim().ToLower();
                query = query.Where(e => e.UserName.ToLower().Contains(user));
            }

            if (ActionFilter is { } action)
            {
                query = query.Where(e => e.Action == action);
            }

            if (!string.IsNullOrWhiteSpace(TargetFilter))
            {
                var target = TargetFilter.Trim().ToLower();
                query = query.Where(e => e.Target.ToLower().Contains(target));
            }

            var total = await query.CountAsync(cancellationToken);

            // Сводка по всему журналу, а не по отфильтрованному:
            // кнопки очистки работают со всеми записями, и показывать
            // рядом с ними число из фильтра было бы обманом.
            TotalEntries = await _db.AuditEntries.CountAsync(cancellationToken);

            OldestEntryAt = await _db.AuditEntries
                .OrderBy(e => e.At)
                .Select(e => (DateTime?)e.At)
                .FirstOrDefaultAsync(cancellationToken);

            if (RetentionDays > 0)
            {
                var threshold = DateTime.UtcNow.AddDays(-RetentionDays);

                OldEntries = await _db.AuditEntries.CountAsync(e => e.At < threshold, cancellationToken);
            }

            TotalPages = Math.Max(1, (int)Math.Ceiling(total / (double)pageSize));

            if (PageNumber > TotalPages)
            {
                PageNumber = TotalPages;
            }

            Entries = await query
                .OrderByDescending(e => e.At)
                .ThenByDescending(e => e.Id)
                .Skip((PageNumber - 1) * pageSize)
                .Take(pageSize)
                .ToListAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            DatabaseError = ex.Message;
        }
    }

    public static string Describe(AuditAction action) => action switch
    {
        AuditAction.Upload => "загрузка",
        AuditAction.Download => "скачивание",
        AuditAction.MoveToTrash => "в корзину",
        AuditAction.RestoreFromTrash => "восстановление",
        AuditAction.Purge => "стирание",
        AuditAction.CreateFolder => "создание папки",
        AuditAction.DeleteFolder => "удаление папки",
        AuditAction.ChangeFolderSettings => "настройки папки",
        AuditAction.ChangePermissions => "права доступа",
        AuditAction.RetentionCleanup => "автоочистка",
        AuditAction.Rename => "переименование",
        AuditAction.Preview => "предпросмотр",
        AuditAction.Move => "перемещение",
        AuditAction.PurgeAuditLog => "очистка журнала",
        AuditAction.ViewConversation => "чтение переписки",
        _ => action.ToString()
    };
}
