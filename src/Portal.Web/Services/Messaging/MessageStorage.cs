using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Messaging;

/// <summary>
/// Файлы, приложенные к сообщениям.
///
/// ПОЧЕМУ НЕ ОБЩЕЕ ФАЙЛОВОЕ ХРАНИЛИЩЕ
///
/// В хранилище доступ решается группами Active Directory, здесь — участием
/// в беседе. Это разные правила, и держать их в одном месте опасно:
/// достаточно один раз перепутать проверку, чтобы чужая переписка
/// оказалась видна через раздел «Файлы».
///
/// Поэтому вложения лежат отдельной веткой на том же диске:
///     {RootPath}\messages\{номер беседы}\{случайное имя}
///
/// Имя, данное человеком, в пути не участвует НИКОГДА. Иначе файл,
/// названный «..\..\web.config», записался бы туда, куда никто не просил.
/// Настоящее имя хранится в базе.
/// </summary>
public sealed class MessageStorage
{
    private readonly StorageOptions _options;
    private readonly ILogger<MessageStorage> _logger;

    public MessageStorage(IOptions<StorageOptions> options, ILogger<MessageStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.RootPath);

    private string Root => Path.Combine(_options.RootPath, "messages");

    private string DirectoryFor(int conversationId) =>
        Path.Combine(Root, conversationId.ToString("D6"));

    private string PathFor(int conversationId, string storageName)
    {
        // Имя на диске задаём только мы сами, но проверяем всё равно:
        // одна ошибка в другом месте не должна превращаться в доступ
        // к произвольному файлу сервера.
        if (storageName.Contains('/') || storageName.Contains('\\') || storageName.Contains(".."))
        {
            throw new InvalidOperationException($"Недопустимое имя файла на диске: {storageName}");
        }

        return Path.Combine(DirectoryFor(conversationId), storageName);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Не задан путь Storage:RootPath — вложения сохранять некуда.");
        }
    }

    /// <summary>Сохраняет вложение и возвращает имя, под которым оно легло на диск.</summary>
    public async Task<string> SaveAsync(int conversationId, Stream content, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var directory = DirectoryFor(conversationId);
        Directory.CreateDirectory(directory);

        var name = Guid.NewGuid().ToString("N");

        await using var target = new FileStream(
            Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await content.CopyToAsync(target, cancellationToken);

        return name;
    }

    public bool Exists(int conversationId, string storageName) =>
        IsConfigured && File.Exists(PathFor(conversationId, storageName));

    public Stream OpenRead(int conversationId, string storageName)
    {
        EnsureConfigured();

        return new FileStream(
            PathFor(conversationId, storageName), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>Стирает вложение с диска. Ошибку не поднимает — только пишет в журнал.</summary>
    public void Delete(int conversationId, string storageName)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var path = PathFor(conversationId, storageName);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Не смогли стереть — это повод разобраться, но не повод
            // ронять фоновую уборку или страницу.
            _logger.LogWarning(ex,
                "Не удалось стереть вложение {Name} беседы {Conversation}.", storageName, conversationId);
        }
    }

    /// <summary>Убирает пустой каталог беседы — чтобы на диске не копился мусор.</summary>
    public void DeleteFolderIfEmpty(int conversationId)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var directory = DirectoryFor(conversationId);

            if (Directory.Exists(directory) && Directory.GetFileSystemEntries(directory).Length == 0)
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось убрать каталог беседы {Conversation}.", conversationId);
        }
    }
}
