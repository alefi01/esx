using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Security;

namespace Portal.Web.Pages;

/// <summary>
/// Главная страница портала. Закрыта политикой по умолчанию (см. Program.cs, п. 4):
/// неаутентифицированного пользователя сюда не пустят — его перебросит на форму входа.
/// </summary>
public class IndexModel : PageModel
{
    private readonly ActiveDirectoryOptions _adOptions;

    public IndexModel(IOptions<ActiveDirectoryOptions> adOptions) => _adOptions = adOptions.Value;

    public string UserName => User.Identity?.Name ?? "—";

    public string DisplayName =>
        User.FindFirstValue(ClaimTypes.GivenName) is { Length: > 0 } value ? value : UserName;

    public string? Email => User.FindFirstValue(ClaimTypes.Email);

    public string OfficeName =>
        User.FindFirstValue(PortalClaimTypes.OfficeName) is { Length: > 0 } value ? value : "не определён";

    public string AuthenticatedBy =>
        User.FindFirstValue(PortalClaimTypes.AuthenticatedBy) is { Length: > 0 } value ? value : "—";

    public bool IsAdmin => User.IsInRole(_adOptions.AdminGroup);

    public string AdminGroupName => _adOptions.AdminGroup;

    /// <summary>Все группы AD пользователя: в cookie они лежат как роли.</summary>
    public IReadOnlyList<string> Groups =>
        User.FindAll(ClaimTypes.Role)
            .Select(c => c.Value)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();
}
