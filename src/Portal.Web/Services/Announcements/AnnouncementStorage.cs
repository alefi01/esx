using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Announcements;

/// <summary>
/// Файлы, приложенные к объявлениям.
///
/// Устроено так же, как вложения в переписках, и по той же причине:
/// в файловом хранилище доступ решается группами Active Directory,
/// а объявление видно всем, у кого есть доступ к порталу. Две разные
/// модели доступа в одном месте — источник ошибок, поэтому у вложений
/// своя ветка на диске:
///
///     {RootPath}\announcements\{номер объявления}\{случайное имя}
///
/// Имя, данное человеком, в пути не участвует никогда.
/// </summary>
public sealed class AnnouncementStorage
{
    private readonly StorageOptions _options;
    private readonly ILogger<AnnouncementStorage> _logger;

    /// <summary>
    /// Расширения, которые показываем прямо в ленте картинкой.
    ///
    /// SVG сюда не входит намеренно: это картинка, но внутри неё может быть
    /// код, а объявление видно всем сотрудникам сразу.
    /// </summary>
    private static readonly HashSet<string> ImageExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".png", ".jpg", ".jpeg", ".gif", ".webp", ".bmp" };

    public AnnouncementStorage(IOptions<StorageOptions> options, ILogger<AnnouncementStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.RootPath);

    public static bool LooksLikeImage(string fileName) =>
        ImageExtensions.Contains(Path.GetExtension(fileName));

    private string DirectoryFor(int announcementId) =>
        Path.Combine(_options.RootPath, "announcements", announcementId.ToString("D6"));

    private string PathFor(int announcementId, string storageName)
    {
        // Имя на диске задаём только мы сами, но проверяем всё равно:
        // одна ошибка в другом месте не должна открывать доступ
        // к произвольному файлу сервера.
        if (storageName.Contains('/') || storageName.Contains('\\') || storageName.Contains(".."))
        {
            throw new InvalidOperationException($"Недопустимое имя файла на диске: {storageName}");
        }

        return Path.Combine(DirectoryFor(announcementId), storageName);
    }

    public async Task<string> SaveAsync(int announcementId, Stream content, CancellationToken cancellationToken)
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Не задан путь Storage:RootPath — вложения сохранять некуда.");
        }

        var directory = DirectoryFor(announcementId);
        Directory.CreateDirectory(directory);

        var name = Guid.NewGuid().ToString("N");

        await using var target = new FileStream(
            Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await content.CopyToAsync(target, cancellationToken);

        return name;
    }

    public bool Exists(int announcementId, string storageName) =>
        IsConfigured && File.Exists(PathFor(announcementId, storageName));

    public Stream OpenRead(int announcementId, string storageName) =>
        new FileStream(PathFor(announcementId, storageName), FileMode.Open, FileAccess.Read, FileShare.Read);

    /// <summary>Стирает вложение. Ошибку не поднимает — только пишет в журнал.</summary>
    public void Delete(int announcementId, string storageName)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var path = PathFor(announcementId, storageName);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Не удалось стереть вложение {Name} объявления {Announcement}.", storageName, announcementId);
        }
    }

    /// <summary>Убирает всю папку объявления — при его удалении.</summary>
    public void DeleteAll(int announcementId)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var directory = DirectoryFor(announcementId);

            if (Directory.Exists(directory))
            {
                Directory.Delete(directory, recursive: true);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось убрать папку объявления {Announcement}.", announcementId);
        }
    }
}
