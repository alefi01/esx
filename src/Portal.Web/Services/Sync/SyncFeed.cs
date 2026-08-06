using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Sync;

/// <summary>
/// Сторона ОТДАЮЩЕГО: собирает для соседа пачку изменений.
///
/// Работает так: берём из журнала записи с номером больше того, на котором
/// сосед остановился, и к каждой доклеиваем нынешнее состояние объекта
/// из базы. Состояние читается СЕЙЧАС, а не хранится в журнале, — см.
/// пояснение в PortalDbContext.RecordSyncChanges о том, почему так.
/// </summary>
public sealed class SyncFeed
{
    private readonly PortalDbContext _db;
    private readonly SyncOptions _options;

    public SyncFeed(PortalDbContext db, IOptions<SyncOptions> options)
    {
        _db = db;
        _options = options.Value;
    }

    public async Task<SyncBatch> BuildAsync(long after, int? take, CancellationToken cancellationToken)
    {
        var size = Math.Clamp(take ?? _options.BatchSize, 1, 1000);

        // Берём на одну запись больше, чем отдадим: так узнаём,
        // осталось ли что-то дальше, не считая всю таблицу.
        var rows = await _db.SyncOutbox
            .Where(e => e.Id > after)
            .OrderBy(e => e.Id)
            .Take(size + 1)
            .ToListAsync(cancellationToken);

        var hasMore = rows.Count > size;

        if (hasMore)
        {
            rows.RemoveAt(rows.Count - 1);
        }

        var batch = new SyncBatch
        {
            Branch = _options.BranchCode,
            HasMore = hasMore,
            LastId = rows.Count > 0 ? rows[^1].Id : after
        };

        // Один и тот же объект мог измениться несколько раз. Отдаём его
        // один раз, по последней записи: состояние всё равно нынешнее,
        // и повторы только гоняли бы канал впустую.
        var latest = rows
            .GroupBy(r => new { r.Kind, r.GlobalId })
            .Select(g => g.OrderBy(r => r.Id).Last())
            .OrderBy(r => r.Id)
            .ToList();

        foreach (var row in latest)
        {
            var entry = new SyncEntry
            {
                Id = row.Id,
                Kind = row.Kind,
                GlobalId = row.GlobalId,

                // Пусто бывает у строк, которые лежали в базе ещё до перехода
                // на филиальную схему: тогда филиалов не было, и записать
                // было нечего. Такие подписываем собой — они и правда наши.
                OriginBranch = string.IsNullOrEmpty(row.OriginBranch) ? _options.BranchCode : row.OriginBranch,
                ChangedAt = row.ChangedAt,
                Deleted = row.Deleted
            };

            if (!row.Deleted)
            {
                entry.Payload = await LoadAsync(row.Kind, row.GlobalId, cancellationToken);

                // Объекта уже нет — значит его удалили после записи в журнал.
                // Отдаём как удаление: соседу важен итог, а не история.
                if (entry.Payload is null)
                {
                    entry.Deleted = true;
                }
            }

            batch.Entries.Add(entry);
        }

        return batch;
    }

    private Task<SyncPayload?> LoadAsync(string kind, Guid globalId, CancellationToken cancellationToken) =>
        kind switch
        {
            SyncKinds.Announcement => AnnouncementAsync(globalId, cancellationToken),
            SyncKinds.Folder => FolderAsync(globalId, cancellationToken),
            SyncKinds.File => FileAsync(globalId, cancellationToken),
            SyncKinds.Conversation => ConversationAsync(globalId, cancellationToken),
            SyncKinds.Message => MessageAsync(globalId, cancellationToken),
            _ => Task.FromResult<SyncPayload?>(null)
        };

    private async Task<SyncPayload?> AnnouncementAsync(Guid globalId, CancellationToken cancellationToken)
    {
        var item = await _db.Announcements
            .Include(a => a.Files)
            .FirstOrDefaultAsync(a => a.GlobalId == globalId, cancellationToken);

        if (item is null)
        {
            return null;
        }

        return new SyncPayload
        {
            Title = item.Title,
            Body = item.Body,
            AuthorUserName = item.AuthorUserName,
            AuthorDisplayName = item.AuthorDisplayName,
            CreatedAt = item.CreatedAt,
            UpdatedAt = item.UpdatedAt,
            IsPinned = item.IsPinned,
            IsImportant = item.IsImportant,
            Attachments = item.Files
                .Select(f => new SyncAttachment
                {
                    GlobalId = f.GlobalId,
                    OriginalName = f.OriginalName,
                    SizeBytes = f.SizeBytes,
                    ContentType = f.ContentType
                })
                .ToList()
        };
    }

