using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Announcements;

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
    private readonly AnnouncementFeed _feed;
    private readonly Portal.Web.Services.Tasks.UserPreferences _preferences;

    public NotificationService(
        PortalDbContext db,
        TimeProvider time,
        AnnouncementFeed feed,
        Portal.Web.Services.Tasks.UserPreferences preferences)
    {
        _db = db;
        _time = time;
        _feed = feed;
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

    /// <summary>
    /// Сводка непрочитанного.
    ///
    /// Принимает не логин, а самого человека с его группами: объявления
    /// теперь живут в папках, и «что ему видно» считается по правам
    /// на эти папки.
    /// </summary>
    public async Task<NotificationSummary> GetAsync(
        ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var userName = user.Identity?.Name ?? "";

        var (unreadMessages, messageItems, byConversation) =
            await MessagesAsync(userName, cancellationToken);

        var unreadAnnouncements = await _feed.UnreadCountAsync(user, cancellationToken);

        var fresh = await _feed.UnreadAsync(user, MaxItems, cancellationToken);

        var announcements = fresh
            .Select(entry => new NotificationItem(
                "announcement",
                entry.Item.Id,
                entry.Item.Title,
                entry.Item.AuthorDisplayName,
                entry.Item.CreatedAt.ToLocalTime(),
                // Ведём в папку, а не в ленту: ленты больше нет, объявление
                // живёт там, где лежат документы отдела.
                entry.Folder is null ? "/" : "/Files?id=" + entry.Folder.Id,
                Shorten(entry.Item.Body)))
            .ToList();

        var items = announcements
            .Concat(messageItems)
            .OrderByDescending(i => i.At)
            .Take(MaxItems)
            .ToList();

        return new NotificationSummary(
            unreadAnnouncements + unreadMessages, unreadAnnouncements, unreadMessages, items,
            byConversation);
    }

    /// <summary>
    /// Отметить прочитанным всё, что человеку сейчас видно.
    ///
    /// Кнопка «Прочитать все» в колокольчике. Обычный путь другой:
    /// объявления папки становятся прочитанными, когда человек эту папку
    /// открывает (см. AnnouncementFeed).
    /// </summary>
    public async Task MarkAllSeenAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        await _feed.MarkAllSeenAsync(user, cancellationToken);

        // Заодно двигаем старую общую границу: по ней считаются объявления,
        // оставшиеся от прежней ленты и не привязанные ни к какой папке.
        var userName = user.Identity?.Name ?? "";

        if (string.IsNullOrEmpty(userName))
        {
            return;
        }

        var newest = await _db.Announcements
            .Where(a => a.FolderId == null)
            .MaxAsync(a => (int?)a.Id, cancellationToken) ?? 0;

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
        string userName, string displayName, bool isHead, CancellationToken cancellationToken)
    {
        var state = await _db.Set<UserSeenState>().AsTracking()
            .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

        if (state is null)
        {
            // Строки ещё нет — заводим её прямо здесь.
            //
            // Раньше тут стоял выход «заведёт первый же запрос уведомлений»,
            // и из-за него терялось всё, ради чего этот метод и зовут:
            // человек входил ПЕРВЫЙ раз, строки не было, имя и признак
            // начальника не сохранялись, а строку потом заводил колокольчик —
            // пустую. В списке «кого назначить начальником» такой человек
            // не появлялся до второго входа.
            //
            // Граница прочитанных остаётся ПУСТОЙ: это признак «человек
            // здесь впервые», и по нему служба уведомлений не вываливает
            // на него все объявления за всю историю (см. GetAsync).
            _db.Add(new UserSeenState
            {
                UserName = userName,
                DisplayName = displayName ?? "",
                IsHead = isHead,
                UpdatedAt = _time.GetUtcNow().UtcDateTime
            });

            await _db.SaveChangesAsync(cancellationToken);

            return;
        }

        var changed = false;

        if (!string.IsNullOrWhiteSpace(displayName) && state.DisplayName != displayName)
        {
            state.DisplayName = displayName;
            changed = true;
        }

        // Членство в группе начальников запоминается при каждом входе:
        // по нему администратор выбирает, кого назначить начальником
        // отдела, и этот выбор должен работать, даже когда контроллер
        // домена недоступен (см. UserSeenState.IsHead).
        if (state.IsHead != isHead)
        {
            state.IsHead = isHead;
            changed = true;
        }

        if (changed)
        {
            state.UpdatedAt = _time.GetUtcNow().UtcDateTime;

            await _db.SaveChangesAsync(cancellationToken);
        }
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
        if (announcementId > (state.LastSeenAnnouncementId ?? 0)
            || state.LastSeenAnnouncementId is null)
        {
            state.LastSeenAnnouncementId = announcementId;
        }

        state.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken);
    }
}
