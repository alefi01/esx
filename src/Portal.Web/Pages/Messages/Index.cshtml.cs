using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Messaging;
using Portal.Web.Services.Notifications;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Messages;

/// <summary>
/// Переписки: список бесед слева, выбранная беседа справа.
///
/// Одна страница на всё, а не отдельные адреса на каждую беседу: так проще
/// и человеку (список всегда перед глазами), и в сопровождении. Номер
/// выбранной беседы приходит в адресе, поэтому ссылку на переписку можно
/// послать и вернуться к ней кнопкой «назад».
///
/// Все действия проверяют участие в беседе ЗАНОВО, по данным из базы.
/// Скрытая кнопка — это удобство, а не защита.
/// </summary>
public class IndexModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly ConversationService _conversations;
    private readonly MessageStorage _storage;
    private readonly IUserDirectory _directory;
    private readonly UploadValidator _validator;
    private readonly AuditLog _audit;
    private readonly PresenceService _presence;
    private readonly StorageOptions _storageOptions;
    private readonly ActiveDirectoryOptions _ad;
    private readonly TimeProvider _time;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        PortalDbContext db,
        ConversationService conversations,
        MessageStorage storage,
        IUserDirectory directory,
        UploadValidator validator,
        AuditLog audit,
        PresenceService presence,
        IOptions<StorageOptions> storageOptions,
        IOptions<ActiveDirectoryOptions> ad,
        TimeProvider time,
        ILogger<IndexModel> logger)
    {
        _db = db;
        _conversations = conversations;
        _storage = storage;
        _directory = directory;
        _validator = validator;
        _audit = audit;
        _presence = presence;
        _storageOptions = storageOptions.Value;
        _ad = ad.Value;
        _time = time;
        _logger = logger;
    }

    public IReadOnlyList<ConversationSummary> Conversations { get; private set; } = [];

    /// <summary>Строка поиска по перепискам.</summary>
    [BindProperty(SupportsGet = true, Name = "q")]
    public string? Query { get; set; }

    public bool IsSearching => !string.IsNullOrWhiteSpace(Query);

    /// <summary>Найденные сообщения — показываются вместо списка бесед.</summary>
    public IReadOnlyList<MessageHit> FoundMessages { get; private set; } = [];

    public Conversation? Current { get; private set; }
    public IReadOnlyList<Message> Items { get; private set; } = [];

    public string Title { get; private set; } = "";
    public bool IsOwner { get; private set; }
    public bool CanWrite { get; private set; }

    /// <summary>Администратор смотрит чужую переписку, не будучи её участником.</summary>
    public bool IsOutsideObserver { get; private set; }

    /// <summary>
    /// Присутствие собеседников: логин → «в сети» и когда видели.
    /// Для группы — по каждому участнику, для личной — по одному.
    /// </summary>
    public IReadOnlyDictionary<string, Presence> Presence { get; private set; } =
        new Dictionary<string, Presence>();

    /// <summary>
    /// Номер последнего сообщения, которое СОБЕСЕДНИК уже прочитал.
    ///
    /// Всё, что не новее, помечается двойной галочкой. В группе берётся
    /// минимум по всем остальным участникам: «прочитано» для группы
    /// честно означает «прочитали все», иначе двойная галочка появлялась
    /// бы, когда половина ещё не открывала беседу.
    /// </summary>
    public int ReadByOthersUpTo { get; private set; }

    /// <summary>Подпись под именем собеседника в шапке беседы.</summary>
    public string PresenceCaption { get; private set; } = "";

    /// <summary>Собеседник (или хоть кто-то из группы) прямо сейчас в портале.</summary>
    public bool IsOnline { get; private set; }

    public bool StorageConfigured => _storage.IsConfigured;

    public string UserName => User.Identity?.Name ?? "";

    public long MaxAttachmentBytes =>
        _storageOptions.FileSizeUnlimited
            ? 0
            : (long)_storageOptions.AbsoluteMaxFileSizeMb * 1024 * 1024;

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    [BindProperty]
    [MaxLength(8000, ErrorMessage = "Сообщение не длиннее 8000 символов")]
    public string Body { get; set; } = "";

    [BindProperty]
    [MaxLength(120, ErrorMessage = "Название не длиннее 120 символов")]
    public string GroupTitle { get; set; } = "";

    [BindProperty]
    public string[] Members { get; set; } = [];

    public async Task<IActionResult> OnGetAsync(int? id, CancellationToken cancellationToken)
    {
        await LoadAsync(id, cancellationToken);

        if (id is not null && Current is null)
        {
            return NotFound();
        }

        return Page();
    }

    private async Task LoadAsync(int? id, CancellationToken cancellationToken)
    {
        Conversations = await _conversations.ListAsync(UserName, cancellationToken);

        if (IsSearching)
        {
            var needle = Query!.Trim();

            // Список бесед фильтруем по названию и по имени собеседника,
            // а сообщения ищем отдельным запросом. Человек обычно не помнит,
            // что именно он ищет — беседу или фразу, — поэтому показываем и то и другое.
            Conversations = Conversations
                .Where(c => c.Title.Contains(needle, StringComparison.CurrentCultureIgnoreCase))
                .ToList();

            FoundMessages = await _conversations.SearchMessagesAsync(
                UserName, needle, 50, cancellationToken);
        }

        if (id is null)
        {
            return;
        }

        var conversation = await _conversations.GetAsync(id.Value, cancellationToken);

        if (conversation is null || !_conversations.CanRead(User, conversation))
        {
            // Не «запрещено», а «не найдено»: сам факт существования переписки
            // между двумя людьми — уже сведения, которых знать незачем.
            return;
        }

        Current = conversation;
        Title = ConversationService.TitleFor(conversation, UserName);
        IsOwner = ConversationService.IsOwner(User, conversation);
        CanWrite = ConversationService.CanWrite(User, conversation);
        IsOutsideObserver = !ConversationService.IsParticipant(User, conversation);

        Items = await _db.Messages
            .Where(m => m.ConversationId == conversation.Id)
            .Include(m => m.Files)
            .OrderBy(m => m.Id)
            .ToListAsync(cancellationToken);

        // Кто из собеседников в сети и до какого сообщения они дочитали.
        var others = conversation.Participants
            .Where(p => !string.Equals(p.UserName, UserName, StringComparison.OrdinalIgnoreCase))
            .ToList();

        Presence = await _presence.ForAsync(others.Select(p => p.UserName).ToList(), cancellationToken);

        ReadByOthersUpTo = others.Count == 0 ? 0 : others.Min(p => p.LastReadMessageId);

        if (IsOutsideObserver)
        {
            // Постороннему (администратору) статусы собеседников не показываем:
            // ему важно, ЧТО в переписке, а не кто из двоих сейчас за столом.
            PresenceCaption = conversation.IsGroup
                ? $"Участников: {conversation.Participants.Count}"
                : "личная переписка";
        }
        else if (!conversation.IsGroup && others.Count == 1)
        {
            var single = Presence.GetValueOrDefault(others[0].UserName, new Presence(false, null));

            IsOnline = single.Online;
            PresenceCaption = PresenceService.Describe(single, DateTime.Now);
        }
        else if (conversation.IsGroup)
        {
            var online = others.Count(p => Presence.GetValueOrDefault(p.UserName, new Presence(false, null)).Online);

            IsOnline = online > 0;

            PresenceCaption = $"Участников: {conversation.Participants.Count}"
                              + (online > 0 ? $" · в сети: {online}" : "");
        }

        if (IsOutsideObserver)
        {
            // Администратор портала открыл ЧУЖУЮ переписку. Право на это
            // у него есть, но след остаться должен: иначе «администратор
            // может читать всё» означает «никто не знает, что он читал».
            //
            // Отметку прочтения при этом не ставим — она принадлежит
            // участникам беседы, и посторонний не должен её сдвигать:
            // человек увидел бы, что сообщение «прочитано», хотя собеседник
            // его не открывал.
            await _audit.WriteAsync(
                AuditAction.ViewConversation,
                Title,
                $"переписка №{conversation.Id}, участников: {conversation.Participants.Count}",
                cancellationToken);
        }
        else
        {
            await _conversations.MarkReadAsync(conversation.Id, UserName, cancellationToken);
        }
    }

    private string DisplayName =>
        User.FindFirstValue(ClaimTypes.GivenName) ?? UserName;

    // ==================================================================
    // Отправка
    // ==================================================================

    public async Task<IActionResult> OnPostSendAsync(
        int id, List<IFormFile> attachments, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null)
        {
            return NotFound();
        }

        if (!ConversationService.CanWrite(User, conversation))
        {
            // Администратор может читать чужую переписку, но не писать в неё:
            // сообщение от его имени в чужой беседе — это подлог.
            return Forbid();
        }

        var text = (Body ?? "").Trim();
        var files = attachments ?? [];

        if (text.Length == 0 && files.Count == 0)
        {
            return RedirectToPage(new { id });
        }

        if (text.Length > 8000)
        {
            ErrorMessage = "Сообщение слишком длинное.";
            return RedirectToPage(new { id });
        }

        var now = _time.GetUtcNow().UtcDateTime;

        var message = new Message
        {
            ConversationId = id,
            AuthorUserName = UserName,
            AuthorDisplayName = DisplayName,
            Body = text,
            CreatedAt = now,

            // Адрес отправителя. Показывается только администратору портала
            // и только в чужой переписке — см. Message.AuthorIp.
            AuthorIp = HttpContext.Connection.RemoteIpAddress?.ToString()
        };

        var problems = new List<string>();

        foreach (var upload in files)
        {
            if (upload.Length == 0)
            {
                continue;
            }

            // Те же проверки, что и в файловом хранилище: запрет опасных
            // расширений и предел размера. Переписка не должна становиться
            // обходным путём для того, что запрещено в папках.
            var rejection = _validator.Validate(
                upload.FileName, upload.Length, MaxAttachmentBytes, null, 0);

            if (rejection is not null)
            {
                problems.Add($"«{rejection.FileName}» — {rejection.Reason}");
                continue;
            }

            var safeName = UploadValidator.SanitizeName(upload.FileName);

            try
            {
                await using var content = upload.OpenReadStream();

                var storageName = await _storage.SaveAsync(id, content, cancellationToken);

                message.Files.Add(new MessageFile
                {
                    OriginalName = safeName,
                    StorageName = storageName,
                    SizeBytes = upload.Length,
                    ContentType = UploadValidator.ResolveContentType(safeName)
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось сохранить вложение {Name}.", safeName);

                problems.Add($"«{safeName}» — не удалось сохранить");
            }
        }

        if (message.Body.Length == 0 && message.Files.Count == 0)
        {
            ErrorMessage = problems.Count > 0
                ? "Ничего не отправлено: " + string.Join("; ", problems)
                : "Пустое сообщение.";

            return RedirectToPage(new { id });
        }

        _db.Messages.Add(message);

        var tracked = await _db.Conversations.AsTracking().FirstAsync(c => c.Id == id, cancellationToken);
        tracked.LastMessageAt = now;

        await _db.SaveChangesAsync(cancellationToken);

        // Своё отправленное сразу считается прочитанным — иначе счётчик
        // непрочитанного показывал бы человеку его собственные сообщения.
        await _conversations.MarkReadAsync(id, UserName, cancellationToken);

        if (problems.Count > 0)
        {
            ErrorMessage = "Не приложены: " + string.Join("; ", problems);
        }

        return RedirectToPage(new { id });
    }

    // ==================================================================
    // Создание бесед
    // ==================================================================

    public async Task<IActionResult> OnPostStartAsync(string withUserName, CancellationToken cancellationToken)
    {
        var other = (withUserName ?? "").Trim();

        if (string.IsNullOrEmpty(other)
            || string.Equals(other, UserName, StringComparison.OrdinalIgnoreCase))
        {
            ErrorMessage = "Выберите собеседника.";
            return RedirectToPage();
        }

        if (!await _directory.ExistsAsync(other, cancellationToken))
        {
            ErrorMessage = $"Сотрудник «{other}» не найден среди тех, у кого есть доступ к порталу.";
            return RedirectToPage();
        }

        var conversation = await _conversations.StartDirectAsync(
            UserName, DisplayName, other, cancellationToken);

        return RedirectToPage(new { id = conversation.Id });
    }

    public async Task<IActionResult> OnPostCreateGroupAsync(CancellationToken cancellationToken)
    {
        var title = (GroupTitle ?? "").Trim();

        if (title.Length == 0)
        {
            ErrorMessage = "У группы должно быть название.";
            return RedirectToPage();
        }

        if (Members.Length == 0)
        {
            ErrorMessage = "Добавьте в группу хотя бы одного человека.";
            return RedirectToPage();
        }

        var conversation = await _conversations.CreateGroupAsync(
            UserName, DisplayName, title, Members, cancellationToken);

        StatusMessage = $"Группа «{conversation.Title}» создана.";

        return RedirectToPage(new { id = conversation.Id });
    }

    // ==================================================================
    // Управление группой
    // ==================================================================

    public async Task<IActionResult> OnPostRenameGroupAsync(
        int id, string newTitle, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null || !conversation.IsGroup)
        {
            return NotFound();
        }

        if (!ConversationService.IsOwner(User, conversation))
        {
            return Forbid();
        }

        var title = (newTitle ?? "").Trim();

        if (title.Length is 0 or > 120)
        {
            ErrorMessage = "Название группы — от 1 до 120 символов.";
            return RedirectToPage(new { id });
        }

        var tracked = await _db.Conversations.AsTracking().FirstAsync(c => c.Id == id, cancellationToken);
        tracked.Title = title;

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = "Название группы изменено.";

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostAddMemberAsync(
        int id, string memberUserName, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null || !conversation.IsGroup)
        {
            return NotFound();
        }

        if (!ConversationService.IsOwner(User, conversation))
        {
            return Forbid();
        }

        var member = (memberUserName ?? "").Trim();

        if (conversation.Participants.Count >= ConversationService.MaxParticipants)
        {
            ErrorMessage = $"В группе уже {ConversationService.MaxParticipants} человек — это предел.";
            return RedirectToPage(new { id });
        }

        if (conversation.Participants.Any(p =>
                string.Equals(p.UserName, member, StringComparison.OrdinalIgnoreCase)))
        {
            ErrorMessage = "Этот человек уже в группе.";
            return RedirectToPage(new { id });
        }

        if (!await _directory.ExistsAsync(member, cancellationToken))
        {
            ErrorMessage = $"Сотрудник «{member}» не найден среди тех, у кого есть доступ к порталу.";
            return RedirectToPage(new { id });
        }

        _db.Participants.Add(new ConversationParticipant
        {
            ConversationId = id,
            UserName = member,
            DisplayName = await _directory.DisplayNameAsync(member, cancellationToken),
            JoinedAt = _time.GetUtcNow().UtcDateTime,

            // Новый участник видит переписку с начала. Так решено осознанно:
            // прятать от него прошлое означало бы хранить у каждого свою
            // видимость истории — а это уже совсем другая сложность.
            // Добавляя человека в группу, помните, что он прочитает всё.
            LastReadMessageId = 0
        });

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = "Человек добавлен в группу.";

        return RedirectToPage(new { id });
    }

    public async Task<IActionResult> OnPostRemoveMemberAsync(
        int id, string memberUserName, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null || !conversation.IsGroup)
        {
            return NotFound();
        }

        var member = (memberUserName ?? "").Trim();
        var isSelf = string.Equals(member, UserName, StringComparison.OrdinalIgnoreCase);

        // Убрать другого может только хозяин группы; выйти самому — любой.
        if (!isSelf && !ConversationService.IsOwner(User, conversation))
        {
            return Forbid();
        }

        var participant = await _db.Participants.AsTracking().FirstOrDefaultAsync(
            p => p.ConversationId == id && p.UserName.ToLower() == member.ToLower(), cancellationToken);

        if (participant is null)
        {
            return RedirectToPage(new { id });
        }

        if (participant.IsOwner && conversation.Participants.Count > 1)
        {
            // Группа без хозяина осталась бы без управления. Передаём права
            // самому давнему из оставшихся — так группа продолжает жить.
            var successor = await _db.Participants.AsTracking()
                .Where(p => p.ConversationId == id && p.Id != participant.Id)
                .OrderBy(p => p.JoinedAt)
                .FirstOrDefaultAsync(cancellationToken);

            if (successor is not null)
            {
                successor.IsOwner = true;
            }
        }

        _db.Participants.Remove(participant);

        await _db.SaveChangesAsync(cancellationToken);

        StatusMessage = isSelf ? "Вы вышли из группы." : "Человек убран из группы.";

        return isSelf ? RedirectToPage() : RedirectToPage(new { id });
    }

    // ==================================================================
    // Удаление сообщения
    // ==================================================================

    public async Task<IActionResult> OnPostDeleteMessageAsync(
        int id, int messageId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null)
        {
            return NotFound();
        }

        var message = await _db.Messages.AsTracking()
            .Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, cancellationToken);

        if (message is null || message.DeletedAt is not null)
        {
            return RedirectToPage(new { id });
        }

        var isAuthor = string.Equals(message.AuthorUserName, UserName, StringComparison.OrdinalIgnoreCase);

        if (!isAuthor && !User.IsInRole(_ad.AdminGroup))
        {
            return Forbid();
        }

        message.DeletedAt = _time.GetUtcNow().UtcDateTime;
        message.DeletedByUserName = UserName;

        // Вложения стираем с диска сразу: они и занимают место,
        // и именно из-за них чаще всего сообщение и удаляют.
        foreach (var file in message.Files.Where(f => f.PurgedAt is null))
        {
            _storage.Delete(id, file.StorageName);
            file.PurgedAt = message.DeletedAt;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToPage(new { id });
    }

    // ==================================================================
    // Правка сообщения
    // ==================================================================

    /// <summary>
    /// Правит текст уже отправленного сообщения.
    ///
    /// Править может ТОЛЬКО автор — здесь администратор не исключение,
    /// в отличие от удаления. Удаление скрывает текст и оставляет честную
    /// пометку «сообщение удалено»; правка же подменяет слова, и чужие
    /// слова не должен менять никто.
    ///
    /// Факт правки виден собеседнику пометкой «изменено»: незаметная правка
    /// означала бы, что переписке нельзя верить.
    ///
    /// Вложения правка не трогает: менять их — это уже другое сообщение,
    /// и проще его переслать заново.
    /// </summary>
    public async Task<IActionResult> OnPostEditMessageAsync(
        int id, int messageId, string newBody, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null)
        {
            return NotFound();
        }

        var message = await _db.Messages.AsTracking()
            .Include(m => m.Files)
            .FirstOrDefaultAsync(m => m.Id == messageId && m.ConversationId == id, cancellationToken);

        if (message is null || message.DeletedAt is not null)
        {
            return RedirectToPage(new { id });
        }

        if (!string.Equals(message.AuthorUserName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            return Forbid();
        }

        var text = (newBody ?? "").Trim();

        if (text.Length > 8000)
        {
            ErrorMessage = "Сообщение не длиннее 8000 символов.";

            return RedirectToPage(new { id });
        }

        // Пустой текст допустим только у сообщения с вложениями: иначе
        // правка превратилась бы в способ удалить сообщение, не оставив
        // пометки «удалено».
        if (text.Length == 0 && message.Files.All(f => f.PurgedAt is not null))
        {
            ErrorMessage = "Пустое сообщение. Чтобы убрать его, воспользуйтесь удалением.";

            return RedirectToPage(new { id });
        }

        if (text == message.Body)
        {
            return RedirectToPage(new { id });
        }

        message.Body = text;
        message.EditedAt = _time.GetUtcNow().UtcDateTime;

        await _db.SaveChangesAsync(cancellationToken);

        return RedirectToPage(new { id });
    }

    // ==================================================================
    // Удаление переписки
    // ==================================================================

    /// <summary>
    /// Удаляет переписку целиком — вместе с сообщениями и вложениями.
    ///
    /// КТО МОЖЕТ
    ///
    /// Группу — только её хозяин. Остальным участникам вместо удаления
    /// доступен выход из группы (RemoveMember): переписка десяти человек
    /// не должна исчезать оттого, что одному из них она надоела.
    ///
    /// Личную переписку — любой из двоих. Другого разумного правила тут нет:
    /// участников всего два, и «удалить только у себя» означало бы, что
    /// у одного текст есть, а у другого его нет, — а потом спор
    /// «я такого не писал».
    ///
    /// Администратор портала удалять чужие переписки НЕ может, хотя и видит
    /// их: чтение и уничтожение — разные права, и второе ему для работы
    /// не нужно.
    ///
    /// Удаление НАСТОЯЩЕЕ, без корзины: переписка — это не документ,
    /// восстанавливать её никто не просил, а держать «удалённые» беседы
    /// вечно значит хранить то, что люди сознательно убрали.
    /// </summary>
    public async Task<IActionResult> OnPostDeleteConversationAsync(
        int id, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null)
        {
            return NotFound();
        }

        if (!ConversationService.IsParticipant(User, conversation))
        {
            // Не «запрещено», а «не найдено»: постороннему незачем узнавать,
            // что такая переписка вообще есть.
            return NotFound();
        }

        if (conversation.IsGroup && !ConversationService.IsOwner(User, conversation))
        {
            ErrorMessage = "Удалить группу может только тот, кто её создал. Вы можете выйти из неё.";

            return RedirectToPage(new { id });
        }

        var messages = await _db.Messages.AsTracking()
            .Include(m => m.Files)
            .Where(m => m.ConversationId == id)
            .ToListAsync(cancellationToken);

        // Сначала файлы с диска, потом записи из базы. В обратном порядке
        // упавшее посреди дела удаление оставило бы на диске файлы,
        // на которые больше ничто не ссылается, — их потом не найти.
        foreach (var file in messages.SelectMany(m => m.Files).Where(f => f.PurgedAt is null))
        {
            _storage.Delete(id, file.StorageName);
        }

        _db.Messages.RemoveRange(messages);

        var participants = await _db.Participants.AsTracking()
            .Where(p => p.ConversationId == id)
            .ToListAsync(cancellationToken);

        _db.Participants.RemoveRange(participants);

        var tracked = await _db.Conversations.AsTracking().FirstAsync(c => c.Id == id, cancellationToken);

        _db.Conversations.Remove(tracked);

        await _db.SaveChangesAsync(cancellationToken);

        // Пустой каталог беседы на диске убираем следом — иначе от каждой
        // удалённой переписки оставалась бы пустая папка.
        _storage.DeleteFolderIfEmpty(id);

        StatusMessage = conversation.IsGroup ? "Группа удалена." : "Переписка удалена.";

        return RedirectToPage();
    }

    // ==================================================================
    // Вложения
    // ==================================================================

    public async Task<IActionResult> OnGetAttachmentAsync(int fileId, CancellationToken cancellationToken)
    {
        var file = await _db.MessageFiles
            .Include(f => f.Message)
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file?.Message is null || file.PurgedAt is not null || file.Message.DeletedAt is not null)
        {
            return NotFound();
        }

        var conversation = await _conversations.GetAsync(file.Message.ConversationId, cancellationToken);

        if (conversation is null || !_conversations.CanRead(User, conversation))
        {
            return NotFound();
        }

        if (!_storage.Exists(conversation.Id, file.StorageName))
        {
            return NotFound();
        }

        var stream = _storage.OpenRead(conversation.Id, file.StorageName);

        return new FileStreamResult(stream, file.ContentType)
        {
            FileDownloadName = file.OriginalName,
            EnableRangeProcessing = true
        };
    }

    // ==================================================================
    // Обращения из кода страницы
    // ==================================================================

    /// <summary>Поиск сотрудников для окна «Написать» и «Создать группу».</summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q, CancellationToken cancellationToken)
    {
        var people = await _directory.SearchAsync(q, UserName, 50, cancellationToken);

        return new JsonResult(people.Select(p => new { userName = p.UserName, displayName = p.DisplayName }));
    }

    /// <summary>
    /// Есть ли в беседе что-то новее указанного сообщения.
    /// Страница спрашивает об этом раз в несколько секунд, пока открыта.
    /// </summary>
    public async Task<IActionResult> OnGetNewAsync(
        int id, int afterId, CancellationToken cancellationToken)
    {
        var conversation = await _conversations.GetAsync(id, cancellationToken);

        if (conversation is null || !_conversations.CanRead(User, conversation))
        {
            return NotFound();
        }

        var hasNew = await _db.Messages
            .AnyAsync(m => m.ConversationId == id && m.Id > afterId, cancellationToken);

        return new JsonResult(new { hasNew });
    }

    /// <summary>Подсветка найденного куска. Возвращает части: до, само совпадение, после.</summary>
    public (string Before, string Match, string After) Highlight(string text)
    {
        var needle = (Query ?? "").Trim();

        if (needle.Length == 0)
        {
            return (text, "", "");
        }

        var at = text.IndexOf(needle, StringComparison.CurrentCultureIgnoreCase);

        return at < 0
            ? (text, "", "")
            : (text[..at], text.Substring(at, needle.Length), text[(at + needle.Length)..]);
    }

    public static string FormatSize(long bytes) => UploadValidator.Format(bytes);

    /// <summary>
    /// Подпись разделителя дня в переписке: «Сегодня», «Вчера» либо дата.
    ///
    /// Под каждым сообщением стоит только время — этого достаточно, пока
    /// читаешь сегодняшнюю переписку, и совершенно недостаточно во всех
    /// остальных случаях: «14:20» без дня не говорит ничего. Дату к каждому
    /// сообщению не приписываем — она повторялась бы десятки раз подряд;
    /// вместо этого день отбивается один раз, там, где он меняется.
    ///
    /// Год показывается только у прошлых лет: в этом году он и так понятен,
    /// а строка от него становится длиннее без всякой пользы.
    /// </summary>
    public static string DayLabel(DateTime localDay, DateTime localToday)
    {
        if (localDay == localToday)
        {
            return "Сегодня";
        }

        if (localDay == localToday.AddDays(-1))
        {
            return "Вчера";
        }

        // Culture задаётся явно, а не берётся у сервера: на английской
        // сборке Windows названия месяцев были бы английскими, и в русской
        // переписке появилось бы «12 August».
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");

        return localDay.Year == localToday.Year
            ? localDay.ToString("d MMMM", ru)
            : localDay.ToString("d MMMM yyyy", ru);
    }
}
