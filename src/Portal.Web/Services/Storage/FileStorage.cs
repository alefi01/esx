using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Хранение файлов на диске.
///
/// Раскладка простая и намеренно «глупая»:
///
///     {RootPath}\{номер папки}\{идентификатор файла}
///
/// Никаких пользовательских имён в пути нет. Отсюда сразу следует, что
/// невозможны: выход за пределы хранилища через «..\..\», совпадение имён,
/// запрещённые в файловой системе символы и слишком длинные пути.
/// Настоящее имя файла и его расширение живут в базе данных.
///
/// Цена решения: без базы содержимое диска нечитаемо. Это осознанный размен —
/// зато резервная копия базы и папки с файлами вместе восстанавливаются полностью,
/// а по отдельности они и так бесполезны.
/// </summary>
public sealed class FileStorage
{
    private readonly StorageOptions _options;
    private readonly ILogger<FileStorage> _logger;

    public FileStorage(IOptions<StorageOptions> options, ILogger<FileStorage> logger)
    {
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>Настроен ли путь к хранилищу.</summary>
    public bool IsConfigured => !string.IsNullOrWhiteSpace(_options.RootPath);

    public string RootPath => _options.RootPath;

    /// <summary>
    /// Сохраняет содержимое и возвращает имя файла на диске.
    /// Поток копируется потоком: файл целиком в память не читается,
    /// иначе загрузка нескольких больших файлов разом съела бы память сервера.
    /// </summary>
    public async Task<string> SaveAsync(int folderId, Stream content, CancellationToken cancellationToken)
    {
        EnsureConfigured();

        var directory = DirectoryFor(folderId);
        Directory.CreateDirectory(directory);

        var storageName = Guid.NewGuid().ToString("N");
        var path = Path.Combine(directory, storageName);

        try
        {
            await using var target = new FileStream(
                path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);

            await content.CopyToAsync(target, cancellationToken);
        }
        catch
        {
            // Не оставляем на диске обрывок: если запись не удалась или её
            // прервали, недописанный файл никому не нужен, а место занимает.
            TryDelete(path);
            throw;
        }

        return storageName;
    }

    /// <summary>Открывает файл на чтение. Бросает исключение, если файла нет.</summary>
    public Stream OpenRead(int folderId, string storageName)
    {
        EnsureConfigured();

        return new FileStream(
            PathFor(folderId, storageName),
            FileMode.Open, FileAccess.Read, FileShare.Read,
            bufferSize: 81920, useAsync: true);
    }

    public bool Exists(int folderId, string storageName) =>
        IsConfigured && File.Exists(PathFor(folderId, storageName));

    /// <summary>
    /// Стирает файл с диска. Отсутствие файла ошибкой не считается:
    /// цель — «файла нет», и она достигнута.
    /// </summary>
    public void Delete(int folderId, string storageName)
    {
        if (!IsConfigured)
        {
            return;
        }

        TryDelete(PathFor(folderId, storageName));
    }

    /// <summary>Убирает пустую папку с диска после удаления папки в портале.</summary>
    public void DeleteFolderIfEmpty(int folderId)
    {
        if (!IsConfigured)
        {
            return;
        }

        var directory = DirectoryFor(folderId);

        try
        {
            if (Directory.Exists(directory) && !Directory.EnumerateFileSystemEntries(directory).Any())
            {
                Directory.Delete(directory);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось убрать папку хранилища {Directory}.", directory);
        }
    }

    /// <summary>Проверка доступности хранилища — для страницы диагностики.</summary>
    public (bool Ok, string? Error) Probe()
    {
        if (!IsConfigured)
        {
            return (false, "Не задан путь Storage:RootPath.");
        }

        try
        {
            Directory.CreateDirectory(_options.RootPath);

            // Проверяем именно запись: прав на чтение может хватать,
            // а на запись — нет, и выяснится это в самый неподходящий момент.
            var probe = Path.Combine(_options.RootPath, $".probe-{Guid.NewGuid():N}");

            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            return (true, null);
        }
        catch (Exception ex)
        {
            return (false, ex.Message);
        }
    }

    private void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            // Файл мог быть открыт антивирусом или процессом резервного копирования.
            // Ронять из-за этого операцию не стоит: запись в базе уже удалена,
            // а осиротевший файл на диске мы увидим в журнале.
            _logger.LogWarning(ex, "Не удалось удалить файл {Path} с диска.", path);
        }
    }

    private string DirectoryFor(int folderId) =>
        Path.Combine(_options.RootPath, folderId.ToString("D6"));

    private string PathFor(int folderId, string storageName) =>
        Path.Combine(DirectoryFor(folderId), storageName);

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "Не задан путь к файловому хранилищу (Storage:RootPath). " +
                "Укажите его в appsettings.Production.json — см. docs/08-этап-3-файлы.md.");
        }
    }
}
