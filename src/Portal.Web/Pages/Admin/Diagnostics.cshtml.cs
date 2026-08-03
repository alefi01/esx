using System.Diagnostics;
using System.Net.Sockets;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services;
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
    private readonly DatabaseStatus _databaseStatus;
    private readonly PortalDbContext _db;
    private readonly Portal.Web.Services.Storage.FileStorage _fileStorage;

    public DiagnosticsModel(
        IOfficeResolver offices,
        IOptions<ActiveDirectoryOptions> ad,
        DatabaseStatus databaseStatus,
        PortalDbContext db,
        Portal.Web.Services.Storage.FileStorage fileStorage)
    {
        _offices = offices;
        _ad = ad.Value;
        _databaseStatus = databaseStatus;
        _db = db;
        _fileStorage = fileStorage;
    }

    public sealed record DcProbe(string Host, int Port, bool Reachable, long ElapsedMs, string? Error);

    public string? RemoteIp { get; private set; }
    public string DetectedOffice { get; private set; } = "не определён";
    public IReadOnlyList<string> DomainControllerOrder { get; private set; } = [];
    public List<DcProbe> Probes { get; } = [];
    public ActiveDirectoryOptions AdOptions => _ad;
    public IReadOnlyList<OfficeDefinition> Offices => _offices.All;

    /// <summary>Состояние базы данных на момент запуска приложения.</summary>
    public DatabaseStatus Database => _databaseStatus;

    /// <summary>Отвечает ли база прямо сейчас (проверяется при открытии страницы).</summary>
    public bool DatabaseRespondsNow { get; private set; }

    /// <summary>Ошибка текущей проверки базы, если она не прошла.</summary>
    public string? DatabaseLiveError { get; private set; }

    /// <summary>Сколько объявлений лежит в базе — заодно подтверждает, что таблица создана.</summary>
    public int? AnnouncementCount { get; private set; }

    /// <summary>Путь к файловому хранилищу и доступно ли оно на запись.</summary>
    public string StorageRoot => _fileStorage.RootPath;
    public bool StorageOk { get; private set; }
    public string? StorageError { get; private set; }
    public int? FileCount { get; private set; }
    public long? StorageUsedBytes { get; private set; }

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

        await ProbeDatabaseAsync(cancellationToken);

        // Проверяем именно запись: прав на чтение может хватать,
        // а на запись — нет, и выяснится это при первой же загрузке файла.
        (StorageOk, StorageError) = _fileStorage.Probe();

        if (DatabaseRespondsNow)
        {
            try
            {
                FileCount = await _db.Files.CountAsync(f => f.DeletedAt == null, cancellationToken);
                StorageUsedBytes = await _db.Files.SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;
            }
            catch
            {
                // Таблицы могло ещё не быть — не повод ронять всю диагностику.
            }
        }
    }

    /// <summary>
    /// Состояние на момент запуска могло устареть: базу могли починить или,
    /// наоборот, остановить уже после старта приложения. Поэтому проверяем ещё и сейчас.
    /// </summary>
    private async Task ProbeDatabaseAsync(CancellationToken cancellationToken)
    {
        try
        {
            DatabaseRespondsNow = await _db.Database.CanConnectAsync(cancellationToken);

            if (DatabaseRespondsNow)
            {
                AnnouncementCount = await _db.Announcements.CountAsync(cancellationToken);
            }
        }
        catch (Exception ex)
        {
            DatabaseRespondsNow = false;
            DatabaseLiveError = ex.Message;
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
