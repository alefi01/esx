using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Services.Announcements;

/// <summary>Объявление вместе с папкой, на которой оно висит.</summary>
/// <param name="Item">Само объявление.</param>
/// <param name="Folder">Папка. null — объявление, оставшееся от общей ленты.</param>
public sealed record FeedItem(Announcement Item, StorageFolder? Folder);

/// <summary>
/// Объявления, разложенные по папкам: что человеку видно и что из этого
/// он ещё не читал.
///
/// ОДНО ПРАВИЛО ВМЕСТО ПРАВ У ОБЪЯВЛЕНИЯ
///
/// Своих прав у объявления нет. Оно висит на папке, и видят его те, кто
/// видит папку. В «Общей» части отдела — весь портал, во «Внутренней
/// работе» — те, кому её открыл начальник. Ни одного нового понятия
/// заводить не пришлось.
///
/// ПОЧЕМУ ОТМЕЧАЕТ ПРОЧИТАННЫМ ПАПКА
///
/// Раньше это делала лента: открыл — значит увидел. Ленты больше нет,
/// её место заняла папка. Главная при этом прочитанным ничего не делает:
/// она показывает только непрочитанное, и помечай она его при показе —
/// пустела бы от одного взгляда.
/// </summary>
public sealed class AnnouncementFeed
{
    private readonly PortalDbContext _db;
    private readonly FolderTree _tree;
    private readonly TimeProvider _time;

    public AnnouncementFeed(PortalDbContext db, FolderTree tree, TimeProvider time)
    {
        _db = db;
        _tree = tree;
        _time = time;
    }

    /// <summary>Номера папок, в которые человеку разрешено смотреть.</summary>
    public async Task<HashSet<int>> ReadableFolderIdsAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        var all = await _tree.LoadAsync(cancellationToken);

