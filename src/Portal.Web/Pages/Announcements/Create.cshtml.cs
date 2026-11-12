using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Data;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Создание объявления В ПАПКЕ ОТДЕЛА.
///
/// Отдельной ленты объявлений больше нет: объявление висит на папке,
/// и видят его те, кто видит папку. Поэтому страница всегда открывается
/// с номером папки, а право проверяется по ней же — начальник этого
/// отдела либо администратор. Политики на странице нет: «издатель
/// вообще» больше не существует как роль.
/// </summary>
public class CreateModel : PageModel
{
    private readonly Portal.Web.Services.Announcements.AnnouncementPublisher _publisher;
    private readonly Portal.Web.Services.Storage.FolderTree _tree;
    private readonly Portal.Web.Configuration.ActiveDirectoryOptions _ad;

    public CreateModel(
        Portal.Web.Services.Announcements.AnnouncementPublisher publisher,
        Portal.Web.Services.Storage.FolderTree tree,
        Microsoft.Extensions.Options.IOptions<Portal.Web.Configuration.ActiveDirectoryOptions> ad)
    {
        _publisher = publisher;
        _tree = tree;
        _ad = ad.Value;
    }

    /// <summary>Папка, на которой появится объявление.</summary>
    public StorageFolder? Folder { get; private set; }

    [BindProperty(SupportsGet = true)]
    public int FolderId { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    /// <summary>
    /// Отдельная модель для формы, а не сама сущность Announcement.
    ///
    /// Так сделано намеренно: если привязывать форму прямо к сущности,
    /// злоумышленник может дописать в отправляемые данные поля, которых
    /// в форме нет — например, AuthorUserName — и опубликовать объявление
    /// от чужого имени. Здесь же в модели есть ровно два поля, и подделать
    /// автора невозможно: он берётся из cookie, а не из формы.
    /// </summary>
    public sealed class InputModel
    {
        [Required(ErrorMessage = "Введите заголовок")]
        [MaxLength(200, ErrorMessage = "Заголовок не длиннее 200 символов")]
        [Display(Name = "Заголовок")]
        public string Title { get; set; } = "";

        [Required(ErrorMessage = "Введите текст объявления")]
        [MaxLength(10000, ErrorMessage = "Текст не длиннее 10000 символов")]
        [Display(Name = "Текст")]
        public string Body { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(CancellationToken cancellationToken)
    {
        var denied = await LoadFolderAsync(cancellationToken);

        return denied ?? Page();
    }

    /// <summary>
    /// Читает папку и проверяет право вешать на неё объявления.
    /// Возвращает ответ-отказ или null, если всё в порядке.
    /// </summary>
    private async Task<IActionResult?> LoadFolderAsync(CancellationToken cancellationToken)
    {
        await _tree.LoadAsync(cancellationToken);

        Folder = _tree.Get(FolderId);

        if (Folder is null || !_tree.CanRead(User, Folder))
        {
            // Не «запрещено», а «не найдено»: существование чужой папки —
            // тоже сведения.
            return NotFound();
        }

        return Portal.Web.Security.AnnouncementPermissions.CanPublishIn(User, Folder, _tree, _ad.AdminGroup)
            ? null
            : Forbid();
    }

    public async Task<IActionResult> OnPostAsync(
        List<IFormFile> attachments, CancellationToken cancellationToken)
    {
        var denied = await LoadFolderAsync(cancellationToken);

        if (denied is not null)
        {
            return denied;
        }

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var result = await _publisher.PublishAsync(
            FolderId, Input.Title, Input.Body, User, attachments, cancellationToken);

        if (result.Error is not null)
        {
            ErrorMessage = result.Error;

            return Page();
        }

        if (result.Problems.Count > 0)
        {
            // Объявление уже опубликовано, поэтому не ошибка, а предупреждение:
            // текст на месте, не приложились только некоторые файлы.
            TempData["ErrorMessage"] = "Не приложены: " + string.Join("; ", result.Problems);
        }

        // TempData переживает перенаправление и очищается после первого показа —
        // ровно то, что нужно для сообщения «готово».
        TempData["StatusMessage"] = "Объявление опубликовано.";

        // Возвращаемся в папку: ленты, куда можно было бы вернуться,
        // больше нет, а объявление живёт именно здесь.
        return RedirectToPage("/Files/Index", new { id = FolderId });
    }

}
