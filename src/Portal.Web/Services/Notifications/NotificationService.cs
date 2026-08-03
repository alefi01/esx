using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Notifications;

/// <summary>Одно непрочитанное событие для показа в колокольчике и во всплывающем сообщении.</summary>
/// <param name="Kind">Вид события: пока только "announcement", позже добавятся сообщения.</param>
/// <param name="Id">Номер объекта — по нему страница понимает, что уже показывала.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Author">Кто.</param>
/// <param name="At">Когда, в местном времени.</param>
/// <param name="Url">Куда вести по нажатию.</param>
public sealed record NotificationItem(string Kind, int Id, string Title, string Author, DateTime At, string Url);

/// <summary>Сводка непрочитанного для одного человека.</summary>
public sealed record NotificationSummary(int Unread, IReadOnlyList<NotificationItem> Items);

/// <summary>
/// Подсчёт непрочитанного и отметка «прочитано».
///
/// Никаких фоновых рассылок: страница сама раз в минуту спрашивает сервер,
/// нет ли чего нового. Для двадцати человек это несколько запросов в минуту —
/// нагрузки не создаёт, зато не нужны ни постоянные соединения, ни очереди.
/// Постоянное соединение (WebSocket) мы к тому же сознательно не используем:
/// между офисами канал с урезанным MTU, и рвущееся соединение выглядело бы
/// как «портал завис».
/// </summary>
public sealed class NotificationService
{
    /// <summary>Сколько последних непрочитанных показывать в списке.</summary>
    private const int MaxItems = 10;

    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public NotificationService(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    public async Task<NotificationSummary> GetAsync(string userName, CancellationToken cancellationToken)
    {
        var state = await _db.Set<UserSeenState>()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        var lastSeen = state?.LastSeenAnnouncementId ?? 0;

        // Первый вход человека в портал не должен вываливать на него
        // все объявления за всю историю как «новые». Поэтому при отсутствии
        // отметки считаем, что всё старое он уже видел, и заводим отметку молча.
        if (state is null)
        {
            var newest = await _db.Announcements
                .OrderByDescending(a => a.Id)
                .Select(a => a.Id)
                .FirstOrDefaultAsync(cancellationToken);

            await SaveStateAsync(userName, newest, cancellationToken);

            return new NotificationSummary(0, []);
        }

        var unreadQuery = _db.Announcements.Where(a => a.Id > lastSeen);

        var unread = await unreadQuery.CountAsync(cancellationToken);

        var items = await unreadQuery
            .OrderByDescending(a => a.Id)
            .Take(MaxItems)
            .Select(a => new
            {
                a.Id,
                a.Title,
                a.AuthorDisplayName,
                a.CreatedAt
            })
            .ToListAsync(cancellationToken);

        return new NotificationSummary(
            unread,
            items.Select(a => new NotificationItem(
                    "announcement",
                    a.Id,
                    a.Title,
                    a.AuthorDisplayName,
                    a.CreatedAt.ToLocalTime(),
                    "/Announcements"))
                .ToList());
    }

    /// <summary>Отметить всё текущее прочитанным.</summary>
    public async Task MarkAllSeenAsync(string userName, CancellationToken cancellationToken)
    {
        var newest = await _db.Announcements
            .OrderByDescending(a => a.Id)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);

        await SaveStateAsync(userName, newest, cancellationToken);
    }

    private async Task SaveStateAsync(string userName, int announcementId, CancellationToken cancellationToken)
    {
        var state = await _db.Set<UserSeenState>()
            .AsTracking()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        if (state is null)
        {
            state = new UserSeenState { UserName = userName };
            _db.Add(state);
        }

        // Отметку двигаем только вперёд: если в соседней вкладке уже отметили
        // более свежее объявление, откатывать назад нельзя.
        if (announcementId > state.LastSeenAnnouncementId)
        {
            state.LastSeenAnnouncementId = announcementId;
        }

        state.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
