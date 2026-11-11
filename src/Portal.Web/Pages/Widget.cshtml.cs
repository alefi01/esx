using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Pages;

/// <summary>
/// Страница настольного виджета.
///
/// Сама по себе ничего не считает: всё, что показывает виджет, он берёт
/// у уже существующего адреса /api/notifications — того же, которым
/// живёт колокольчик в портале. Заводить для виджета вторую такую
/// выборку значило бы получить два разных ответа на один вопрос
/// «что нового».
/// </summary>
public class WidgetModel : PageModel
{
    private readonly BrandingOptions _branding;

    public WidgetModel(IOptions<BrandingOptions> branding) => _branding = branding.Value;

    /// <summary>Название портала в заголовке виджета.</summary>
    public string Title => string.IsNullOrWhiteSpace(_branding.Title) ? "Портал" : _branding.Title;

    /// <summary>Первая буква названия — для значка в заголовке.</summary>
    public string Letter => Title.TrimStart()[..1].ToUpperInvariant();
}
