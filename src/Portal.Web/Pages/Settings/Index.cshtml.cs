using Microsoft.AspNetCore.Mvc.RazorPages;
using Portal.Web.Services.Client;

namespace Portal.Web.Pages.Settings;

/// <summary>
/// Личные настройки — свои у каждого.
///
/// Не путать с «Настройкой портала»: та про весь портал и доступна
/// администратору, эта про себя и доступна всем. Разделение простое:
/// если настройка меняет что-то для коллег — она там, если только
/// для себя — здесь.
///
/// Две настройки, которые здесь есть, живут в браузере, поэтому читать
/// и сохранять модели нечего. Единственное, что она выясняет, — выложен
/// ли на портале настольный виджет: ссылку на то, чего нет, показывать
/// нельзя.
/// </summary>
public class IndexModel : PageModel
{
    private readonly IWebHostEnvironment _environment;

    public IndexModel(IWebHostEnvironment environment) => _environment = environment;

    /// <summary>Администратор выложил файл виджета — значит его можно скачать.</summary>
    public bool WidgetReady { get; private set; }

    public void OnGet() => WidgetReady = ClientDownloads.WidgetReady(_environment);
}
