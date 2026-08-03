using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Files;

/// <summary>
/// Файловое хранилище: просмотр папки, загрузка, скачивание, удаление.
///
/// Все действия проверяют права ЗАНОВО, по данным из базы, а не по тому,
/// что пришло в запросе. Скрытая кнопка — это удобство интерфейса,
/// а не защита: любой POST можно отправить и без кнопки.
/// </summary>
public class IndexModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;
    private readonly FileStorage _storage;
    private readonly UploadValidator _validator;
    private readonly AuditLog _audit;
    private readonly ActiveDirectoryOptions _ad;
    private readonly TimeProvider _time;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        PortalDbContext db,
        FolderTree tree,
        FileStorage storage,
        UploadValidator validator,
        AuditLog audit,
        IOptions<ActiveDirectoryOptions> ad,
        TimeProvider time,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _tree = tree;
        _storage = storage;
        _validator = validator;
        _audit = audit;
        _ad = ad.Value;
        _time = time;
        _logger = logger;
    }

    /// <summary>Текущая папка. null — показываем список папок верхнего уровня.</summary>
    public StorageFolder? Current { get; private set; }

    public IReadOnlyList<StorageFolder> Breadcrumbs { get; private set; } = [];
    public IReadOnlyList<StorageFolder> Subfolders { get; private set; } = [];
    public IReadOnlyList<StoredFile> FilesInFolder { get; private set; } = [];

    public FolderAccess Access { get; private set; } = FolderAccess.None;
    public bool IsAdmin => User.IsInRole(_ad.AdminGroup);

    public long MaxFileSizeBytes { get; private set; }
    public long? QuotaBytes { get; private set; }
    public long UsedBytes { get; private set; }

    public bool StorageConfigured => _storage.IsConfigured;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    [BindProperty]
    [Required(ErrorMessage = "Введите название папки")]
    [MaxLength(100, ErrorMessage = "Название не длиннее 100 символов")]
    public string NewFolderName { get; set; } = "";

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken cancellationToken)
    {
        return await LoadAsync(id, cancellationToken) ?? Page();
    }

    /// <summary>
    /// Скачивание файла. Обработчик именованный: адрес получается
    /// /Files?handler=Download&amp;fileId=5 — отдельная страница ради этого не нужна.
    /// </summary>
    public async Task<IActionResult> OnGetDownloadAsync(int fileId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file is null || file.DeletedAt is not null)
        {
            return NotFound();
        }

        var folder = _tree.Get(file.FolderId);

        // Права проверяем по папке, в которой файл лежит СЕЙЧАС.
        if (folder is null || !_tree.CanRead(User, folder))
        {
            _logger.LogWarning(
                "Пользователь {User} пытался скачать файл {File}, не имея прав на папку.",
                User.Identity?.Name, fileId);

            return NotFound();   // не «запрещено»: не подтверждаем, что файл существует
        }

        if (!_storage.Exists(file.FolderId, file.StorageName))
        {
            _logger.LogError(
                "Файл {File} есть в базе, но отсутствует на диске ({Storage}).",
                file.OriginalName, file.StorageName);

            ErrorMessage = "Файл не найден на диске. Сообщите администратору.";

            return RedirectToPage(new { id = file.FolderId });
        }

        await _audit.WriteAsync(
            AuditAction.Download, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}»", cancellationToken);

        var stream = _storage.OpenRead(file.FolderId, file.StorageName);

        // enableRangeProcessing: браузер сможет докачать файл с места обрыва.
        // На канале между офисами с потерями это заметно помогает.

        // Явное указание имени и типа. Заголовок Content-Disposition: attachment
        // заставляет браузер скачать файл, а не пытаться его показать, —
        // на этапе 4 для PDF и картинок мы это поведение изменим осознанно.
        // enableRangeProcessing позволяет докачивать файл с обрыва и перематывать
        // мультимедиа. На канале между офисами с потерями это заметно помогает.
        return new FileStreamResult(stream, file.ContentType)
        {
            FileDownloadName = file.OriginalName,
            EnableRangeProcessing = true
        };
    }

    public async Task<IActionResult> OnPostUploadAsync(
        int folderId, List<IFormFile> uploads, CancellationToken cancellationToken)
    {
        var redirect = await LoadAsync(folderId, cancellationToken);

        if (redirect is not null)
        {
            return redirect;
        }

        if (Current is null || Access < FolderAccess.Write)
        {
            return Forbid();
        }

        if (!_storage.IsConfigured)
        {
            ErrorMessage = "Файловое хранилище не настроено: не задан путь Storage:RootPath.";
            return RedirectToPage(new { id = folderId });
        }

        if (uploads.Count == 0)
        {
            ErrorMessage = "Не выбрано ни одного файла.";
            return RedirectToPage(new { id = folderId });
        }

        var rejected = new List<UploadRejection>();
        var accepted = 0;
        var used = UsedBytes;

        foreach (var upload in uploads)
        {
            var rejection = _validator.Validate(
                upload.FileName, upload.Length, MaxFileSizeBytes, QuotaBytes, used);

            if (rejection is not null)
            {
                rejected.Add(rejection);
                continue;
            }

            var safeName = UploadValidator.SanitizeName(upload.FileName);

            string storageName;

            try
            {
                await using var content = upload.OpenReadStream();

                storageName = await _storage.SaveAsync(folderId, content, cancellationToken);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось записать файл {Name} на диск.", safeName);

                rejected.Add(new UploadRejection(safeName, "не удалось записать файл на диск"));
                continue;
            }

            _db.Files.Add(new StoredFile
            {
                FolderId = folderId,
                OriginalName = safeName,
                StorageName = storageName,
                SizeBytes = upload.Length,
                ContentType = UploadValidator.ResolveContentType(safeName),
                UploadedAt = _time.GetUtcNow().UtcDateTime,
                UploadedByUserName = User.Identity?.Name ?? "",
                UploadedByDisplayName = User.FindFirstValue(ClaimTypes.GivenName) ?? User.Identity?.Name ?? ""
            });

            _audit.Add(AuditAction.Upload, safeName,
                $"папка «{_tree.DisplayPath(Current)}», размер {UploadValidator.Format(upload.Length)}");

            used += upload.Length;
            accepted++;
        }

        if (accepted > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);

            StatusMessage = accepted == 1
                ? "Файл загружен."
                : $"Загружено файлов: {accepted}.";
        }

        if (rejected.Count > 0)
        {
            ErrorMessage = "Не загружены: " +
                string.Join("; ", rejected.Select(r => $"«{r.FileName}» — {r.Reason}"));
        }

        return RedirectToPage(new { id = folderId });
    }

    public async Task<IActionResult> OnPostCreateFolderAsync(
        int? parentId, CancellationToken cancellationToken)
    {
        var redirect = await LoadAsync(parentId, cancellationToken);

        if (redirect is not null)
        {
            return redirect;
        }

        // Папку верхнего уровня может создать только администратор портала:
        // корень хранилища — это структура организации, а не личное дело
        // каждого, у кого есть право записи хоть куда-то.
        if (Current is null)
        {
            if (!IsAdmin)
            {
                return Forbid();
            }
        }
        else if (Access < FolderAccess.Write)
        {
            return Forbid();
        }

        if (!ModelState.IsValid)
        {
            ErrorMessage = "Введите название папки (не длиннее 100 символов).";
            return RedirectToPage(new { id = parentId });
        }

        var name = NewFolderName.Trim();

        var duplicate = await _db.Folders
            .AnyAsync(f => f.ParentId == parentId && f.Name.ToLower() == name.ToLower(), cancellationToken);

        if (duplicate)
        {
            ErrorMessage = $"Папка «{name}» здесь уже есть.";
            return RedirectToPage(new { id = parentId });
        }

        var folder = new StorageFolder
        {
            Name = name,
            ParentId = parentId,
            CreatedAt = _time.GetUtcNow().UtcDateTime,
            CreatedByUserName = User.Identity?.Name ?? "",
            InheritPermissions = true
        };

        _db.Folders.Add(folder);

        _audit.Add(AuditAction.CreateFolder, name,
            Current is null ? "верхний уровень" : $"внутри «{_tree.DisplayPath(Current)}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Папка «{name}» создана.";

        return RedirectToPage(new { id = parentId });
    }

    /// <summary>Удаление файла — в корзину, а не с диска.</summary>
    public async Task<IActionResult> OnPostDeleteFileAsync(int fileId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var file = await _db.Files.AsTracking().FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file is null || file.DeletedAt is not null)
        {
            return NotFound();
        }

        var folder = _tree.Get(file.FolderId);

        if (folder is null)
        {
            return NotFound();
        }

        // Удалить файл может тот, кто им управляет, либо тот, кто его загрузил:
        // за своё загруженное человек отвечает сам, и просить администратора
        // убрать случайно залитый черновик — лишний шаг.
        var isOwner = string.Equals(file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

        if (!_tree.CanManage(User, folder) && !(isOwner && _tree.CanWrite(User, folder)))
        {
            return Forbid();
        }

        file.DeletedAt = _time.GetUtcNow().UtcDateTime;
        file.DeletedByUserName = User.Identity?.Name ?? "";

        _audit.Add(AuditAction.MoveToTrash, file.OriginalName, $"папка «{_tree.DisplayPath(folder)}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл «{file.OriginalName}» перемещён в корзину.";

        return RedirectToPage(new { id = file.FolderId });
    }

    /// <summary>
    /// Удаление папки. Только пустой — ни файлов (включая корзину), ни подпапок.
    ///
    /// Так решено сознательно: рекурсивное удаление одним нажатием — это
    /// возможность снести дерево документов по ошибке, и никакое окно
    /// подтверждения от этого не спасает. Чтобы удалить папку, придётся
    /// сначала разобраться с её содержимым, и это правильный порядок действий.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteFolderAsync(int folderId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var folder = _tree.Get(folderId);

        if (folder is null)
        {
            return NotFound();
        }

        if (!_tree.CanManage(User, folder))
        {
            return Forbid();
        }

        var parentId = folder.ParentId;

        if (folder.Children.Count > 0)
        {
            ErrorMessage = $"Папка «{folder.Name}» не пуста: в ней есть подпапки. " +
                           "Сначала удалите их.";

            return RedirectToPage(new { id = folderId });
        }

        var fileCount = await _db.Files.CountAsync(f => f.FolderId == folderId, cancellationToken);

        if (fileCount > 0)
        {
            ErrorMessage = $"Папка «{folder.Name}» не пуста: в ней {fileCount} файл(ов), " +
                           "включая находящиеся в корзине. Сначала очистите её.";

            return RedirectToPage(new { id = folderId });
        }

        var tracked = await _db.Folders.AsTracking().FirstAsync(f => f.Id == folderId, cancellationToken);

        _db.Folders.Remove(tracked);

        _audit.Add(AuditAction.DeleteFolder, folder.Name, _tree.DisplayPath(folder));

        await _db.SaveChangesAsync(cancellationToken);

        _storage.DeleteFolderIfEmpty(folderId);

        StatusMessage = $"Папка «{folder.Name}» удалена.";

        return RedirectToPage(new { id = parentId });
    }

    /// <summary>
    /// Общая подготовка: загрузить дерево, найти папку, проверить права,
    /// посчитать содержимое и ограничения. Возвращает не-null, если
    /// дальше работать нельзя и нужно перенаправление или отказ.
    /// </summary>
    private async Task<IActionResult?> LoadAsync(int? id, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        if (id is null)
        {
            Current = null;
            Breadcrumbs = [];
            Access = IsAdmin ? FolderAccess.Manage : FolderAccess.None;

            Subfolders = _tree.RootFolders()
                .Where(f => _tree.IsVisible(User, f))
                .ToList();

            return null;
        }

        var folder = _tree.Get(id.Value);

        if (folder is null)
        {
            return NotFound();
        }

        if (!_tree.IsVisible(User, folder))
        {
            // Не «доступ запрещён», а «не найдено»: сам факт существования
            // папки «Приказы по кадрам» — уже сведения, которых человеку знать незачем.
            return NotFound();
        }

        Current = folder;
        Access = _tree.AccessFor(User, folder);
        Breadcrumbs = _tree.PathTo(folder);

        Subfolders = folder.Children
            .Where(f => _tree.IsVisible(User, f))
            .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        MaxFileSizeBytes = _tree.EffectiveMaxFileSizeBytes(folder);
        QuotaBytes = _tree.EffectiveQuotaBytes(folder);

        // В занятое место входят и файлы из корзины: они по-прежнему
        // лежат на диске, и показывать место освободившимся было бы обманом.
        UsedBytes = await _db.Files
            .Where(f => f.FolderId == folder.Id)
            .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

        // Содержимое показываем только тем, кто имеет право читать саму папку.
        // Если папка видна лишь как «дорога» к вложенной разрешённой,
        // список файлов останется пустым — и это правильно.
        FilesInFolder = Access >= FolderAccess.Read
            ? await _db.Files
                .Where(f => f.FolderId == folder.Id && f.DeletedAt == null)
                .OrderBy(f => f.OriginalName)
                .ToListAsync(cancellationToken)
            : [];

        return null;
    }

    public string FormatSize(long bytes) => UploadValidator.Format(bytes);

    /// <summary>Может ли текущий пользователь удалить этот файл — для показа кнопки.</summary>
    public bool CanDelete(StoredFile file) =>
        Access >= FolderAccess.Manage
        || (Access >= FolderAccess.Write
            && string.Equals(file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase));
}
