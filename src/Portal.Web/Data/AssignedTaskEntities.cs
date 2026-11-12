using System.ComponentModel.DataAnnotations;

namespace Portal.Web.Data;

/// <summary>Как часто повторяется задача.</summary>
public enum TaskRepeat
{
    /// <summary>Разовая: один срок, один экземпляр.</summary>
    Once = 0,

    /// <summary>Каждый день.</summary>
    Daily = 1,

    /// <summary>По выбранным дням недели.</summary>
    Weekdays = 2,

    /// <summary>Раз в неделю, в выбранный день.</summary>
    Weekly = 3,

    /// <summary>Раз в месяц, в выбранное число.</summary>
    Monthly = 4
}

/// <summary>Состояние экземпляра задачи.</summary>
public enum TaskState
{
    /// <summary>Поставлена, ждёт работы.</summary>
    Open = 0,

    /// <summary>Сдана на проверку, ждёт решения того, кто поставил.</summary>
    Submitted = 1,

    /// <summary>Проверена и принята. Закрыта.</summary>
    Accepted = 2,

    /// <summary>Возвращена с замечанием: сдавать заново.</summary>
    Returned = 3,

    /// <summary>Выполнена — для задач, которым проверка не нужна. Закрыта.</summary>
    Done = 4,

    /// <summary>Снята тем, кто поставил. Закрыта.</summary>
    Cancelled = 5,

    /// <summary>
    /// Срок прошёл, а повторяющаяся задача завела следующий экземпляр.
    /// Закрыта.
    ///
    /// Нужно, чтобы долги НЕ копились: иначе к концу отпуска у человека
    /// висит двадцать невыполненных ежедневных задач, и список перестают
    /// читать совсем. Факт «не сдал» при этом не пропадает — он здесь.
    /// </summary>
    Missed = 6
}

/// <summary>
/// Задача, поставленная одним человеком другому, — шаблон.
///
/// ПОЧЕМУ ОТДЕЛЬНО ОТ ЛИЧНЫХ ЗАДАЧ (UserTask)
///
/// Личная задача — это заметка: человек её завёл, он же закрыл, и кроме
/// него она никому не нужна. Поставленная задача живёт иначе: её сдают,
/// проверяют, возвращают и принимают, и распоряжается ею не исполнитель,
/// а тот, кто поставил.
///
/// Свести их в одну таблицу означало бы десяток колонок, всегда пустых
/// у одной из сторон, и проверку «а чья это задача» в каждом запросе.
/// Поэтому таблицы разные, а страница «Задачи» показывает оба списка
/// рядом — человеку-то всё равно, из какой они таблицы.
///
/// ШАБЛОН И ЭКЗЕМПЛЯР
///
/// Здесь лежит то, что не меняется от срока к сроку: что сделать, кому,
/// как часто. Сами сроки — в AssignedTaskInstance, по одному на каждый
/// период. Без такого разделения повторяющаяся задача либо затирает
/// собственную историю, либо превращается в сотню копий.
/// </summary>
/// <summary>
/// Исполнитель задачи.
///
/// ПОЧЕМУ ОТДЕЛЬНОЙ ТАБЛИЦЕЙ
///
/// Одно и то же поручение часто адресуют нескольким: «сдать отчёты
/// до пятницы» касается всего отдела. Раньше начальнику приходилось
/// заводить пять одинаковых задач и потом принимать их по одной,
/// не помня, какая чья.
///
/// Каждому исполнителю заводится СВОЙ экземпляр — свой срок сдачи,
/// свои файлы, своё состояние. Иначе сдача одного закрывала бы задачу
/// для всех.
/// </summary>
public class AssignedTaskAssignee
{
    public int Id { get; set; }

    public int TaskId { get; set; }
    public AssignedTask? Task { get; set; }

    [MaxLength(256)]
    public string UserName { get; set; } = "";

    [MaxLength(256)]
    public string DisplayName { get; set; } = "";

