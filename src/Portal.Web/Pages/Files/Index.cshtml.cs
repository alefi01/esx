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
    private readonly FavoriteService _favorites;
    private readonly ActiveDirectoryOptions _ad;
    private readonly TimeProvider _time;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        PortalDbContext db,
        FolderTree tree,
        FileStorage storage,
        UploadValidator validator,
        AuditLog audit,
        FavoriteService favorites,
        IOptions<ActiveDirectoryOptions> ad,
        TimeProvider time,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _tree = tree;
        _storage = storage;
        _validator = validator;
        _audit = audit;
        _favorites = favorites;
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

    /// <summary>
    /// Запрещённые расширения — для проверки в браузере ДО отправки файла.
    /// Настоящая проверка всё равно на сервере (см. UploadValidator);
    /// здесь она нужна, чтобы не гнать по каналу то, что всё равно отвергнут.
    /// </summary>
    public IReadOnlyCollection<string> BlockedExtensions => _validator.BlockedExtensions;
    public long? QuotaBytes { get; private set; }
    public long UsedBytes { get; private set; }

    public bool StorageConfigured => _storage.IsConfigured;

    /// <summary>Что этот человек отметил звёздочкой — чтобы зажечь её на плитках.</summary>
    public HashSet<int> FavoriteFileIds { get; private set; } = [];
    public HashSet<int> FavoriteFolderIds { get; private set; } = [];

    /// <summary>
    /// Содержимое страницы одним списком: папки, потом файлы.
    ///
    /// Разметка списка общая на все разделы (см. FileEntry), поэтому здесь
    /// сущности из базы один раз превращаются в то, что нужно шаблону,
    /// и шаблон больше никуда не лезет — в том числе за правами.
    /// </summary>
    public IReadOnlyList<Portal.Web.Pages.Shared.FileEntry> Entries { get; private set; } = [];

    /// <summary>Собирает список для показа. Вызывается в самом конце подготовки страницы.</summary>
    private void BuildEntries()
    {
        var list = new List<Portal.Web.Pages.Shared.FileEntry>();

        if (IsSearching)
        {
            // В найденном главное — где файл лежит, поэтому у каждой строки
            // показывается путь до папки.
            foreach (var hit in SearchResults)
            {
                list.Add(Portal.Web.Pages.Shared.FileEntry.ForFile(
                    hit.File,
                    Url.Page("Index", "Download", new { fileId = hit.File.Id }) ?? "#",
                    PreviewKindOf(hit.File),
                    FavoriteFileIds.Contains(hit.File.Id),
                    CanDelete(hit.File),
                    hit.FolderPath));
            }

            Entries = list;

            return;
        }

        foreach (var folder in Subfolders)
        {
            list.Add(Portal.Web.Pages.Shared.FileEntry.ForFolder(
                folder,
                Url.Page("Index", new { id = folder.Id }) ?? "#",
                ChildCounts.GetValueOrDefault(folder.Id),
                FavoriteFolderIds.Contains(folder.Id),
                CanManageFolder(folder),
                ShowSizes ? FolderSizes.GetValueOrDefault(folder.Id) : null));
        }

        foreach (var file in FilesInFolder)
        {
            list.Add(Portal.Web.Pages.Shared.FileEntry.ForFile(
                file,
                Url.Page("Index", "Download", new { fileId = file.Id }) ?? "#",
                PreviewKindOf(file),
                FavoriteFileIds.Contains(file.Id),
                CanDelete(file)));
        }

        Entries = list;
    }

    /// <summary>
    /// Объём каждой видимой подпапки вместе со всем вложенным, байты.
    /// Считается только для тех, кто вправе это видеть (см. ShowSizes):
    /// размер папки — косвенный признак её содержимого, и показывать его
    /// всем подряд незачем.
    /// </summary>
    public IReadOnlyDictionary<int, long> FolderSizes { get; private set; } =
        new Dictionary<int, long>();

    /// <summary>
    /// Сколько всего лежит в каждой видимой подпапке — подпапок и файлов
    /// вместе. Отсюда берётся подпись «пусто» либо «7 элем.».
    /// </summary>
    public IReadOnlyDictionary<int, int> ChildCounts { get; private set; } =
        new Dictionary<int, int>();

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

    /// <summary>
    /// Порядок сортировки списка: name (по умолчанию), date, size, type.
    ///
    /// Сортируем НА СЕРВЕРЕ, а не в браузере. Причин две: порядок попадает
    /// в адрес страницы, и ссылку можно послать коллеге; и список остаётся
    /// отсортированным даже там, где JavaScript почему-то не отработал.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? Sort { get; set; }

    /// <summary>Проверенное значение — чтобы чужая строка в адресе ничего не сломала.</summary>
    public string SortMode => Sort is "date" or "size" or "type" ? Sort : "name";

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
        var redirect = await LoadAsync(id, cancellationToken);

        if (redirect is not null)
        {
            return redirect;
        }

        BuildEntries();

        return Page();
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

    /// <summary>Сколько файлов максимум кладём в один архив.</summary>
    private const int MaxZipEntries = 5000;

    /// <summary>
    /// Ускоренное скачивание: файл или целая папка отдаются одним архивом ZIP.
    ///
    /// ЗАЧЕМ
    ///
    /// Обычное скачивание папки — это десятки отдельных нажатий и десятки
    /// отдельных запросов, каждый со своим установлением соединения. Один
    /// архив идёт одним потоком и приходит заметно быстрее, а документы
    /// (Word, Excel, текст, чертежи в DWG) вдобавок ужимаются в разы.
    ///
    /// КАК УСТРОЕНО
    ///
    /// Архив НЕ собирается на диске и не копится в памяти: он пишется прямо
    /// в ответ, файл за файлом. Поэтому папка на десять гигабайт не требует
    /// ни десяти гигабайт места, ни ожидания перед началом скачивания —
    /// оно начинается сразу.
    ///
    /// Плата за это — неизвестный заранее размер: браузер покажет скачивание
    /// без полосы прогресса. Размен сознательный: считать размер заранее
    /// значило бы сжать всё дважды.
    ///
    /// Права проверяются по КАЖДОЙ папке, а не только по корню архива:
    /// внутри дерева попадаются папки с собственными правами, и содержимое
    /// закрытой не должно уехать в архиве вместе с открытой.
    /// </summary>
    public async Task<IActionResult> OnGetDownloadZipAsync(
        int? fileId, int? folderId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        if (!_storage.IsConfigured)
        {
            return NotFound();
        }

        // Что кладём в архив: имя внутри архива и где файл лежит на диске.
        var entries = new List<(string Name, int FolderId, string StorageName)>();
        string archiveName;
        string auditTarget;
        string auditDetails;

        if (fileId is { } id)
        {
            var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);

            if (file is null || file.DeletedAt is not null)
            {
                return NotFound();
            }

            var folder = _tree.Get(file.FolderId);

            if (folder is null || !_tree.CanRead(User, folder))
            {
                _logger.LogWarning(
                    "Пользователь {User} пытался скачать архивом файл {File}, не имея прав на папку.",
                    User.Identity?.Name, id);

                return NotFound();
            }

            if (!_storage.Exists(file.FolderId, file.StorageName))
            {
                return NotFound();
            }

            entries.Add((file.OriginalName, file.FolderId, file.StorageName));

            archiveName = Path.GetFileNameWithoutExtension(file.OriginalName) + ".zip";
            auditTarget = file.OriginalName;
            auditDetails = $"архивом, папка «{_tree.DisplayPath(folder)}»";
        }
        else if (folderId is { } rootId)
        {
            var root = _tree.Get(rootId);

            if (root is null || !_tree.CanRead(User, root))
            {
                return NotFound();
            }

            await CollectForZipAsync(root, "", entries, cancellationToken);

            archiveName = root.Name + ".zip";
            auditTarget = root.Name;
            auditDetails = $"папка «{_tree.DisplayPath(root)}» архивом, файлов: {entries.Count}";
        }
        else
        {
            return NotFound();
        }

        if (entries.Count == 0)
        {
            ErrorMessage = "Скачивать нечего: здесь нет ни одного доступного файла.";

            return RedirectToPage(new { id = folderId });
        }

        await _audit.WriteAsync(AuditAction.Download, auditTarget, auditDetails, cancellationToken);

        var disposition = new Microsoft.Net.Http.Headers.ContentDispositionHeaderValue("attachment");
        disposition.SetHttpFileName(SafeArchiveName(archiveName));

        Response.ContentType = "application/zip";
        Response.Headers.ContentDisposition = disposition.ToString();

        // Архив собирается на лету и его длина заранее неизвестна, поэтому
        // сжатие ответа отключаем явно: посредник, решивший «дожать» и без
        // того сжатый поток, ничего не выиграет, а буферизацией отложит
        // начало скачивания.
        Response.Headers.ContentEncoding = "identity";

        // ПОЧЕМУ ЗДЕСЬ РАЗРЕШЕНА СИНХРОННАЯ ЗАПИСЬ
        //
        // Из-за этого архивы приходили битыми, и разобраться стоит один раз.
        //
        // Kestrel и IIS по умолчанию запрещают синхронную запись в ответ
        // (AllowSynchronousIO = false): она занимает поток на всё время
        // отправки по сети. Обычно это правильно.
        //
        // Но ZipArchive устроен синхронно в двух местах, и обойти их нельзя:
        // закрывая каждый файл, он дописывает хвост сжатого потока
        // (DeflateStream.PurgeBuffers), а закрывая весь архив — оглавление
        // в самом конце. Обе записи идут через Stream.Write, и даже
        // асинхронное закрытие (DisposeAsync) сводится к ним же.
        //
        // Что при этом видел человек: содержимое файлов успевало уйти,
        // запись хвоста падала исключением, а оборвать уже начатый ответ
        // нельзя — и в браузер приезжал архив без оглавления. Распаковщик
        // на таком говорит «архив повреждён» и не показывает ни одного файла.
        //
        // Разрешение действует ТОЛЬКО на этот запрос, а не на весь портал.
        // Записи здесь редкие и крупными кусками (буфер сжатия), поэтому
        // поток занимается ненадолго.
        var bodyControl = HttpContext.Features.Get<Microsoft.AspNetCore.Http.Features.IHttpBodyControlFeature>();

        if (bodyControl is not null)
        {
            bodyControl.AllowSynchronousIO = true;
        }

        // Кодировка имён задаётся ЯВНО. По умолчанию ZipArchive ставит UTF-8
        // только для неанглийских имён и помечает это отдельным признаком,
        // который понимают не все распаковщики: русские имена превращались бы
        // в набор символов. С явной UTF-8 имя одинаково читается везде.
        await using (var archive = new System.IO.Compression.ZipArchive(
                   Response.Body,
                   System.IO.Compression.ZipArchiveMode.Create,
                   leaveOpen: true,
                   entryNameEncoding: System.Text.Encoding.UTF8))
        {
            foreach (var entry in entries)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (!_storage.Exists(entry.FolderId, entry.StorageName))
                {
                    // Файл есть в базе, но пропал с диска. Роняем не весь
                    // архив, а только этот файл: остальное человеку нужнее,
                    // чем сообщение об ошибке вместо всего сразу.
                    _logger.LogError(
                        "Файл {File} есть в базе, но отсутствует на диске — пропущен при сборке архива.",
                        entry.Name);

                    continue;
                }

                var item = archive.CreateEntry(entry.Name, CompressionFor(entry.Name));

                await using var source = _storage.OpenRead(entry.FolderId, entry.StorageName);
                await using var target = item.Open();

                await source.CopyToAsync(target, cancellationToken);
            }
        }

        await Response.Body.FlushAsync(cancellationToken);

        return new EmptyResult();
    }

    /// <summary>
    /// Обходит папку со всем вложенным и собирает список файлов для архива.
    /// В архив попадает только то, что человеку и так видно: закрытая
    /// подпапка пропускается целиком.
    /// </summary>
    private async Task CollectForZipAsync(
        StorageFolder folder,
        string prefix,
        List<(string Name, int FolderId, string StorageName)> entries,
        CancellationToken cancellationToken)
    {
        if (entries.Count >= MaxZipEntries)
        {
            return;
        }

        var files = await _db.Files
            .Where(f => f.FolderId == folder.Id && f.DeletedAt == null)
            .OrderBy(f => f.Id)
            .ToListAsync(cancellationToken);

        // Два файла с одинаковым именем в одной папке базой не запрещены,
        // а в архиве такая пара разворачивается в один файл поверх другого.
        // Поэтому повторам приписывается номер, как это делает проводник.
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var file in files)
        {
            if (entries.Count >= MaxZipEntries)
            {
                return;
            }

            var name = UniqueName(file.OriginalName, used);

            entries.Add((prefix + name, folder.Id, file.StorageName));
        }

        foreach (var child in folder.Children.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            if (!_tree.CanRead(User, child))
            {
                continue;
            }

            await CollectForZipAsync(
                child, prefix + SafeEntryName(child.Name) + "/", entries, cancellationToken);
        }
    }

    /// <summary>Делает имя неповторяющимся: «отчёт.docx», «отчёт (2).docx».</summary>
    private static string UniqueName(string original, HashSet<string> used)
    {
        var name = SafeEntryName(original);

        if (used.Add(name))
        {
            return name;
        }

        var stem = Path.GetFileNameWithoutExtension(name);
        var extension = Path.GetExtension(name);

        for (var n = 2; ; n++)
        {
            var candidate = $"{stem} ({n}){extension}";

            if (used.Add(candidate))
            {
                return candidate;
            }
        }
    }

    /// <summary>
    /// Имя внутри архива. Косые черты и «..» из имени убираются: имя приходит
    /// из базы, но попадает в путь, по которому распаковщик создаст файл,
    /// и складывать его туда как есть нельзя.
    /// </summary>
    private static string SafeEntryName(string name)
    {
        var safe = name.Replace('\\', '_').Replace('/', '_').Trim();

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            safe = safe.Replace(bad, '_');
        }

        return safe.Length == 0 || safe is "." or ".." ? "файл" : safe;
    }

    /// <summary>Имя самого архива — по тем же правилам, что и имена внутри него.</summary>
    private static string SafeArchiveName(string name)
    {
        var safe = SafeEntryName(name);

        return safe.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? safe : safe + ".zip";
    }

    /// <summary>
    /// Сжимать ли этот файл.
    ///
    /// Снимки, видео и уже готовые архивы сжаты внутри себя и от второго
    /// прохода не уменьшаются ни на процент — только отнимают время
    /// у остальных файлов. Их кладём в архив как есть.
    ///
    /// Всё остальное жмём НА МАКСИМУМ (SmallestSize). Портал отдаёт архив
    /// по сети, и узкое место здесь — канал, а не процессор сервера:
    /// лишние секунды сжатия окупаются меньшим объёмом передачи. На папках
    /// с документами разница с обычным сжатием — проценты объёма, но платит
    /// за них сервер, а выигрывает каждый скачивающий.
    /// </summary>
    private static System.IO.Compression.CompressionLevel CompressionFor(string name) =>
        Path.GetExtension(name).ToLowerInvariant() switch
        {
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".webp" or ".heic"
                or ".mp3" or ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv"
                or ".zip" or ".7z" or ".rar" or ".gz" or ".xz"
                => System.IO.Compression.CompressionLevel.NoCompression,

            _ => System.IO.Compression.CompressionLevel.SmallestSize
        };

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
    /// Перемещение файлов в другую папку — перетаскивание плитки на папку.
    ///
    /// Права проверяются с ОБЕИХ сторон: убрать файл из исходной папки
    /// и положить его в целевую. Копирования между папками портала нет:
    /// оно было завязано на собственный буфер обмена, который путали
    /// с буфером обмена Windows, и убрано вместе с ним.
    /// </summary>
    public async Task<IActionResult> OnPostMoveAsync(
        int targetFolderId, int[] fileIds, CancellationToken cancellationToken)
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
                // Файл бросили на ту же папку, где он и лежит, — делать нечего.
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
            var isOwner = string.Equals(
                file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase);

            if (!_tree.CanManage(User, source) && !(isOwner && _tree.CanWrite(User, source)))
            {
                problems.Add($"«{file.OriginalName}» — нет прав убрать файл из исходной папки");
                continue;
            }

            // Ограничения целевой папки действуют и здесь: иначе перетаскиванием
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
                _storage.Move(file.FolderId, targetFolderId, file.StorageName);

                _audit.Add(AuditAction.Move, file.OriginalName,
                    $"из «{_tree.DisplayPath(source)}» в «{_tree.DisplayPath(Current)}»");

                file.FolderId = targetFolderId;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось переместить файл {File}.", file.OriginalName);

                problems.Add($"«{file.OriginalName}» — ошибка при работе с диском");
                continue;
            }

            used += file.SizeBytes;
            done++;
        }

        if (done > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);

            StatusMessage = $"Перемещено файлов: {done}.";
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

        // Отметки читаются одним запросом на всю страницу, а не по запросу
        // на каждую плитку.
        FavoriteFileIds = await _favorites.FileIdsAsync(User, cancellationToken);
        FavoriteFolderIds = await _favorites.FolderIdsAsync(User, cancellationToken);

        if (id is null)
        {
            Current = null;
            Breadcrumbs = [];
            Access = IsAdmin ? FolderAccess.Manage : FolderAccess.None;

            Subfolders = _tree.RootFolders()
                .Where(f => _tree.IsVisible(User, f))
                .ToList();

            // Объёмы считаются ДО сортировки: по ним сортируют «по размеру».
            await LoadFolderFactsAsync(cancellationToken);

            Subfolders = SortFolders(Subfolders);

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

        Subfolders = folder.Children.Where(f => _tree.IsVisible(User, f)).ToList();

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
        // Сортируем УЖЕ ПОСЛЕ выборки, в памяти. По-русски база сортирует
        // по своим правилам сравнения, которые зависят от того, с какой
        // локалью её создали, — и «Ёлка» может встать не туда. Файлов
        // в одной папке немного, лишних затрат тут нет.
        FilesInFolder = Access >= FolderAccess.Read
            ? SortFiles(await _db.Files
                .Where(f => f.FolderId == folder.Id && f.DeletedAt == null)
                .ToListAsync(cancellationToken))
            : [];

        // Объёмы считаются ДО сортировки: по ним сортируют «по размеру».
        await LoadFolderFactsAsync(cancellationToken);

        Subfolders = SortFolders(Subfolders);

        if (IsSearching)
        {
            await SearchAsync([folder], cancellationToken);
        }

        return null;
    }

    /// <summary>
    /// Сортировка папок. У папки нет ни размера, ни типа, поэтому «по размеру»
    /// для неё работает по объёму вложенного, а «по типу» — как по имени.
    /// Папки при любой сортировке идут первыми: так же ведёт себя проводник.
    /// </summary>
    private IReadOnlyList<StorageFolder> SortFolders(IEnumerable<StorageFolder> folders) => SortMode switch
    {
        "date" => folders.OrderByDescending(f => f.CreatedAt).ToList(),

        "size" => folders
            .OrderByDescending(f => FolderSizes.GetValueOrDefault(f.Id))
            .ThenBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        _ => folders.OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase).ToList()
    };

    /// <summary>Сортировка файлов по выбранному в верхней панели порядку.</summary>
    private IReadOnlyList<StoredFile> SortFiles(IEnumerable<StoredFile> files) => SortMode switch
    {
        // «Сначала новые»: при сортировке по дате людей интересует свежее.
        "date" => files.OrderByDescending(f => f.UploadedAt).ToList(),

        // «Сначала крупные»: по размеру сортируют, когда ищут, что занимает место.
        "size" => files.OrderByDescending(f => f.SizeBytes).ToList(),

        // По типу — то есть по расширению, а внутри одного типа по имени.
        "type" => files
            .OrderBy(f => Path.GetExtension(f.OriginalName), StringComparer.OrdinalIgnoreCase)
            .ThenBy(f => f.OriginalName, StringComparer.CurrentCultureIgnoreCase)
            .ToList(),

        _ => files.OrderBy(f => f.OriginalName, StringComparer.CurrentCultureIgnoreCase).ToList()
    };

    /// <summary>
    /// Считает то, что показывается в подписи под каждой видимой подпапкой:
    /// сколько в ней элементов и (кому это положено видеть) сколько она весит
    /// вместе со всем вложенным.
    ///
    /// Один запрос группировки на всё хранилище вместо запроса на каждую папку:
    /// папок немного, а по одному запросу на строку списка — это классический
    /// способ незаметно посадить страницу.
    /// </summary>
    private async Task LoadFolderFactsAsync(CancellationToken cancellationToken)
    {
        if (Subfolders.Count == 0)
        {
            return;
        }

        // Сколько в каждой подпапке элементов, видно всем: подпись «пусто»
        // или «7 элем.» не выдаёт ничего, чего человек не увидел бы, просто
        // зайдя в папку.
        ChildCounts = await _tree.ChildCountsAsync(User, Subfolders, cancellationToken);

        if (!ShowSizes)
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

        // ОТДЕЛЬНАЯ политика безопасности для самого файла.
        //
        // Общая политика (Program.cs) содержит object-src 'none' — запрет
        // встраиваемых объектов. Для страниц портала это правильно, но она
        // вешается и на ЭТОТ ответ, то есть на сам PDF. А встроенный
        // просмотрщик PDF в Edge и Chrome — это как раз объект-плагин,
        // и в части версий браузера запрет его глушит: окно предпросмотра
        // остаётся пустым белым, без единой ошибки. Отсюда и разница
        // между офисами: браузеры там разных версий.
        //
        // Ослаблять безопасность это не значит. Здесь отдаётся не страница,
        // а файл из белого списка (картинка, PDF или обычный текст),
        // и заголовок nosniff запрещает браузеру «додумывать» тип. Выполнить
        // такое содержимое как код невозможно, поэтому из всей политики
        // осмысленно лишь ограничение на встраивание в чужие страницы.
        Response.Headers["Content-Security-Policy"] = "frame-ancestors 'self'";

        return new FileStreamResult(stream, contentType) { EnableRangeProcessing = true };
    }

    /// <summary>
    /// Предпросмотр документа Office: Word, Excel, PowerPoint.
    ///
    /// Отдаём НЕ файл, а разобранное из него содержимое — кусок разметки.
    /// Разбором занимается OfficeDocuments; там же объяснено, почему это
    /// вообще возможно без сторонних программ.
    ///
    /// Такой путь заодно безопаснее прямой отдачи файла: браузер никогда
    /// не видит исходный документ и не пытается ничего с ним сделать,
    /// а весь текст из документа мы кодируем при выводе.
    /// </summary>
    public async Task<IActionResult> OnGetOfficePreviewAsync(int fileId, CancellationToken cancellationToken)
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

        if (!OfficeDocuments.IsSupported(file.OriginalName)
            || !_storage.Exists(file.FolderId, file.StorageName))
        {
            return NotFound();
        }

        string html;

        try
        {
            await using var stream = _storage.OpenRead(file.FolderId, file.StorageName);

            html = OfficeDocuments.ToHtml(stream, file.OriginalName);
        }
        catch (Exception ex)
        {
            // Испорченный или необычный файл не должен ронять страницу.
            // Пишем в журнал приложения и показываем человеку понятную строку.
            _logger.LogWarning(ex, "Не удалось разобрать документ {File} для предпросмотра.", file.OriginalName);

            return Content(
                "<div class=\"doc\"><p class=\"doc__note\">Не удалось разобрать документ. " +
                "Скачайте файл и откройте его в своей программе.</p></div>",
                "text/html; charset=utf-8");
        }

        await _audit.WriteAsync(
            AuditAction.Preview, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}», разбор документа", cancellationToken);

        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Предпросмотр архива ZIP: список того, что внутри.
    ///
    /// Сам архив наружу не отдаётся — уходит только готовая разметка списка.
    /// Идёт отдельным обработчиком, а не через OnGetPreviewAsync, по той же
    /// причине, что и документы Office: там отдаётся файл, здесь — разметка.
    /// </summary>
    public async Task<IActionResult> OnGetArchivePreviewAsync(int fileId, CancellationToken cancellationToken)
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

        if (PreviewSupport.KindOf(file.OriginalName) != PreviewKind.Archive
            || !_storage.Exists(file.FolderId, file.StorageName))
        {
            return NotFound();
        }

        string html;

        try
        {
            await using var stream = _storage.OpenRead(file.FolderId, file.StorageName);

            html = OfficeDocuments.ArchiveToHtml(stream);
        }
        catch (Exception ex)
        {
            // Повреждённый или защищённый паролем архив не должен ронять страницу.
            _logger.LogWarning(ex, "Не удалось прочитать архив {File} для предпросмотра.", file.OriginalName);

            return Content(
                "<div class=\"doc\"><p class=\"doc__note\">Не удалось прочитать архив. " +
                "Возможно, он повреждён или защищён паролем.</p></div>",
                "text/html; charset=utf-8");
        }

        await _audit.WriteAsync(
            AuditAction.Preview, file.OriginalName,
            $"папка «{_tree.DisplayPath(folder)}», содержимое архива", cancellationToken);

        return Content(html, "text/html; charset=utf-8");
    }

    /// <summary>
    /// Кто имеет доступ к папке — списком строк вида «Бухгалтерия — запись».
    ///
    /// Права собираются вверх по дереву, пока действует наследование:
    /// именно так их и считает FolderTree, и показывать надо то же самое,
    /// иначе список вводил бы в заблуждение.
    /// </summary>
    private static List<string> DescribeAccess(StorageFolder folder)
    {
        // Наибольшее право на группу: одна и та же группа может быть назначена
        // и на папку, и на её родителя — остаётся сильнейшее.
        var best = new Dictionary<string, FolderAccess>(StringComparer.OrdinalIgnoreCase);

        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            foreach (var permission in current.Permissions)
            {
                if (!best.TryGetValue(permission.GroupName, out var existing) || permission.Access > existing)
                {
                    best[permission.GroupName] = permission.Access;
                }
            }

            if (!current.InheritPermissions)
            {
                break;
            }

            current = current.Parent;
        }

        return best
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => pair.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(pair => pair.Key + " — " + pair.Value switch
            {
                FolderAccess.Manage => "управление",
                FolderAccess.Write => "запись",
                FolderAccess.Read => "чтение",
                _ => "нет доступа"
            })
            .ToList();
    }

    /// <summary>
    /// Поставить или снять звёздочку. Отвечает JSON-ом, а не перенаправлением:
    /// страница при этом не перезагружается, и человек не теряет ни выделение,
    /// ни прокрутку. Без JavaScript кнопка просто не появится — потери
    /// небольшие, избранное это удобство, а не обязательная часть работы.
    /// </summary>
    public async Task<IActionResult> OnPostFavoriteAsync(
        int? fileId, int? folderId, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        // Отметку можно ставить только на то, что человеку и так видно.
        // Иначе избранное стало бы способом узнать, существует ли папка
        // с определённым номером.
        if (fileId is { } id)
        {
            var file = await _db.Files.FirstOrDefaultAsync(f => f.Id == id, cancellationToken);
            var parent = file is null ? null : _tree.Get(file.FolderId);

            if (file is null || parent is null || !_tree.CanRead(User, parent))
            {
                return NotFound();
            }
        }
        else if (folderId is { } fid)
        {
            var folder = _tree.Get(fid);

            if (folder is null || !_tree.IsVisible(User, folder))
            {
                return NotFound();
            }
        }
        else
        {
            return BadRequest();
        }

        var added = await _favorites.ToggleAsync(User, fileId, folderId, cancellationToken);

        return new JsonResult(new { favorite = added });
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
                },

                // Кому открыта папка, в которой лежит файл. Показывается
                // в окне предпросмотра блоком «Доступ»: человек сразу видит,
                // кому он на самом деле показал документ, прежде чем
                // отправлять на него ссылку.
                access = DescribeAccess(parent)
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
        PreviewKind.Office => "office",
        PreviewKind.Video => "video",
        PreviewKind.Audio => "audio",
        PreviewKind.Archive => "archive",
        _ => ""
    };

    /// <summary>Семейство файла для цвета значка: image, pdf, word, excel и так далее.</summary>
    public static string FileKindOf(StoredFile file) => FileKinds.Of(file.OriginalName);

    /// <summary>
    /// Расширение для подписи на значке: «PDF», «DOCX».
    /// Слишком длинное на значок не влезет, поэтому такое не подписываем.
    /// </summary>
    public static string ExtensionOf(StoredFile file)
    {
        var extension = Path.GetExtension(file.OriginalName).TrimStart('.').ToUpperInvariant();

        return extension.Length is 0 or > 4 ? "" : extension;
    }

    /// <summary>Предел размера текстового файла для показа целиком.</summary>
    public static long MaxTextPreviewBytes => PreviewSupport.MaxTextPreviewBytes;

    /// <summary>Может ли текущий пользователь удалить этот файл — для показа кнопки.</summary>
    public bool CanDelete(StoredFile file) =>
        Access >= FolderAccess.Manage
        || (Access >= FolderAccess.Write
            && string.Equals(file.UploadedByUserName, User.Identity?.Name, StringComparison.OrdinalIgnoreCase));
}
