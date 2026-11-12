using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Security;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Tasks;

namespace Portal.Web.Pages.Tasks;

/// <summary>
/// «Задачи подчинённым» — вторая половина раздела «Задачи»: что человек
/// поручил другим, кто что сдал и что ждёт его проверки.
///
/// В МЕНЮ ОТДЕЛЬНОГО ПУНКТА НЕТ: для человека это один раздел с двумя
/// вкладками, «Мои» и «Подчинённым». Два пункта меню про задачи
/// заставляли обходить оба, чтобы понять, не ждёт ли чего-нибудь.
/// Теперь на одном пункте одно число — всё, что ждёт этого человека.
///
/// СТРАНИЦА при этом осталась отдельной, и намеренно: списки живут
/// по разным правилам (здесь принимают и возвращают, там сдают),
/// у страницы своё право доступа и десяток своих обработчиков.
/// Склеить их в один файл значило бы получить полторы тысячи строк,
/// где половина кода недоступна тому, кто открыл страницу.
///
/// Право на страницу даёт членство в группе начальников (политика
/// AssignTasks). Назначение начальником папки тут ни при чём: задачи
/// ставятся кому угодно в портале, а отдел — это про объявления и файлы.
/// </summary>
[Authorize(Policy = PortalPolicies.AssignTasks)]
public class AssignedModel : PageModel
{
    private readonly AssignedTaskService _assigned;
    private readonly TaskService _tasks;
    private readonly TaskFileStorage _files;
    private readonly PortalDbContext _db;
    private readonly IUserDirectory _directory;
    private readonly Portal.Web.Services.Storage.UploadValidator _validator;
    private readonly Portal.Web.Services.Notifications.NotificationHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<AssignedModel> _logger;

    public AssignedModel(
        AssignedTaskService assigned,
        TaskService tasks,
        TaskFileStorage files,
        PortalDbContext db,
        IUserDirectory directory,
        Portal.Web.Services.Storage.UploadValidator validator,
        Portal.Web.Services.Notifications.NotificationHub hub,
        TimeProvider time,
        ILogger<AssignedModel> logger)
    {
        _assigned = assigned;
        _tasks = tasks;
        _files = files;
        _db = db;
        _directory = directory;
        _validator = validator;
        _hub = hub;
        _time = time;
        _logger = logger;
    }

    private string UserName => User.Identity?.Name ?? "";

    private string DisplayName => User.FindFirstValue(ClaimTypes.GivenName) ?? UserName;

    /// <summary>Какой список: active — в работе и на проверке, done — выполненные.</summary>
    [BindProperty(SupportsGet = true, Name = "view")]
    public string? View { get; set; }

    public string ViewMode => View == "done" ? "done" : "active";

    /// <summary>
    /// Как сортировать карточки в столбцах доски.
    ///
    /// «due» — по сроку, сначала то, что горит. «important» — важные
    /// наверх, внутри них снова по сроку. Больше ничего: сортировка
    /// по названию или по исполнителю звучит разумно, пока не
    /// попробуешь ею пользоваться.
    ///
    /// Живёт в адресе, а не в настройках: тогда не слетает при
    /// перезагрузке и кладётся в закладку.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "sort")]
    public string? Sort { get; set; }

    public bool ImportantFirst => Sort == "important";

    /// <summary>Столбец доски: заголовок, признак для цвета и карточки.</summary>
    public sealed record Column(string Title, string Key, IReadOnlyList<AssignedCard> Cards);

    /// <summary>
    /// Столбцы доски.
    ///
    /// В работе их три — ровно по тому, чьего действия ждёт задача:
    /// исполнителя, начальника и снова исполнителя. Закрытые разложены
    /// по тому, чем кончилось. Пустые столбцы остаются на месте: доска,
    /// у которой при каждом движении меняется число колонок, перестаёт
    /// читаться с одного взгляда.
    /// </summary>
    public IReadOnlyList<Column> Columns => ViewMode == "done"
        ?
        [
            new("Принято", "accepted", Of(TaskState.Accepted)),
            new("Выполнено", "done", Of(TaskState.Done)),
            new("Снято", "cancelled", Of(TaskState.Cancelled)),
            new("Не сдано", "missed", Of(TaskState.Missed))
        ]
        :
        [
            new("В работе", "open", Of(TaskState.Open)),
            new("Сдано на проверку", "submitted", Of(TaskState.Submitted)),
            new("На доработке", "returned", Of(TaskState.Returned))
        ];