    /// <summary>
    /// Этому исполнителю поручен контроль.
    ///
    /// Контролёр принимает и возвращает работу наравне с автором —
    /// и чужую, и свою. Нужно это там, где начальник ставит задачу
    /// отделу, а следит за ней старший: без такой пометки принимать
    /// пришлось бы самому начальнику, и задача повисала бы на нём.
    /// </summary>
    public bool IsController { get; set; }
}

public class AssignedTask
{
    public int Id { get; set; }

    /// <summary>Кто поставил. Он же принимает, возвращает и снимает.</summary>
    [MaxLength(256)]
    public string AuthorUserName { get; set; } = "";

    [MaxLength(256)]
    public string AuthorDisplayName { get; set; } = "";

    /// <summary>
    /// Кому поставлена — основной исполнитель.
    ///
    /// Остаётся ради совместимости со старыми задачами и ради простых
    /// случаев «одному человеку». Полный список — в Assignees, и когда
    /// исполнителей несколько, читать нужно его.
    /// </summary>
    [MaxLength(256)]
    public string AssigneeUserName { get; set; } = "";

    [MaxLength(256)]
    public string AssigneeDisplayName { get; set; } = "";

    /// <summary>Все исполнители задачи. У каждого свой экземпляр.</summary>
    public List<AssignedTaskAssignee> Assignees { get; set; } = [];

    /// <summary>
    /// Когда задачу правили в последний раз. null — не правили.
    ///
    /// Показывается так же, как в переписке: «изменено» и время.
    /// Исполнитель должен видеть, что условия поменялись, — иначе
    /// он сделает по тому, что прочитал вчера.
    /// </summary>
    public DateTime? EditedAt { get; set; }

    [Required(ErrorMessage = "Напишите, что нужно сделать")]
    [MaxLength(300, ErrorMessage = "Название не длиннее 300 символов")]
    public string Title { get; set; } = "";

    [MaxLength(4000, ErrorMessage = "Подробности не длиннее 4000 символов")]
    public string? Notes { get; set; }

    public TaskRepeat Repeat { get; set; } = TaskRepeat.Once;

    /// <summary>
    /// Дни недели для Weekdays и Weekly: бит 0 — понедельник, бит 6 — воскресенье.
    /// Числом, а не отдельной таблицей: семь значений, которые читаются и пишутся
    /// всегда целиком.
    /// </summary>
    public int WeekDays { get; set; }

    /// <summary>Число месяца для Monthly. 29–31 в коротком месяце съезжают на последний день.</summary>
    public int MonthDay { get; set; }

    /// <summary>
    /// Время срока внутри дня, минуты от полуночи. null — конец дня.
    ///
    /// У личных задач времени нет намеренно («к пятнице» — нормальный срок,
    /// «к пятнице 14:35» — нет). Здесь оно нужно: по нему считается
    /// опоздание и уходит напоминание. Но остаётся необязательным.
    /// </summary>
    public int? DueMinutes { get; set; }

    /// <summary>Без вложения сдать нельзя — иначе «сдано» значит «нажал кнопку».</summary>
    public bool NeedFile { get; set; }

    /// <summary>
    /// Сданное ждёт решения того, кто поставил. Снято — исполнитель
    /// закрывает задачу сам кнопкой «Выполнено».
    /// </summary>
    public bool NeedCheck { get; set; } = true;

    /// <summary>
    /// Важная. Яркая пометка в списке, а на рабочем ПК — окно поверх всех
    /// программ со звуком. Ставится по решению автора: всплывай такое окно
    /// на каждую задачу, его перестали бы замечать через неделю.
    /// </summary>
    public bool Important { get; set; }

    /// <summary>
    /// За сколько минут до срока напомнить. 0 — не напоминать.
    /// </summary>
    public int RemindMinutes { get; set; }

