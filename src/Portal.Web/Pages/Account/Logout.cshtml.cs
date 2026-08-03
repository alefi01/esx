using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Web.Pages.Account;

/// <summary>
/// Выход из системы.
///
/// Обрабатывается только POST-запросом. Это важно: если сделать выход по ссылке (GET),
/// то любая картинка с адресом /Account/Logout на постороннем сайте будет
/// выкидывать ваших пользователей из портала. POST-форма защищена antiforgery-токеном.
/// </summary>
public class LogoutModel : PageModel
{
    private readonly ILogger<LogoutModel> _logger;

    public LogoutModel(ILogger<LogoutModel> logger) => _logger = logger;

    // Заход по адресу выхода вручную не должен ничего ломать — просто вернём на главную.
    public IActionResult OnGet() => RedirectToPage("/Index");

    public async Task<IActionResult> OnPostAsync()
    {
        var userName = User.Identity?.Name;

        // Удаляет cookie аутентификации у браузера.
        await HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);

        _logger.LogInformation("Пользователь {User} вышел из системы.", userName);

        return RedirectToPage("/Account/Login");
    }
}
