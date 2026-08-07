using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>
/// Уровень доступа к папке. Значения упорядочены по возрастанию прав,
/// поэтому их можно просто сравнивать: Manage > Write > Read > None.
/// </summary>
public enum FolderAccess
{
    /// <summary>Папка не видна вообще.</summary>
    None = 0,

    /// <summary>Видеть папку, её содержимое и скачивать файлы.</summary>
    Read = 1,

    /// <summary>Дополнительно: загружать файлы и создавать подпапки.</summary>
    Write = 2,

    /// <summary>Дополнительно: удалять, менять настройки папки и раздавать права на неё.</summary>
    Manage = 3
}

/// <summary>
/// Папка файлового хранилища.
///
/// Дерево папок живёт в базе, а не на диске: так права, ограничения и сроки
/// хранения задаются на уровне приложения, а на диске лежит плоская
/// и неинтересная структура с обезличенными именами. Побочный, но важный
/// эффект: пользовательские имена никогда не попадают в путь файловой
/// системы, а значит и выйти за пределы хранилища через «..\..\» нельзя.
/// </summary>
public class StorageFolder : ISyncable
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

    [Required(ErrorMessage = "Введите название папки")]
    [MaxLength(100, ErrorMessage = "Название не длиннее 100 символов")]
    [Display(Name = "Название")]
    public string Name { get; set; } = "";

    /// <summary>Родительская папка. null — папка верхнего уровня.</summary>
    public int? ParentId { get; set; }
    public StorageFolder? Parent { get; set; }

    public List<StorageFolder> Children { get; set; } = [];
    public List<StoredFile> Files { get; set; } = [];
    public List<FolderPermission> Permissions { get; set; } = [];

    /// <summary>
    /// Наследовать права родительской папки.
    ///
    /// true (по умолчанию) — к собственным правам папки добавляются права,
    /// действующие на родителе, и так вверх до корня. Права складываются:
    /// у человека остаётся наибольший из полученных уровней.
    ///
    /// false — папка «закрытая»: действуют только права, назначенные ей самой.
    /// Так делают для папок с ограниченным доступом внутри общего раздела.
    /// Администраторы портала попадают в такие папки всё равно.
    /// </summary>
    public bool InheritPermissions { get; set; } = true;

    /// <summary>
    /// Закреплена ли папка наверху своего каталога.
    ///
    /// Это НЕ избранное. Избранное — личное: каждый отмечает себе своё,
    /// и другие этого не видят. Закрепление общее: его ставит тот, кто
    /// управляет папкой, и видят все, кто в неё заходит. Так наверх
    /// поднимают то, чем пользуются каждый день, — «Приказы», «Шаблоны», —
    /// не переименовывая их в «1 Приказы» ради места в алфавите.
    /// </summary>
    public bool IsPinned { get; set; }

    /// <summary>
    /// Предел размера ОДНОГО файла, мегабайты.
    /// null — брать значение родительской папки, а если и там не задано,
    /// то общее по умолчанию (Storage:DefaultMaxFileSizeMb, обычно 50).
    /// </summary>
    public int? MaxFileSizeMb { get; set; }

    /// <summary>
    /// Предел суммарного объёма папки, мегабайты. null — без ограничения
    /// (или как у родителя, если задано у него).
    ///
    /// Учитываются и файлы в корзине: они по-прежнему занимают место на диске,
    /// и было бы нечестно показывать, что место освободилось, когда это не так.
    /// </summary>
    public int? QuotaMb { get; set; }

    /// <summary>
    /// Автоматическая очистка: удалять файлы старше указанного числа дней.
    /// null — очистка выключена (значение по умолчанию для новых папок).
    ///
    /// Удалённые таким образом файлы попадают в корзину, а не стираются сразу, —
    /// то есть у ошибки в настройке есть срок на исправление.
    /// Настройка на подпапки НЕ распространяется: у каждой папки свой срок.
    /// Так безопаснее — случайно назначенный на корень срок не выметет всё дерево.
    /// </summary>
    public int? RetentionDays { get; set; }

    /// <summary>Когда автоочистка последний раз проходила по этой папке.</summary>
    public DateTime? RetentionLastRunAt { get; set; }

    public DateTime CreatedAt { get; set; }

    [MaxLength(256)]
    public string CreatedByUserName { get; set; } = "";
}

/// <summary>
/// Право группы Active Directory на папку.
///
/// Отдельной таблицы пользователей у портала нет и здесь тоже: право выдаётся
/// группе, а членство в группе — это забота оснастки «Пользователи и компьютеры».
/// </summary>
public class FolderPermission
{
    public int Id { get; set; }

    public int FolderId { get; set; }
    public StorageFolder? Folder { get; set; }

    /// <summary>Короткое имя группы AD (sAMAccountName), как оно приходит в claim роли.</summary>
    [Required(ErrorMessage = "Укажите группу")]
    [MaxLength(256)]
    [Display(Name = "Группа AD")]
    public string GroupName { get; set; } = "";

