using System.Diagnostics;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace Portal.Web.Pages;

/// <summary>
/// Страница необработанной ошибки.
///
/// Пользователю показываем только идентификатор запроса — по нему ошибку
/// можно найти в журнале. Текст исключения и стек на страницу НЕ выводим:
/// это подсказка злоумышленнику и утечка внутренних путей.
/// </summary>
[ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
public class ErrorModel : PageModel
{
    public string? RequestId { get; private set; }

    public bool ShowRequestId => !string.IsNullOrEmpty(RequestId);

    public void OnGet() =>
        RequestId = Activity.Current?.Id ?? HttpContext.TraceIdentifier;
}
