using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>
/// Беседа: переписка двоих или группа.
///
/// ПОЧЕМУ ОДНА ТАБЛИЦА НА ОБА СЛУЧАЯ
///
/// Напрашивается сделать отдельно «личные сообщения» и отдельно «группы».
/// Но с точки зрения портала разница только в числе участников и в наличии
/// названия: всё остальное — сообщения, вложения, отметки прочтения —
/// устроено одинаково. Две таблицы означали бы два почти одинаковых набора
/// кода, которые со временем разъедутся.
/// </summary>
public class Conversation : ISyncable
{
    // ---------- Синхронизация между филиалами (см. ISyncable) ----------

    /// <summary>Общий для всех филиалов номер объекта.</summary>
    public Guid GlobalId { get; set; } = Guid.NewGuid();

    /// <summary>Код филиала, где объект создан.</summary>
    [MaxLength(40)]
    public string OriginBranch { get; set; } = "";

    /// <summary>Время последнего изменения, UTC. По нему решается спор между филиалами.</summary>
    public DateTime ChangedAt { get; set; }

    public int Id { get; set; }

    /// <summary>true — группа, false — переписка двоих.</summary>
    public bool IsGroup { get; set; }

    /// <summary>
    /// Название группы. У переписки двоих его нет: там «названием»
    /// служит имя собеседника, а оно у каждой стороны своё.
    /// </summary>
    [MaxLength(120)]
    public string Title { get; set; } = "";

    /// <summary>
    /// Ключ пары для переписки двоих: логины через «|», отсортированные.
    /// У групп пусто.
    ///
    /// Нужен, чтобы «Иванов → Петров» и «Петров → Иванов» были ОДНОЙ беседой.
    /// Без такого ключа два человека, написавшие друг другу одновременно,
    /// завели бы две параллельные переписки и потеряли половину сообщений.
    /// Уникальность обеспечивается индексом в базе, а не только кодом.
    /// </summary>
    [MaxLength(520)]
    public string PairKey { get; set; } = "";

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Время последнего сообщения. Отдельным полем, а не запросом по таблице
    /// сообщений: по нему сортируется список бесед, и делать ради сортировки
    /// подзапрос на каждую строку — верный способ посадить страницу.
    /// </summary>
    public DateTime LastMessageAt { get; set; }

    public List<ConversationParticipant> Participants { get; set; } = [];
    public List<Message> Messages { get; set; } = [];
}

/// <summary>Участник беседы.</summary>
public class ConversationParticipant
{
    public int Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "";

    /// <summary>
    /// Имя на момент добавления. Хранится копией, а не берётся из каталога:
    /// список участников должен читаться, даже когда контроллер домена
    /// недоступен, а человек — уже уволен и из каталога удалён.
    /// </summary>
    [MaxLength(256)]
    public string DisplayName { get; set; } = "";

    /// <summary>Создатель группы: может переименовать её, добавлять и убирать людей.</summary>
    public bool IsOwner { get; set; }

    public DateTime JoinedAt { get; set; }

    /// <summary>
    /// Номер последнего прочитанного сообщения.
    /// Всё, что новее, считается непрочитанным — тот же приём,
    /// что и с объявлениями: одна отметка вместо таблицы уведомлений.
    /// </summary>
    public int LastReadMessageId { get; set; }
}

/// <summary>Сообщение в беседе.</summary>
public class Message : ISyncable
{
    // ---------- Синхронизация между филиалами (см. ISyncable) ----------

    /// <summary>Общий для всех филиалов номер объекта.</summary>
    public Guid GlobalId { get; set; } = Guid.NewGuid();

    /// <summary>Код филиала, где объект создан.</summary>
    [MaxLength(40)]
    public string OriginBranch { get; set; } = "";

    /// <summary>Время последнего изменения, UTC. По нему решается спор между филиалами.</summary>
    public DateTime ChangedAt { get; set; }

    public int Id { get; set; }

    public int ConversationId { get; set; }
    public Conversation? Conversation { get; set; }

    [MaxLength(256)]
    public string AuthorUserName { get; set; } = "";

    [MaxLength(256)]
    public string AuthorDisplayName { get; set; } = "";

    /// <summary>
    /// Текст. Может быть пустым, если сообщение состоит только из вложений.
    /// Хранится как есть, без разметки: превращать текст в разметку —
    /// это открывать дверь чужому коду на страницу.
    /// </summary>
    [MaxLength(8000)]
    public string Body { get; set; } = "";

    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Когда удалено. Само сообщение остаётся в базе, но текст и вложения
    /// больше не показываются — вместо них видна пометка «сообщение удалено».
    ///
    /// Так решено сознательно: удаление «у себя» оставляет собеседника
    /// с текстом, которого у автора уже нет, и приводит к спорам
    /// «я такого не писал». Пометка честнее.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    [MaxLength(256)]
    public string? DeletedByUserName { get; set; }

    /// <summary>
    /// Когда текст правили в последний раз. null — не правили.
    ///
    /// Отметка ВИДНА собеседнику, и это главное в ней. Незаметная правка
    /// уже отправленного сообщения означает, что переписке нельзя верить:
    /// человек прочитал одно, а в беседе осталось другое. Поэтому портал
    /// правку разрешает, но всегда о ней говорит.
    ///
    /// Прежний текст не сохраняется: это переписка, а не документооборот,
    /// и хранить все черновики каждой реплики незачем.
    /// </summary>
    public DateTime? EditedAt { get; set; }

    public bool IsDeleted => DeletedAt is not null;

    public bool IsEdited => EditedAt is not null;

    public List<MessageFile> Files { get; set; } = [];
}

/// <summary>
/// Вложение к сообщению.
///
/// ПОЧЕМУ ОТДЕЛЬНО ОТ ФАЙЛОВОГО ХРАНИЛИЩА
///
/// Соблазн переиспользовать папки велик: там уже есть и квоты, и корзина,
/// и автоочистка. Но права в хранилище устроены по группам Active Directory,
/// а здесь право ровно одно — «ты участник этой беседы». Смешивать две
/// разные модели доступа в одном месте — верный способ однажды показать
/// переписку не тому человеку.
///
/// Поэтому у вложений своя таблица, свой каталог на диске и своя проверка.
/// </summary>
public class MessageFile
{
    /// <summary>
    /// Общий для всех филиалов номер вложения. По нему сосед забирает
    /// содержимое файла: /api/sync/blob?kind=…&amp;globalId=…
    /// </summary>
    public Guid GlobalId { get; set; } = Guid.NewGuid();

    public int Id { get; set; }

    public int MessageId { get; set; }
    public Message? Message { get; set; }

    /// <summary>Имя, которое видит человек.</summary>
    [MaxLength(260)]
    public string OriginalName { get; set; } = "";

    /// <summary>
    /// Имя на диске — случайное. Имя, данное человеком, в пути к файлу
    /// не участвует никогда: иначе «..\..\web.config» стал бы рабочим приёмом.
    /// </summary>
    [MaxLength(64)]
    public string StorageName { get; set; } = "";

    public long SizeBytes { get; set; }

    [MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    /// <summary>Когда вложение стёрто с диска автоочисткой. Строка остаётся ради истории.</summary>
    public DateTime? PurgedAt { get; set; }
}
