using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Files;

/// <summary>
/// Корзина: файлы, удалённые из хранилища, но ещё не стёртые с диска.
///
/// Человек видит здесь то, что удалил сам, а также всё удалённое в папках,
/// которыми он управляет. Администратор портала видит всё.
/// </summary>
public class TrashModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;
    private readonly FileStorage _storage;
    private readonly AuditLog _audit;
    private readonly StorageOptions _options;

    public TrashModel(
        PortalDbContext db,
        FolderTree tree,
        FileStorage storage,
        AuditLog audit,
        IOptions<StorageOptions> options)
    {
        _db = db;
        _tree = tree;
        _storage = storage;
        _audit = audit;
        _options = options.Value;
    }

    public sealed record TrashItem(StoredFile File, string FolderPath, bool CanRestore, DateTime PurgeAt);

    public IReadOnlyList<TrashItem> Items { get; private set; } = [];

    public int RetentionDays => _options.TrashRetentionDays;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostRestoreAsync(int fileId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var file = await _db.Files.AsTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file?.DeletedAt is null)
        {
            return NotFound();
        }

        var folder = _tree.Get(file.FolderId);

        if (folder is null || !CanAct(file, folder))
        {
            return Forbid();
        }

        // Проверяем, что восстанавливать есть что: файл мог быть стёрт с диска
        // вручную или потерян при переносе. Восстановленная запись без файла
        // хуже, чем честное сообщение об ошибке.
        if (!_storage.Exists(file.FolderId, file.StorageName))
        {
            ErrorMessage = $"Файл «{file.OriginalName}» отсутствует на диске, восстановить нечего.";
            return RedirectToPage();
        }

        file.DeletedAt = null;
        file.DeletedByUserName = null;

        _audit.Add(AuditAction.RestoreFromTrash, file.OriginalName, $"папка «{_tree.DisplayPath(folder)}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл «{file.OriginalName}» восстановлен.";

        return RedirectToPage();
    }

    /// <summary>Стереть немедленно, не дожидаясь срока.</summary>
    public async Task<IActionResult> OnPostPurgeAsync(int fileId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var file = await _db.Files.AsTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file?.DeletedAt is null)
        {
            return NotFound();
        }

        var folder = _tree.Get(file.FolderId);

        if (folder is null || !CanAct(file, folder))
        {
            return Forbid();
        }

        _storage.Delete(file.FolderId, file.StorageName);
        _db.Files.Remove(file);

        _audit.Add(AuditAction.Purge, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}», стёрт вручную");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл «{file.OriginalName}» стёрт окончательно.";

        return RedirectToPage();
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var deleted = await _db.Files
            .Where(f => f.DeletedAt != null)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(cancellationToken);

        var items = new List<TrashItem>();

        foreach (var file in deleted)
        {
            var folder = _tree.Get(file.FolderId);

            if (folder is null || !CanAct(file, folder))
            {
                continue;
            }

            items.Add(new TrashItem(
                file,
                _tree.DisplayPath(folder),
                CanRestore: _tree.CanWrite(User, folder),
                PurgeAt: file.DeletedAt!.Value.AddDays(_options.TrashRetentionDays)));
        }

        Items = items;
    }

    /// <summary>
    /// Видеть и трогать запись в корзине может тот, кто управляет папкой,
    /// либо тот, кто сам этот файл и удалил.
    /// </summary>
    private bool CanAct(StoredFile file, StorageFolder folder)
    {
        if (_tree.CanManage(User, folder))
        {
            return true;
        }

        return string.Equals(file.DeletedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase)
               && _tree.CanRead(User, folder);
    }

    public static string FormatSize(long bytes) => UploadValidator.Format(bytes);
}
