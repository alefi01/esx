using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Files;

/// <summary>
/// Настройки папки: ограничения, срок хранения, наследование и права доступа.
/// Открыта тем, у кого на папку уровень Manage.
/// </summary>
public class SettingsModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;
    private readonly AuditLog _audit;
    private readonly StorageOptions _storage;
    private readonly ActiveDirectoryOptions _ad;

    public SettingsModel(
        PortalDbContext db,
        FolderTree tree,
        AuditLog audit,
        IOptions<StorageOptions> storage,
        IOptions<ActiveDirectoryOptions> ad)
    {
        _db = db;
        _tree = tree;
        _audit = audit;
        _storage = storage.Value;
        _ad = ad.Value;
    }

    public StorageFolder? Folder { get; private set; }
    public IReadOnlyList<StorageFolder> Breadcrumbs { get; private set; } = [];
    public IReadOnlyList<FolderPermission> Permissions { get; private set; } = [];

    /// <summary>Действующие значения с учётом наследования — чтобы было видно, что получится при «не задано».</summary>
    public long EffectiveMaxFileSize { get; private set; }
    public long? EffectiveQuota { get; private set; }
    public long UsedBytes { get; private set; }

    public int AbsoluteMaxMb => _storage.AbsoluteMaxFileSizeMb;
    public int DefaultMaxMb => _storage.DefaultMaxFileSizeMb;
    public int TrashDays => _storage.TrashRetentionDays;
    public string AdminGroup => _ad.AdminGroup;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    [BindProperty]
    public SettingsInput Input { get; set; } = new();

    [BindProperty]
    public PermissionInput NewPermission { get; set; } = new();

    public sealed class SettingsInput
    {
        [Display(Name = "Наследовать права родительской папки")]
        public bool InheritPermissions { get; set; } = true;

        // Ноль допустим и означает «без ограничения» — см. подсказку на странице.
        [Range(0, 10_000_000, ErrorMessage = "Укажите размер в мегабайтах, 0 (без ограничения) или оставьте поле пустым")]
        [Display(Name = "Предел размера одного файла, МБ")]
        public int? MaxFileSizeMb { get; set; }

        [Range(1, 10_000_000, ErrorMessage = "Укажите объём в мегабайтах или оставьте поле пустым")]
        [Display(Name = "Квота на объём папки, МБ")]
        public int? QuotaMb { get; set; }

        [Range(1, 3650, ErrorMessage = "Срок хранения — от 1 до 3650 дней, либо пусто")]
        [Display(Name = "Автоочистка: удалять файлы старше, дней")]
        public int? RetentionDays { get; set; }
    }

    public sealed class PermissionInput
    {
        [MaxLength(256)]
        [Display(Name = "Группа Active Directory")]
        public string GroupName { get; set; } = "";

        [Display(Name = "Уровень доступа")]
        public FolderAccess Access { get; set; } = FolderAccess.Read;
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(id, cancellationToken);

        if (result is not null)
        {
            return result;
        }

        Input = new SettingsInput
        {
            InheritPermissions = Folder!.InheritPermissions,
            MaxFileSizeMb = Folder.MaxFileSizeMb,
            QuotaMb = Folder.QuotaMb,
            RetentionDays = Folder.RetentionDays
        };

        return Page();
    }

    /// <summary>
    /// Куда вернуть человека после сохранения.
    ///
    /// «files» — он правит настройки в окне поверх списка файлов и должен
    /// остаться там же, а не оказаться на отдельной странице настроек,
    /// которую не открывал. Пусто — обычный путь, страница настроек.
    /// </summary>
    [BindProperty(SupportsGet = true)]
    public string? ReturnTo { get; set; }

    private IActionResult Back(int id) =>
        string.Equals(ReturnTo, "files", StringComparison.OrdinalIgnoreCase)
            ? RedirectToPage("Index", new { id })
            : RedirectToPage(new { id });

    public async Task<IActionResult> OnPostSaveAsync(int id, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(id, cancellationToken);

        if (result is not null)
        {
            return result;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        // Ноль здесь означает «без ограничения», поэтому сравнивать с потолком
        // портала имеет смысл только положительные значения. И только если
        // сам потолок задан: он тоже может быть снят.
        if (!_storage.FileSizeUnlimited
            && Input.MaxFileSizeMb > 0
            && Input.MaxFileSizeMb > _storage.AbsoluteMaxFileSizeMb)
        {
            ModelState.AddModelError("Input.MaxFileSizeMb",
                $"Больше общего верхнего предела ({_storage.AbsoluteMaxFileSizeMb} МБ) выставить нельзя. " +
                "Его меняют в настройке Storage:AbsoluteMaxFileSizeMb — и одновременно " +
                "в web.config, иначе IIS оборвёт загрузку раньше приложения.");

            return Page();
        }

        var folder = await _db.Folders.AsTracking().FirstAsync(f => f.Id == id, cancellationToken);

        // Записываем в журнал, что именно поменялось: «настройки изменены»
        // без подробностей никому потом не поможет.
        var changes = new List<string>();

        if (folder.InheritPermissions != Input.InheritPermissions)
        {
            changes.Add($"наследование прав: {Describe(folder.InheritPermissions)} → {Describe(Input.InheritPermissions)}");
        }

        if (folder.MaxFileSizeMb != Input.MaxFileSizeMb)
        {
            changes.Add($"предел файла: {Describe(folder.MaxFileSizeMb)} → {Describe(Input.MaxFileSizeMb)} МБ");
        }

        if (folder.QuotaMb != Input.QuotaMb)
        {
            changes.Add($"квота: {Describe(folder.QuotaMb)} → {Describe(Input.QuotaMb)} МБ");
        }

        if (folder.RetentionDays != Input.RetentionDays)
        {
            changes.Add($"автоочистка: {Describe(folder.RetentionDays)} → {Describe(Input.RetentionDays)} дн.");
        }

        folder.InheritPermissions = Input.InheritPermissions;
        folder.MaxFileSizeMb = Input.MaxFileSizeMb;
        folder.QuotaMb = Input.QuotaMb;
        folder.RetentionDays = Input.RetentionDays;

        if (changes.Count > 0)
        {
            _audit.Add(AuditAction.ChangeFolderSettings, _tree.DisplayPath(Folder!), string.Join("; ", changes));
        }

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = changes.Count > 0 ? "Настройки сохранены." : "Изменений не было.";

        return Back(id);
    }

    public async Task<IActionResult> OnPostAddPermissionAsync(int id, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(id, cancellationToken);

        if (result is not null)
        {
            return result;
        }

        var group = NewPermission.GroupName.Trim();

        if (string.IsNullOrWhiteSpace(group))
        {
            ErrorMessage = "Укажите имя группы Active Directory.";
            return Back(id);
        }

        var existing = await _db.FolderPermissions.AsTracking()
            .FirstOrDefaultAsync(p => p.FolderId == id && p.GroupName.ToLower() == group.ToLower(), cancellationToken);

        if (existing is not null)
        {
            // Повторное добавление той же группы — это изменение уровня,
            // а не ошибка. Так удобнее: не надо сначала удалять запись.
            var old = existing.Access;
            existing.Access = NewPermission.Access;

            _audit.Add(AuditAction.ChangePermissions, _tree.DisplayPath(Folder!),
                $"группа «{group}»: {old} → {NewPermission.Access}");

            StatusMessage = $"Уровень доступа группы «{group}» изменён на {NewPermission.Access}.";
        }
        else
        {
            _db.FolderPermissions.Add(new FolderPermission
            {
                FolderId = id,
                GroupName = group,
                Access = NewPermission.Access
            });

            _audit.Add(AuditAction.ChangePermissions, _tree.DisplayPath(Folder!),
                $"добавлена группа «{group}» с уровнем {NewPermission.Access}");

            StatusMessage = $"Группе «{group}» выдан доступ: {NewPermission.Access}.";
        }

        await _db.SaveChangesAsync(cancellationToken);

        return Back(id);
    }

    public async Task<IActionResult> OnPostRemovePermissionAsync(
        int id, int permissionId, CancellationToken cancellationToken)
    {
        var result = await LoadAsync(id, cancellationToken);

        if (result is not null)
        {
            return result;
        }

        var permission = await _db.FolderPermissions.AsTracking()
            .FirstOrDefaultAsync(p => p.Id == permissionId && p.FolderId == id, cancellationToken);

        if (permission is null)
        {
            return Back(id);
        }

        _db.FolderPermissions.Remove(permission);

        _audit.Add(AuditAction.ChangePermissions, _tree.DisplayPath(Folder!),
            $"убрана группа «{permission.GroupName}» (была {permission.Access})");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Доступ группы «{permission.GroupName}» убран.";

        return Back(id);
    }

    private async Task<IActionResult?> LoadAsync(int id, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var folder = _tree.Get(id);

        if (folder is null)
        {
            return NotFound();
        }

        if (!_tree.CanManage(User, folder))
        {
            return Forbid();
        }

        Folder = folder;
        Breadcrumbs = _tree.PathTo(folder);
        Permissions = folder.Permissions.OrderBy(p => p.GroupName).ToList();

        EffectiveMaxFileSize = _tree.EffectiveMaxFileSizeBytes(folder);
        EffectiveQuota = _tree.EffectiveQuotaBytes(folder);

        UsedBytes = await _db.Files
            .Where(f => f.FolderId == id)
            .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

        return null;
    }

    public string FormatSize(long bytes) => UploadValidator.Format(bytes);

    private static string Describe(bool value) => value ? "да" : "нет";
    private static string Describe(int? value) => value?.ToString() ?? "не задано";
}
