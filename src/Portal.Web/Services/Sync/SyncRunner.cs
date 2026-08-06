using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Announcements;
using Portal.Web.Services.Messaging;
using Portal.Web.Services.Storage;

namespace Portal.Web.Services.Sync;

/// <summary>Итог одного обмена с одним филиалом — для страницы состояния.</summary>
public sealed record SyncRunReport(
    string PeerCode,
    string PeerName,
    bool Ok,
    int Received,
    int Applied,
    int Deferred,
    int Blobs,
    string? Error);

/// <summary>
/// Один проход синхронизации: спросить каждого соседа и применить полученное.
///
/// Вынесено отдельно от фоновой задачи намеренно: этот же код запускается
/// кнопкой «Синхронизировать сейчас» в панели администратора. Иначе
/// проверка настройки означала бы ожидание следующего срабатывания
/// по расписанию, а это никуда не годится.
/// </summary>
public sealed class SyncRunner
{
    /// <summary>
    /// Сколько пачек забрать у одного соседа за проход.
    ///
    /// Ограничение нужно, чтобы первый обмен с большой базой не длился
    /// бесконечно: остаток заберём в следующий раз. Потеряться при этом
    /// ничего не может — отсчёт ведётся по номеру записи.
    /// </summary>
    private const int MaxBatchesPerRun = 25;

    private readonly PortalDbContext _db;
    private readonly SyncClient _client;
    private readonly SyncApplier _applier;
    private readonly FileStorage _files;
    private readonly MessageStorage _messageFiles;
    private readonly AnnouncementStorage _announcementFiles;
    private readonly SyncOptions _options;
    private readonly TimeProvider _time;
    private readonly ILogger<SyncRunner> _logger;

    public SyncRunner(
        PortalDbContext db,
        SyncClient client,
        SyncApplier applier,
        FileStorage files,
        MessageStorage messageFiles,
        AnnouncementStorage announcementFiles,
        IOptions<SyncOptions> options,
        TimeProvider time,
        ILogger<SyncRunner> logger)
    {
        _db = db;
        _client = client;
        _applier = applier;
        _files = files;
        _messageFiles = messageFiles;
        _announcementFiles = announcementFiles;
        _options = options.Value;
        _time = time;
        _logger = logger;
    }

    public async Task<IReadOnlyList<SyncRunReport>> RunAsync(CancellationToken cancellationToken)
    {
        var reports = new List<SyncRunReport>();

        foreach (var peer in _options.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.BaseUrl) || string.IsNullOrWhiteSpace(peer.Code))
            {
                continue;
            }

