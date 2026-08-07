using Microsoft.Extensions.Caching.Memory;

namespace Portal.Web.Services.Appearance;

/// <summary>Одна картинка-фон рабочего пространства.</summary>
/// <param name="Id">Имя файла — им тема запоминается в браузере.</param>
/// <param name="Name">Что видит человек в списке тем: имя файла без расширения.</param>
/// <param name="Url">Адрес картинки от корня сайта.</param>
public sealed record BackgroundTheme(string Id, string Name, string Url);

/// <summary>
/// Список фоновых тем — картинок, которые администратор кладёт в папку.
///
/// ПОЧЕМУ ПАПКА, А НЕ ЗАГРУЗКА ЧЕРЕЗ БРАУЗЕР
///
/// Фон — оформление, а не данные портала. Класть картинку в папку
/// на сервере администратор умеет и так, а страница загрузки означала бы
/// ещё одну форму, ещё одну проверку размера и типа и ещё один способ
/// положить на сервер файл. Ради пяти картинок, которые меняют раз в год,
/// это не окупается.
///
/// КАК ПОЛЬЗОВАТЬСЯ
///
/// Положите в wwwroot\img\themes файлы с говорящими именами:
///
///     Море.jpg, Горы.jpg, Город вечером.jpg
///
/// Имя файла (без расширения) станет названием темы в списке. Порядок
/// в списке — по имени. Показываются первые пять: длинный список
/// в маленьком меню выбирать неудобно.
///
/// ПОЧЕМУ С КЭШЕМ
///
/// Список нужен на КАЖДОЙ странице портала — меню тем есть в шапке.
/// Ходить за ним в файловую систему на каждый запрос незачем: папка
/// меняется раз в год, а обращения к диску идут постоянно. Держим
/// результат в памяти минуту; положенная картинка появится в списке
/// не мгновенно, и это ожидаемо.
/// </summary>
public sealed class BackgroundCatalog
{
    /// <summary>Сколько тем показываем. Больше в маленьком меню не выбрать.</summary>
    public const int MaxThemes = 5;

    private const string CacheKey = "appearance-backgrounds";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>Папка внутри wwwroot, куда кладут картинки.</summary>
    private const string FolderName = "img/themes";

    private static readonly string[] Extensions = [".jpg", ".jpeg", ".png", ".webp", ".avif"];

    private readonly IWebHostEnvironment _environment;
    private readonly IMemoryCache _cache;
    private readonly ILogger<BackgroundCatalog> _logger;

    public BackgroundCatalog(
        IWebHostEnvironment environment,
        IMemoryCache cache,
        ILogger<BackgroundCatalog> logger)
    {
        _environment = environment;
        _cache = cache;
        _logger = logger;
    }

    public IReadOnlyList<BackgroundTheme> All()
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyList<BackgroundTheme>? cached) && cached is not null)
        {
            return cached;
        }

        var list = Read();

        _cache.Set(CacheKey, list, CacheFor);

        return list;
    }

    private IReadOnlyList<BackgroundTheme> Read()
    {
        var root = _environment.WebRootPath;

        if (string.IsNullOrEmpty(root))
        {
            return [];
        }

        var directory = Path.Combine(root, FolderName.Replace('/', Path.DirectorySeparatorChar));

        if (!Directory.Exists(directory))
        {
            // Папки нет — тем нет. Это не ошибка: портал работает
            // со светлой и тёмной темой и без единой картинки.
            return [];
        }

        try
        {
            return Directory.EnumerateFiles(directory)
                .Where(path => Extensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(path => Path.GetFileName(path), StringComparer.CurrentCultureIgnoreCase)
                .Take(MaxThemes)
                .Select(path =>
                {
                    var file = Path.GetFileName(path);

                    // Имя файла попадает в адрес, а в нём допустима не всякая
                    // буква: пробелы и кириллица кодируются, иначе браузер
                    // запросит не тот адрес и картинка не покажется.
                    return new BackgroundTheme(
                        Id: file,
                        Name: Path.GetFileNameWithoutExtension(file),
                        Url: "/" + FolderName + "/" + Uri.EscapeDataString(file));
                })
                .ToList();
        }
        catch (Exception ex)
        {
            // Нет прав на папку, сетевой диск отвалился — оформление
            // не то, ради чего стоит ронять страницу.
            _logger.LogWarning(ex, "Не удалось прочитать папку с фонами {Directory}.", directory);

            return [];
        }
    }
}
