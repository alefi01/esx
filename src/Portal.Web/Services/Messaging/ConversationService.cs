using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.ActiveDirectory;

namespace Portal.Web.Services.Messaging;

/// <summary>Строка в списке слева: беседа, как её видит конкретный человек.</summary>
/// <param name="Id">Номер беседы.</param>
/// <param name="Title">Название группы или имя собеседника.</param>
/// <param name="IsGroup">Группа или переписка двоих.</param>
/// <param name="Preview">Начало последнего сообщения.</param>
/// <param name="LastAt">Когда пришло последнее сообщение.</param>
/// <param name="Unread">Сколько непрочитанных.</param>
/// <param name="People">Сколько участников — показывается у групп.</param>
public sealed record ConversationSummary(
    int Id, string Title, bool IsGroup, string Preview, DateTime LastAt, int Unread, int People);

/// <summary>
/// Всё, что портал умеет делать с перепиской: искать, создавать,
/// проверять доступ, считать непрочитанное.
///
/// ПРАВО ДОСТУПА ЗДЕСЬ РОВНО ОДНО: «ты участник этой беседы».
/// Никаких групп Active Directory, никаких уровней. Единственное исключение —
/// администратор портала, и оно оговорено отдельно (см. CanRead).
/// </summary>
public sealed class ConversationService
{
    private readonly PortalDbContext _db;
    private readonly IUserDirectory _directory;
    private readonly ActiveDirectoryOptions _ad;
    private readonly TimeProvider _time;

    /// <summary>Сколько человек можно собрать в одной группе.</summary>
    public const int MaxParticipants = 100;

    public ConversationService(
        PortalDbContext db,
        IUserDirectory directory,
        IOptions<ActiveDirectoryOptions> ad,
        TimeProvider time)
    {
        _db = db;
        _directory = directory;
        _ad = ad.Value;
        _time = time;
    }

    /// <summary>
    /// Ключ пары для переписки двоих.
    ///
    /// Логины приводятся к нижнему регистру и сортируются, поэтому
    /// «Иванов и Петров» и «Петров и Иванов» дают одну и ту же строку.
    /// Благодаря этому и уникальному индексу в базе два человека,
    /// написавшие друг другу одновременно, попадут в одну беседу,
    /// а не заведут две параллельные.
    /// </summary>
    public static string PairKeyFor(string first, string second)
    {
        var a = first.ToLowerInvariant();
        var b = second.ToLowerInvariant();

        return string.CompareOrdinal(a, b) <= 0 ? $"{a}|{b}" : $"{b}|{a}";
    }

    /// <summary>
    /// Может ли человек читать беседу.
    ///
    /// Участник — да. Администратор портала — тоже да: так решено
    /// сознательно, по требованию к служебной переписке. Сотрудники должны
    /// быть предупреждены об этом заранее и письменно — иначе это выяснится
    /// в самый неподходящий момент.
    /// </summary>
    public bool CanRead(ClaimsPrincipal user, Conversation conversation) =>
        IsParticipant(user, conversation) || user.IsInRole(_ad.AdminGroup);

    /// <summary>Писать в беседу может только участник — администратор здесь не исключение.</summary>
    public static bool CanWrite(ClaimsPrincipal user, Conversation conversation) =>
        IsParticipant(user, conversation);