            reports.Add(await RunPeerAsync(peer, cancellationToken));
        }

        await CleanOutboxAsync(cancellationToken);

        return reports;
    }

    private async Task<SyncRunReport> RunPeerAsync(SyncPeer peer, CancellationToken cancellationToken)
    {
        var state = await StateAsync(peer.Code, cancellationToken);

        state.LastAttemptAt = _time.GetUtcNow().UtcDateTime;

        var received = 0;
        var applied = 0;
        var deferred = 0;
        var blobs = 0;

        try
        {
            for (var round = 0; round < MaxBatchesPerRun; round++)
            {
                var batch = await _client.ChangesAsync(peer, state.LastReceivedId, cancellationToken);

                if (batch.Entries.Count == 0)
                {
                    break;
                }

                received += batch.Entries.Count;

                // Номер, до которого можно двигать отметку. Как только
                // одна запись отложена, дальше отметку двигать НЕЛЬЗЯ:
                // иначе отложенная потеряется навсегда.
                var safeId = state.LastReceivedId;
                var stop = false;

                foreach (var entry in batch.Entries)
                {
                    var result = await _applier.ApplyAsync(entry, cancellationToken);

                    if (result == SyncApplyResult.Deferred)
                    {
                        deferred++;
                        stop = true;
                        break;
                    }

                    if (result == SyncApplyResult.Applied)
                    {
                        applied++;
                    }

                    safeId = entry.Id;
                }

                state.LastReceivedId = safeId;

                await SaveStateAsync(state, cancellationToken);

                if (stop || !batch.HasMore)
                {
                    break;
                }
            }

            blobs = await FetchBlobsAsync(peer, cancellationToken);

            state.LastSuccessAt = _time.GetUtcNow().UtcDateTime;
            state.LastError = null;
            state.LastReceivedCount = received;

            await SaveStateAsync(state, cancellationToken);

            if (received > 0 || blobs > 0)
            {
                _logger.LogInformation(
                    "Синхронизация с филиалом {Peer}: получено {Received}, применено {Applied}, файлов {Blobs}.",
                    peer.Code, received, applied, blobs);
            }

            return new SyncRunReport(peer.Code, peer.Name, true, received, applied, deferred, blobs, null);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            var message = ex is SyncException ? ex.Message : $"{ex.GetType().Name}: {ex.Message}";

            state.LastError = message.Length > 500 ? message[..500] : message;
            state.LastReceivedCount = received;

            await SaveStateAsync(state, cancellationToken);

            _logger.LogWarning(ex, "Не удалось обменяться с филиалом {Peer}.", peer.Code);

            return new SyncRunReport(peer.Code, peer.Name, false, received, applied, deferred, blobs, message);
        }
    }

    /// <summary>
    /// Докачивает содержимое файлов, о которых мы уже знаем, но которых
    /// ещё нет на диске.
    ///
    /// Это и есть «файл загружается в свой филиал, а дальше расходится
    /// по остальным»: после этого шага в каждом филиале лежит своя копия,
    /// и открытие файла больше не идёт по каналу между офисами.
    /// </summary>
    private async Task<int> FetchBlobsAsync(SyncPeer peer, CancellationToken cancellationToken)
    {
        var done = 0;
        var chunk = Math.Clamp(_options.ChunkSizeKb, 16, 8192) * 1024;

        // Список мог накопить повторы: один и тот же файл упоминается
        // в нескольких записях журнала.
        foreach (var item in _applier.Pending.Distinct().ToList())
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                if (await FetchOneAsync(peer, item, chunk, cancellationToken))
                {
                    done++;
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // Один не скачавшийся файл не должен рушить весь обмен:
                // сведения о нём уже применены, содержимое доедет
                // в следующий раз — запись останется недокачанной.
                _logger.LogWarning(ex,
                    "Не удалось получить содержимое файла {Id} от филиала {Peer}.", item.GlobalId, peer.Code);
            }
        }

        _applier.Pending.Clear();

        return done;
    }

    private async Task<bool> FetchOneAsync(
        SyncPeer peer, PendingBlob item, int chunk, CancellationToken cancellationToken)
    {
        // Куда класть и подо что: у каждого вида вложений своё хранилище.
        var target = await ResolveTargetAsync(item, cancellationToken);

        if (target is null)
        {
            return false;
        }

        using var buffer = new MemoryStream();

        long offset = 0;

        while (true)
        {
            var part = await _client.BlobAsync(peer, item.Kind, item.GlobalId, offset, chunk, cancellationToken);

            if (part.Length == 0)
            {
                break;
            }

            await buffer.WriteAsync(part, cancellationToken);
            offset += part.Length;

            if (part.Length < chunk)
            {
                break;
            }
        }

        buffer.Position = 0;

        await target.Save(buffer, cancellationToken);

        return true;
    }

    /// <summary>
    /// Находит, куда сохранить скачанное, и как записать имя на диске.
    /// null — запись уже исчезла или содержимое больше не нужно.
    /// </summary>
    private async Task<BlobTarget?> ResolveTargetAsync(PendingBlob item, CancellationToken cancellationToken)
    {
        switch (item.Kind)
        {
            case SyncKinds.File:
            {
                var file = await _db.Files
                    .AsTracking()
                    .FirstOrDefaultAsync(f => f.GlobalId == item.GlobalId, cancellationToken);

                if (file is null || !string.IsNullOrEmpty(file.StorageName))
                {
                    return null;
                }

                return new BlobTarget(async (content, ct) =>
                {
                    file.StorageName = await _files.SaveAsync(file.FolderId, content, ct);

                    await SaveWithoutOutboxAsync(ct);
                });
            }

            case SyncKinds.Message:
            {
                var file = await _db.MessageFiles
                    .AsTracking()
                    .Include(f => f.Message)
                    .FirstOrDefaultAsync(f => f.GlobalId == item.GlobalId, cancellationToken);

                if (file?.Message is null || !string.IsNullOrEmpty(file.StorageName))
                {
                    return null;
                }

                var conversationId = file.Message.ConversationId;

                return new BlobTarget(async (content, ct) =>
                {
                    file.StorageName = await _messageFiles.SaveAsync(conversationId, content, ct);

                    await SaveWithoutOutboxAsync(ct);
                });
            }

            case SyncKinds.Announcement:
            {
                var file = await _db.AnnouncementFiles
                    .AsTracking()
                    .FirstOrDefaultAsync(f => f.GlobalId == item.GlobalId, cancellationToken);

                if (file is null || !string.IsNullOrEmpty(file.StorageName))
                {
                    return null;
                }

                var announcementId = file.AnnouncementId;

                return new BlobTarget(async (content, ct) =>
                {
                    file.StorageName = await _announcementFiles.SaveAsync(announcementId, content, ct);

                    await SaveWithoutOutboxAsync(ct);
                });
            }

            default:
                return null;
        }
    }

    /// <summary>
    /// Записать имя скачанного файла, не поднимая шума в журнале изменений.
    ///
    /// Имя файла на диске — дело сугубо местное: у каждого филиала оно своё,
    /// и рассылать его соседям незачем.
    /// </summary>
    private async Task SaveWithoutOutboxAsync(CancellationToken cancellationToken)
    {
        _db.SuppressSyncOutbox = true;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        finally
        {
            _db.SuppressSyncOutbox = false;
        }
    }

    private async Task<SyncPeerState> StateAsync(string code, CancellationToken cancellationToken)
    {
        var state = await _db.SyncPeers
            .AsTracking()
            .FirstOrDefaultAsync(p => p.PeerCode == code, cancellationToken);

        if (state is null)
        {
            state = new SyncPeerState { PeerCode = code };

            _db.SyncPeers.Add(state);

            await SaveWithoutOutboxAsync(cancellationToken);
        }

        return state;
    }

    private Task SaveStateAsync(SyncPeerState state, CancellationToken cancellationToken)
    {
        // Контекст очищается после каждой применённой записи
        // (см. SyncApplier), поэтому строку состояния каждый раз
        // подцепляем к контексту заново.
        _db.Entry(state).State = EntityState.Modified;

        return SaveWithoutOutboxAsync(cancellationToken);
    }

    /// <summary>
    /// Убирает из журнала записи старше срока хранения.
    ///
    /// Журнал только пополняется, и без уборки растёт без конца. Срок
    /// берётся с запасом: филиал, простоявший выключенным дольше,
    /// пропустит изменения безвозвратно, и это надо понимать
    /// при выборе значения.
    /// </summary>
    private async Task CleanOutboxAsync(CancellationToken cancellationToken)
    {
        var days = Math.Clamp(_options.OutboxRetentionDays, 7, 3650);
        var edge = _time.GetUtcNow().UtcDateTime.AddDays(-days);

        var removed = await _db.SyncOutbox
            .Where(e => e.ChangedAt < edge)
            .ExecuteDeleteAsync(cancellationToken);

        if (removed > 0)
        {
            _logger.LogInformation("Из журнала изменений убрано {Count} записей старше {Days} дней.", removed, days);
        }
    }

    /// <summary>Куда положить скачанное содержимое.</summary>
    private sealed record BlobTarget(Func<Stream, CancellationToken, Task> Save);
}