    private async Task<SyncPayload?> FolderAsync(Guid globalId, CancellationToken cancellationToken)
    {
        var folder = await _db.Folders
            .Include(f => f.Permissions)
            .FirstOrDefaultAsync(f => f.GlobalId == globalId, cancellationToken);

        if (folder is null)
        {
            return null;
        }

        // Родителя называем общим номером, а не местным: у соседа
        // местные номера свои, и ссылка на «папку №7» ничего не значит.
        Guid? parent = folder.ParentId is null
            ? null
            : await _db.Folders
                .Where(f => f.Id == folder.ParentId)
                .Select(f => (Guid?)f.GlobalId)
                .FirstOrDefaultAsync(cancellationToken);

        return new SyncPayload
        {
            Title = folder.Name,
            ParentGlobalId = parent,
            InheritPermissions = folder.InheritPermissions,
            MaxFileSizeMb = folder.MaxFileSizeMb,
            RetentionDays = folder.RetentionDays,
            CreatedAt = folder.CreatedAt,
            AuthorUserName = folder.CreatedByUserName,
            Permissions = folder.Permissions
                .Select(p => new SyncPermission { GroupName = p.GroupName, Access = (int)p.Access })
                .ToList()
        };
    }

    private async Task<SyncPayload?> FileAsync(Guid globalId, CancellationToken cancellationToken)
    {
        var file = await _db.Files.FirstOrDefaultAsync(f => f.GlobalId == globalId, cancellationToken);

        if (file is null)
        {
            return null;
        }

        var folder = await _db.Folders
            .Where(f => f.Id == file.FolderId)
            .Select(f => (Guid?)f.GlobalId)
            .FirstOrDefaultAsync(cancellationToken);

        return new SyncPayload
        {
            FolderGlobalId = folder,
            OriginalName = file.OriginalName,
            SizeBytes = file.SizeBytes,
            ContentType = file.ContentType,
            CreatedAt = file.UploadedAt,
            AuthorUserName = file.UploadedByUserName,
            AuthorDisplayName = file.UploadedByDisplayName,

            // Корзина тоже расходится по филиалам: файл, убранный в одном
            // офисе, не должен остаться видимым в другом.
            DeletedAt = file.DeletedAt
        };
    }

    private async Task<SyncPayload?> ConversationAsync(Guid globalId, CancellationToken cancellationToken)
    {
        var conversation = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.GlobalId == globalId, cancellationToken);

        if (conversation is null)
        {
            return null;
        }

        return new SyncPayload
        {
            IsGroup = conversation.IsGroup,
            Title = conversation.Title,
            PairKey = conversation.PairKey,
            CreatedByUserName = conversation.CreatedByUserName,
            CreatedAt = conversation.CreatedAt,
            UpdatedAt = conversation.LastMessageAt,
            Participants = conversation.Participants
                .Select(p => new SyncParticipant
                {
                    UserName = p.UserName,
                    DisplayName = p.DisplayName,
                    IsOwner = p.IsOwner,
                    JoinedAt = p.JoinedAt
                })
                .ToList()
        };
    }

    private async Task<SyncPayload?> MessageAsync(Guid globalId, CancellationToken cancellationToken)
    {
        var message = await _db.Messages
            .Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.GlobalId == globalId, cancellationToken);

        if (message is null)
        {
            return null;
        }

        var conversation = await _db.Conversations
            .Where(c => c.Id == message.ConversationId)
            .Select(c => (Guid?)c.GlobalId)
            .FirstOrDefaultAsync(cancellationToken);

        return new SyncPayload
        {
            ConversationGlobalId = conversation,
            Body = message.Body,
            AuthorUserName = message.AuthorUserName,
            AuthorDisplayName = message.AuthorDisplayName,
            CreatedAt = message.CreatedAt,
            DeletedAt = message.DeletedAt,
            Attachments = message.Files
                .Select(f => new SyncAttachment
                {
                    GlobalId = f.GlobalId,
                    OriginalName = f.OriginalName,
                    SizeBytes = f.SizeBytes,
                    ContentType = f.ContentType,
                    PurgedAt = f.PurgedAt
                })
                .ToList()
        };
    }
}
