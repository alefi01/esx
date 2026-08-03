using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Services.Offices;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Страница диагностики для администратора портала.
///
/// Отвечает на три вопроса, которые чаще всего возникают при разборе жалоб
/// «не могу войти»:
///   1. Каким видит адрес клиента само приложение (за IIS это не всегда очевидно).
///   2. В какой офис этот адрес попал и в каком порядке будут опрашиваться
///      контроллеры домена.
///   3. Отвечают ли эти контроллеры вообще, и за какое время.
///
/// Проверка связи — это обычное TCP-подключение к порту LDAP, без bind:
/// пароля у нас нет, да он и не нужен, чтобы отличить «сервер лежит»
/// от «пароль неверный».
///
/// Доступ ограничен группой администраторов: страница лежит в папке /Admin,
/// на которую в Program.cs навешана политика PortalPolicies.Admin.
/// </summary>
public class DiagnosticsModel : PageModel
{
    private readonly IOfficeResolver _offices;
    private readonly ActiveDirectoryOptions _ad;

    public DiagnosticsModel(IOfficeResolver offices, IOptions<ActiveDirectoryOptions> ad)
    {
        _offices = offices;
        _ad = ad.Value;
    }

    public sealed record DcProbe(string Host, int Port, bool Reachable, long ElapsedMs, string? Error);

    public string? RemoteIp { get; private set; }
    public string DetectedOffice { get; private set; } = "не определён";
    public IReadOnlyList<string> DomainControllerOrder { get; private set; } = [];
    public List<DcProbe> Probes { get; } = [];
    public ActiveDirectoryOptions AdOptions => _ad;
    public IReadOnlyList<OfficeDefinition> Offices => _offices.All;

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        var address = HttpContext.Connection.RemoteIpAddress;
        RemoteIp = address?.ToString();

        var office = _offices.ResolveByAddress(address);
        DetectedOffice = office is null ? "не определён" : $"{office.Name} ({office.Code})";

        DomainControllerOrder = _offices.GetDomainControllerOrder(office?.Code);

        foreach (var host in DomainControllerOrder)
        {
            Probes.Add(await ProbeAsync(host, _ad.Port, _ad.TimeoutSeconds, cancellationToken));
        }
    }

    private static async Task<DcProbe> ProbeAsync(string host, int port, int timeoutSeconds, CancellationToken ct)
    {
        var stopwatch = Stopwatch.StartNew();

        try
        {
            using var client = new TcpClient();

            // Свой таймаут: без него неотвечающий узел держал бы страницу
            // до системного таймаута TCP — это около минуты.
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(timeoutSeconds));

            await client.ConnectAsync(host, port, timeout.Token);

            stopwatch.Stop();

            return new DcProbe(host, port, true, stopwatch.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            stopwatch.Stop();

            var reason = ex is OperationCanceledException
                ? $"таймаут {timeoutSeconds} с"
                : ex.Message;

            return new DcProbe(host, port, false, stopwatch.ElapsedMilliseconds, reason);
        }
    }
}
