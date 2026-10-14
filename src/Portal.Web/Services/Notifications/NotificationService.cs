using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Notifications;

/// <summary>Одно непрочитанное событие для показа в колокольчике и во всплывающем сообщении.</summary>
/// <param name="Kind">Вид события: "announcement" — объявление, "message" — сообщение в беседе.</param>
/// <param name="Id">Номер объекта — по нему страница понимает, что уже показывала.</param>
/// <param name="Title">Заголовок.</param>
/// <param name="Author">Кто.</param>
/// <param name="At">Когда, в местном времени.</param>
/// <param name="Url">Куда вести по нажатию.</param>
/// <param name="Preview">
/// Начало самого текста — то, что показывается во всплывашке и в окошке
/// Windows под именем.
///
/// Раньше в уведомлении было только «Новое сообщение» и имя: чтобы понять,
/// нужно ли бросать работу, приходилось открывать портал. Теперь видно
/// и первые слова; если в сообщении только вложение — так и написано.
/// </param>
/// <param name="Sound">Каким звуком сообщить о событии. Сигнал один на всё.</param>
public sealed record NotificationItem(
    string Kind, int Id, string Title, string Author, DateTime At, string Url,
    string Preview = "",
    string Sound = Portal.Web.Services.Tasks.UserPreferences.MessageSound);

/// <summary>Сколько непрочитанного в одной беседе — для значка в списке бесед.</summary>
/// <param name="Id">Номер беседы.</param>
/// <param name="Unread">Сколько непрочитанных сообщений.</param>
public sealed record ConversationUnread(int Id, int Unread);