        return all.Values
            .Where(folder => _tree.CanRead(user, folder))
            .Select(folder => folder.Id)
            .ToHashSet();
    }

    /// <summary>
    /// Непрочитанные объявления человека — по всем папкам, куда он допущен.
    ///
    /// Объявления без папки (остались от прежней общей ленты) видны всем
    /// и считаются по старой общей границе в UserSeenState.
    /// </summary>
    public async Task<IReadOnlyList<FeedItem>> UnreadAsync(
        ClaimsPrincipal user, int take, CancellationToken cancellationToken)
    {
        var userName = user.Identity?.Name ?? "";
        var readable = await ReadableFolderIdsAsync(user, cancellationToken);

        var marks = await MarksAsync(userName, cancellationToken);
        var legacyBorder = await LegacyBorderAsync(userName, cancellationToken);

        // Берём с запасом и отсеиваем в памяти: условие «новее отметки
        // своей папки» в запрос не вкладывается, а объявлений на портале
        // столько, что они умещаются в один разумный выбор.
        var candidates = await _db.Announcements
            .Where(a => a.FolderId == null || readable.Contains(a.FolderId.Value))
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(200)
            .Include(a => a.Files)
            .ToListAsync(cancellationToken);

        var unread = candidates
            .Where(a => a.Id > Border(a, marks, legacyBorder))
            .Take(take)
            .Select(a => new FeedItem(a, a.FolderId is { } id ? _tree.Get(id) : null))
            .ToList();

        return unread;
    }

    /// <summary>Сколько непрочитанного — для числа на колокольчике.</summary>
    public async Task<int> UnreadCountAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userName = user.Identity?.Name ?? "";
        var readable = await ReadableFolderIdsAsync(user, cancellationToken);

        var marks = await MarksAsync(userName, cancellationToken);
        var legacyBorder = await LegacyBorderAsync(userName, cancellationToken);

        var rows = await _db.Announcements
            .Where(a => a.FolderId == null || readable.Contains(a.FolderId.Value))
            .Select(a => new { a.Id, a.FolderId })
            .ToListAsync(cancellationToken);

        return rows.Count(a => a.Id > Border(a.FolderId, marks, legacyBorder));
    }

    /// <summary>
    /// Закреплённые объявления из доступных папок.
    ///
    /// Показываются независимо от прочтения: закрепляют как раз то, что
    /// должно висеть перед глазами постоянно — режим работы, телефон
    /// охраны, порядок в праздники.
    /// </summary>
    public async Task<IReadOnlyList<FeedItem>> PinnedAsync(
        ClaimsPrincipal user, int take, CancellationToken cancellationToken)
    {
        var readable = await ReadableFolderIdsAsync(user, cancellationToken);

        var rows = await _db.Announcements
            .Where(a => a.IsPinned && (a.FolderId == null || readable.Contains(a.FolderId.Value)))
            .OrderByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .Take(take)
            .Include(a => a.Files)
            .ToListAsync(cancellationToken);

        return rows
            .Select(a => new FeedItem(a, a.FolderId is { } id ? _tree.Get(id) : null))
            .ToList();
    }

    /// <summary>Объявления одной папки — для врезки на странице папки.</summary>
    public async Task<IReadOnlyList<Announcement>> OfFolderAsync(
        int folderId, CancellationToken cancellationToken) =>
        await _db.Announcements
            .Where(a => a.FolderId == folderId)
            .Include(a => a.Files)
            .OrderByDescending(a => a.IsPinned)
            .ThenByDescending(a => a.CreatedAt)
            .ThenByDescending(a => a.Id)
            .ToListAsync(cancellationToken);

    /// <summary>
    /// Человек открыл папку — значит её объявления он увидел.
    ///
    /// Отметка двигается только вперёд: в соседней вкладке могли отметить
    /// более свежее, и откатывать назад нельзя.
    /// </summary>
    public async Task MarkFolderSeenAsync(
        string userName, int folderId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return;
        }

        var newest = await _db.Announcements
            .Where(a => a.FolderId == folderId)
            .MaxAsync(a => (int?)a.Id, cancellationToken) ?? 0;

        if (newest == 0)
        {
            // В папке объявлений нет — и строку заводить незачем.
            return;
        }

        var state = await _db.Set<FolderSeenState>().AsTracking()
            .FirstOrDefaultAsync(
                s => s.FolderId == folderId && s.UserName.ToLower() == userName.ToLower(),
                cancellationToken);

        if (state is null)
        {
            _db.Add(new FolderSeenState
            {
                UserName = userName,
                FolderId = folderId,
                LastSeenAnnouncementId = newest,
                UpdatedAt = _time.GetUtcNow().UtcDateTime
            });
        }
        else if (newest > state.LastSeenAnnouncementId)
        {
            state.LastSeenAnnouncementId = newest;
            state.UpdatedAt = _time.GetUtcNow().UtcDateTime;
        }
        else
        {
            return;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    // ==================================================================
    // Кто прочитал объявление
    // ==================================================================

    /// <summary>
    /// Записать, что человек видел эти объявления.
    ///
    /// Вызывается при открытии папки: там объявление показано целиком,
    /// вместе с текстом. Отдельного, другого понятия «просмотрено»
    /// у портала нет — иначе пришлось бы объяснять людям разницу
    /// между двумя числами.
    ///
    /// Своё собственное объявление автору не засчитывается: «прочитал
    /// сам себя» в списке выглядит издевательством над смыслом списка.
    /// </summary>
    public async Task MarkViewedAsync(
        string userName,
        string displayName,
        IReadOnlyList<Announcement> announcements,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName) || announcements.Count == 0)
        {
            return;
        }

        var ids = announcements
            .Where(a => !string.Equals(a.AuthorUserName, userName, StringComparison.OrdinalIgnoreCase))
            .Select(a => a.Id)
            .ToList();

        if (ids.Count == 0)
        {
            return;
        }

        // Уже записанное не трогаем: время первого прочтения и есть ответ
        // на вопрос «когда увидел».
        var known = await _db.AnnouncementViews
            .Where(v => ids.Contains(v.AnnouncementId)
                        && v.UserName.ToLower() == userName.ToLower())
            .Select(v => v.AnnouncementId)
            .ToListAsync(cancellationToken);

        var fresh = ids.Except(known).ToList();

        if (fresh.Count == 0)
        {
            return;
        }

        foreach (var id in fresh)
        {
            _db.AnnouncementViews.Add(new AnnouncementView
            {
                AnnouncementId = id,
                UserName = userName,
                DisplayName = string.IsNullOrWhiteSpace(displayName) ? userName : displayName,
                At = _time.GetUtcNow().UtcDateTime
            });
        }

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Две вкладки открыли папку разом — вторая наткнулась
            // на уникальный указатель. Это не ошибка: запись уже есть,
            // а ради неё ронять страницу нельзя.
        }
    }

    /// <summary>Сколько человек прочитало каждое из этих объявлений.</summary>
    public async Task<IReadOnlyDictionary<int, int>> ViewCountsAsync(
        IReadOnlyList<int> announcementIds, CancellationToken cancellationToken)
    {
        if (announcementIds.Count == 0)
        {
            return new Dictionary<int, int>();
        }

        var rows = await _db.AnnouncementViews
            .Where(v => announcementIds.Contains(v.AnnouncementId))
            .GroupBy(v => v.AnnouncementId)
            .Select(g => new { Id = g.Key, Count = g.Count() })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.Id, row => row.Count);
    }

    /// <summary>Кто прочитал это объявление — для окна со списком.</summary>
    public async Task<IReadOnlyList<AnnouncementView>> ViewersAsync(
        int announcementId, CancellationToken cancellationToken) =>
        await _db.AnnouncementViews
            .Where(v => v.AnnouncementId == announcementId)
            .OrderByDescending(v => v.At)
            .ToListAsync(cancellationToken);

    /// <summary>Отметить прочитанным всё, что человеку видно, — кнопкой «Прочитать все».</summary>
    public async Task MarkAllSeenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userName = user.Identity?.Name ?? "";

        if (string.IsNullOrEmpty(userName))
        {
            return;
        }

        var readable = await ReadableFolderIdsAsync(user, cancellationToken);

        var folders = await _db.Announcements
            .Where(a => a.FolderId != null && readable.Contains(a.FolderId.Value))
            .Select(a => a.FolderId!.Value)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var folderId in folders)
        {
            await MarkFolderSeenAsync(userName, folderId, cancellationToken);
        }
    }

    private async Task<Dictionary<int, int>> MarksAsync(
        string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return [];
        }

        var rows = await _db.Set<FolderSeenState>()
            .Where(s => s.UserName.ToLower() == userName.ToLower())
            .Select(s => new { s.FolderId, s.LastSeenAnnouncementId })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(row => row.FolderId, row => row.LastSeenAnnouncementId);
    }

    /// <summary>Старая общая граница — только для объявлений без папки.</summary>
    private async Task<int> LegacyBorderAsync(string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return 0;
        }

        var state = await _db.Set<UserSeenState>()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        return state?.LastSeenAnnouncementId ?? 0;
    }

    private static int Border(Announcement item, Dictionary<int, int> marks, int legacy) =>
        Border(item.FolderId, marks, legacy);

    private static int Border(int? folderId, Dictionary<int, int> marks, int legacy) =>
        folderId is { } id ? marks.GetValueOrDefault(id) : legacy;
}
