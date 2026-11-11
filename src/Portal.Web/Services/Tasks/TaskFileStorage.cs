using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Tasks;

/// <summary>
/// Файлы задач: бланк, приложенный автором, и то, что сдал исполнитель.
///
/// ПОЧЕМУ ОТДЕЛЬНО ОТ ФАЙЛОВОГО ХРАНИЛИЩА
///
/// Та же причина, по которой там не лежат вложения переписки: в хранилище
/// доступ решается группами Active Directory, здесь — участием в задаче
/// («я поставил» или «я исполняю»). Это разные правила, и держать их
/// в одном месте опасно: достаточно один раз перепутать проверку,
/// чтобы чужой отчёт оказался виден через раздел «Файлы».
///
/// Поэтому файлы задач лежат отдельной веткой на том же диске:
///     {RootPath}\tasks\{номер задачи}\{случайное имя}
///
/// Имя, данное человеком, в пути не участвует НИКОГДА: иначе файл,
/// названный «..\..\web.config», записался бы туда, куда никто не просил.
/// Настоящее имя хранится в базе.
/// </summary>
public sealed class TaskFileStorage
{
    private readonly StorageOptions _options;
    private readonly ILogger<TaskFileStorage> _logger;

    public TaskFileStorage(IOptions<StorageOptions> options, ILogger<TaskFileStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.RootPath);

    private string Root => Path.Combine(_options.RootPath, "tasks");

    private string DirectoryFor(int taskId) => Path.Combine(Root, taskId.ToString("D6"));

    private string PathFor(int taskId, string storageName)
    {
        // Имя на диске задаём только мы сами, но проверяем всё равно:
        // одна ошибка в другом месте не должна превращаться в доступ
        // к произвольному файлу сервера.
        if (storageName.Contains('/') || storageName.Contains('\\') || storageName.Contains(".."))
        {
            throw new InvalidOperationException($"Недопустимое имя файла на диске: {storageName}");
        }

        return Path.Combine(DirectoryFor(taskId), storageName);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Не задан путь Storage:RootPath — файлы задач сохранять некуда.");
        }
    }

    public async Task<string> SaveAsync(int taskId, Stream content, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var directory = DirectoryFor(taskId);
        Directory.CreateDirectory(directory);

        var name = Guid.NewGuid().ToString("N");

        await using var target = new FileStream(
            Path.Combine(directory, name), FileMode.CreateNew, FileAccess.Write, FileShare.None);

        await content.CopyToAsync(target, cancellationToken);

        return name;
    }

    public bool Exists(int taskId, string storageName) =>
        IsConfigured && File.Exists(PathFor(taskId, storageName));

    public Stream OpenRead(int taskId, string storageName)
    {
        EnsureConfigured();

        return new FileStream(
            PathFor(taskId, storageName), FileMode.Open, FileAccess.Read, FileShare.Read);
    }

    /// <summary>Стирает файл с диска. Ошибку не поднимает — только пишет в журнал.</summary>
    public void Delete(int taskId, string storageName)
    {
        if (!IsConfigured)
        {
            return;
        }

        try
        {
            var path = PathFor(taskId, storageName);

            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex,
                "Не удалось стереть файл {Name} задачи {Task}.", storageName, taskId);
        }
    }
}
