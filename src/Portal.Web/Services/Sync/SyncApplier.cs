using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Announcements;
using Portal.Web.Services.Messaging;
using Portal.Web.Services.Storage;

namespace Portal.Web.Services.Sync;

/// <summary>Чем закончилось применение одной записи — для журнала и страницы состояния.</summary>
public enum SyncApplyResult
{
    /// <summary>Изменение применено.</summary>
    Applied,

    /// <summary>У нас уже есть более свежая правка этого объекта — оставили свою.</summary>
    SkippedOlder,

    /// <summary>Пока применить нельзя: не пришёл объект, от которого этот зависит.</summary>
    Deferred,

    /// <summary>Применять нечего (пустое содержимое у неудалённой записи).</summary>
    Ignored
}

/// <summary>
/// Сторона ПРИНИМАЮЩЕГО: кладёт присланное соседом к себе в базу.
///
/// ПРАВИЛО РАЗБОРА СПОРОВ
///
/// Если объект уже есть и его местное время изменения ПОЗЖЕ присланного —
/// оставляем своё. Иначе перезаписываем присланным. Это то самое
/// «последнее изменение сохраняется»: правило простое, предсказуемое,
/// и человеку его можно объяснить одной фразой.
///
/// ЗАВИСИМОСТИ
///
/// Файл нельзя положить, пока нет его папки; сообщение — пока нет беседы.
/// Порядок в журнале обычно правильный (папку создали раньше файла),
/// но полагаться на это нельзя: пачка могла оборваться на середине.
/// Поэтому такие записи откладываются (Deferred) и берутся в следующий раз,
/// когда недостающее уже придёт. Номер, на котором остановились, при этом
/// НЕ двигается дальше отложенной записи — иначе она потерялась бы навсегда.
/// </summary>
public sealed class SyncApplier
{
    private readonly PortalDbContext _db;
    private readonly FileStorage _files;
    private readonly MessageStorage _messageFiles;
    private readonly AnnouncementStorage _announcementFiles;
    private readonly ILogger<SyncApplier> _logger;

    public SyncApplier(
        PortalDbContext db,
        FileStorage files,
        MessageStorage messageFiles,
        AnnouncementStorage announcementFiles,
        ILogger<SyncApplier> logger)
    {
        _db = db;
        _files = files;
        _messageFiles = messageFiles;
        _announcementFiles = announcementFiles;
        _logger = logger;
    }

    /// <summary>
    /// Что нужно докачать после применения: содержимое файлов, которых
    /// у нас ещё нет. Заполняется по ходу, забирается вызывающим.
    /// </summary>
    public List<PendingBlob> Pending { get; } = [];

    /// <summary>
    /// Применяет одну запись.
    ///
    /// Сохранение делается ЗДЕСЬ ЖЕ, по записи за раз, а не одной большой
    /// пачкой в конце. Так одна испорченная запись не отменяет всё принятое
    /// до неё: канал между офисами рвётся, и терять из-за этого час работы
    /// синхронизации нельзя.
    /// </summary>
    public async Task<SyncApplyResult> ApplyAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        // Ставим отметку: всё, что мы сейчас сохраним, — это ЧУЖОЕ изменение,
        // и объявлять его своим в журнале не нужно.
        _db.SuppressSyncOutbox = true;

