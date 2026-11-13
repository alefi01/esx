using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Tasks;

/// <summary>Экземпляр задачи вместе с шаблоном и признаками для страницы.</summary>
/// <param name="Instance">Экземпляр на срок.</param>
/// <param name="Task">Шаблон: что, кому, как часто.</param>
/// <param name="Overdue">Срок вышел, а задача ещё в работе.</param>
/// <param name="Urgency">Насколько горит — та же шкала, что и у личных задач.</param>
public sealed record AssignedCard(
    AssignedTaskInstance Instance, AssignedTask Task, bool Overdue, TaskUrgency Urgency);

/// <summary>
/// Задачи, которые один человек поставил другому.
///
/// ЧТО ЗДЕСЬ ЗА ПРАВИЛА
///
/// Распоряжается задачей тот, кто её поставил: он принимает сделанное,
/// возвращает на доработку и снимает. Исполнитель может ровно одно —
/// сдать. Удалить поставленную задачу он не может: иначе поручение
/// превращается в просьбу.
///
/// Проверки стоят в КАЖДОМ методе, а не в вызывающем коде: страница
/// может забыть, служба не забудет.
/// </summary>
public sealed class AssignedTaskService
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public AssignedTaskService(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    // ==================================================================
    // Сроки и повторение
    // ==================================================================

    /// <summary>
    /// Срок экземпляра: дата плюс время внутри дня.
    ///
    /// Время необязательно — тогда срок истекает в конце дня. Так сделано
    /// потому, что «к пятнице» — обычный срок в работе, а «к пятнице 14:35»
    /// бывает нужен редко, и заставлять указывать минуты каждый раз значит
    /// получить список выдуманных минут.
    ///
    /// Считается в МЕСТНОМ времени и переводится в UTC: «к 18:00» — это
    /// восемнадцать часов на часах у человека, а не в базе.
    /// </summary>
    public static DateTime DueAtOf(DateTime localDate, int? dueMinutes)
    {
        var local = dueMinutes is { } minutes
            ? localDate.Date.AddMinutes(minutes)
            : localDate.Date.AddDays(1).AddSeconds(-1);

        return DateTime.SpecifyKind(local, DateTimeKind.Local).ToUniversalTime();
    }

    /// <summary>
    /// Когда наступит следующий срок после указанной даты.
    /// null — повторения нет либо оно прекращено.
    /// </summary>
    public static DateTime? NextDueAfter(AssignedTask task, DateTime afterLocal)
    {
        if (task.Stopped || task.Repeat == TaskRepeat.Once)
        {
            return null;
        }

        var day = afterLocal.Date.AddDays(1);

        // Перебором по дням, а не вычислением: правил всего четыре,
        // год — это 366 шагов в худшем случае, и читается такой код
        // без карандаша.
        for (var step = 0; step < 400; step++)
        {
            if (Matches(task, day))
            {
                return day;
            }

            day = day.AddDays(1);
        }

        return null;
    }

    private static bool Matches(AssignedTask task, DateTime day) => task.Repeat switch
    {
        TaskRepeat.Daily => true,
        TaskRepeat.Weekdays or TaskRepeat.Weekly => (task.WeekDays & (1 << WeekIndex(day))) != 0,

        // 29–31 в коротком месяце съезжают на последний день: «тридцать
        // первого» в феврале не бывает, а задача должна случиться.
        TaskRepeat.Monthly => day.Day == Math.Min(
            task.MonthDay <= 0 ? 1 : task.MonthDay,
            DateTime.DaysInMonth(day.Year, day.Month)),

        _ => false
    };

    /// <summary>Понедельник — 0, воскресенье — 6. В .NET неделя начинается с воскресенья.</summary>
    private static int WeekIndex(DateTime day) => ((int)day.DayOfWeek + 6) % 7;

    // ==================================================================
    // Создание и правка
    // ==================================================================

    /// <summary>
    /// Ставит задачу и сразу заводит первый экземпляр: задача без срока
    /// не задача, а пожелание.
    /// </summary>
    public async Task<AssignedTask> CreateAsync(
        AssignedTask task, DateTime firstDueLocal, CancellationToken cancellationToken)
    {
        task.CreatedAt = Now;

        // Основным исполнителем считается первый в списке: по нему
        // задача называется в списках, где место есть только на одного.
        var first = task.Assignees.FirstOrDefault();

        if (first is not null)
        {
            task.AssigneeUserName = first.UserName;
            task.AssigneeDisplayName = first.DisplayName;
        }
        else if (task.AssigneeUserName.Length > 0)
        {
            // Задачу поставили одному человеку старым способом —
            // заводим ему запись исполнителя, чтобы дальше всё
            // работало единообразно.
            task.Assignees.Add(new AssignedTaskAssignee
            {
                UserName = task.AssigneeUserName,
                DisplayName = task.AssigneeDisplayName
            });
        }

        _db.AssignedTasks.Add(task);
        await _db.SaveChangesAsync(cancellationToken);

        await AddInstancesAsync(task, firstDueLocal, cancellationToken);

        return task;
    }

    /// <summary>
    /// Завести экземпляр КАЖДОМУ исполнителю.
    ///
    /// Свой экземпляр на человека — не прихоть: у каждого своя сдача,
    /// свои файлы и своё состояние. Один общий экземпляр означал бы,
    /// что сдача первого закрывает задачу для всех.
    /// </summary>
    private async Task AddInstancesAsync(
        AssignedTask task, DateTime dueLocal, CancellationToken cancellationToken)
    {
        var dueAt = DueAtOf(dueLocal, task.DueMinutes);
        var now = Now;

        var people = task.Assignees.Count > 0
            ? task.Assignees.Select(a => (a.UserName, a.DisplayName)).ToList()
            : [(task.AssigneeUserName, task.AssigneeDisplayName)];

        foreach (var (login, name) in people)
        {
            _db.AssignedTaskInstances.Add(new AssignedTaskInstance
            {
                TaskId = task.Id,
                AssigneeUserName = login,
                AssigneeDisplayName = name,
                DueAt = dueAt,
                State = TaskState.Open,
                CreatedAt = now
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Правка шаблона. Действует на БУДУЩИЕ экземпляры: тот, что человек
    /// уже делает, не должен меняться у него под руками.
    /// </summary>
    public async Task<bool> UpdateAsync(
        int taskId, string authorUserName, Action<AssignedTask> change, CancellationToken cancellationToken)
    {
        var task = await _db.AssignedTasks.AsTracking()
            .Include(t => t.Assignees)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task is null || !IsAuthor(task, authorUserName))
        {
            return false;
        }

        change(task);

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Правка поставленной задачи: название, подробности, срок
    /// и признаки.
    ///
    /// ПОЧЕМУ ЭТО НУЖНО
    ///
    /// Условия меняются: перенесли совещание, уточнили, что именно
    /// приложить. Раньше задачу приходилось снимать и ставить заново —
    /// и вместе с ней терялась вся история сдачи.
    ///
    /// ПОЧЕМУ ВИДНО, ЧТО ПРАВИЛИ
    ///
    /// Исполнитель мог прочитать задачу вчера и делать по тому, что
    /// прочитал. Отметка «изменено» и время — то же, что в переписке:
    /// молча подменённый текст хуже отсутствия правки вовсе.
    ///
    /// Срок меняется у НЕЗАКРЫТЫХ экземпляров: сданное и принятое
    /// переписывать задним числом нельзя.
    /// </summary>
    public async Task<bool> EditAsync(
        int taskId,
        string userName,
        string title,
        string? notes,
        DateTime? dueLocal,
        int? dueMinutes,
        bool needFile,
        bool needCheck,
        bool important,
        CancellationToken cancellationToken)
    {
        var task = await _db.AssignedTasks.AsTracking()
            .Include(t => t.Assignees)
            .Include(t => t.Instances)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        // Править может только тот, кто поставил. Контролёр принимает
        // работу, но условия задачи — не его: он за них не отвечает.
        if (task is null || !IsAuthor(task, userName))
        {
            return false;
        }

        task.Title = title.Trim();
        task.Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim();
        task.DueMinutes = dueMinutes;
        task.NeedFile = needFile;
        task.NeedCheck = needCheck;
        task.Important = important;
        task.EditedAt = Now;

        if (dueLocal is { } due)
        {
            var dueAt = DueAtOf(due, dueMinutes);

            foreach (var instance in task.Instances.Where(i => !i.IsClosed))
            {
                instance.DueAt = dueAt;

                // Срок сдвинули — напоминание должно прийти заново.
                instance.Reminded = false;
                instance.Alerted = false;
            }
        }

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Задача со всем, что нужно для правки. Права проверяет вызывающий.</summary>
    public async Task<AssignedTask?> TaskAsync(int taskId, CancellationToken cancellationToken) =>
        await _db.AssignedTasks
            .Include(t => t.Assignees)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

    private static bool IsAuthor(AssignedTask task, string userName) =>
        string.Equals(task.AuthorUserName, userName, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Кто вправе принимать и возвращать: автор задачи и тот, кому
    /// поручен контроль.
    ///
    /// Контролёр нужен там, где начальник ставит задачу отделу,
    /// а следит за ней старший: без этого принимать пришлось бы самому
    /// начальнику, и задача повисала бы на нём.
    /// </summary>
    private static bool CanCheck(AssignedTask task, string userName) =>
        IsAuthor(task, userName)
        || task.Assignees.Any(a => a.IsController
            && string.Equals(a.UserName, userName, StringComparison.OrdinalIgnoreCase));

    /// <summary>Этот экземпляр принадлежит этому человеку.</summary>
    private static bool IsAssignee(AssignedTaskInstance instance, string userName) =>
        string.Equals(
            instance.AssigneeUserName.Length > 0
                ? instance.AssigneeUserName
                : instance.Task?.AssigneeUserName ?? "",
            userName,
            StringComparison.OrdinalIgnoreCase);

    // ==================================================================
    // Действия исполнителя
    // ==================================================================

    /// <summary>
    /// Взять задачу в работу.
    ///
    /// ЗАЧЕМ ЭТО НУЖНО
    ///
    /// Поставленная задача и задача, которую человек увидел и взял, —
    /// это разные вещи. Без отметки начальник не знает, дошло ли
    /// поручение вообще, и спрашивает об этом в переписке; исполнитель
    /// не видит, что в его списке новое, а что он уже разбирал.
    ///
    /// Сдать задачу можно и не беря её: отметка — это сведения,
    /// а не пропуск. Ставить лишний шаг между человеком и работой
    /// было бы издевательством над тем, кто сделал всё за минуту.
    ///
    /// Повторное нажатие ничего не меняет: время первой отметки
    /// и есть ответ на вопрос «когда увидел».
    /// </summary>
    public async Task<string?> TakeAsync(
        int instanceId, string userName, CancellationToken cancellationToken)
    {
        var instance = await _db.AssignedTaskInstances.AsTracking()
            .Include(i => i.Task)!
                .ThenInclude(t => t!.Assignees)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);

        if (instance?.Task is null || !IsAssignee(instance, userName))
        {
            return "Задача не найдена.";
        }

        if (instance.IsClosed)
        {
            return "Задача уже закрыта.";
        }

        if (instance.TakenAt is not null)
        {
            return null;
        }

        instance.TakenAt = Now;

        await _db.SaveChangesAsync(cancellationToken);

        return null;
    }

    /// <summary>
    /// Сдать задачу.
    ///
    /// Если проверка нужна — экземпляр уходит в «сдано на проверку»
    /// и ждёт решения автора. Если не нужна — закрывается сразу.
    ///
    /// Требование файла проверяется ЗДЕСЬ, по базе, а не по тому, показали
    /// ли мы кнопку: без вложения «сдано» означало бы «нажал кнопку».
    /// </summary>
    public async Task<string?> SubmitAsync(
        int instanceId, string userName, CancellationToken cancellationToken)
    {
        var instance = await _db.AssignedTaskInstances.AsTracking()
            .Include(i => i.Task)!
                .ThenInclude(t => t!.Assignees)
            .Include(i => i.Files)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);

        if (instance?.Task is null || !IsAssignee(instance, userName))
        {
            return "Задача не найдена.";
        }

        if (instance.IsClosed)
        {
            return "Задача уже закрыта.";
        }

        // Сдать можно только то, что взято в работу.
        //
        // Иначе «взял в работу» превращается в необязательную кнопку,
        // которую никто не нажимает, и начальник снова не знает, видел
        // ли человек задачу вообще. Шаг стоит одного нажатия, а взамен
        // в беседах пропадает вопрос «ты её получил?».
        //
        // Возвращённой задачи это не касается: её уже брали, и просить
        // взять заново значило бы придираться.
        if (instance is { State: TaskState.Open, TakenAt: null })
        {
            return "Сначала возьмите задачу в работу.";
        }

        if (instance.Task.NeedFile && instance.Files.Count == 0)
        {
            return "К этой задаче нужно приложить файл.";
        }

        instance.SubmittedAt = Now;

        if (instance.Task.NeedCheck)
        {
            instance.State = TaskState.Submitted;
        }
        else
        {
            instance.State = TaskState.Done;
            instance.ClosedAt = Now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return null;
    }

    // ==================================================================
    // Действия автора
    // ==================================================================

    /// <summary>Принять сданное. Экземпляр закрывается.</summary>
    public Task<bool> AcceptAsync(int instanceId, string userName, CancellationToken cancellationToken) =>
        AuthorActionAsync(instanceId, userName, instance =>
        {
            instance.State = TaskState.Accepted;
            instance.ClosedAt = Now;
        }, cancellationToken);

    /// <summary>
    /// Вернуть на доработку с замечанием. Экземпляр снова открыт,
    /// но помнит, что его возвращали, — и сколько раз.
    /// </summary>
    public Task<bool> ReturnAsync(
        int instanceId, string userName, string reason, CancellationToken cancellationToken) =>
        AuthorActionAsync(instanceId, userName, instance =>
        {
            instance.State = TaskState.Returned;
            instance.ReturnReason = string.IsNullOrWhiteSpace(reason) ? null : reason.Trim();
            instance.Returns++;
            instance.SubmittedAt = null;
        }, cancellationToken);

    /// <summary>
    /// Снять задачу на этот раз: конкретный экземпляр закрывается,
    /// повторение продолжается.
    /// </summary>
    public Task<bool> CancelAsync(int instanceId, string userName, CancellationToken cancellationToken) =>
        AuthorActionAsync(instanceId, userName, instance =>
        {
            instance.State = TaskState.Cancelled;
            instance.ClosedAt = Now;
        }, cancellationToken, allowClosed: false);

    private async Task<bool> AuthorActionAsync(
        int instanceId,
        string userName,
        Action<AssignedTaskInstance> change,
        CancellationToken cancellationToken,
        bool allowClosed = false)
    {
        var instance = await _db.AssignedTaskInstances.AsTracking()
            .Include(i => i.Task)
            .FirstOrDefaultAsync(i => i.Id == instanceId, cancellationToken);

        if (instance?.Task is null || !CanCheck(instance.Task, userName))
        {
            return false;
        }

        if (instance.IsClosed && !allowClosed)
        {
            return false;
        }

        change(instance);

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Прекратить задачу совсем: повторение останавливается, а открытый
    /// экземпляр снимается. Строки остаются — история сдач не стирается.
    /// </summary>
    public async Task<bool> StopAsync(int taskId, string userName, CancellationToken cancellationToken)
    {
        var task = await _db.AssignedTasks.AsTracking()
            .Include(t => t.Instances)
            .FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task is null || !IsAuthor(task, userName))
        {
            return false;
        }

        task.Stopped = true;

        foreach (var instance in task.Instances.Where(i => !i.IsClosed))
        {
            instance.State = TaskState.Cancelled;
            instance.ClosedAt = Now;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    // ==================================================================
    // Списки
    // ==================================================================

    /// <summary>Задачи, которые человеку поставили: открытые и сданные.</summary>
    public async Task<IReadOnlyList<AssignedCard>> ForAssigneeAsync(
        string userName, bool closed, CancellationToken cancellationToken)
    {
        // Ищем по исполнителю ЭКЗЕМПЛЯРА: у задачи их может быть
        // несколько, и каждый сдаёт своё. У старых задач, заведённых
        // до появления нескольких исполнителей, поле пустое —
        // для них смотрим на саму задачу.
        var rows = await _db.AssignedTaskInstances
            .Include(i => i.Task)!
                .ThenInclude(t => t!.Assignees)
            .Include(i => i.Files)
            .Where(i => i.AssigneeUserName.ToLower() == userName.ToLower()
                        || (i.AssigneeUserName == ""
                            && i.Task!.AssigneeUserName.ToLower() == userName.ToLower()))
            .OrderBy(i => i.DueAt)
            .ToListAsync(cancellationToken);

        return Cards(rows, closed);
    }

    /// <summary>Задачи, которые человек поставил другим.</summary>
    public async Task<IReadOnlyList<AssignedCard>> ForAuthorAsync(
        string userName, bool closed, CancellationToken cancellationToken)
    {
        // Свои поставленные — и те, где человеку поручен контроль:
        // он их принимает, значит они его работа.
        var rows = await _db.AssignedTaskInstances
            .Include(i => i.Task)!
                .ThenInclude(t => t!.Assignees)
            .Include(i => i.Files)
            .Where(i => i.Task!.AuthorUserName.ToLower() == userName.ToLower()
                        || i.Task!.Assignees.Any(a => a.IsController
                            && a.UserName.ToLower() == userName.ToLower()))
            .OrderBy(i => i.DueAt)
            .ToListAsync(cancellationToken);

        return Cards(rows, closed);
    }

    private IReadOnlyList<AssignedCard> Cards(List<AssignedTaskInstance> rows, bool closed)
    {
        var now = Now;
        var today = DateTime.Now.Date;

        return rows
            .Where(instance => instance.IsClosed == closed)
            .Select(instance => new AssignedCard(
                instance,
                instance.Task!,
                Overdue: !instance.IsClosed && instance.DueAt < now,
                Urgency: UrgencyOf(instance, today)))
            .OrderBy(card => card.Instance.IsClosed)
            .ThenBy(card => card.Instance.DueAt)
            .ToList();
    }

    /// <summary>
    /// Насколько горит. Шкала та же, что у личных задач: иначе «красное»
    /// в двух списках на одной странице значило бы разное.
    /// </summary>
    public static TaskUrgency UrgencyOf(AssignedTaskInstance instance, DateTime today)
    {
        if (instance.IsClosed)
        {
            return TaskUrgency.Calm;
        }

        var left = (instance.DueAt.ToLocalTime().Date - today).Days;

        return left switch
        {
            <= 1 => TaskUrgency.Now,
            <= 3 => TaskUrgency.Close,
            <= 5 => TaskUrgency.Soon,
            _ => TaskUrgency.Calm
        };
    }

    /// <summary>
    /// Сколько задач ТРЕБУЕТ ВНИМАНИЯ исполнителя — число у пункта меню.
    ///
    /// Считаются новые, возвращённые на доработку и просроченные. Сданное
    /// и ждущее проверки не считается: от человека там больше ничего
    /// не нужно, и число, которое не убрать, перестают замечать.
    /// </summary>
    public async Task<int> NeedsAttentionCountAsync(string userName, CancellationToken cancellationToken)
    {
        // Просроченные считать отдельно не нужно: они остаются открытыми,
        // пока их не сдадут, и уже попадают в это же число.
        return await _db.AssignedTaskInstances
            .Where(i => i.AssigneeUserName.ToLower() == userName.ToLower()
                        || (i.AssigneeUserName == ""
                            && i.Task!.AssigneeUserName.ToLower() == userName.ToLower()))
            .CountAsync(i => i.State == TaskState.Open || i.State == TaskState.Returned, cancellationToken);
    }

    /// <summary>
    /// Поручали ли этому человеку хоть что-нибудь — когда-либо.
    ///
    /// По этому и решается, показывать ли вкладку «От руководителя».
    /// Начальник тоже бывает чьим-то подчинённым, поэтому убирать
    /// вкладку по должности нельзя; но тому, кому никто никогда
    /// ничего не поручал, она обещает раздел, которого нет.
    /// </summary>
    public async Task<bool> HasAssignmentsAsync(string userName, CancellationToken cancellationToken) =>
        await _db.AssignedTaskInstances
            .AnyAsync(i => i.AssigneeUserName.ToLower() == userName.ToLower()
                           || (i.AssigneeUserName == ""
                               && i.Task!.AssigneeUserName.ToLower() == userName.ToLower()),
                cancellationToken);

    /// <summary>Сколько сдано на проверку — число у пункта меню начальника.</summary>
    public async Task<int> WaitingCheckCountAsync(string userName, CancellationToken cancellationToken) =>
        await _db.AssignedTaskInstances
            .Where(i => i.Task!.AuthorUserName.ToLower() == userName.ToLower()
                        || i.Task!.Assignees.Any(a => a.IsController
                            && a.UserName.ToLower() == userName.ToLower()))
            .CountAsync(i => i.State == TaskState.Submitted, cancellationToken);

    /// <summary>Один экземпляр со всем, что нужно странице. Права проверяет вызывающий.</summary>
    public async Task<AssignedTaskInstance?> InstanceAsync(int id, CancellationToken cancellationToken) =>
        await _db.AssignedTaskInstances
            .Include(i => i.Task)!
                .ThenInclude(t => t!.Assignees)
            .Include(i => i.Files)
            .FirstOrDefaultAsync(i => i.Id == id, cancellationToken);

    /// <summary>Человек участвует в задаче: поставил её или должен выполнить.</summary>
    public static bool CanSee(AssignedTask task, string userName) =>
        IsAuthor(task, userName)
        || string.Equals(task.AssigneeUserName, userName, StringComparison.OrdinalIgnoreCase)
        || task.Assignees.Any(a => string.Equals(a.UserName, userName, StringComparison.OrdinalIgnoreCase));

    // ==================================================================
    // Повторение: работа планировщика
    // ==================================================================

    /// <summary>
    /// Заводит экземпляры, которым пришёл срок, и закрывает просроченные.
    ///
    /// ДОЛГИ НЕ КОПЯТСЯ. Если прошлый экземпляр не сдан, он закрывается
    /// пометкой «не сдано», и заводится новый. Иначе к концу отпуска
    /// у человека висит двадцать невыполненных ежедневных задач, и список
    /// перестают читать совсем. Факт «не сдал» при этом не пропадает —
    /// он остаётся состоянием закрытого экземпляра.
    ///
    /// Возвращает, сколько экземпляров заведено.
    /// </summary>
    public async Task<int> AdvanceRepeatsAsync(CancellationToken cancellationToken)
    {
        var now = Now;
        var today = DateTime.Now.Date;
        var created = 0;

        var tasks = await _db.AssignedTasks
            .AsTracking()
            .Include(t => t.Instances)
            .Include(t => t.Assignees)
            .Where(t => !t.Stopped && t.Repeat != TaskRepeat.Once)
            .ToListAsync(cancellationToken);

        foreach (var task in tasks)
        {
            // Незакрытые экземпляры — по одному на исполнителя.
            var open = task.Instances.Where(i => !i.IsClosed).ToList();

            // Пока хоть у кого-то срок не наступил, новый круг
            // не начинаем: иначе у разных исполнителей разъехались бы
            // сроки одной и той же задачи.
            if (open.Any(i => i.DueAt > now))
            {
                continue;
            }

            // Сданное на проверку не трогаем: оно ждёт автора,
            // а не исполнителя, и «не сдано» было бы неправдой.
            if (open.Any(i => i.State == TaskState.Submitted))
            {
                continue;
            }

            foreach (var instance in open)
            {
                instance.State = TaskState.Missed;
                instance.ClosedAt = now;
            }

            var lastDue = task.Instances.Count == 0
                ? today.AddDays(-1)
                : task.Instances.Max(i => i.DueAt).ToLocalTime().Date;

            var next = NextDueAfter(task, lastDue < today.AddDays(-1) ? today.AddDays(-1) : lastDue);

            if (next is null)
            {
                continue;
            }

            // Новый круг — каждому исполнителю свой экземпляр.
            var people = task.Assignees.Count > 0
                ? task.Assignees.Select(a => (a.UserName, a.DisplayName)).ToList()
                : [(task.AssigneeUserName, task.AssigneeDisplayName)];

            foreach (var (login, name) in people)
            {
                _db.AssignedTaskInstances.Add(new AssignedTaskInstance
                {
                    TaskId = task.Id,
                    AssigneeUserName = login,
                    AssigneeDisplayName = name,
                    DueAt = DueAtOf(next.Value, task.DueMinutes),
                    State = TaskState.Open,
                    CreatedAt = now
                });

                created++;
            }
        }

        if (created > 0 || tasks.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return created;
    }

    /// <summary>
    /// Экземпляры, о которых пора напомнить: срок близко, напоминание
    /// ещё не отправляли. Планировщик помечает их отправленными.
    /// </summary>
    public async Task<IReadOnlyList<AssignedTaskInstance>> TakeDueRemindersAsync(
        CancellationToken cancellationToken)
    {
        var now = Now;

        var rows = await _db.AssignedTaskInstances
            .AsTracking()
            .Include(i => i.Task)
            .Where(i => !i.Reminded
                        && i.Task!.RemindMinutes > 0
                        && (i.State == TaskState.Open || i.State == TaskState.Returned))
            .ToListAsync(cancellationToken);

        var due = rows
            .Where(i => i.DueAt.AddMinutes(-i.Task!.RemindMinutes) <= now)
            .ToList();

        foreach (var instance in due)
        {
            instance.Reminded = true;
        }

        if (due.Count > 0)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return due;
    }
}
