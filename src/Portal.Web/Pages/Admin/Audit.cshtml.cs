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
        _ => action.ToString()
    };
}