        try
        {
            var result = entry.Kind switch
            {
                SyncKinds.Announcement => await AnnouncementAsync(entry, cancellationToken),
                SyncKinds.Folder => await FolderAsync(entry, cancellationToken),
                SyncKinds.File => await FileAsync(entry, cancellationToken),
                SyncKinds.Conversation => await ConversationAsync(entry, cancellationToken),
                SyncKinds.Message => await MessageAsync(entry, cancellationToken),

                // Незнакомый вид — не ошибка: возможно, сосед новее нас
                // версией. Пропускаем и идём дальше, а не падаем.
                _ => SyncApplyResult.Ignored
            };

            if (result == SyncApplyResult.Applied)
            {
                await _db.SaveChangesAsync(cancellationToken);
            }

            return result;
        }
        finally
        {
            _db.SuppressSyncOutbox = false;

            // Контекст живёт всю синхронизацию, и следы неудачной записи
            // не должны утащить за собой следующую.
            _db.ChangeTracker.Clear();
        }
    }

    /// <summary>Присланное старее нашего — своё не трогаем.</summary>
    private static bool OursIsNewer(ISyncable ours, SyncEntry entry) => ours.ChangedAt > entry.ChangedAt;

    private static void Stamp(ISyncable target, SyncEntry entry)
    {
        target.GlobalId = entry.GlobalId;
        target.OriginBranch = entry.OriginBranch;
        target.ChangedAt = entry.ChangedAt;
    }

    // ==================================================================
    // Объявления
    // ==================================================================

    private async Task<SyncApplyResult> AnnouncementAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        var existing = await _db.Announcements
            .AsTracking()
            .Include(a => a.Files)
            .FirstOrDefaultAsync(a => a.GlobalId == entry.GlobalId, cancellationToken);

        if (entry.Deleted)
        {
            if (existing is null)
            {
                return SyncApplyResult.Ignored;
            }

            _announcementFiles.DeleteAll(existing.Id);
            _db.Announcements.Remove(existing);

            return SyncApplyResult.Applied;
        }

        var payload = entry.Payload;

        if (payload is null)
        {
            return SyncApplyResult.Ignored;
        }

        if (existing is not null && OursIsNewer(existing, entry))
        {
            return SyncApplyResult.SkippedOlder;
        }

        var item = existing ?? new Announcement();

        item.Title = payload.Title ?? "";
        item.Body = payload.Body ?? "";
        item.AuthorUserName = payload.AuthorUserName ?? "";
        item.AuthorDisplayName = payload.AuthorDisplayName ?? "";
        item.CreatedAt = payload.CreatedAt;
        item.UpdatedAt = payload.UpdatedAt;
        item.IsPinned = payload.IsPinned;
        item.IsImportant = payload.IsImportant;
        Stamp(item, entry);

        if (existing is null)
        {
            _db.Announcements.Add(item);
        }

        SyncAttachments(
            payload.Attachments,
            item.Files,
            f => f.GlobalId,
            file => new AnnouncementFile
            {
                GlobalId = file.GlobalId,
                OriginalName = file.OriginalName,
                SizeBytes = file.SizeBytes,
                ContentType = file.ContentType,
                IsImage = AnnouncementStorage.LooksLikeImage(file.OriginalName),

                // Имя на диске у каждого филиала своё: файл здесь ещё
                // не лежит, и придумывать общее имя незачем.
                StorageName = ""
            },
            (target, file) =>
            {
                target.OriginalName = file.OriginalName;
                target.SizeBytes = file.SizeBytes;
                target.ContentType = file.ContentType;
            },
            removed => _announcementFiles.Delete(item.Id, removed.StorageName));

        // Содержимое вложений докачаем отдельно: см. PendingBlob.
        foreach (var file in item.Files.Where(f => string.IsNullOrEmpty(f.StorageName)))
        {
            Pending.Add(new PendingBlob(SyncKinds.Announcement, file.GlobalId));
        }

        return SyncApplyResult.Applied;
    }

    // ==================================================================
    // Папки
    // ==================================================================

    private async Task<SyncApplyResult> FolderAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        var existing = await _db.Folders
            .AsTracking()
            .Include(f => f.Permissions)
            .FirstOrDefaultAsync(f => f.GlobalId == entry.GlobalId, cancellationToken);

        if (entry.Deleted)
        {
            if (existing is null)
            {
                return SyncApplyResult.Ignored;
            }

            // Папку с содержимым не удаляем: в базе стоит запрет, и обойти
            // его здесь означало бы удалить чужие файлы по цепочке.
            // Если у соседа папку удалили, у нас она опустеет теми же
            // записями синхронизации, и удаление применится позже.
            var busy = await _db.Folders.AnyAsync(f => f.ParentId == existing.Id, cancellationToken)
                       || await _db.Files.AnyAsync(f => f.FolderId == existing.Id, cancellationToken);

            if (busy)
            {
                return SyncApplyResult.Deferred;
            }

            var folderId = existing.Id;

            _db.Folders.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);

            _files.DeleteFolderIfEmpty(folderId);

            return SyncApplyResult.Applied;
        }

        var payload = entry.Payload;

        if (payload is null)
        {
            return SyncApplyResult.Ignored;
        }

        int? parentId = null;

        if (payload.ParentGlobalId is { } parentGlobal)
        {
            var parent = await _db.Folders
                .Where(f => f.GlobalId == parentGlobal)
                .Select(f => (int?)f.Id)
                .FirstOrDefaultAsync(cancellationToken);

            if (parent is null)
            {
                // Родительская папка ещё не пришла — подождём следующего раза.
                return SyncApplyResult.Deferred;
            }

            parentId = parent;
        }

        if (existing is not null && OursIsNewer(existing, entry))
        {
            return SyncApplyResult.SkippedOlder;
        }

        var folder = existing ?? new StorageFolder();

        folder.Name = payload.Title ?? "";
        folder.ParentId = parentId;
        folder.InheritPermissions = payload.InheritPermissions;
        folder.MaxFileSizeMb = payload.MaxFileSizeMb;
        folder.RetentionDays = payload.RetentionDays;
        folder.CreatedAt = payload.CreatedAt;
        folder.CreatedByUserName = payload.AuthorUserName ?? "";
        Stamp(folder, entry);

        if (existing is null)
        {
            _db.Folders.Add(folder);
        }

        // Права переписываем целиком: их немного, а сравнивать построчно
        // значит писать код, который потом никто не проверит.
        folder.Permissions.Clear();

        foreach (var permission in payload.Permissions ?? [])
        {
            folder.Permissions.Add(new FolderPermission
            {
                GroupName = permission.GroupName,
                Access = (FolderAccess)permission.Access
            });
        }

        return SyncApplyResult.Applied;
    }

    // ==================================================================
    // Файлы
    // ==================================================================

    private async Task<SyncApplyResult> FileAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        var existing = await _db.Files
            .AsTracking()
            .FirstOrDefaultAsync(f => f.GlobalId == entry.GlobalId, cancellationToken);

        if (entry.Deleted)
        {
            if (existing is null)
            {
                return SyncApplyResult.Ignored;
            }

            _files.Delete(existing.FolderId, existing.StorageName);
            _db.Files.Remove(existing);

            return SyncApplyResult.Applied;
        }

        var payload = entry.Payload;

        if (payload?.FolderGlobalId is not { } folderGlobal)
        {
            return SyncApplyResult.Ignored;
        }

        var folderId = await _db.Folders
            .Where(f => f.GlobalId == folderGlobal)
            .Select(f => (int?)f.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (folderId is null)
        {
            return SyncApplyResult.Deferred;
        }

        if (existing is not null && OursIsNewer(existing, entry))
        {
            return SyncApplyResult.SkippedOlder;
        }

        var file = existing ?? new StoredFile();
        var wasFolderId = existing?.FolderId;

        file.FolderId = folderId.Value;
        file.OriginalName = payload.OriginalName ?? "";
        file.SizeBytes = payload.SizeBytes;
        file.ContentType = payload.ContentType ?? "application/octet-stream";
        file.UploadedAt = payload.CreatedAt;
        file.UploadedByUserName = payload.AuthorUserName ?? "";
        file.UploadedByDisplayName = payload.AuthorDisplayName ?? "";
        file.DeletedAt = payload.DeletedAt;
        Stamp(file, entry);

        if (existing is null)
        {
            _db.Files.Add(file);
        }
        else if (wasFolderId is { } from && from != file.FolderId && !string.IsNullOrEmpty(file.StorageName))
        {
            // Файл переложили в другую папку — переложим и на диске.
            try
            {
                _files.Move(from, file.FolderId, file.StorageName);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Не удалось перенести файл {Name} между папками при синхронизации.", file.OriginalName);
            }
        }

        // Содержимого у нас может ещё не быть — тогда его надо докачать.
        // Именно это и есть «файл загружается в свой филиал, а потом
        // расходится по остальным».
        if (string.IsNullOrEmpty(file.StorageName))
        {
            Pending.Add(new PendingBlob(SyncKinds.File, file.GlobalId));
        }

        return SyncApplyResult.Applied;
    }

    // ==================================================================
    // Беседы и сообщения
    // ==================================================================

    private async Task<SyncApplyResult> ConversationAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        var existing = await _db.Conversations
            .AsTracking()
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.GlobalId == entry.GlobalId, cancellationToken);

        if (entry.Deleted)
        {
            if (existing is null)
            {
                return SyncApplyResult.Ignored;
            }

            _db.Conversations.Remove(existing);

            return SyncApplyResult.Applied;
        }

        var payload = entry.Payload;

        if (payload is null)
        {
            return SyncApplyResult.Ignored;
        }

        if (existing is not null && OursIsNewer(existing, entry))
        {
            return SyncApplyResult.SkippedOlder;
        }

        var conversation = existing;

        if (conversation is null && !string.IsNullOrEmpty(payload.PairKey))
        {
            // Переписка двоих могла завестись в обоих филиалах сразу:
            // Иванов написал Петрову у себя, Петров Иванову у себя.
            // Ключ пары одинаков, и в базе на него стоит запрет повторов —
            // значит объединяем в одну, а не пытаемся создать вторую.
            conversation = await _db.Conversations
                .AsTracking()
                .Include(c => c.Participants)
                .FirstOrDefaultAsync(c => c.PairKey == payload.PairKey, cancellationToken);
        }

        var isNew = conversation is null;

        conversation ??= new Conversation();

        conversation.IsGroup = payload.IsGroup;
        conversation.Title = payload.Title ?? "";
        conversation.PairKey = payload.PairKey ?? "";
        conversation.CreatedByUserName = payload.CreatedByUserName ?? "";
        conversation.CreatedAt = payload.CreatedAt;

        // Время последнего сообщения не откатываем назад: у нас могло
        // прийти сообщение, о котором сосед ещё не знает.
        var lastAt = payload.UpdatedAt ?? payload.CreatedAt;

        conversation.LastMessageAt = lastAt > conversation.LastMessageAt ? lastAt : conversation.LastMessageAt;
        Stamp(conversation, entry);

        if (isNew)
        {
            _db.Conversations.Add(conversation);
        }

        SyncParticipants(conversation, payload.Participants ?? []);

        return SyncApplyResult.Applied;
    }

    /// <summary>
    /// Состав беседы приводится к присланному. Отметки прочтения при этом
    /// сохраняются: «до какого сообщения дочитал» — дело местное, у каждого
    /// филиала своё, и затирать его чужими данными нельзя.
    /// </summary>
    private static void SyncParticipants(Conversation conversation, List<SyncParticipant> incoming)
    {
        foreach (var person in incoming)
        {
            var mine = conversation.Participants
                .FirstOrDefault(p => string.Equals(p.UserName, person.UserName, StringComparison.OrdinalIgnoreCase));

            if (mine is null)
            {
                conversation.Participants.Add(new ConversationParticipant
                {
                    UserName = person.UserName,
                    DisplayName = person.DisplayName,
                    IsOwner = person.IsOwner,
                    JoinedAt = person.JoinedAt
                });

                continue;
            }

            mine.DisplayName = person.DisplayName;
            mine.IsOwner = person.IsOwner;
        }

        var gone = conversation.Participants
            .Where(p => !incoming.Any(i => string.Equals(i.UserName, p.UserName, StringComparison.OrdinalIgnoreCase)))
            .ToList();

        foreach (var person in gone)
        {
            conversation.Participants.Remove(person);
        }
    }

    private async Task<SyncApplyResult> MessageAsync(SyncEntry entry, CancellationToken cancellationToken)
    {
        var existing = await _db.Messages
            .AsTracking()
            .Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.GlobalId == entry.GlobalId, cancellationToken);

        if (entry.Deleted)
        {
            if (existing is null)
            {
                return SyncApplyResult.Ignored;
            }

            foreach (var file in existing.Files)
            {
                _messageFiles.Delete(existing.ConversationId, file.StorageName);
            }

            _db.Messages.Remove(existing);

            return SyncApplyResult.Applied;
        }

        var payload = entry.Payload;

        if (payload?.ConversationGlobalId is not { } conversationGlobal)
        {
            return SyncApplyResult.Ignored;
        }

        var conversationId = await _db.Conversations
            .Where(c => c.GlobalId == conversationGlobal)
            .Select(c => (int?)c.Id)
            .FirstOrDefaultAsync(cancellationToken);

        if (conversationId is null)
        {
            return SyncApplyResult.Deferred;
        }

        if (existing is not null && OursIsNewer(existing, entry))
        {
            return SyncApplyResult.SkippedOlder;
        }

        var message = existing ?? new Message();

        message.ConversationId = conversationId.Value;
        message.AuthorUserName = payload.AuthorUserName ?? "";
        message.AuthorDisplayName = payload.AuthorDisplayName ?? "";
        message.Body = payload.Body ?? "";
        message.CreatedAt = payload.CreatedAt;
        message.DeletedAt = payload.DeletedAt;
        Stamp(message, entry);

        if (existing is null)
        {
            _db.Messages.Add(message);
        }

        SyncAttachments(
            payload.Attachments,
            message.Files,
            f => f.GlobalId,
            file => new MessageFile
            {
                GlobalId = file.GlobalId,
                OriginalName = file.OriginalName,
                SizeBytes = file.SizeBytes,
                ContentType = file.ContentType,
                PurgedAt = file.PurgedAt,
                StorageName = ""
            },
            (target, file) =>
            {
                target.OriginalName = file.OriginalName;
                target.SizeBytes = file.SizeBytes;
                target.ContentType = file.ContentType;
                target.PurgedAt = file.PurgedAt;
            },
            removed => _messageFiles.Delete(message.ConversationId, removed.StorageName));

        foreach (var file in message.Files.Where(f => string.IsNullOrEmpty(f.StorageName) && f.PurgedAt is null))
        {
            Pending.Add(new PendingBlob(SyncKinds.Message, file.GlobalId));
        }

        return SyncApplyResult.Applied;
    }

    /// <summary>
    /// Приводит список вложений к присланному: чего нет — добавляет,
    /// что есть — обновляет, лишнее — убирает вместе с файлом на диске.
    ///
    /// Один метод на объявления и на сообщения: сущности разные,
    /// но правило одно, и писать его дважды значит однажды поправить
    /// только в одном месте.
    /// </summary>
    private static void SyncAttachments<T>(
        List<SyncAttachment>? incoming,
        List<T> mine,
        Func<T, Guid> idOf,
        Func<SyncAttachment, T> create,
        Action<T, SyncAttachment> update,
        Action<T> delete)
        where T : class
    {
        var list = incoming ?? [];

        foreach (var file in list)
        {
            var found = mine.FirstOrDefault(m => idOf(m) == file.GlobalId);

            if (found is null)
            {
                mine.Add(create(file));
            }
            else
            {
                update(found, file);
            }
        }

        var extra = mine.Where(m => !list.Any(f => f.GlobalId == idOf(m))).ToList();

        foreach (var item in extra)
        {
            delete(item);
            mine.Remove(item);
        }
    }
}

/// <summary>Файл, содержимое которого ещё предстоит забрать у соседа.</summary>
public sealed record PendingBlob(string Kind, Guid GlobalId);
