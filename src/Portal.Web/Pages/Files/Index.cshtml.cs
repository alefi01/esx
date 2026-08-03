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

    /// <summary>
    /// Объём каждой видимой подпапки вместе со всем вложенным, байты.
    /// Считается только для тех, кто вправе это видеть (см. ShowSizes):
    /// размер папки — косвенный признак её содержимого, и показывать его
    /// всем подряд незачем.
    /// </summary>
    public IReadOnlyDictionary<int, long> FolderSizes { get; private set; } =
        new Dictionary<int, long>();

    /// <summary>
    /// Показывать ли объёмы папок. Администратору портала — всегда,
    /// остальным — только там, где они и так управляют квотой.
    /// </summary>
    public bool ShowSizes => IsAdmin || Access >= FolderAccess.Manage;

    /// <summary>
    /// Права на КОНКРЕТНУЮ папку из списка, а не на текущую.
    ///
    /// Отдельный метод нужен потому, что на верхнем уровне текущей папки нет,
    /// и права «текущей папки» там равны нулю. Раньше из-за этого меню
    /// управления не появлялось у папок верхнего уровня даже у администратора.
    /// </summary>
    public bool CanManageFolder(StorageFolder folder) => _tree.CanManage(User, folder);

    /// <summary>Поисковый запрос по текущей папке и всему, что в ней вложено.</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    /// <summary>Найденный файл вместе с путём до папки, в которой он лежит.</summary>
    public sealed record SearchHit(StoredFile File, string FolderPath, int FolderId);

    public IReadOnlyList<SearchHit> SearchResults { get; private set; } = [];

    public bool IsSearching => !string.IsNullOrWhiteSpace(Query);

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

        // Страница умеет грузить файлы без перезагрузки, показывая ход выполнения.
        // В этом случае возвращаем не перенаправление, а короткий отчёт:
        // сколько принято, что отклонено и почему.
        if (IsAjax())
        {
            return new JsonResult(new
            {
                accepted,
                rejected = rejected.Select(r => new { file = r.FileName, reason = r.Reason }),
                message = StatusMessage,
                error = ErrorMessage
            });
        }

        return RedirectToPage(new { id = folderId });
    }

    /// <summary>
    /// Копирование файлов в другую папку — то, что происходит по Ctrl+V
    /// после Ctrl+C. Права проверяются с ОБЕИХ сторон: читать исходную папку
    /// и писать в целевую.
    /// </summary>
    public async Task<IActionResult> OnPostCopyAsync(
        int targetFolderId, int[] fileIds, CancellationToken cancellationToken)
    {
        return await CopyOrMoveAsync(targetFolderId, fileIds, move: false, cancellationToken);
    }

    /// <summary>Перемещение файлов — Ctrl+X, Ctrl+V либо перетаскивание на папку.</summary>
    public async Task<IActionResult> OnPostMoveAsync(
        int targetFolderId, int[] fileIds, CancellationToken cancellationToken)
    {
        return await CopyOrMoveAsync(targetFolderId, fileIds, move: true, cancellationToken);
    }

    private async Task<IActionResult> CopyOrMoveAsync(
        int targetFolderId, int[] fileIds, bool move, CancellationToken cancellationToken)
    {
        var redirect = await LoadAsync(targetFolderId, cancellationToken);

        if (redirect is not null)
        {
            return redirect;
        }

        if (Current is null || Access < FolderAccess.Write)
        {
            return Forbid();
        }

        var files = await _db.Files.AsTracking()
            .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null)
            .ToListAsync(cancellationToken);

        var done = 0;
        var problems = new List<string>();
        var used = UsedBytes;

        foreach (var file in files)
        {
            if (file.FolderId == targetFolderId)
            {
                // Вставка в ту же папку, откуда копировали, — ничего не делаем.
                continue;
            }

            var source = _tree.Get(file.FolderId);

            if (source is null || !_tree.CanRead(User, source))
            {
                problems.Add($"«{file.OriginalName}» — нет прав на исходную папку");
                continue;
            }

            // Перемещение — это ещё и удаление из исходной папки,
            // поэтому прав на чтение мало: нужно управление либо своё авторство.
            if (move)
            {
                var isOwner = string.Equals(
                    file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

                if (!_tree.CanManage(User, source) && !(isOwner && _tree.CanWrite(User, source)))
                {
                    problems.Add($"«{file.OriginalName}» — нет прав убрать файл из исходной папки");
                    continue;
                }
            }

            // Ограничения целевой папки действуют и здесь: иначе через копирование
            // можно было бы обойти и предел размера, и квоту, и запрет расширений.
            var rejection = _validator.Validate(
                file.OriginalName, file.SizeBytes, MaxFileSizeBytes, QuotaBytes, used);

            if (rejection is not null)
            {
                problems.Add($"«{rejection.FileName}» — {rejection.Reason}");
                continue;
            }

            try
            {
                if (move)
                {
                    _storage.Move(file.FolderId, targetFolderId, file.StorageName);

                    _audit.Add(AuditAction.Move, file.OriginalName,
                        $"из «{_tree.DisplayPath(source)}» в «{_tree.DisplayPath(Current)}»");

                    file.FolderId = targetFolderId;
                }
                else
                {
                    var newStorageName = _storage.Copy(file.FolderId, targetFolderId, file.StorageName);

                    _db.Files.Add(new StoredFile
                    {
                        FolderId = targetFolderId,
                        OriginalName = file.OriginalName,
                        StorageName = newStorageName,
                        SizeBytes = file.SizeBytes,
                        ContentType = file.ContentType,
                        UploadedAt = _time.GetUtcNow().UtcDateTime,
                        UploadedByUserName = User.Identity?.Name ?? "",
                        UploadedByDisplayName =
                            User.FindFirstValue(ClaimTypes.GivenName) ?? User.Identity?.Name ?? ""
                    });

                    _audit.Add(AuditAction.Copy, file.OriginalName,
                        $"из «{_tree.DisplayPath(source)}» в «{_tree.DisplayPath(Current)}»");
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось {Operation} файл {File}.",
                    move ? "переместить" : "скопировать", file.OriginalName);

                problems.Add($"«{file.OriginalName}» — ошибка при работе с диском");
                continue;
            }

            used += file.SizeBytes;
            done++;
        }

        if (done > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);

            StatusMessage = move
                ? $"Перемещено файлов: {done}."
                : $"Скопировано файлов: {done}.";
        }

        if (problems.Count > 0)
        {
            ErrorMessage = string.Join("; ", problems);
        }

        return RedirectToPage(new { id = targetFolderId });
    }

    /// <summary>Удаление нескольких выделенных файлов сразу — клавишей Delete.</summary>
    public async Task<IActionResult> OnPostDeleteFilesAsync(
        int folderId, int[] fileIds, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var files = await _db.Files.AsTracking()
            .Where(f => fileIds.Contains(f.Id) && f.DeletedAt == null)
            .ToListAsync(cancellationToken);

        var done = 0;
        var refused = 0;

        foreach (var file in files)
        {
            var folder = _tree.Get(file.FolderId);

            if (folder is null)
            {
                continue;
            }

            var isOwner = string.Equals(
                file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

            if (!_tree.CanManage(User, folder) && !(isOwner && _tree.CanWrite(User, folder)))
            {
                refused++;
                continue;
            }

            file.DeletedAt = _time.GetUtcNow().UtcDateTime;
            file.DeletedByUserName = User.Identity?.Name ?? "";

            _audit.Add(AuditAction.MoveToTrash, file.OriginalName, $"папка «{_tree.DisplayPath(folder)}»");

            done++;
        }

        if (done > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
            StatusMessage = done == 1 ? "Файл перемещён в корзину." : $"В корзину перемещено файлов: {done}.";
        }

        if (refused > 0)
        {
            ErrorMessage = $"Не хватило прав удалить файлов: {refused}.";
        }

        return RedirectToPage(new { id = folderId });
    }

    /// <summary>Запрос пришёл из кода страницы, а не из обычной формы.</summary>
    private bool IsAjax() =>
        string.Equals(Request.Headers["X-Requested-With"], "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

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

        if (await HasSiblingNamedAsync(parentId, name, exceptId: null, cancellationToken))
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

            await LoadFolderSizesAsync(cancellationToken);

            if (IsSearching)
            {
                // На верхнем уровне «текущей папки» нет, поэтому ищем
                // сразу по всем видимым корневым папкам.
                await SearchAsync(Subfolders, cancellationToken);
            }

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

        await LoadFolderSizesAsync(cancellationToken);

        if (IsSearching)
        {
            await SearchAsync([folder], cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Считает объём каждой видимой подпапки вместе со всем вложенным.
    ///
    /// Один запрос группировки на всё хранилище вместо запроса на каждую папку:
    /// папок немного, а по одному запросу на строку списка — это классический
    /// способ незаметно посадить страницу.
    /// </summary>
    private async Task LoadFolderSizesAsync(CancellationToken cancellationToken)
    {
        if (!ShowSizes || Subfolders.Count == 0)
        {
            return;
        }

        var own = await _db.Files
            .GroupBy(f => f.FolderId)
            .Select(g => new { FolderId = g.Key, Size = g.Sum(f => f.SizeBytes) })
            .ToDictionaryAsync(x => x.FolderId, x => x.Size, cancellationToken);

        var sizes = new Dictionary<int, long>();

        foreach (var subfolder in Subfolders)
        {
            sizes[subfolder.Id] = SumRecursive(subfolder, own);
        }

        FolderSizes = sizes;
    }

    private static long SumRecursive(StorageFolder folder, IReadOnlyDictionary<int, long> own, int depth = 0)
    {
        // Ограничение глубины — та же защита от испорченного дерева,
        // что и в вычислении прав: цикл в данных не должен вешать страницу.
        if (depth > 64)
        {
            return 0;
        }

        var total = own.TryGetValue(folder.Id, out var size) ? size : 0;

        foreach (var child in folder.Children)
        {
            total += SumRecursive(child, own, depth + 1);
        }

        return total;
    }

    /// <summary>
    /// Отдача файла «на просмотр»: с заголовком inline вместо attachment,
    /// чтобы браузер показал его, а не предложил сохранить.
    ///
    /// Тип содержимого берётся из белого списка (PreviewSupport), а НЕ из того,
    /// что записано в базе при загрузке. Иначе файл, притворившийся картинкой,
    /// мог бы выполниться в браузере как страница нашего портала.
    /// </summary>
    public async Task<IActionResult> OnGetPreviewAsync(int fileId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file is null || file.DeletedAt is not null)
        {
            return NotFound();
        }

        var folder = _tree.Get(file.FolderId);

        if (folder is null || !_tree.CanRead(User, folder))
        {
            return NotFound();
        }

        var contentType = PreviewSupport.ContentTypeFor(file.OriginalName);

        if (contentType is null || !_storage.Exists(file.FolderId, file.StorageName))
        {
            return NotFound();
        }

        await _audit.WriteAsync(
            AuditAction.Preview, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}»", cancellationToken);

        var stream = _storage.OpenRead(file.FolderId, file.StorageName);

        // Content-Disposition: inline — «покажи, а не сохраняй».
        // Имя всё равно указываем: браузер подставит его в заголовок окна
        // просмотра PDF и в кнопку «Сохранить» внутри него.
        //
        // Заголовок X-Content-Type-Options: nosniff ставится для всех ответов
        // в Program.cs — и именно он здесь главный: браузеру запрещено
        // «додумывать» тип содержимого, поэтому файл будет разобран ровно как
        // указано в белом списке, а не как страница с кодом внутри.
        // SetHttpFileName, а не просто FileName: у нас имена по-русски,
        // а в заголовках HTTP допустима только латиница. Этот метод запишет
        // имя дважды — упрощённое для старых браузеров и полное в кодировке
        // UTF-8 (filename*=), которое понимают все нынешние.
        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("inline");
        disposition.SetHttpFileName(file.OriginalName);

        Response.Headers.ContentDisposition = disposition.ToString();

        return new FileStreamResult(stream, contentType) { EnableRangeProcessing = true };
    }

    /// <summary>Сведения о файле или папке для окна «Свойства».</summary>
    public async Task<IActionResult> OnGetPropertiesAsync(
        int? fileId, int? folderId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        if (fileId is { } id)
        {
            var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            var parent = file is null ? null : _tree.Get(file.FolderId);

            if (file is null || parent is null || !_tree.CanRead(User, parent))
            {
                return NotFound();
            }

            return new JsonResult(new
            {
                kind = "file",
                title = file.OriginalName,
                rows = new[]
                {
                    new { name = "Тип", value = PreviewSupport.Describe(file.OriginalName) },
                    new { name = "Размер", value = UploadValidator.Format(file.SizeBytes) },
                    new { name = "Папка", value = _tree.DisplayPath(parent) },
                    new { name = "Загрузил", value = file.UploadedByDisplayName },
                    new { name = "Дата загрузки", value = file.UploadedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") },
                    new { name = "Тип содержимого", value = file.ContentType }
                }
            });
        }

        if (folderId is { } fid)
        {
            var folder = _tree.Get(fid);

            if (folder is null || !_tree.CanRead(User, folder))
            {
                return NotFound();
            }

            var own = await _db.Files
                .Where(f => f.FolderId == fid)
                .GroupBy(f => f.FolderId)
                .Select(g => new { Size = g.Sum(f => f.SizeBytes), Count = g.Count() })
                .FirstOrDefaultAsync(cancellationToken);

            var quota = _tree.EffectiveQuotaBytes(folder);
            var canManage = _tree.CanManage(User, folder);

            var rows = new List<object>
            {
                new { name = "Тип", value = "папка" },
                new { name = "Путь", value = _tree.DisplayPath(folder) },
                new { name = "Файлов в папке", value = (own?.Count ?? 0).ToString() },
                new { name = "Занято", value = UploadValidator.Format(own?.Size ?? 0) },
                new { name = "Подпапок", value = folder.Children.Count.ToString() },
                new { name = "Предел файла", value = UploadValidator.Format(_tree.EffectiveMaxFileSizeBytes(folder)) },
                new { name = "Квота", value = quota is null ? "не задана" : UploadValidator.Format(quota.Value) },
                new { name = "Наследование прав", value = folder.InheritPermissions ? "включено" : "выключено" },
                new
                {
                    name = "Автоочистка",
                    value = folder.RetentionDays is { } days ? $"файлы старше {days} дн." : "выключена"
                },
                new { name = "Создана", value = folder.CreatedAt.ToLocalTime().ToString("dd.MM.yyyy HH:mm") }
            };

            // Список групп с правами — сведения чувствительные,
            // показываем только тем, кто этими правами и управляет.
            if (canManage && folder.Permissions.Count > 0)
            {
                rows.Add(new
                {
                    name = "Права выданы",
                    value = string.Join(", ", folder.Permissions
                        .OrderBy(x => x.GroupName)
                        .Select(x => $"{x.GroupName} — {x.Access}"))
                });
            }

            return new JsonResult(new { kind = "folder", title = folder.Name, rows });
        }

        return NotFound();
    }

    /// <summary>Переименование файла. Менять может тот, кто вправе его удалить.</summary>
    public async Task<IActionResult> OnPostRenameFileAsync(
        int fileId, string newName, CancellationToken cancellationToken)
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

        var isOwner = string.Equals(
            file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

        if (!_tree.CanManage(User, folder) && !(isOwner && _tree.CanWrite(User, folder)))
        {
            return Forbid();
        }

        var safeName = UploadValidator.SanitizeName(newName);

        if (string.IsNullOrWhiteSpace(safeName))
        {
            ErrorMessage = "Пустое имя файла.";
            return RedirectToPage(new { id = file.FolderId });
        }

        // Расширение проверяем заново: иначе переименованием можно было бы
        // превратить безобидный файл в исполняемый и обойти запрет при загрузке.
        var rejection = _validator.Validate(
            safeName, file.SizeBytes,
            _tree.EffectiveMaxFileSizeBytes(folder), null, 0);

        if (rejection is not null)
        {
            ErrorMessage = $"Переименовать не удалось: {rejection.Reason}.";
            return RedirectToPage(new { id = file.FolderId });
        }

        var oldName = file.OriginalName;

        file.OriginalName = safeName;
        file.ContentType = UploadValidator.ResolveContentType(safeName);

        _audit.Add(AuditAction.Rename, safeName, $"было «{oldName}», папка «{_tree.DisplayPath(folder)}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Файл переименован в «{safeName}».";

        return RedirectToPage(new { id = file.FolderId });
    }

    /// <summary>Переименование папки. Требует прав управления ею.</summary>
    public async Task<IActionResult> OnPostRenameFolderAsync(
        int folderId, string newName, CancellationToken cancellationToken)
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

        var name = (newName ?? string.Empty).Trim();

        if (string.IsNullOrWhiteSpace(name) || name.Length > 100)
        {
            ErrorMessage = "Название папки должно быть от 1 до 100 символов.";
            return RedirectToPage(new { id = folder.ParentId });
        }

        if (await HasSiblingNamedAsync(folder.ParentId, name, folderId, cancellationToken))
        {
            ErrorMessage = $"Папка «{name}» здесь уже есть.";
            return RedirectToPage(new { id = folder.ParentId });
        }

        var tracked = await _db.Folders.AsTracking().FirstAsync(f => f.Id == folderId, cancellationToken);
        var oldName = tracked.Name;

        tracked.Name = name;

        _audit.Add(AuditAction.Rename, name, $"папка, было «{oldName}»");

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = $"Папка переименована в «{name}».";

        return RedirectToPage(new { id = folder.ParentId });
    }

    /// <summary>
    /// Есть ли рядом папка с таким же именем.
    ///
    /// ПОЧЕМУ ОТДЕЛЬНЫМ МЕТОДОМ, А НЕ ОДНИМ УСЛОВИЕМ
    ///
    /// Напрашивается написать «f.ParentId == parentId», и для вложенных папок
    /// это работает. А для папок верхнего уровня — нет: там parentId равен null,
    /// а в языке запросов к базе сравнение с пустым значением через «равно»
    /// не даёт истины НИКОГДА, даже если слева тоже пусто. Проверка молча
    /// переставала работать, и на верхнем уровне можно было завести вторую
    /// папку с тем же именем — а потом гадать, в какой из них лежат документы.
    ///
    /// Поэтому случай «верхний уровень» выделен явно, через IS NULL.
    /// Уникальный индекс в базе от этого, кстати, тоже не спасает: пустые
    /// значения там считаются различными, и пара (NULL, «Договоры») дважды
    /// его не нарушает. Отдельный индекс для верхнего уровня добавлен миграцией.
    /// </summary>
    private async Task<bool> HasSiblingNamedAsync(
        int? parentId, string name, int? exceptId, CancellationToken cancellationToken)
    {
        var siblings = parentId is null
            ? _db.Folders.Where(f => f.ParentId == null)
            : _db.Folders.Where(f => f.ParentId == parentId);

        return await siblings.AnyAsync(
            f => f.Name.ToLower() == name.ToLower() && (exceptId == null || f.Id != exceptId),
            cancellationToken);
    }

    /// <summary>
    /// Поиск файлов внутри текущей папки и всех вложенных.
    /// Ищем только там, куда у человека есть доступ на чтение: иначе поиск
    /// стал бы способом узнать, что лежит в закрытых папках.
    /// </summary>
    private async Task SearchAsync(
        IEnumerable<StorageFolder> roots, CancellationToken cancellationToken)
    {
        var readable = new List<StorageFolder>();

        void Collect(StorageFolder folder, int depth)
        {
            if (depth > 64)
            {
                return;
            }

            if (_tree.CanRead(User, folder))
            {
                readable.Add(folder);
            }

            foreach (var child in folder.Children)
            {
                Collect(child, depth + 1);
            }
        }

        foreach (var root in roots)
        {
            Collect(root, 0);
        }

        if (readable.Count == 0)
        {
            return;
        }

        var ids = readable.Select(f => f.Id).ToList();
        var pattern = Query!.Trim().ToLower();

        var found = await _db.Files
            .Where(f => ids.Contains(f.FolderId)
                        && f.DeletedAt == null
                        && f.OriginalName.ToLower().Contains(pattern))
            .OrderBy(f => f.OriginalName)
            .Take(200)
            .ToListAsync(cancellationToken);

        SearchResults = found
            .Select(f => new SearchHit(f, _tree.DisplayPath(_tree.Get(f.FolderId)!), f.FolderId))
            .ToList();
    }

    public string FormatSize(long bytes) => UploadValidator.Format(bytes);

    /// <summary>
    /// Как именно показывать файл в правой панели: "image", "pdf", "text"
    /// либо пустая строка, если предпросмотр невозможен.
    ///
    /// Строкой, а не перечислением: значение уходит в data-атрибут плитки,
    /// и код страницы сравнивает его как есть, без таблицы соответствий.
    /// </summary>
    public static string PreviewKindOf(StoredFile file) => PreviewSupport.KindOf(file.OriginalName) switch
    {
        PreviewKind.Image => "image",
        PreviewKind.Pdf => "pdf",
        PreviewKind.Text => "text",
        _ => ""
    };

    /// <summary>Предел размера текстового файла для показа целиком.</summary>
    public static long MaxTextPreviewBytes => PreviewSupport.MaxTextPreviewBytes;

    /// <summary>Может ли текущий пользователь удалить этот файл — для показа кнопки.</summary>
    public bool CanDelete(StoredFile file) =>
        Access >= FolderAccess.Manage
        || (Access >= FolderAccess.Write
            && string.Equals(file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase));
}
