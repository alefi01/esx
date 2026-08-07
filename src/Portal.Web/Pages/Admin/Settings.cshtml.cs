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
    private readonly PortalCleanup _cleanup;

    public SettingsModel(PortalSettings settings, StorageUsage usage, PortalCleanup cleanup)
    {
        _settings = settings;
        _usage = usage;
        _cleanup = cleanup;
    }

    /// <summary>Сколько занято сейчас — чтобы вводимый предел было с чем сравнить.</summary>
    public StorageUsageInfo Usage { get; private set; } = new(0, 0, 0, Known: false);

    /// <summary>Что накопилось и что уйдёт при уборке.</summary>
    public IReadOnlyList<CleanupLine> Cleanup { get; private set; } = [];

    public static string Size(long bytes) => UploadValidator.Format(bytes);

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

        /// <summary>
        /// Сроки хранения, дни. Ноль означает «не убирать вовсе» и стоит
        /// по умолчанию: портал не должен начать удалять данные оттого,
        /// что его обновили.
        ///
        /// Верхняя граница в 10 лет — защита от опечатки, а не правило.
        /// </summary>
        [Display(Name = "Сообщения в переписках, дней")]
        [Range(0, 3650, ErrorMessage = "Срок — от 0 до 3650 дней")]
        public int MessageDays { get; set; }

        [Display(Name = "Объявления, дней")]
        [Range(0, 3650, ErrorMessage = "Срок — от 0 до 3650 дней")]
        public int AnnouncementDays { get; set; }

        [Display(Name = "Корзина, дней")]
        [Range(0, 3650, ErrorMessage = "Срок — от 0 до 3650 дней")]
        public int TrashDays { get; set; }

        [Display(Name = "Журнал действий, дней")]
        [Range(0, 3650, ErrorMessage = "Срок — от 0 до 3650 дней")]
        public int AuditDays { get; set; }
    }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        await LoadAsync(cancellationToken);

        var (messages, announcements, trash, audit) = await _cleanup.DaysAsync(cancellationToken);

        Input.TotalCapacityGb = await _settings.TotalCapacityGbAsync(cancellationToken);
        Input.MessageDays = messages;
        Input.AnnouncementDays = announcements;
        Input.TrashDays = trash;
        Input.AuditDays = audit;
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Usage = await _usage.GetAsync(cancellationToken);

        try
        {
            Cleanup = await _cleanup.PreviewAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Сводка — справка, а не действие. База не ответила — страница
            // всё равно должна открыться и дать сохранить настройки.
            Cleanup = [];
        }
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!ModelState.IsValid)
        {
            await LoadAsync(cancellationToken);

            return Page();
        }

        var who = User.Identity?.Name ?? "";

        await _settings.SetTotalCapacityGbAsync(Input.TotalCapacityGb, who, cancellationToken);

        await _settings.SetIntAsync(PortalCleanup.MessageDaysKey, Input.MessageDays, who, cancellationToken);
        await _settings.SetIntAsync(PortalCleanup.AnnouncementDaysKey, Input.AnnouncementDays, who, cancellationToken);
        await _settings.SetIntAsync(PortalCleanup.TrashDaysKey, Input.TrashDays, who, cancellationToken);
        await _settings.SetIntAsync(PortalCleanup.AuditDaysKey, Input.AuditDays, who, cancellationToken);

        StatusMessage = "Настройки сохранены.";

        return RedirectToPage();
    }

    /// <summary>
    /// «Убрать сейчас» — не дожидаясь фоновой уборки.
    ///
    /// Действует по ТЕМ ЖЕ срокам, что и фоновая: кнопка не убирает больше
    /// и не убирает раньше, она только не заставляет ждать. Иначе получилось
    /// бы два разных правила удаления, и однажды кнопка снесла бы не то.
    /// </summary>
    public async Task<IActionResult> OnPostCleanupAsync(CancellationToken cancellationToken)
    {
        StatusMessage = await _cleanup.RunAsync(User.Identity?.Name ?? "", cancellationToken);

        return RedirectToPage();
    }
}
