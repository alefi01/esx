using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Редактирование объявления.
///
/// Политика PublishAnnouncements пускает сюда всех публикаторов, поэтому
/// внутри обязательно проверяется ещё и авторство: публикатор правит только
/// свои объявления, администратор — любые. Без этой второй проверки любой
/// член группы публикаторов мог бы переписать чужое объявление,
/// просто подставив другой номер в адресе.
/// </summary>
public class EditModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly ILogger<EditModel> _logger;

    public EditModel(
        PortalDbContext db,
        TimeProvider time,
        IOptions<ActiveDirectoryOptions> adOptions,
        ILogger<EditModel> logger)
    {
        _db = db;
        _time = time;
        _adOptions = adOptions.Value;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public string? ErrorMessage { get; private set; }

    public sealed class InputModel
    {
        /// <summary>
        /// Номер редактируемого объявления. Приходит из формы, поэтому доверять
        /// ему нельзя: права проверяются по записи, загруженной из базы по этому
        /// номеру, а не по чему-либо из формы.
        /// </summary>
        public int Id { get; set; }

        [Required(ErrorMessage = "Введите заголовок")]
        [MaxLength(200, ErrorMessage = "Заголовок не длиннее 200 символов")]
        [Display(Name = "Заголовок")]
        public string Title { get; set; } = "";

        [Required(ErrorMessage = "Введите текст объявления")]
        [MaxLength(10000, ErrorMessage = "Текст не длиннее 10000 символов")]
        [Display(Name = "Текст")]
        public string Body { get; set; } = "";
    }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var announcement = await _db.Announcements
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (announcement is null)
        {
            return NotFound();
        }

        if (!AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup))
        {
            return Forbid();
        }

        Input = new InputModel
        {
            Id = announcement.Id,
            Title = announcement.Title,
            Body = announcement.Body
        };

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        // AsTracking нужен потому, что по умолчанию контекст настроен
        // на запросы без отслеживания (см. Program.cs). Без него EF
        // не заметит изменений и SaveChanges ничего не сохранит.
        var announcement = await _db.Announcements
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == Input.Id, cancellationToken);

        if (announcement is null)
        {
            return NotFound();
        }

        // Права проверяются ЗАНОВО при сохранении, а не только при открытии формы:
        // между открытием и отправкой могло пройти время, а сам POST можно
        // отправить и в обход страницы.
        if (!AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup))
        {
            return Forbid();
        }

        announcement.Title = Input.Title.Trim();
        announcement.Body = Input.Body.Trim();
        announcement.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось сохранить изменения объявления {Id}.", Input.Id);

            ErrorMessage = "Не удалось сохранить изменения: база данных недоступна. " +
                           "Скопируйте текст, чтобы не потерять, и сообщите администратору.";

            return Page();
        }

        _logger.LogInformation(
            "Пользователь {User} изменил объявление {Id}.", User.Identity?.Name, announcement.Id);

        TempData["StatusMessage"] = "Изменения сохранены.";

        return RedirectToPage("Index");
    }
}
