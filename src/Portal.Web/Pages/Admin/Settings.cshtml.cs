using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Настройки портала целиком. Только для администраторов: доступ ко всей
/// папке /Admin закрыт политикой (см. Program.cs).
///
/// Здесь то, что меняют по ходу работы. Всё, что задаётся один раз
/// при установке — строка подключения, адреса контроллеров домена,
/// путь к хранилищу, — остаётся в appsettings.json: править его из браузера
/// значило бы дать возможность одним неверным полем уронить портал целиком.
/// </summary>
public class SettingsModel : PageModel
{
    private readonly PortalSettings _settings;
    private readonly StorageUsage _usage;

    public SettingsModel(PortalSettings settings, StorageUsage usage)
    {
        _settings = settings;
        _usage = usage;
    }

    /// <summary>Сколько занято сейчас — чтобы вводимый предел было с чем сравнить.</summary>
    public StorageUsageInfo Usage { get; private set; } = new(0, 0, 0, Known: false);

    [TempData]
    public string? StatusMessage { get; set; }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    public class InputModel
    {
        /// <summary>
        /// Сколько всего отведено под портал, гигабайты.
        ///
        /// Верхняя граница в миллион гигабайт — не ограничение по смыслу,
        /// а защита от опечатки: число из двадцати цифр не влезет в int,
        /// и без проверки страница ответила бы невнятной ошибкой разбора.
        /// </summary>
        [Display(Name = "Максимальный размер портала, ГБ")]
        [Range(0, 1_000_000, ErrorMessage = "Размер — от 0 до 1 000 000 ГБ")]
        public int TotalCapacityGb { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        Input.TotalCapacityGb = await _settings.TotalCapacityGbAsync(cancellationToken);
        Usage = await _usage.GetAsync(cancellationToken);
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            Usage = await _usage.GetAsync(cancellationToken);

            return Page();
        }

        await _settings.SetTotalCapacityGbAsync(
            Input.TotalCapacityGb, User.Identity?.Name ?? "", cancellationToken);

        StatusMessage = Input.TotalCapacityGb == 0
            ? "Предел снят: портал показывает занятое место без сравнения."
            : $"Максимальный размер портала — {Input.TotalCapacityGb} ГБ.";

        return RedirectToPage();
    }
}