/// <summary>Сводка непрочитанного для одного человека.</summary>
/// <param name="Unread">Всего непрочитанного — число на колокольчике.</param>
/// <param name="Announcements">Из них объявлений — число рядом с пунктом меню.</param>
/// <param name="Messages">Из них сообщений в беседах — число рядом с пунктом меню.</param>
/// <param name="Items">Последние события списком, вперемешку, свежие сверху.</param>
/// <param name="Conversations">
/// Непрочитанное по каждой беседе, где оно есть.
///
/// Нужно затем, чтобы значки в списке бесед гасли и загорались сами,
/// а не оставались такими, какими их отрисовал сервер при загрузке
/// страницы. Именно из-за этого число непрочитанных «не всегда
/// пропадало»: беседу открыли, отметка на сервере сдвинулась, а значок
/// в списке рядом остался висеть до перезагрузки.
/// </param>
/// <param name="Tasks">
/// Сколько задач в работе. К «непрочитанному» не прибавляется: задача —
/// не новость, и колокольчик от неё звонить не должен. Это просто число
/// рядом с пунктом меню, чтобы список дел не забывали открывать.
/// </param>
public sealed record NotificationSummary(
    int Unread,
    int Announcements,
    int Messages,
    IReadOnlyList<NotificationItem> Items,
    IReadOnlyList<ConversationUnread> Conversations,
    int Tasks = 0);

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
    private readonly Portal.Web.Services.Tasks.UserPreferences _preferences;

    public NotificationService(
        PortalDbContext db, TimeProvider time, Portal.Web.Services.Tasks.UserPreferences preferences)
    {
        _db = db;
        _time = time;
        _preferences = preferences;
    }

    /// <summary>
    /// Непрочитанные сообщения из бесед.
    ///
    /// Считается тем же приёмом, что и объявления: у каждого участника
    /// хранится номер последнего прочитанного сообщения, непрочитанное —
    /// это всё, что новее. Своё написанное в счёт не идёт.
    /// </summary>
    private async Task<(int Unread, List<NotificationItem> Items, List<ConversationUnread> ByConversation)> MessagesAsync(
        string userName, CancellationToken cancellationToken)
    {
        var mine = await _db.Participants
            .Where(p => p.UserName.ToLower() == userName.ToLower())
            .Select(p => new { p.ConversationId, p.LastReadMessageId })
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
        {
            return (0, [], []);
        }

        var ids = mine.Select(m => m.ConversationId).ToList();
        var marks = mine.ToDictionary(m => m.ConversationId, m => m.LastReadMessageId);

        var fresh = await _db.Messages
            .Where(m => ids.Contains(m.ConversationId)
                        && m.DeletedAt == null
                        && m.AuthorUserName.ToLower() != userName.ToLower())
            .OrderByDescending(m => m.Id)
            .Select(m => new
            {
                m.Id,
                m.ConversationId,
                m.AuthorDisplayName,
                m.Body,
                m.CreatedAt,
                m.Conversation!.IsGroup,
                m.Conversation.Title
            })
            .Take(500)
            .ToListAsync(cancellationToken);

        var unread = fresh.Where(m => m.Id > marks.GetValueOrDefault(m.ConversationId)).ToList();

        // В списке — по одной строке на беседу, а не на каждое сообщение:
        // десять сообщений от одного человека это одно событие «вам пишут».
        var items = unread
            .GroupBy(m => m.ConversationId)
            .Select(g => g.OrderByDescending(m => m.Id).First())
            .OrderByDescending(m => m.Id)
            .Take(MaxItems)
            .Select(m => new NotificationItem(
                "message",
                m.Id,
                m.IsGroup && !string.IsNullOrWhiteSpace(m.Title) ? m.Title : m.AuthorDisplayName,
                m.AuthorDisplayName,
                m.CreatedAt.ToLocalTime(),
                "/Messages?id=" + m.ConversationId,
                Shorten(m.Body)))
            .ToList();

        // Сколько непрочитанного в какой беседе — по этому списку страница
        // гасит и зажигает значки в списке бесед, не перезагружаясь.
        var byConversation = unread
            .GroupBy(m => m.ConversationId)
            .Select(g => new ConversationUnread(g.Key, g.Count()))
            .ToList();

        return (unread.Count, items, byConversation);
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

            // Сообщения при этом НЕ прячем: их отметка своя, по каждой беседе,
            // и человек, которому написали до первого входа, должен это увидеть.
            var firstVisit = await MessagesAsync(userName, cancellationToken);

            return new NotificationSummary(
                firstVisit.Unread, 0, firstVisit.Unread, firstVisit.Items, firstVisit.ByConversation);
        }

        var unreadQuery = _db.Announcements.Where(a => a.Id > lastSeen);

        var unreadAnnouncements = await unreadQuery.CountAsync(cancellationToken);

        var announcements = await unreadQuery
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

        var (unreadMessages, messageItems, byConversation) =
            await MessagesAsync(userName, cancellationToken);

        var items = announcements
            .Select(a => new NotificationItem(
                "announcement", a.Id, a.Title, a.AuthorDisplayName, a.CreatedAt.ToLocalTime(),
                "/Announcements"))
            .Concat(messageItems)
            .OrderByDescending(i => i.At)
            .Take(MaxItems)
            .ToList();

        return new NotificationSummary(
            unreadAnnouncements + unreadMessages, unreadAnnouncements, unreadMessages, items,
            byConversation);
    }

    /// <summary>
    /// Начало текста для уведомления.
    ///
    /// Коротко: всплывашка живёт несколько секунд, а окошко Windows
    /// обрезает длинный текст само, но по-своему — лучше сделать это
    /// предсказуемо. Переводы строк заменяются пробелом: в одну строку
    /// уведомления они всё равно не влезут.
    ///
    /// Пустой текст означает сообщение из одного вложения — так и пишем,
    /// иначе уведомление выглядело бы оборванным.
    /// </summary>
    private static string Shorten(string? body)
    {
        var text = (body ?? "").Replace('\r', ' ').Replace('\n', ' ').Trim();

        while (text.Contains("  ", StringComparison.Ordinal))
        {
            text = text.Replace("  ", " ", StringComparison.Ordinal);
        }

        if (text.Length == 0)
        {
            return "вложение";
        }

        return text.Length <= 120 ? text : text[..119] + "…";
    }

    /// <summary>
    /// Граница «прочитанного»: номер последнего объявления, которое человек
    /// отметил как увиденное. Всё, что новее, для него новое.
    ///
    /// Нужна главной странице: она показывает только непрочитанное и должна
    /// считать его по той же границе, что и колокольчик, — иначе числа
    /// в шапке и на странице разъедутся.
    ///
    /// Первый вход человека в портал не должен вываливать на него все
    /// объявления за всю историю как «новые»: отметки ещё нет, и границей
    /// считается самое свежее объявление. Здесь она только читается,
    /// а заводится при первом обращении колокольчика (см. GetAsync).
    /// </summary>
    public async Task<int> LastSeenAnnouncementIdAsync(
        string userName, CancellationToken cancellationToken)
    {
        var state = await _db.Set<UserSeenState>()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        if (state is not null)
        {
            return state.LastSeenAnnouncementId;
        }

        return await _db.Announcements
            .OrderByDescending(a => a.Id)
            .Select(a => a.Id)
            .FirstOrDefaultAsync(cancellationToken);
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

    /// <summary>
    /// Запоминает, как показывать этого человека.
    ///
    /// Нужно для переписок: если контроллер домена недоступен, список
    /// собеседников собирается из тех, кто уже входил в портал, — и без
    /// имени в нём были бы одни логины. Вызывается при входе.
    /// </summary>
    public async Task RememberUserAsync(
        string userName, string displayName, CancellationToken cancellationToken)
    {
        var state = await _db.Set<UserSeenState>().AsTracking()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        if (state is null)
        {
            // Строки ещё нет — заведёт её первый же запрос уведомлений,
            // а пока просто нечего обновлять.
            return;
        }

        if (!string.IsNullOrWhiteSpace(displayName) && state.DisplayName != displayName)
        {
            state.DisplayName = displayName;
            state.UpdatedAt = _time.GetUtcNow().UtcDateTime;

            await _db.SaveChangesAsync(cancellationToken);
        }
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
