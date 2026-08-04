using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Фоновая уборка хранилища. Делает две вещи:
///
///   1. Автоочистка папок. Если у папки задан срок хранения (RetentionDays),
///      файлы старше него отправляются в КОРЗИНУ — не стираются сразу.
///      Ошибка в настройке срока не должна стоить документов: у неё есть
///      Storage:TrashRetentionDays дней на то, чтобы её заметили.
///
///   2. Вычистка корзины. Файлы, пролежавшие в ней дольше положенного,
///      стираются с диска и удаляются из базы окончательно.
///
/// Обе операции пишутся в журнал действий — потом всегда можно ответить
/// на вопрос «куда делся файл».
///
/// Задача выполняется в том же процессе, что и сайт. Это допустимо, потому
/// что веб-сервер один. Если появится второй, две копии начнут убирать
/// одновременно — тогда уборку надо будет вынести в отдельную службу
/// или выключить (Storage:CleanupEnabled) на всех, кроме одного сервера.
/// </summary>
public sealed class StorageCleanupService : BackgroundService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly StorageOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<StorageCleanupService> _logger;

    public StorageCleanupService(
        IServiceScopeFactory scopeFactory,
        IOptions<StorageOptions> options,
        TimeProvider time,
        ILogger<StorageCleanupService> logger)
    {
        _scopeFactory = scopeFactory;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.CleanupEnabled)
        {
            _logger.LogInformation("Фоновая уборка хранилища выключена (Storage:CleanupEnabled = false).");
            return;
        }

        // Небольшая пауза после старта: пусть приложение сначала поднимется
        // и применит миграции, а уж потом мы полезем в базу.
        await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);

        var interval = TimeSpan.FromHours(Math.Max(1, _options.CleanupIntervalHours));

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Одна неудачная уборка не должна останавливать все последующие.
                _logger.LogError(ex, "Ошибка при фоновой уборке хранилища. Повтор через {Interval}.", interval);
            }

            try
            {
                await Task.Delay(interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    /// <summary>Один проход уборки. Вынесен отдельно, чтобы его можно было вызвать из теста.</summary>
    public async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();
        var storage = scope.ServiceProvider.GetRequiredService<FileStorage>();

        var now = _time.GetUtcNow().UtcDateTime;

        var movedToTrash = await ApplyRetentionAsync(db, now, cancellationToken);
        var purged = await PurgeTrashAsync(db, storage, now, cancellationToken);
        var forgotten = await PurgeAuditAsync(db, now, cancellationToken);

        if (movedToTrash > 0 || purged > 0)
        {
            _logger.LogInformation(
                "Уборка хранилища завершена: в корзину отправлено {Moved}, стёрто окончательно {Purged}, " +
                "записей журнала убрано {Forgotten}.",
                movedToTrash, purged, forgotten);
        }
    }

    /// <summary>Шаг 1: файлы старше срока хранения папки — в корзину.</summary>
    private async Task<int> ApplyRetentionAsync(PortalDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var folders = await db.Folders
            .AsTracking()
            .Where(f => f.RetentionDays != null && f.RetentionDays > 0)
            .ToListAsync(cancellationToken);

        var total = 0;

        foreach (var folder in folders)
        {
            var threshold = now.AddDays(-folder.RetentionDays!.Value);

            var expired = await db.Files
                .AsTracking()
                .Where(f => f.FolderId == folder.Id && f.DeletedAt == null && f.UploadedAt < threshold)
                .ToListAsync(cancellationToken);

            foreach (var file in expired)
            {
                file.DeletedAt = now;
                file.DeletedByUserName = "система";

                db.AuditEntries.Add(AuditLog.SystemEntry(
                    now, AuditAction.RetentionCleanup, file.OriginalName,
                    $"папка «{folder.Name}», срок хранения {folder.RetentionDays} дн., " +
                    $"загружен {file.UploadedAt:dd.MM.yyyy}"));
            }

            folder.RetentionLastRunAt = now;
            total += expired.Count;

            if (expired.Count > 0)
            {
                _logger.LogInformation(
                    "Автоочистка папки «{Folder}»: в корзину отправлено {Count} файлов старше {Days} дн.",
                    folder.Name, expired.Count, folder.RetentionDays);
            }
        }

        if (folders.Count > 0)
        {
            await db.SaveChangesAsync(cancellationToken);
        }

        return total;
    }

    /// <summary>Шаг 2: содержимое корзины старше срока — стереть с диска и из базы.</summary>
    /// <summary>
    /// Убирает записи журнала старше заданного срока.
    ///
    /// Удаляем одним запросом к базе, а не вычиткой в память: записей
    /// за полгода могут быть десятки тысяч, и тащить их на сервер приложения
    /// только чтобы тут же удалить — бессмысленная работа.
    /// </summary>
    private async Task<int> PurgeAuditAsync(
        PortalDbContext db, DateTime now, CancellationToken cancellationToken)
    {
        var days = _options.AuditRetentionDays;

        if (days <= 0)
        {
            return 0;   // 0 — хранить вечно
        }

        var threshold = now.AddDays(-days);

        return await db.AuditEntries
            .Where(a => a.At < threshold)
            .ExecuteDeleteAsync(cancellationToken);
    }

    private async Task<int> PurgeTrashAsync(
        PortalDbContext db, FileStorage storage, DateTime now, CancellationToken cancellationToken)
    {
        var threshold = now.AddDays(-Math.Max(0, _options.TrashRetentionDays));

        var expired = await db.Files
            .AsTracking()
            .Where(f => f.DeletedAt != null && f.DeletedAt < threshold)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var file in expired)
        {
            // Сначала диск, потом база. Если упадём между шагами, в базе
            // останется запись о файле, которого нет, — это заметно и чинится.
            // Обратный порядок дал бы «мусор» на диске, о котором никто не узнает.
            storage.Delete(file.FolderId, file.StorageName);

            db.AuditEntries.Add(AuditLog.SystemEntry(
                now, AuditAction.Purge, file.OriginalName,
                $"стёрт из корзины окончательно, удалён был {file.DeletedAt:dd.MM.yyyy}"));
        }

        db.Files.RemoveRange(expired);

        await db.SaveChangesAsync(cancellationToken);

        return expired.Count;
    }
}