    /// <summary>
    /// Карточки одного состояния в выбранном порядке.
    ///
    /// Список приходит уже отсортированным по сроку — это порядок
    /// по умолчанию. «Важные сверху» не ломает его, а добавляет первый
    /// ключ: внутри важных и внутри обычных срок по-прежнему решает.
    /// </summary>
    private IReadOnlyList<AssignedCard> Of(TaskState state)
    {
        var cards = Items.Where(card => card.Instance.State == state);

        return ImportantFirst
            ? cards.OrderByDescending(card => card.Task.Important).ToList()
            : cards.ToList();
    }

    public IReadOnlyList<AssignedCard> Items { get; private set; } = [];

    /// <summary>Сколько ждёт проверки — это и есть работа начальника.</summary>
    public int WaitingCount { get; private set; }

    /// <summary>
    /// Число на вкладке «Мои»: начальник тоже чей-то подчинённый,
    /// и своё, пока он проверяет чужое, теряться не должно. Формула
    /// та же, что на пункте меню и на странице своих задач.
    /// </summary>
    public int MineAttention { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    [BindProperty]
    public NewAssignedTask Input { get; set; } = new();

    public class NewAssignedTask
    {
        [Required(ErrorMessage = "Выберите, кому поставить задачу")]
        public string Assignee { get; set; } = "";

        [Required(ErrorMessage = "Напишите, что нужно сделать")]
        [MaxLength(300, ErrorMessage = "Название не длиннее 300 символов")]
        public string Title { get; set; } = "";

        [MaxLength(4000, ErrorMessage = "Подробности не длиннее 4000 символов")]
        public string? Notes { get; set; }

        /// <summary>Срок первого (или единственного) выполнения.</summary>
        [Required(ErrorMessage = "Укажите срок")]
        [DataType(DataType.Date)]
        public DateTime? DueOn { get; set; }

        /// <summary>Время внутри дня. Пусто — конец дня.</summary>
        [DataType(DataType.Time)]
        public TimeOnly? DueTime { get; set; }

        public TaskRepeat Repeat { get; set; } = TaskRepeat.Once;

        /// <summary>Дни недели для повторения — отмеченные галочками.</summary>
        public int[] WeekDays { get; set; } = [];

        [Range(1, 31, ErrorMessage = "Число месяца — от 1 до 31")]
        public int MonthDay { get; set; } = 1;

        public bool NeedFile { get; set; }

        public bool NeedCheck { get; set; } = true;

        public bool Important { get; set; }

        /// <summary>За сколько минут до срока напомнить. 0 — не напоминать.</summary>
        [Range(0, 10080, ErrorMessage = "Напоминание — от 0 до 10080 минут (неделя)")]
        public int RemindMinutes { get; set; } = 60;
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Items = await _assigned.ForAuthorAsync(UserName, closed: ViewMode == "done", cancellationToken);
        WaitingCount = await _assigned.WaitingCheckCountAsync(UserName, cancellationToken);

        var overdueOwn = (await _tasks.UrgentAsync(UserName, cancellationToken))
            .Count(t => t.DueOn!.Value.Date < DateTime.Now.Date);

        MineAttention = await _assigned.NeedsAttentionCountAsync(UserName, cancellationToken) + overdueOwn;
    }

    /// <summary>
    /// Поиск сотрудников для выбора исполнителя.
    ///
    /// Берём с запасом — двести человек, — и сообщаем, если нашлось
    /// ещё больше. Раньше бралось пятьдесят, и в организации на двести
    /// человек половина в подсказке просто не появлялась: выглядело
    /// это как «портал их не знает», хотя он знал.
    /// </summary>
    public async Task<IActionResult> OnGetPeopleAsync(string? q, CancellationToken cancellationToken)
    {
        const int limit = 200;

        var people = await _directory.SearchAsync(q, "", limit + 1, cancellationToken);

        var more = people.Count > limit;

        return new JsonResult(new
        {
            more,
            people = people.Take(limit)
                .Select(p => new { userName = p.UserName, displayName = p.DisplayName })
        });
    }

    public async Task<IActionResult> OnPostCreateAsync(
        List<IFormFile> attachments, CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);

            return Page();
        }