    public static bool IsParticipant(ClaimsPrincipal user, Conversation conversation) =>
        conversation.Participants.Any(p =>
            string.Equals(p.UserName, user.Identity?.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Хозяин группы: может переименовать её и менять состав.</summary>
    public static bool IsOwner(ClaimsPrincipal user, Conversation conversation) =>
        conversation.Participants.Any(p =>
            p.IsOwner && string.Equals(p.UserName, user.Identity?.Name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Беседа со всем, что нужно для проверки прав и показа.</summary>
    public Task<Conversation?> GetAsync(int id, CancellationToken cancellationToken) =>
        _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

    /// <summary>Список бесед человека, свежие сверху.</summary>
    public async Task<IReadOnlyList<ConversationSummary>> ListAsync(
        string userName, CancellationToken cancellationToken)
    {
        var mine = await _db.Participants
            .Where(p => p.UserName.ToLower() == userName.ToLower())
            .Select(p => new { p.ConversationId, p.LastReadMessageId })
            .ToListAsync(cancellationToken);

        if (mine.Count == 0)
        {
            return [];
        }

        var ids = mine.Select(m => m.ConversationId).ToList();

        var conversations = await _db.Conversations
            .Where(c => ids.Contains(c.Id))
            .Include(c => c.Participants)
            .OrderByDescending(c => c.LastMessageAt)
            .ToListAsync(cancellationToken);

        // Последнее сообщение каждой беседы одним запросом, а не по одному
        // на строку списка: двадцать бесед — двадцать запросов, и страница
        // начинает заметно думать.
        var lastMessages = await _db.Messages
            .Where(m => ids.Contains(m.ConversationId))
            .GroupBy(m => m.ConversationId)
            .Select(g => new { ConversationId = g.Key, LastId = g.Max(m => m.Id) })
            .ToListAsync(cancellationToken);

        var lastIds = lastMessages.Select(x => x.LastId).ToList();

        var previews = await _db.Messages
            .Where(m => lastIds.Contains(m.Id))
            .Select(m => new { m.Id, m.ConversationId, m.Body, m.DeletedAt, m.AuthorDisplayName, HasFiles = m.Files.Count > 0 })
            .ToListAsync(cancellationToken);

        // Непрочитанное: сообщения новее отметки и написанные не мной.
        var unread = await _db.Messages
            .Where(m => ids.Contains(m.ConversationId)
                        && m.DeletedAt == null
                        && m.AuthorUserName.ToLower() != userName.ToLower())
            .Select(m => new { m.Id, m.ConversationId })
            .ToListAsync(cancellationToken);

        var readMarks = mine.ToDictionary(m => m.ConversationId, m => m.LastReadMessageId);

        var result = new List<ConversationSummary>();

        foreach (var conversation in conversations)
        {
            var preview = previews.FirstOrDefault(p => p.ConversationId == conversation.Id);
            var mark = readMarks.GetValueOrDefault(conversation.Id);

            result.Add(new ConversationSummary(
                conversation.Id,
                TitleFor(conversation, userName),
                conversation.IsGroup,
                PreviewText(preview?.Body, preview?.DeletedAt is not null, preview?.HasFiles ?? false,
                    conversation.IsGroup ? preview?.AuthorDisplayName : null),
                conversation.LastMessageAt,
                unread.Count(u => u.ConversationId == conversation.Id && u.Id > mark),
                conversation.Participants.Count));
        }

        return result;
    }

    /// <summary>
    /// Как назвать беседу для конкретного человека.
    /// У группы это её название, у переписки двоих — имя собеседника,
    /// то есть у каждой стороны своё.
    /// </summary>
    public static string TitleFor(Conversation conversation, string userName)
    {
        if (conversation.IsGroup)
        {
            return string.IsNullOrWhiteSpace(conversation.Title) ? "Группа" : conversation.Title;
        }

        var other = conversation.Participants.FirstOrDefault(p =>
            !string.Equals(p.UserName, userName, StringComparison.OrdinalIgnoreCase));

        return other?.DisplayName ?? "Переписка";
    }

    private static string PreviewText(string? body, bool deleted, bool hasFiles, string? author)
    {
        if (deleted)
        {
            return "сообщение удалено";
        }

        var text = (body ?? "").ReplaceLineEndings(" ").Trim();

        if (text.Length == 0)
        {
            text = hasFiles ? "вложение" : "";
        }

        if (text.Length > 90)
        {
            text = text[..90] + "…";
        }

        return string.IsNullOrEmpty(author) ? text : $"{author}: {text}";
    }

    /// <summary>
    /// Находит переписку двоих или заводит новую.
    ///
    /// Гонку двух одновременных обращений ловим не проверкой, а уникальным
    /// индексом: проверка «а вдруг уже есть» между двумя запросами
    /// всегда оставляет щель, а индекс — нет.
    /// </summary>
    public async Task<Conversation> StartDirectAsync(
        string userName, string displayName, string otherUserName, CancellationToken cancellationToken)
    {
        var key = PairKeyFor(userName, otherUserName);

        var existing = await _db.Conversations
            .Include(c => c.Participants)
            .FirstOrDefaultAsync(c => c.PairKey == key, cancellationToken);

        if (existing is not null)
        {
            return existing;
        }

        var otherDisplayName = await _directory.DisplayNameAsync(otherUserName, cancellationToken);
        var now = _time.GetUtcNow().UtcDateTime;

        var conversation = new Conversation
        {
            IsGroup = false,
            PairKey = key,
            CreatedByUserName = userName,
            CreatedAt = now,
            LastMessageAt = now,
            Participants =
            [
                new ConversationParticipant
                {
                    UserName = userName, DisplayName = displayName, JoinedAt = now, IsOwner = true
                },
                new ConversationParticipant
                {
                    UserName = otherUserName, DisplayName = otherDisplayName, JoinedAt = now
                }
            ]
        };

        _db.Conversations.Add(conversation);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException)
        {
            // Кто-то успел первым — берём его беседу.
            _db.Entry(conversation).State = EntityState.Detached;

            var winner = await _db.Conversations
                .Include(c => c.Participants)
                .FirstOrDefaultAsync(c => c.PairKey == key, cancellationToken);

            if (winner is null)
            {
                throw;
            }

            return winner;
        }

        return conversation;
    }

    /// <summary>Создаёт группу. Создатель становится её хозяином.</summary>
    public async Task<Conversation> CreateGroupAsync(
        string userName,
        string displayName,
        string title,
        IEnumerable<string> members,
        CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow().UtcDateTime;

        var conversation = new Conversation
        {
            IsGroup = true,
            Title = title.Trim(),
            PairKey = "",
            CreatedByUserName = userName,
            CreatedAt = now,
            LastMessageAt = now,
            Participants =
            [
                new ConversationParticipant
                {
                    UserName = userName, DisplayName = displayName, JoinedAt = now, IsOwner = true
                }
            ]
        };

        var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { userName };

        foreach (var member in members)
        {
            if (conversation.Participants.Count >= MaxParticipants || !added.Add(member))
            {
                continue;
            }

            // В группу можно добавить только того, кто имеет доступ к порталу.
            if (!await _directory.ExistsAsync(member, cancellationToken))
            {
                continue;
            }

            conversation.Participants.Add(new ConversationParticipant
            {
                UserName = member,
                DisplayName = await _directory.DisplayNameAsync(member, cancellationToken),
                JoinedAt = now
            });
        }

        _db.Conversations.Add(conversation);

        await _db.SaveChangesAsync(cancellationToken);

        return conversation;
    }

    /// <summary>Отметить всё прочитанным в этой беседе.</summary>
    public async Task MarkReadAsync(int conversationId, string userName, CancellationToken cancellationToken)
    {
        var participant = await _db.Participants.AsTracking()
            .FirstOrDefaultAsync(
                p => p.ConversationId == conversationId && p.UserName.ToLower() == userName.ToLower(),
                cancellationToken);

        if (participant is null)
        {
            return;
        }

        var newest = await _db.Messages
            .Where(m => m.ConversationId == conversationId)
            .MaxAsync(m => (int?)m.Id, cancellationToken) ?? 0;

        // Отметку двигаем только вперёд: в соседней вкладке могли прочитать
        // больше, и откатывать её назад нельзя.
        if (newest > participant.LastReadMessageId)
        {
            participant.LastReadMessageId = newest;

            await _db.SaveChangesAsync(cancellationToken);
        }
    }
}
