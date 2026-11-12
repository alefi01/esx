using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Tasks;

namespace Portal.Web.Pages.Tasks;

/// <summary>
/// «Задачи»: два списка на одной странице.
///
/// СВЕРХУ — ОТ НАЧАЛЬНИКА. Их нельзя удалить и нельзя закрыть просто так:
/// их сдают, а закрывает тот, кто поставил. Это поручение, а не заметка.
///
/// НИЖЕ — СВОИ. Заметки со сроком, которые можно показать коллегам.
/// Намеренно просто: ни проектов, ни этапов, ни приоритетов — всё это
/// появляется в любом списке дел на второй неделе и ровно так же быстро
/// перестаёт заполняться. Название, срок и кому видно.
///
/// Списки рядом, но не смешаны: у них разные правила, и карточка, которую
/// нельзя закрыть галочкой, не должна выглядеть как та, которую можно.
/// </summary>
public class IndexModel : PageModel
{
    private readonly TaskService _tasks;
    private readonly AssignedTaskService _assigned;
    private readonly TaskFileStorage _files;
    private readonly Portal.Web.Data.PortalDbContext _db;
    private readonly Portal.Web.Services.Storage.UploadValidator _validator;
    private readonly Portal.Web.Services.Notifications.NotificationHub _hub;
    private readonly IUserDirectory _directory;
    private readonly TimeProvider _time;
    private readonly Microsoft.Extensions.Options.IOptions<Portal.Web.Configuration.ActiveDirectoryOptions> _ad;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        TaskService tasks,
        AssignedTaskService assigned,
        TaskFileStorage files,
        Portal.Web.Data.PortalDbContext db,
        Portal.Web.Services.Storage.UploadValidator validator,
        Portal.Web.Services.Notifications.NotificationHub hub,
        IUserDirectory directory,
        TimeProvider time,
        Microsoft.Extensions.Options.IOptions<Portal.Web.Configuration.ActiveDirectoryOptions> ad,
        ILogger<IndexModel> logger)
    {
        _tasks = tasks;
        _assigned = assigned;
        _files = files;
        _db = db;
        _validator = validator;
        _hub = hub;
        _directory = directory;
        _time = time;
        _ad = ad;
        _logger = logger;
    }

    private string UserName => User.Identity?.Name ?? "";

    /// <summary>
    /// Чьи задачи показывать: own — свои заметки, head — поручения
    /// руководителя.
    ///
    /// Две половины одного раздела на одной странице, а не две страницы:
    /// списки разные, но всё вокруг — заголовок, вкладки, доска —
    /// одно и то же, и держать это в двух файлах значит однажды
    /// поправить только один.
    /// </summary>
    [BindProperty(SupportsGet = true, Name = "scope")]
    public string? Scope { get; set; }

    public bool FromHeadView => Scope == "head";

    /// <summary>Какой список показываем: active или done.</summary>
    /// <remarks>
    /// Архива больше нет. Он был третьим списком, куда убирали «с глаз»,
    /// и этим ничем не отличался от выполненного: открывали его почти
    /// никогда, а объяснять разницу между «выполнено» и «в архиве»
    /// приходилось каждому. Убранное раньше в архив лежит теперь
    /// в «Выполненных» — ничего не потерялось.
    /// </remarks>
    [BindProperty(SupportsGet = true, Name = "view")]
    public string? View { get; set; }

    public string ViewMode => View == "done" ? "done" : "active";

    /// <summary>Столбец доски: заголовок, признак для цвета и карточки.</summary>
    /// <param name="Head">Задачи от начальника.</param>
    /// <param name="Own">Свои заметки.</param>
    public sealed record Column(
        string Title, string Key,
        IReadOnlyList<AssignedCard> Head,
        IReadOnlyList<TaskCard> Own)
    {
        public int Count => Head.Count + Own.Count;
    }

    /// <summary>Задача горит: срок прошёл или истекает сегодня.</summary>
    private static bool Burning(bool overdue, TaskUrgency urgency) =>
        overdue || urgency == TaskUrgency.Now;

    /// <summary>
    /// Столбцы доски.
    ///
    /// У своих заметок это путь дела: в работе → выполнено → архив.
    /// Архив здесь столбец, а не отдельный список: убранное с глаз
    /// открывают редко, но когда открывают — хотят видеть рядом
    /// с остальным, а не искать переключатель.
    ///
    /// У поручений руководителя — чьего действия ждёт задача:
    /// исполнителя, начальника и снова исполнителя. Выполненные
    /// и снятые в этой доске не показываются: для них переключатель
    /// «Выполненные», как у начальника.
    /// </summary>
    public IReadOnlyList<Column> Columns => FromHeadView ? HeadColumns : OwnColumns;

    private IReadOnlyList<Column> OwnColumns => ViewMode == "done"
        ?
        [
            new("Выполненные", "accepted", [], Items.Where(c => c.Task.IsDone).ToList()),
            new("Архив", "archive", [], Items.Where(c => !c.Task.IsDone).ToList())
        ]
        :
        [
            new("Горит", "late", [], Items.Where(c => Burning(c.Overdue, c.Urgency)).ToList()),
            new("В работе", "open", [], Items.Where(c => !Burning(c.Overdue, c.Urgency)).ToList())
        ];

    private IReadOnlyList<Column> HeadColumns => ViewMode == "done"
        ?
        [
            new("Принято", "accepted", OfState(Portal.Web.Data.TaskState.Accepted,
                Portal.Web.Data.TaskState.Done), []),
            new("Не сдано", "missed", OfState(Portal.Web.Data.TaskState.Missed,
                Portal.Web.Data.TaskState.Cancelled), [])
        ]
        :
        [
            new("Новые", "fresh", OfState(Portal.Web.Data.TaskState.Open)
                .Where(card => card.Instance.TakenAt is null).ToList(), []),
            new("В работе", "open", OfState(Portal.Web.Data.TaskState.Open)
                .Where(card => card.Instance.TakenAt is not null).ToList(), []),
            new("Сдано на проверку", "submitted", OfState(Portal.Web.Data.TaskState.Submitted), []),
            new("На доработке", "returned", OfState(Portal.Web.Data.TaskState.Returned), [])
        ];

    private IReadOnlyList<AssignedCard> OfState(params Portal.Web.Data.TaskState[] states) =>
        FromHead.Where(card => states.Contains(card.Instance.State)).ToList();

    /// <summary>Свои заметки: в работе или закрытые — смотря какая вкладка.</summary>
    public IReadOnlyList<TaskCard> Items { get; private set; } = [];

    /// <summary>Поручения руководителя.</summary>
    public IReadOnlyList<AssignedCard> FromHead { get; private set; } = [];

    /// <summary>Сколько своих задач в работе — число на переключателе.</summary>
    public int ActiveCount { get; private set; }

    /// <summary>
    /// Сколько поручений требует внимания: новые, возвращённые
    /// на доработку и просроченные. Это число стоит на вкладке
    /// «От руководителя».
    /// </summary>
    public int HeadCount { get; private set; }

    /// <summary>Человеку когда-либо что-то поручали — от этого зависит, есть ли вкладка.</summary>
    public bool HasHead { get; private set; }

    /// <summary>Сколько подчинённые сдали на проверку — число на третьей вкладке.</summary>
    public int WaitingCount { get; private set; }

    /// <summary>Вправе ли человек ставить задачи — от этого зависит третья вкладка.</summary>
    public bool CanAssign => User.IsInRole(_ad.Value.AdminGroup) || User.IsInRole(_ad.Value.HeadGroup);

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    [BindProperty]
    public NewTask Input { get; set; } = new();

    public class NewTask
    {
        [Required(ErrorMessage = "Напишите, что нужно сделать")]
        [MaxLength(300, ErrorMessage = "Название не длиннее 300 символов")]
        [Display(Name = "Что сделать")]
        public string Title { get; set; } = "";

        [MaxLength(4000, ErrorMessage = "Подробности не длиннее 4000 символов")]
        public string? Notes { get; set; }

        /// <summary>
        /// Срок. Приходит из поля типа date, то есть датой без времени,
        /// и такой же хранится.
        /// </summary>
        [DataType(DataType.Date)]
        public DateTime? DueOn { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        // Свои заметки: в работе или всё закрытое — выполненное
        // и убранное в архив. Разложит их по столбцам доска.
        Items = await _tasks.ListAsync(UserName, ViewMode, cancellationToken);
        ActiveCount = await _tasks.ActiveCountAsync(UserName, cancellationToken);

        FromHead = await _assigned.ForAssigneeAsync(
            UserName, closed: ViewMode == "done", cancellationToken);

        // Число на вкладке «От руководителя»: новые поручения,
        // возвращённые на доработку и просроченные.
        HeadCount = await _assigned.NeedsAttentionCountAsync(UserName, cancellationToken);
        HasHead = HeadCount > 0 || await _assigned.HasAssignmentsAsync(UserName, cancellationToken);

        // Число на вкладке «Подчинённым» нужно только начальнику —
        // лишний запрос для остальных не делаем.
        WaitingCount = CanAssign
            ? await _assigned.WaitingCheckCountAsync(UserName, cancellationToken)
            : 0;
    }

    /// <summary>
    /// Поправить свою заметку.
    ///
    /// Правит только хозяин: тот, кому задачу показали, видит её,
    /// но менять чужой текст не может — иначе «показать коллеге»
    /// означало бы «отдать».
    /// </summary>
    public async Task<IActionResult> OnPostEditAsync(
        int id, string title, string? notes, DateTime? dueOn, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(title))
        {
            ErrorMessage = "Название задачи не может быть пустым.";

            return RedirectToPage(new { scope = Scope, view = View });
        }

        if (!await _tasks.EditAsync(id, UserName, title, notes, dueOn, cancellationToken))
        {
            ErrorMessage = "Изменить можно только свою задачу.";
        }
        else
        {
            StatusMessage = "Задача изменена.";
        }

        return RedirectToPage(new { scope = Scope, view = View });
    }

    // ==================================================================
    // Задачи от начальника
    // ==================================================================

    /// <summary>
    /// Взять задачу в работу.
    ///
    /// Нужна, чтобы новое не терялось среди давно разобранного:
    /// до отметки задача лежит в столбце «Новые», после — в «В работе».
    /// Начальник при этом видит, дошло ли поручение вообще.
    /// </summary>
    public async Task<IActionResult> OnPostTakeAsync(int instanceId, CancellationToken cancellationToken)
    {
        var error = await _assigned.TakeAsync(instanceId, UserName, cancellationToken);

        if (error is not null)
        {
            ErrorMessage = error;
        }

        return RedirectToPage(new { scope = Scope, view = View });
    }

    /// <summary>
    /// Сдать задачу. Файл необязателен, если задача его не требует, —
    /// но если требует, без него сдать нельзя (проверяет служба).
    /// </summary>
    public async Task<IActionResult> OnPostSubmitAsync(
        int instanceId, List<IFormFile> attachments, CancellationToken cancellationToken)
    {
        var instance = await _assigned.InstanceAsync(instanceId, cancellationToken);

        if (instance?.Task is null
            || !string.Equals(instance.Task.AssigneeUserName, UserName, StringComparison.OrdinalIgnoreCase))
        {
            return NotFound();
        }

        var problems = await AttachAsync(instance, attachments, cancellationToken);

        var error = await _assigned.SubmitAsync(instanceId, UserName, cancellationToken);

        if (error is not null)
        {
            ErrorMessage = error;

            return RedirectToPage(new { view = View });
        }

        // Будим страницу того, кто поставил: ему пришла работа — проверить.
        _hub.Notify([instance.Task.AuthorUserName]);

        StatusMessage = problems.Count > 0
            ? "Задача сдана, но не приложены: " + string.Join("; ", problems)
            : instance.Task.NeedCheck ? "Задача сдана на проверку." : "Задача отмечена выполненной.";

        return RedirectToPage(new { view = View });
    }

    /// <summary>Скачать файл задачи — свой сданный или приложенный автором.</summary>
    public async Task<IActionResult> OnGetFileAsync(int fileId, CancellationToken cancellationToken)
    {
        var file = await _db.AssignedTaskFiles
            .Include(f => f.Instance)
            .FirstOrDefaultAsync(f => f.Id == fileId, cancellationToken);

        if (file is null)
        {
            return NotFound();
        }

        var taskId = file.TaskId ?? file.Instance?.TaskId;

        if (taskId is null)
        {
            return NotFound();
        }

        var task = await _db.AssignedTasks.FirstOrDefaultAsync(t => t.Id == taskId, cancellationToken);

        // Файл задачи виден ровно двоим: тому, кто её поставил, и тому,
        // кто исполняет. Ни группы, ни папки здесь ни при чём.
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

    /// <summary>Сохраняет сданные файлы. Возвращает список того, что не вышло.</summary>
    private async Task<List<string>> AttachAsync(
        Portal.Web.Data.AssignedTaskInstance instance,
        List<IFormFile>? uploads,
        CancellationToken cancellationToken)
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

            // Те же проверки, что и в файловом хранилище: запрет опасных
            // расширений и предел размера. Задача не должна становиться
            // обходным путём для того, что запрещено в папках.
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

                var storageName = await _files.SaveAsync(instance.TaskId, content, cancellationToken);

                _db.AssignedTaskFiles.Add(new Portal.Web.Data.AssignedTaskFile
                {
                    InstanceId = instance.Id,
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
                _logger.LogError(ex, "Не удалось сохранить файл {Name} по задаче.", safeName);

                problems.Add($"«{safeName}» — не удалось сохранить");
            }
        }

        if (added)
        {
            await _db.SaveChangesAsync(cancellationToken);
        }

        return problems;
    }

    public async Task<IActionResult> OnPostCreateAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);

            return Page();
        }

        await _tasks.CreateAsync(User, Input.Title, Input.Notes, Input.DueOn, cancellationToken);

        StatusMessage = "Задача добавлена.";

        return RedirectToPage(new { view = View });
    }

    public async Task<IActionResult> OnPostDoneAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _tasks.ToggleDoneAsync(id, UserName, cancellationToken))
        {
            ErrorMessage = "Отмечать выполненной может только тот, чья это задача.";
        }

        return RedirectToPage(new { view = View });
    }

    public async Task<IActionResult> OnPostArchiveAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _tasks.ToggleArchivedAsync(id, UserName, cancellationToken))
        {
            ErrorMessage = "Убирать в архив может только тот, чья это задача.";
        }

        return RedirectToPage(new { view = View });
    }

    public async Task<IActionResult> OnPostDeleteAsync(int id, CancellationToken cancellationToken)
    {
        if (!await _tasks.DeleteAsync(id, UserName, cancellationToken))
        {
            ErrorMessage = "Удалить задачу может только тот, чья она.";
        }
        else
        {
            StatusMessage = "Задача удалена.";
        }

        return RedirectToPage(new { view = View });
    }

    public async Task<IActionResult> OnPostShareAsync(
        int id, string? login, CancellationToken cancellationToken)
    {
        var name = (login ?? "").Trim();

        // Имя для показа берём из справочника: в списке «кому видно»
        // должно стоять «Пётр Петров», а не «petrov».
        var display = name.Length > 0
            ? await _directory.DisplayNameAsync(name, cancellationToken)
            : "";

        var problem = await _tasks.ShareAsync(id, UserName, name, display, cancellationToken);

        if (problem is null)
        {
            StatusMessage = "Задача теперь видна коллеге.";
        }
        else
        {
            ErrorMessage = problem;
        }

        return RedirectToPage(new { view = View });
    }

    public async Task<IActionResult> OnPostUnshareAsync(
        int id, string login, CancellationToken cancellationToken)
    {
        await _tasks.UnshareAsync(id, UserName, login, cancellationToken);

        return RedirectToPage(new { view = View });
    }

    /// <summary>
    /// Срок поручения по-человечески. В отличие от личных задач здесь
    /// бывает время внутри дня — его показываем, если оно задано.
    /// </summary>
    public static string DueText(DateTime dueAtUtc, bool withTime)
    {
        var local = dueAtUtc.ToLocalTime();
        var text = DueText(local);

        return withTime ? text + ", " + local.ToString("HH:mm") : text;
    }

    /// <summary>Срок по-человечески: «сегодня», «завтра», «12.08.2026».</summary>
    public static string DueText(DateTime due)
    {
        var days = (due.Date - DateTime.Now.Date).Days;

        return days switch
        {
            0 => "сегодня",
            1 => "завтра",
            -1 => "вчера",
            < 0 => "просрочено на " + (-days) + " дн.",
            <= 7 => "через " + days + " дн.",
            _ => due.ToString("dd.MM.yyyy")
        };
    }
}