        var login = Input.Assignee.Trim();

        if (!await _directory.ExistsAsync(login, cancellationToken))
        {
            ErrorMessage = "Такого сотрудника нет в списке доступа к порталу.";

            await LoadAsync(cancellationToken);

            return Page();
        }

        var mask = 0;

        foreach (var day in Input.WeekDays)
        {
            if (day is >= 0 and <= 6)
            {
                mask |= 1 << day;
            }
        }

        // Повторение по дням недели без единого отмеченного дня не повторится
        // никогда — проверяем здесь, а не в базе: человек должен узнать
        // об этом сразу, а не через неделю молчания.
        if (Input.Repeat is TaskRepeat.Weekdays or TaskRepeat.Weekly && mask == 0)
        {
            ErrorMessage = "Выберите хотя бы один день недели.";

            await LoadAsync(cancellationToken);

            return Page();
        }

        var task = new AssignedTask
        {
            AuthorUserName = UserName,
            AuthorDisplayName = DisplayName,
            AssigneeUserName = login,
            AssigneeDisplayName = await _directory.DisplayNameAsync(login, cancellationToken),
            Title = Input.Title.Trim(),
            Notes = string.IsNullOrWhiteSpace(Input.Notes) ? null : Input.Notes.Trim(),
            Repeat = Input.Repeat,
            WeekDays = mask,
            MonthDay = Input.MonthDay,
            DueMinutes = Input.DueTime is { } time ? time.Hour * 60 + time.Minute : null,
            NeedFile = Input.NeedFile,
            NeedCheck = Input.NeedCheck,
            Important = Input.Important,
            RemindMinutes = Input.RemindMinutes
        };

        await _assigned.CreateAsync(task, Input.DueOn!.Value, cancellationToken);

        var problems = await AttachAsync(task, attachments, cancellationToken);

        // Будим страницу исполнителя: ему поставили задачу, и узнать
        // об этом он должен сейчас, а не при следующем заходе.
        _hub.Notify([login]);

        StatusMessage = problems.Count > 0
            ? "Задача поставлена, но не приложены: " + string.Join("; ", problems)
            : $"Задача поставлена: {task.AssigneeDisplayName}.";

