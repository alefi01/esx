using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Tasks;

namespace Portal.Web.Pages.Tasks;

/// <summary>
/// «Мои задачи»: заметки со сроком, которые можно показать коллегам.
///
/// Намеренно просто. Ни проектов, ни этапов, ни приоритетов: всё это
/// появляется в любом списке дел на второй неделе и ровно так же быстро
/// перестаёт заполняться. Здесь — название, срок и кому видно.
/// </summary>
public class IndexModel : PageModel
{
    private readonly TaskService _tasks;
    private readonly IUserDirectory _directory;

    public IndexModel(TaskService tasks, IUserDirectory directory)
    {
        _tasks = tasks;
        _directory = directory;
    }

    private string UserName => User.Identity?.Name ?? "";

    /// <summary>Какой список показываем: active, done, archive.</summary>
    [BindProperty(SupportsGet = true, Name = "view")]
    public string? View { get; set; }

    public string ViewMode => View is "done" or "archive" ? View : "active";

    public IReadOnlyList<TaskCard> Items { get; private set; } = [];

    public int ActiveCount { get; private set; }

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
