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

    /// <summary>Какой список показываем: active, done, archive.</summary>
    [BindProperty(SupportsGet = true, Name = "view")]
    public string? View { get; set; }

    public string ViewMode => View is "done" or "archive" ? View : "active";

    public IReadOnlyList<TaskCard> Items { get; private set; } = [];

    /// <summary>Задачи, поставленные начальником, — верхним списком.</summary>
    public IReadOnlyList<AssignedCard> FromHead { get; private set; } = [];

    public int ActiveCount { get; private set; }

    /// <summary>Сколько задач от начальника требует внимания — для подписи.</summary>
    public int HeadCount { get; private set; }

    /// <summary>
    /// Число на вкладке «Мои» — то же, что на пункте меню: новые
    /// от начальника, возвращённые и просроченные, свои в том числе.
    /// Считается по той же формуле, что и в /api/notifications: два
    /// разных числа в двух местах про одно и то же — верный способ
    /// перестать им верить.
    /// </summary>
    public int MineAttention { get; private set; }

    /// <summary>Сколько подчинённые сдали на проверку — число на второй вкладке.</summary>
    public int WaitingCount { get; private set; }

    /// <summary>Вправе ли человек ставить задачи — от этого зависят вкладки.</summary>
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
        Items = await _tasks.ListAsync(UserName, ViewMode, cancellationToken);
        ActiveCount = await _tasks.ActiveCountAsync(UserName, cancellationToken);

        // В архиве задач от начальника нет: архив — это про свои заметки,
        // а поручение убирает с глаз тот, кто его поставил.
        FromHead = ViewMode == "archive"
            ? []
            : await _assigned.ForAssigneeAsync(UserName, closed: ViewMode == "done", cancellationToken);

        HeadCount = await _assigned.NeedsAttentionCountAsync(UserName, cancellationToken);

        var overdueOwn = (await _tasks.UrgentAsync(UserName, cancellationToken))
            .Count(t => t.DueOn!.Value.Date < DateTime.Now.Date);

        MineAttention = HeadCount + overdueOwn;

        // Второе число нужно только начальнику — лишний запрос
        // для остальных не делаем.
        WaitingCount = CanAssign
            ? await _assigned.WaitingCheckCountAsync(UserName, cancellationToken)
            : 0;
    }

    // ==================================================================
    // Задачи от начальника
    // ==================================================================

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
