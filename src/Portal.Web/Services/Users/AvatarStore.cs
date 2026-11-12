using Microsoft.Extensions.Caching.Memory;

namespace Portal.Web.Services.Users;

/// <summary>
/// Фотографии сотрудников — те, что человек поставил себе сам.
///
/// ПОЧЕМУ НЕ ИЗ ACTIVE DIRECTORY
///
/// В каталоге есть поле thumbnailPhoto, и в теории фотографии можно
/// брать оттуда. На практике его не заполняет никто: чтобы там что-то
/// появилось, кадровику пришлось бы сажать всех по очереди перед
/// фотоаппаратом и заливать снимки скриптом. Поэтому проще позволить
/// человеку поставить снимок самому — одно нажатие в настройках.
///
/// ПОЧЕМУ ФАЙЛАМИ, А НЕ В БАЗЕ
///
/// Картинка — это файл, и место файлам в файловой системе: отдавать
/// их оттуда умеет сам веб-сервер, а база не распухает на сотню
/// мегабайт картинок, которые никогда не участвуют ни в одном запросе.
/// Лежат рядом с ключами защиты данных, в App_Data: эта папка и так
/// доступна приложению на запись и не затирается при обновлении.
///
/// РАЗМЕР НЕ МЕНЯЕМ
///
/// Уменьшать картинку было бы правильно, но для этого нужна библиотека
/// работы с изображениями — лишняя зависимость в сети без интернета.
/// Вместо этого ограничиваем размер файла: полтора мегабайта на кружок
/// в 32 точки — это с большим запасом, а браузер ужмёт сам.
/// </summary>
public sealed class AvatarStore
{
    /// <summary>Сколько можно загрузить: кружку в 32 точки хватит и сотни килобайт.</summary>
    public const long MaxBytes = 1_500_000;

    /// <summary>Что принимаем. Ровно то, что браузер покажет без плясок.</summary>
    private static readonly Dictionary<string, string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        [".png"] = "image/png",
        [".jpg"] = "image/jpeg",
        [".jpeg"] = "image/jpeg",
        [".webp"] = "image/webp",
        [".gif"] = "image/gif"
    };

    private readonly IWebHostEnvironment _environment;
    private readonly IMemoryCache _cache;
    private readonly ILogger<AvatarStore> _logger;

    public AvatarStore(
        IWebHostEnvironment environment, IMemoryCache cache, ILogger<AvatarStore> logger)
    {
        _environment = environment;
        _cache = cache;
        _logger = logger;
    }

    private string Folder => Path.Combine(_environment.ContentRootPath, "App_Data", "avatars");

    /// <summary>Файл человека — или null, если он себе фотографию не ставил.</summary>
    public string? Find(string userName)
    {
        if (string.IsNullOrWhiteSpace(userName))
        {
            return null;
        }

        // Ответ держим в памяти минуту: аватарка рисуется десятки раз
        // на странице (список бесед, лента сообщений), и ходить за ней
        // в файловую систему на каждый кружок незачем.
        return _cache.GetOrCreate(Key(userName), entry =>
        {
            entry.AbsoluteExpirationRelativeToNow = TimeSpan.FromMinutes(1);

            foreach (var extension in Kinds.Keys)
            {
                var path = Path.Combine(Folder, SafeName(userName) + extension);

                if (File.Exists(path))
                {
                    return path;
                }
            }

            return null;
        });
    }

    public bool Has(string userName) => Find(userName) is not null;

    /// <summary>Тип картинки для ответа браузеру.</summary>
    public static string ContentTypeOf(string path) =>
        Kinds.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");

    /// <summary>Годится ли файл в аватарки. Возвращает причину отказа или null.</summary>
    public static string? Reject(string fileName, long length)
    {
        if (length <= 0)
        {
            return "файл пустой";
        }

        if (length > MaxBytes)
        {
            return $"файл больше {MaxBytes / 1_000_000.0:0.#} МБ";
        }

        return Kinds.ContainsKey(Path.GetExtension(fileName))
            ? null
            : "годятся только PNG, JPG, WEBP и GIF";
    }

    /// <summary>Сохранить новую фотографию, стерев прежнюю.</summary>
    public async Task SaveAsync(
        string userName, Stream content, string fileName, CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(Folder);

        Delete(userName);

        var path = Path.Combine(Folder, SafeName(userName) + Path.GetExtension(fileName).ToLowerInvariant());

        await using (var file = File.Create(path))
        {
            await content.CopyToAsync(file, cancellationToken);
        }

        _cache.Remove(Key(userName));
    }

    /// <summary>Убрать фотографию — вернуться к кружку с буквами.</summary>
    public void Delete(string userName)
    {
        foreach (var extension in Kinds.Keys)
        {
            var path = Path.Combine(Folder, SafeName(userName) + extension);

            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Не удалось стереть аватарку {User}.", userName);
            }
        }

        _cache.Remove(Key(userName));
    }

    private static string Key(string userName) => "avatar:" + userName.ToLowerInvariant();

    /// <summary>
    /// Имя файла из логина.
    ///
    /// Логин приходит из домена, но складывать его в путь как есть
    /// нельзя: в нём встречается и точка, и обратная косая черта
    /// (DOMAIN\ivanov), а это уже другой каталог.
    /// </summary>
    private static string SafeName(string userName)
    {
        var name = userName.ToLowerInvariant();

        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return name.Replace('.', '_').Replace('\\', '_').Replace('/', '_');
    }
}