    /// <summary>Повторение прекращено: новые экземпляры не заводятся.</summary>
    public bool Stopped { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<AssignedTaskInstance> Instances { get; set; } = [];

    /// <summary>Файлы, приложенные автором: бланк, шаблон, образец.</summary>
    public List<AssignedTaskFile> Files { get; set; } = [];
}

/// <summary>
/// Экземпляр задачи — то, что человек видит у себя в списке:
/// «сдать к такому-то сроку».
/// </summary>
public class AssignedTaskInstance
{
    public int Id { get; set; }

    public int TaskId { get; set; }
    public AssignedTask? Task { get; set; }

    /// <summary>
    /// Чей это экземпляр. У задачи с несколькими исполнителями
    /// экземпляр заводится каждому: свои файлы, своё состояние,
    /// своя сдача.
    ///
    /// У задач, заведённых до появления нескольких исполнителей,
    /// поле заполнено исполнителем самой задачи — его проставила
    /// миграция.
    /// </summary>
    [MaxLength(256)]
    public string AssigneeUserName { get; set; } = "";

    [MaxLength(256)]
    public string AssigneeDisplayName { get; set; } = "";

    /// <summary>
    /// Срок. Хранится моментом времени в UTC: у задачи может быть время
    /// внутри дня, и сравнивать «просрочено или нет» нужно по нему.
    /// </summary>
    public DateTime DueAt { get; set; }

    public TaskState State { get; set; } = TaskState.Open;

    public DateTime? SubmittedAt { get; set; }

    /// <summary>Когда принято, выполнено или снято — то есть когда закрыто.</summary>
    public DateTime? ClosedAt { get; set; }

    [MaxLength(1000)]
    public string? ReturnReason { get; set; }

    /// <summary>Сколько раз возвращали. Видно обоим: это часть истории сдачи.</summary>
    public int Returns { get; set; }

    /// <summary>Напоминание о приближении срока уже отправлено.</summary>
    public bool Reminded { get; set; }

    /// <summary>
    /// Окно «в лоб» на рабочем ПК уже показывали. Иначе оно всплывало бы
    /// при каждой проверке, пока человек не ответит.
    /// </summary>
    public bool Alerted { get; set; }

    public DateTime CreatedAt { get; set; }

    public List<AssignedTaskFile> Files { get; set; } = [];

    /// <summary>Закрытые экземпляры в работе больше не участвуют.</summary>
    public bool IsClosed =>
        State is TaskState.Accepted or TaskState.Done or TaskState.Cancelled or TaskState.Missed;
}

/// <summary>
/// Файл задачи: либо приложенный автором к шаблону (бланк, образец),
/// либо сданный исполнителем по конкретному сроку.
///
/// ПОЧЕМУ НЕ В ОБЩЕМ ФАЙЛОВОМ ХРАНИЛИЩЕ
///
/// Та же причина, по какой там не лежат вложения переписки: в хранилище
/// доступ решается группами Active Directory, здесь — участием в задаче
/// («я исполнитель» или «я поставил»). Это разные правила, и держать их
/// в одном месте опасно: достаточно один раз перепутать проверку,
/// чтобы чужой отчёт оказался виден через раздел «Файлы».
/// </summary>
public class AssignedTaskFile
{
    public int Id { get; set; }

    /// <summary>Шаблон, если файл приложен автором задачи.</summary>
    public int? TaskId { get; set; }
    public AssignedTask? Task { get; set; }

    /// <summary>Экземпляр, если файл сдан исполнителем.</summary>
    public int? InstanceId { get; set; }
    public AssignedTaskInstance? Instance { get; set; }

    [MaxLength(260)]
    public string OriginalName { get; set; } = "";

    /// <summary>
    /// Имя на диске — случайное. Имя, данное человеком, в пути к файлу
    /// не участвует никогда.
    /// </summary>
    [MaxLength(64)]
    public string StorageName { get; set; } = "";

    public long SizeBytes { get; set; }

    [MaxLength(200)]
    public string ContentType { get; set; } = "application/octet-stream";

    [MaxLength(256)]
    public string UploadedByUserName { get; set; } = "";

    public DateTime UploadedAt { get; set; }
}
