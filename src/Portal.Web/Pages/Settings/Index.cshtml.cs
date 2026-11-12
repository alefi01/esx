using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Services.Client;
using Portal.Web.Services.Users;

namespace Portal.Web.Pages.Settings;

/// <summary>
/// Личные настройки — свои у каждого.
///
/// Не путать с «Настройкой портала»: та про весь портал и доступна
/// администратору, эта про себя и доступна всем. Разделение простое:
/// если настройка меняет что-то для коллег — она там, если только
/// для себя — здесь.
///
/// Облегчённый режим живёт в браузере, и модели про него знать нечего.
/// А вот фотография — файл на сервере, её кладёт и убирает эта страница.
/// </summary>
public class IndexModel : PageModel
{
    private readonly IWebHostEnvironment _environment;
    private readonly AvatarStore _avatars;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(
        IWebHostEnvironment environment, AvatarStore avatars, ILogger<IndexModel> logger)
    {
        _environment = environment;
        _avatars = avatars;
        _logger = logger;
    }

    private string UserName => User.Identity?.Name ?? "";

    /// <summary>Администратор выложил файл виджета — значит его можно скачать.</summary>
    public bool WidgetReady { get; private set; }

    /// <summary>У человека уже стоит своя фотография.</summary>
    public bool HasAvatar { get; private set; }

    [TempData] public string? StatusMessage { get; set; }
    [TempData] public string? ErrorMessage { get; set; }

    public void OnGet()
    {
        WidgetReady = ClientDownloads.WidgetReady(_environment);
        HasAvatar = _avatars.Has(UserName);
    }

    /// <summary>Поставить себе фотографию.</summary>
    public async Task<IActionResult> OnPostAvatarAsync(
        IFormFile? photo, CancellationToken cancellationToken)
    {
        if (photo is null)
        {
            ErrorMessage = "Выберите файл с фотографией.";

            return RedirectToPage();
        }

        // Проверяем до того, как что-то читать: незачем тащить на сервер
        // двадцать мегабайт, чтобы потом сказать «слишком большой».
        var rejection = AvatarStore.Reject(photo.FileName, photo.Length);

        if (rejection is not null)
        {
            ErrorMessage = "Не подошло: " + rejection + ".";

            return RedirectToPage();
        }

        try
        {
            await using var content = photo.OpenReadStream();

            await _avatars.SaveAsync(UserName, content, photo.FileName, cancellationToken);

            StatusMessage = "Фотография обновлена.";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Не удалось сохранить аватарку {User}.", UserName);

            ErrorMessage = "Не удалось сохранить фотографию. Сообщите администратору.";
        }

        return RedirectToPage();
    }

    /// <summary>Убрать фотографию — вернуться к кружку с буквами.</summary>
    public IActionResult OnPostAvatarRemove()
    {
        _avatars.Delete(UserName);

        StatusMessage = "Фотография убрана.";

        return RedirectToPage();
    }
}
