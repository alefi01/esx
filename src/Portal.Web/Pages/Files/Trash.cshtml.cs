using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Files;

/// <summary>
/// Корзина РАЗДЕЛА: файлы, удалённые из этой папки верхнего уровня
/// и всего, что в ней лежит, но ещё не стёртые с диска.
///
/// КТО ВИДИТ
///
/// Все, кому видна сама папка, откуда файл удалён, — вместе с именем того,
/// кто удалил. Раньше правило было другим: человек видел только своё
/// удалённое. Причина у него была (корзина показывает, кто чем занимался
/// и что решил выбросить), но довод против оказался сильнее: в открытой
/// рабочей папке удаляет кто угодно, и если он в отпуске, восстановить
/// мог только администратор — то есть ровно тогда, когда нужнее всего.
///
/// Общего списка «что удалено по всему порталу» больше нет и пункта
/// в меню тоже: корзина живёт внутри раздела, рядом с его файлами.
/// </summary>
public class TrashModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;
    private readonly FileStorage _storage;
    private readonly AuditLog _audit;
    private readonly StorageOptions _options;
    private readonly ActiveDirectoryOptions _ad;

    public TrashModel(
        PortalDbContext db,
        FolderTree tree,
        FileStorage storage,
        AuditLog audit,
        IOptions<StorageOptions> options,
        IOptions<ActiveDirectoryOptions> ad)
    {
        _db = db;
        _tree = tree;
        _storage = storage;
        _audit = audit;
        _options = options.Value;
        _ad = ad.Value;
    }

    private bool IsAdmin => User.IsInRole(_ad.AdminGroup);

    public sealed record TrashItem(
        StoredFile File,
        string FolderPath,
        string DeletedBy,
        bool CanRestore,
        bool CanPurge,
        DateTime PurgeAt);

    public IReadOnlyList<TrashItem> Items { get; private set; } = [];

    /// <summary>Раздел, чью корзину смотрим. null — такого раздела нет.</summary>
    public StorageFolder? Root { get; private set; }

    /// <summary>Номер раздела из адреса страницы.</summary>
    [BindProperty(SupportsGet = true)]
    public int Id { get; set; }

    public int RetentionDays => _options.TrashRetentionDays;

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        // Раздела нет или он человеку не виден — страницы для него не существует.
        return Root is null ? NotFound() : Page();
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

        if (folder is null || !CanSee(folder) || !CanRestore(folder))
        {
            return Forbid();
        }

        // Проверяем, что восстанавливать есть что: файл мог быть стёрт с диска
        // вручную или потерян при переносе. Восстановленная запись без файла
        // хуже, чем честное сообщение об ошибке.
        if (!_storage.Exists(file.FolderId, file.StorageName))
        {
            ErrorMessage = $"Файл «{file.OriginalName}» отсутствует на диске, восстановить нечего.";
            return RedirectToPage(new { id = Id });
        }

        file.DeletedAt = null;
        file.DeletedByUserName = null;

        _audit.Add(AuditAction.RestoreFromTrash, file.OriginalName, $"папка «{_tree.DisplayPath(folder)}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл «{file.OriginalName}» восстановлен.";

        return RedirectToPage(new { id = Id });
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

        if (folder is null || !CanSee(folder) || !CanPurge(file, folder))
        {
            return Forbid();
        }

        _storage.Delete(file.FolderId, file.StorageName);
        _db.Files.Remove(file);

        _audit.Add(AuditAction.Purge, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}», стёрт вручную");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл «{file.OriginalName}» стёрт окончательно.";

        return RedirectToPage(new { id = Id });
    }

    /// <summary>
    /// Стереть всё, что человек видит в корзине, одним действием.
    ///
    /// Именно «что видит» и «что вправе стереть»: проверка та же, что
    /// и при стирании по одному, поэтому кнопка не может стать способом
    /// обойти разграничение доступа. Чужое удалённое она не трогает —
    /// его сотрёт срок хранения либо начальник отдела.
    /// </summary>
    public async Task<IActionResult> OnPostPurgeAllAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        if (Items.Count == 0)
        {
            return RedirectToPage(new { id = Id });
        }

        var ids = Items.Select(item => item.File.Id).ToList();

        // Перечитываем с отслеживанием: список выше собран запросом
        // без отслеживания, и удалять по нему EF не даст.
        var files = await _db.Files.AsTracking()
            .Where(f => ids.Contains(f.Id))
            .ToListAsync(cancellationToken);

        long freed = 0;

        foreach (var file in files)
        {
            var folder = _tree.Get(file.FolderId);

            // Файл мог быть восстановлен или стёрт, пока страница была открыта.
            if (folder is null || file.DeletedAt is null
                || !CanSee(folder) || !CanPurge(file, folder))
            {
                continue;
            }

            _storage.Delete(file.FolderId, file.StorageName);
            _db.Files.Remove(file);

            freed += file.SizeBytes;

            _audit.Add(AuditAction.Purge, file.OriginalName,
                $"папка «{_tree.DisplayPath(folder)}», очистка корзины");
        }

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = files.Count == 0
            ? "Корзина уже пуста."
            : $"Корзина очищена: стёрто файлов — {files.Count}, освобождено {UploadValidator.Format(freed)}.";

        return RedirectToPage(new { id = Id });
    }

    /// <summary>Сколько всего занимают файлы, видимые в корзине.</summary>
    public long TotalBytes => Items.Sum(item => item.File.SizeBytes);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var root = _tree.Get(Id);

        // Корзина бывает только у раздела — папки верхнего уровня.
        // Файл из подпапки попадает в корзину своего раздела, и там же
        // восстанавливается: туда, откуда его убрали.
        if (root is null || root.ParentId is not null || !_tree.IsVisible(User, root))
        {
            Root = null;
            Items = [];

            return;
        }

        Root = root;

        var deleted = await _db.Files
            .Where(f => f.DeletedAt != null)
            .OrderByDescending(f => f.DeletedAt)
            .ToListAsync(cancellationToken);

        var items = new List<TrashItem>();

        foreach (var file in deleted)
        {
            var folder = _tree.Get(file.FolderId);

            if (folder is null || _tree.RootOf(folder).Id != root.Id || !CanSee(folder))
            {
                continue;
            }

            items.Add(new TrashItem(
                file,
                _tree.DisplayPath(folder),
                // Имя удалившего видно всем, кому видна папка: иначе
                // «кто это выбросил» выясняется только у администратора.
                string.IsNullOrWhiteSpace(file.DeletedByUserName) ? "—" : file.DeletedByUserName,
                CanRestore: CanRestore(folder),
                CanPurge: CanPurge(file, folder),
                PurgeAt: file.DeletedAt!.Value.AddDays(_options.TrashRetentionDays)));
        }

        Items = items;
    }

    /// <summary>
    /// Видеть запись в корзине может всякий, кому видна папка, откуда файл
    /// удалён. Проверяем по САМОЙ папке файла, а не по разделу: файл мог
    /// лежать в закрытой подпапке, и показывать его всему отделу нельзя.
    /// </summary>
    private bool CanSee(StorageFolder folder) => IsAdmin || _tree.CanRead(User, folder);

    /// <summary>
    /// Восстановить может тот, кто вправе класть файлы в эту папку.
    /// Именно в этом и был смысл переноса корзины в раздел: убравший файл
    /// человек может быть в отпуске, а работа стоит.
    /// </summary>
    private bool CanRestore(StorageFolder folder) => IsAdmin || _tree.CanWrite(User, folder);

    /// <summary>
    /// Стереть насовсем — тот, кто управляет папкой (начальник отдела
    /// получает это право на всю свою ветку), и тот, кто сам это удалил.
    ///
    /// Разница с восстановлением не формальная: восстановление возвращает
    /// всё как было, а окончательное стирание отменить нельзя.
    /// </summary>
    private bool CanPurge(StoredFile file, StorageFolder folder) =>
        IsAdmin
        || _tree.CanManage(User, folder)
        || string.Equals(file.DeletedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

    public static string FormatSize(long bytes) => UploadValidator.Format(bytes);
}