        return RedirectToPage(new { view = View, sort = Sort });
    }

    public async Task<IActionResult> OnPostAcceptAsync(int instanceId, CancellationToken cancellationToken)
    {
        if (!await _assigned.AcceptAsync(instanceId, UserName, cancellationToken))
        {
            ErrorMessage = "Принять задачу может только тот, кто её поставил.";

            return RedirectToPage(new { view = View, sort = Sort });
        }

        await NotifyAssigneeAsync(instanceId, cancellationToken);

        StatusMessage = "Принято.";

        return RedirectToPage(new { view = View, sort = Sort });
    }

    public async Task<IActionResult> OnPostReturnAsync(
        int instanceId, string? reason, CancellationToken cancellationToken)
    {
        if (!await _assigned.ReturnAsync(instanceId, UserName, reason ?? "", cancellationToken))
        {
            ErrorMessage = "Вернуть задачу может только тот, кто её поставил.";

            return RedirectToPage(new { view = View, sort = Sort });
        }

        await NotifyAssigneeAsync(instanceId, cancellationToken);

        StatusMessage = "Возвращено на доработку.";

        return RedirectToPage(new { view = View, sort = Sort });
    }

    /// <summary>Снять задачу на этот раз. Повторение продолжается.</summary>
    public async Task<IActionResult> OnPostCancelAsync(int instanceId, CancellationToken cancellationToken)
    {
        if (!await _assigned.CancelAsync(instanceId, UserName, cancellationToken))
        {
            ErrorMessage = "Снять задачу может только тот, кто её поставил.";

            return RedirectToPage(new { view = View, sort = Sort });
        }

        await NotifyAssigneeAsync(instanceId, cancellationToken);

        StatusMessage = "Задача снята.";

        return RedirectToPage(new { view = View, sort = Sort });
    }

    /// <summary>Прекратить задачу совсем: больше она не повторится.</summary>
    public async Task<IActionResult> OnPostStopAsync(int taskId, CancellationToken cancellationToken)
    {
        if (!await _assigned.StopAsync(taskId, UserName, cancellationToken))
        {
            ErrorMessage = "Прекратить задачу может только тот, кто её поставил.";
        }
        else
        {
            StatusMessage = "Задача прекращена: новых сроков не будет.";
        }

        return RedirectToPage(new { view = View, sort = Sort });
    }

    /// <summary>Скачать файл задачи: приложенный автором или сданный исполнителем.</summary>
    public async Task<IActionResult> OnGetFileAsync(int fileId, CancellationToken cancellationToken)
    {
        var file = await _db.AssignedTaskFiles
            .Include(f => f.Instance)
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        var taskId = file?.TaskId ?? file?.Instance?.TaskId;

        if (file is null || taskId is null)
        {
            return NotFound();
        }

        var task = await _db.AssignedTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        if (task is null || !AssignedTaskService.CanSee(task, UserName))
        {
            return NotFound();
        }

        if (!_files.Exists(task.Id, file.StorageName))
        {
            return NotFound();
        }

        var stream = _files.OpenRead(task.Id, file.StorageName);

        return new FileStreamResult(stream, file.ContentType)
        {
            FileDownloadName = file.OriginalName,
            EnableRangeProcessing = true
        };
    }

    private async Task NotifyAssigneeAsync(int instanceId, CancellationToken cancellationToken)
    {
        var instance = await _assigned.InstanceAsync(instanceId, cancellationToken);

        if (instance?.Task is not null)
        {
            _hub.Notify([instance.Task.AssigneeUserName]);
        }
    }

    /// <summary>Файлы, приложенные АВТОРОМ к задаче: бланк, образец, шаблон.</summary>
    private async Task<List<string>> AttachAsync(
        AssignedTask task, List<IFormFile>? uploads, CancellationToken cancellationToken)
    {
        var problems = new List<string>();

        if (uploads is null || uploads.Count == 0)
        {
            return problems;
        }

        if (!_files.IsConfigured)
        {
            problems.Add("хранилище не настроено (Storage:RootPath)");

            return problems;
        }

        var added = false;

        foreach (var upload in uploads)
        {
            if (upload.Length == 0)
            {
                continue;
            }

            var rejection = _validator.Validate(upload.FileName, upload.Length, 0, null, 0);

            if (rejection is not null)
            {
                problems.Add($"«{rejection.FileName}» — {rejection.Reason}");

                continue;
            }

            var safeName = Portal.Web.Services.Storage.UploadValidator.SanitizeName(upload.FileName);

            try
            {
                await using var content = upload.OpenReadStream();

                var storageName = await _files.SaveAsync(task.Id, content, cancellationToken);

                _db.AssignedTaskFiles.Add(new AssignedTaskFile
                {
                    TaskId = task.Id,
                    OriginalName = safeName,
                    StorageName = storageName,
                    SizeBytes = upload.Length,
                    ContentType = Portal.Web.Services.Storage.UploadValidator.ResolveContentType(safeName),
                    UploadedByUserName = UserName,
                    UploadedAt = _time.GetUtcNow().UtcDateTime
                });

                added = true;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Не удалось приложить файл {Name} к задаче.", safeName);

                problems.Add($"«{safeName}» — не удалось сохранить");
            }
        }

        if (added)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return problems;
    }
}
