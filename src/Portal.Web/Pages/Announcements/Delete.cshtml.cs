using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Security;

namespace Portal.Web.Pages.Announcements;

/// <summary>
/// Удаление объявления с подтверждением.
///
/// Отдельная страница, а не кнопка с всплывающим окном, — потому что
/// подтверждение через окно требует JavaScript, а мы обходимся без него.
/// Заодно так удаление гарантированно выполняется POST-запросом:
/// удаление по обычной ссылке (GET) сработало бы от простого перехода
/// по адресу, в том числе от предзагрузки страницы браузером.
/// </summary>
public class DeleteModel : PageModel
{
    private readonly PortalDbContext _db;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly ILogger<DeleteModel> _logger;

    public DeleteModel(PortalDbContext db, IOptions<ActiveDirectoryOptions> adOptions, ILogger<DeleteModel> logger)
    {
        _db = db;
        _adOptions = adOptions.Value;
        _logger = logger;
    }

    public Announcement? Announcement { get; private set; }

    public string? ErrorMessage { get; private set; }

    public async Task<IActionResult> OnGetAsync(int id, CancellationToken cancellationToken)
    {
        var announcement = await _db.Announcements.FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (announcement is null)
        {
            return NotFound();
        }

        if (!AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup))
        {
            return Forbid();
        }

        Announcement = announcement;

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(int id, CancellationToken cancellationToken)
    {
        var announcement = await _db.Announcements
            .AsTracking()
            .FirstOrDefaultAsync(a => a.Id == id, cancellationToken);

        if (announcement is null)
        {
            // Кто-то уже удалил — считаем, что цель достигнута.
            return RedirectToPage("Index");
        }

        if (!AnnouncementPermissions.CanModify(User, announcement, _adOptions.AdminGroup))
        {
            return Forbid();
        }

        try
        {
            _db.Announcements.Remove(announcement);
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Не удалось удалить объявление {Id}.", id);

            Announcement = announcement;
            ErrorMessage = "Не удалось удалить объявление: база данных недоступна.";

            return Page();
        }

        _logger.LogInformation(
            "Пользователь {User} удалил объявление {Id} (автор {Author}).",
            User.Identity?.Name, id, announcement.AuthorUserName);

        TempData["StatusMessage"] = "Объявление удалено.";

        return RedirectToPage("Index");
    }
}
