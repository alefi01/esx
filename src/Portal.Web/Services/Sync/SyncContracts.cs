namespace Portal.Web.Services.Sync;

/// <summary>
/// То, что портал отдаёт соседу в ответ на «что у тебя изменилось».
///
/// Формат намеренно простой и читаемый: это обычный JSON, который можно
/// посмотреть браузером или curl-ом при разборе неполадок. Никаких
/// двоичных форматов и сжатия — объёмы здесь маленькие (сами файлы
/// передаются отдельно, кусками), а возможность заглянуть в обмен
/// глазами стоит дороже экономии килобайтов.
/// </summary>
public sealed class SyncBatch
{
    /// <summary>Код филиала, который отвечает.</summary>
    public string Branch { get; set; } = "";

    /// <summary>Номер последней записи в этой пачке. С него сосед продолжит.</summary>
    public long LastId { get; set; }

    /// <summary>Есть ли ещё записи после этой пачки.</summary>
    public bool HasMore { get; set; }

    public List<SyncEntry> Entries { get; set; } = [];
}

/// <summary>Одно изменение.</summary>
public sealed class SyncEntry
{
    public long Id { get; set; }

    /// <summary>Вид объекта: announcement, folder, file, conversation, message.</summary>
    public string Kind { get; set; } = "";

    public Guid GlobalId { get; set; }

    public string OriginBranch { get; set; } = "";

    public DateTime ChangedAt { get; set; }

    /// <summary>Объект удалён у отправителя.</summary>
    public bool Deleted { get; set; }

    /// <summary>
    /// Состояние объекта на момент ответа. У удалённых пусто.
    ///
    /// Это ИМЕННО нынешнее состояние, а не то, каким объект был в момент
    /// записи в журнал. Если объявление правили пять раз, сосед получит
    /// его окончательный вид одной записью, а не пять промежуточных.
    /// </summary>
    public SyncPayload? Payload { get; set; }
}

/// <summary>
/// Содержимое объекта. Одна структура на все виды: полей немного,
/// а отдельные типы на каждый вид означали бы пять почти одинаковых
/// наборов кода и пять мест, где можно ошибиться.
/// Незаполненные поля просто не участвуют.
/// </summary>
public sealed class SyncPayload
{
    // ---------- Общее ----------
    public string? Title { get; set; }
    public string? Body { get; set; }
    public string? AuthorUserName { get; set; }
    public string? AuthorDisplayName { get; set; }
    public DateTime CreatedAt { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public DateTime? DeletedAt { get; set; }

    // ---------- Объявление ----------
    public bool IsPinned { get; set; }
    public bool IsImportant { get; set; }

    // ---------- Папка ----------
    /// <summary>Общий номер родительской папки. Пусто — папка верхнего уровня.</summary>
    public Guid? ParentGlobalId { get; set; }

    public bool InheritPermissions { get; set; }
    public int? MaxFileSizeMb { get; set; }
    public int? RetentionDays { get; set; }
    public List<SyncPermission>? Permissions { get; set; }

    // ---------- Файл ----------
    /// <summary>Общий номер папки, в которой лежит файл.</summary>
    public Guid? FolderGlobalId { get; set; }

    public string? OriginalName { get; set; }
    public long SizeBytes { get; set; }
    public string? ContentType { get; set; }

    // ---------- Беседа ----------
    public bool IsGroup { get; set; }
    public string? PairKey { get; set; }
    public string? CreatedByUserName { get; set; }
    public List<SyncParticipant>? Participants { get; set; }

    // ---------- Сообщение ----------
    /// <summary>Общий номер беседы, к которой относится сообщение.</summary>
    public Guid? ConversationGlobalId { get; set; }

    public List<SyncAttachment>? Attachments { get; set; }
}

public sealed class SyncPermission
{
    public string GroupName { get; set; } = "";

    /// <summary>Уровень доступа числом — как в перечислении FolderAccess.</summary>
    public int Access { get; set; }
}

public sealed class SyncParticipant
{
    public string UserName { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public bool IsOwner { get; set; }
    public DateTime JoinedAt { get; set; }
}

/// <summary>
/// Вложение. Само содержимое сюда НЕ кладётся: файл может весить десятки
/// мегабайт, и тащить его внутри JSON означало бы, что оборвавшаяся
/// передача рушит весь ответ целиком. Содержимое сосед забирает отдельно,
/// кусками, по адресу /api/sync/blob.
/// </summary>
public sealed class SyncAttachment
{
    public Guid GlobalId { get; set; }
    public string OriginalName { get; set; } = "";
    public long SizeBytes { get; set; }
    public string ContentType { get; set; } = "";
    public DateTime? PurgedAt { get; set; }
}
