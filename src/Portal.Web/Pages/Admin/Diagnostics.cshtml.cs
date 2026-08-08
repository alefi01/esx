using System.Security.Claims;
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
    private readonly Portal.Web.Security.AuthDiagnostics _authDiagnostics;
    private readonly Portal.Web.Services.ActiveDirectory.DirectoryBrowser _browser;
    private readonly IWebHostEnvironment _environment;

    public DiagnosticsModel(
        IOfficeResolver offices,
        IOptions<ActiveDirectoryOptions> ad,
        DatabaseStatus databaseStatus,
        PortalDbContext db,
        Portal.Web.Services.Storage.FileStorage fileStorage,
        Portal.Web.Security.AuthDiagnostics authDiagnostics,
        Portal.Web.Services.ActiveDirectory.DirectoryBrowser browser,
        IWebHostEnvironment environment)
    {
        _offices = offices;
        _ad = ad.Value;
        _databaseStatus = databaseStatus;
        _db = db;
        _fileStorage = fileStorage;
        _authDiagnostics = authDiagnostics;
        _browser = browser;
        _environment = environment;
    }

    // ------------------------------------------------------------------
    // Доступ: почему кому-то отказывают
    // ------------------------------------------------------------------

    public IReadOnlyList<Portal.Web.Security.AuthFailure> AuthFailures { get; private set; } = [];
    public long AuthFailureTotal { get; private set; }

    /// <summary>Пришла ли cookie входа с ЭТИМ запросом и какой длины.</summary>
    public bool AuthCookiePresent { get; private set; }
    public int AuthCookieLength { get; private set; }

    /// <summary>Чем портал признал текущего пользователя.</summary>
    public string AuthenticationType { get; private set; } = "не определён";

    /// <summary>
    /// Группы Active Directory текущего пользователя. В портале это и есть роли:
    /// своего списка пользователей он не ведёт. Раньше показывались на главной —
    /// переехали сюда, где им и место.
    /// </summary>
    public IReadOnlyList<string> Groups { get; private set; } = [];

    public string? Email { get; private set; }
    public string AuthenticatedBy { get; private set; } = "—";

    /// <summary>Папка с ключами шифрования cookie, число файлов и доступность на запись.</summary>
    public string KeysPath { get; private set; } = "";
    public int KeyFileCount { get; private set; }
    public bool KeysWritable { get; private set; }
    public string? KeysError { get; private set; }

    public sealed record DcProbe(string Host, int Port, bool Reachable, long ElapsedMs, string? Error);

    /// <summary>
    /// Состояние всего, от чего зависит признание пользователя вошедшим.
    ///
    /// Самое важное здесь — ключи шифрования cookie. Ими портал подписывает
    /// и расшифровывает вход. Если папка с ключами недоступна на запись,
    /// .NET молча создаёт временные ключи в памяти — и при каждом перезапуске
    /// рабочего процесса IIS все cookie разом перестают приниматься.
    /// Со стороны это выглядит как «портал случайно разлогинивает людей»
    /// и как отказы 401 на запросах уже открытой страницы.
    /// </summary>
    private void ProbeAccess()
    {
        AuthFailures = _authDiagnostics.Recent();
        AuthFailureTotal = _authDiagnostics.Total;

        var cookie = Request.Cookies["Portal.Auth"];

        AuthCookiePresent = cookie is not null;
        AuthCookieLength = cookie?.Length ?? 0;

        AuthenticationType = User.Identity?.AuthenticationType ?? "не определён";

        Groups = User.FindAll(System.Security.Claims.ClaimTypes.Role)
            .Select(c => c.Value)
            .OrderBy(v => v, StringComparer.OrdinalIgnoreCase)
            .ToList();

        Email = User.FindFirstValue(System.Security.Claims.ClaimTypes.Email);

        AuthenticatedBy = User.FindFirstValue(Portal.Web.Security.PortalClaimTypes.AuthenticatedBy)
            is { Length: > 0 } dc ? dc : "—";

        KeysPath = Path.Combine(_environment.ContentRootPath, "App_Data", "keys");

        try
        {
            if (!Directory.Exists(KeysPath))
            {
                KeysError = "папки нет — ключи создаются заново при каждом запуске";
                return;
            }

            KeyFileCount = Directory.GetFiles(KeysPath, "*.xml").Length;

            // Проверяем именно запись: прав на чтение может хватать,
            // а на запись — нет, и тогда новый ключ просто некуда положить.
            var probe = Path.Combine(KeysPath, $"probe-{Guid.NewGuid():N}.tmp");

            // System.IO.File целиком: внутри страницы короткое имя File
            // занято её собственным методом отдачи файла.
            System.IO.File.WriteAllText(probe, "проверка");
            System.IO.File.Delete(probe);

            KeysWritable = true;
        }
        catch (Exception ex)
        {
            KeysError = ex.Message;
        }
    }

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

    /// <summary>
    /// Что отвечает каталог на запрос обзора — по каждому контроллеру.
    /// Отсюда видно, почему окно выбора группы показывает пустое дерево.
    /// </summary>
    public IReadOnlyList<Portal.Web.Services.ActiveDirectory.BrowseProbe> BrowseProbes { get; private set; } = [];

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

        ProbeAccess();

        // Обзор каталога проверяется отдельно от входа: вход спрашивает
        // «правильный ли пароль у человека», обзор — «дают ли САМОМУ ПОРТАЛУ
        // читать дерево». Это разные вопросы и разные учётные записи,
        // и один может работать при сломанном другом.
        try
        {
            BrowseProbes = await Task.Run(_browser.Probe, cancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(25), cancellationToken);
        }
        catch (Exception)
        {
            // Проверка — часть страницы, а не сама страница: не получилось
            // спросить каталог, остальная диагностика всё равно нужна.
            BrowseProbes = [];
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
