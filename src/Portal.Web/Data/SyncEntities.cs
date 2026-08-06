using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>Что за объект изменился. Строкой, а не числом, — чтобы журнал читался глазами.</summary>
public static class SyncKinds
{
    public const string Announcement = "announcement";
    public const string Folder = "folder";
    public const string File = "file";
    public const string Conversation = "conversation";
    public const string Message = "message";
}

/// <summary>
/// Запись журнала изменений — то, что соседние филиалы у нас забирают.
///
/// ПОЧЕМУ ЖУРНАЛ, А НЕ ЗАПРОС «ЧТО ИЗМЕНИЛОСЬ ПОСЛЕ ТАКОГО-ТО ВРЕМЕНИ»
///
/// Напрашивается спрашивать по времени изменения: «дай всё, что новее
/// вчерашнего вечера». Но время на серверах идёт не одинаково, его
/// подводят службы синхронизации, оно прыгает при переходе на летнее
/// и обратно. Один сдвиг часов назад — и часть изменений навсегда
/// окажется «в прошлом» и не будет забрана никогда, причём молча.
///
/// Номер записи в журнале растёт всегда и никогда не повторяется.
/// Сосед помнит последний забранный номер и спрашивает «что после него».
/// Пропустить что-либо при таком порядке невозможно.
/// </summary>
public class SyncOutboxEntry
{
    /// <summary>Возрастающий номер. Именно по нему соседи ведут отсчёт.</summary>
    public long Id { get; set; }

    /// <summary>Вид объекта: см. SyncKinds.</summary>
    [MaxLength(40)]
    public string Kind { get; set; } = "";

    /// <summary>
    /// Общий для всех филиалов идентификатор объекта.
    ///
    /// Обычный числовой Id для этого не годится: в каждом филиале своя
    /// база и свои счётчики, объявление №5 в первом офисе и объявление
    /// №5 во втором — разные объявления. Guid выдаётся один раз тем
    /// филиалом, где объект создан, и дальше не меняется.
    /// </summary>
    public Guid GlobalId { get; set; }

    /// <summary>Где объект создан. Для показа: «объявление из Офиса 2».</summary>
    [MaxLength(40)]
    public string OriginBranch { get; set; } = "";

    /// <summary>
    /// Время изменения по часам того сервера, где оно сделано, UTC.
    /// По нему решается спор, если один объект правили в двух филиалах:
    /// побеждает более позднее.
    /// </summary>
    public DateTime ChangedAt { get; set; }

    /// <summary>Объект удалён. Запись всё равно нужна: соседи должны узнать об удалении.</summary>
    public bool Deleted { get; set; }
}

/// <summary>
/// На чём мы остановились при разговоре с конкретным соседом
/// и чем этот разговор закончился.
/// </summary>
public class SyncPeerState
{
    public int Id { get; set; }

    [MaxLength(40)]
    public string PeerCode { get; set; } = "";

    /// <summary>Последний забранный у соседа номер записи журнала.</summary>
    public long LastReceivedId { get; set; }

    /// <summary>Когда обмен с этим соседом последний раз прошёл успешно.</summary>
    public DateTime? LastSuccessAt { get; set; }

    /// <summary>Когда пробовали в последний раз — успешно или нет.</summary>
    public DateTime? LastAttemptAt { get; set; }

    /// <summary>
    /// Чем закончилась последняя попытка, если неудачей.
    /// Пусто — всё хорошо. Это первое, что стоит посмотреть,
    /// когда «в другом офисе не видно объявления».
    /// </summary>
    [MaxLength(500)]
    public string? LastError { get; set; }

    /// <summary>Сколько записей принято за последний раз — для страницы состояния.</summary>
    public int LastReceivedCount { get; set; }
}

/// <summary>
/// Настройка, которую меняют в панели администратора, а не в файле.
///
/// Настройки из appsettings.json требуют доступа к серверу и перезапуска
/// пула приложений. Для того, что администратор портала подкручивает
/// по ходу дела (например, как часто спрашивать соседей), это неудобно.
/// Такие настройки лежат здесь.
/// </summary>
public class PortalSetting
{
    [MaxLength(100)]
    public string Key { get; set; } = "";

    [MaxLength(500)]
    public string Value { get; set; } = "";

    public DateTime UpdatedAt { get; set; }

    [MaxLength(256)]
    public string UpdatedByUserName { get; set; } = "";
}