    [Display(Name = "Уровень доступа")]
    public FolderAccess Access { get; set; } = FolderAccess.Read;

    /// <summary>
    /// В GroupName лежит логин ЧЕЛОВЕКА, а не имя группы.
    ///
    /// Зачем понадобилось: раздавать доступ группами правильно, но не всегда
    /// возможно — под «дай Петрову посмотреть один каталог» отдельную группу
    /// в домене никто заводить не станет, и всё кончается тем, что папку
    /// открывают всему отделу.
    ///
    /// Проверяется по-разному, потому и признак отдельный: группа — через
    /// членство (IsInRole), человек — сравнением логина. Одно вместо другого
    /// не работает: логин в списке ролей не лежит.
    /// </summary>
    public bool IsUser { get; set; }
}

/// <summary>
/// Файл в хранилище. Сам файл лежит на диске, здесь — только сведения о нём.
/// </summary>
public class StoredFile : ISyncable
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

    public int FolderId { get; set; }
    public StorageFolder? Folder { get; set; }

    /// <summary>Имя, которое видит пользователь и с которым файл скачивается.</summary>
    [MaxLength(260)]
    public string OriginalName { get; set; } = "";

    /// <summary>
    /// Имя файла на диске — сгенерированный идентификатор без расширения.
    /// Пользовательское имя на диск не попадает НИКОГДА: это разом закрывает
    /// и выход за пределы папки, и запрещённые в файловой системе символы,
    /// и совпадение имён.
    /// </summary>
    [MaxLength(64)]
    public string StorageName { get; set; } = "";

    public long SizeBytes { get; set; }

    /// <summary>Тип содержимого, определённый по расширению при загрузке.</summary>
    [MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    public DateTime UploadedAt { get; set; }

    [MaxLength(256)]
    public string UploadedByUserName { get; set; } = "";

    [MaxLength(256)]
    public string UploadedByDisplayName { get; set; } = "";

    /// <summary>
    /// Когда файл отправлен в корзину. null — файл обычный, видимый.
    /// Настоящее удаление с диска происходит позже, фоновой задачей,
    /// через Storage:TrashRetentionDays дней.
    /// </summary>
    public DateTime? DeletedAt { get; set; }

    [MaxLength(256)]
    public string? DeletedByUserName { get; set; }

    /// <summary>Файл в корзине.</summary>
    public bool IsDeleted => DeletedAt is not null;

    /// <summary>
    /// Закреплён ли файл наверху своей папки. Общая отметка, не личная —
    /// см. то же поле у папки.
    /// </summary>
    public bool IsPinned { get; set; }
}

/// <summary>Что именно произошло — для журнала действий.</summary>
public enum AuditAction
{
    Upload = 0,
    Download = 1,
    MoveToTrash = 2,
    RestoreFromTrash = 3,
    Purge = 4,
    CreateFolder = 5,
    DeleteFolder = 6,
    ChangeFolderSettings = 7,
    ChangePermissions = 8,
    RetentionCleanup = 9,

    // Копирование между папками портала убрано вместе с собственным буфером
    // обмена. Значение оставлено НАМЕРЕННО: в журнале уже лежат записи с числом
    // 10, и если убрать эту строку, все последующие действия сдвинутся
    // на единицу — старые записи начнут читаться как совсем другие события.
    Copy = 10,

    Move = 11,
    Rename = 12,
    Preview = 13,
    PurgeAuditLog = 14,

    /// <summary>
    /// Администратор портала открыл чужую переписку.
    ///
    /// Журнал заведён для действий с файлами, но чтение чужой переписки —
    /// ровно то же самое по смыслу: право есть у одного человека, и след
    /// от его использования должен оставаться. Иначе «администратор может
    /// читать всё» означает «никто не знает, что он читал».
    /// </summary>
    ViewConversation = 15
}

/// <summary>
/// Запись журнала действий с файлами.
///
/// Зачем отдельная таблица, если есть журнал приложения: текстовый журнал
/// перезаписывается, теряется при переустановке и по нему неудобно искать.
/// Вопрос «кто скачал этот документ в марте» рано или поздно возникает
/// в любой организации, и отвечать на него надо не грепом по файлам.
/// </summary>
public class AuditEntry
{
    public int Id { get; set; }

    public DateTime At { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "";

    [MaxLength(256)]
    public string UserDisplayName { get; set; } = "";

    /// <summary>Адрес, с которого пришёл запрос. Для действий фоновой очистки пусто.</summary>
    [MaxLength(64)]
    public string? RemoteIp { get; set; }

    public AuditAction Action { get; set; }

    /// <summary>На что подействовали: имя файла или путь папки.</summary>
    [MaxLength(600)]
    public string Target { get; set; } = "";

    /// <summary>Подробности: размер, старое и новое значение настройки и т.п.</summary>
    [MaxLength(1000)]
    public string? Details { get; set; }
}
