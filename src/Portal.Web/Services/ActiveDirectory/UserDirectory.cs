using System.DirectoryServices.Protocols;
using System.Net;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Offices;

namespace Portal.Web.Services.ActiveDirectory;

/// <summary>Сотрудник, которому можно написать.</summary>
/// <param name="UserName">Логин (sAMAccountName) — по нему всё и связывается.</param>
/// <param name="DisplayName">Как показывать: «Иванов Иван Иванович».</param>
public sealed record DirectoryUser(string UserName, string DisplayName);

/// <summary>
/// Справочник сотрудников. Вынесен в интерфейс по той же причине,
/// что и вход в портал: в тестах вместо живого Active Directory
/// подставляется заглушка, иначе каждый тест ждал бы ответа
/// от несуществующего контроллера домена.
/// </summary>
public interface IUserDirectory
{
    Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string? query, string exceptUserName, int take, CancellationToken cancellationToken);

    Task<string> DisplayNameAsync(string userName, CancellationToken cancellationToken);

    Task<bool> ExistsAsync(string userName, CancellationToken cancellationToken);
}

/// <summary>
/// Список сотрудников для переписки: кому вообще можно написать.
///
/// ОТКУДА БЕРЁТСЯ СПИСОК
///
/// Из Active Directory — участники группы доступа к порталу
/// (ActiveDirectory:AccessGroup, обычно WebUsers). Своего списка людей
/// портал не ведёт: любой такой список пришлось бы поддерживать руками,
/// и он немедленно разошёлся бы с действительностью.
///
/// ПОД КАКОЙ УЧЁТНОЙ ЗАПИСЬЮ СПРАШИВАЕМ
///
/// Под той, от имени которой работает сам сайт в IIS. На сервере, введённом
/// в домен, это учётная запись компьютера (DOMEN\SERVER$), и ей по умолчанию
/// разрешено читать каталог. Служебная учётная запись с паролем в настройках
/// НЕ нужна — а значит, нет и пароля, который можно забыть сменить или украсть.
///
/// ЕСЛИ КАТАЛОГ НЕ ОТВЕТИЛ
///
/// Тогда портал показывает тех, кто хотя бы раз в него входил: эти люди
/// ему и так известны. Переписка продолжает работать, просто в списке
/// не будет тех, кто ещё ни разу не заходил. Такое поведение выбрано
/// сознательно: полностью неработающий раздел хуже, чем неполный список.
///
/// КЭШ
///
/// Ответ каталога держится в памяти несколько минут. Двадцать человек,
/// набирающих имя по буквам, иначе устроили бы контроллеру домена
/// несколько запросов в секунду на ровном месте.
/// </summary>
public sealed class UserDirectory : IUserDirectory
{
    private readonly ActiveDirectoryOptions _ad;
    private readonly OfficesOptions _offices;
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;
    private readonly ILogger<UserDirectory> _logger;

    /// <summary>Насколько долго доверяем ранее полученному списку.</summary>
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(15);

    /// <summary>
    /// Насколько долго не трогаем каталог после неудачи.
    ///
    /// Без этой паузы недоступный контроллер домена превращался бы
    /// в тормоз на каждом запросе: страница ждала бы ответа по таймауту
    /// снова и снова. Лучше несколько минут показывать неполный список,
    /// чем держать людей у пустого экрана.
    /// </summary>
    private static readonly TimeSpan RetryAfterFailure = TimeSpan.FromMinutes(5);

    private static DateTimeOffset _failedAt;

    // Кэш общий на всё приложение, поэтому статический и под замком.
    private static readonly Lock CacheLock = new();
    private static List<DirectoryUser>? _cached;
    private static DateTimeOffset _cachedAt;

    public UserDirectory(
        IOptions<ActiveDirectoryOptions> ad,
        IOptions<OfficesOptions> offices,
        PortalDbContext db,
        TimeProvider time,
        ILogger<UserDirectory> logger)
    {
        _ad = ad.Value;
        _offices = offices.Value;
        _db = db;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Поиск сотрудников по части имени или логина.
    /// Пустой запрос возвращает начало списка — так удобнее начинать переписку.
    /// </summary>
    public async Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string? query, string exceptUserName, int take, CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);
        var needle = (query ?? "").Trim();

        var found = all
            .Where(u => !string.Equals(u.UserName, exceptUserName, StringComparison.OrdinalIgnoreCase))
            .Where(u => needle.Length == 0
                        || u.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || u.UserName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .OrderBy(u => u.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .Take(take)
            .ToList();

        return found;
    }

    /// <summary>Как показывать этого человека. Если в каталоге его нет — показываем логин.</summary>
    public async Task<string> DisplayNameAsync(string userName, CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);

        return all.FirstOrDefault(u =>
            string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? userName;
    }

    /// <summary>Есть ли такой человек в списке доступа к порталу.</summary>
    public async Task<bool> ExistsAsync(string userName, CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);

        return all.Any(u => string.Equals(u.UserName, userName, StringComparison.OrdinalIgnoreCase));
    }

    private async Task<IReadOnlyList<DirectoryUser>> AllAsync(CancellationToken cancellationToken)
    {
        var now = _time.GetUtcNow();

        lock (CacheLock)
        {
            if (_cached is not null && now - _cachedAt < CacheFor)
            {
                return _cached;
            }
        }

        // Если каталог только что не ответил, второй раз не ломимся.
        bool skipDirectory;

        lock (CacheLock)
        {
            skipDirectory = _failedAt != default && now - _failedAt < RetryAfterFailure;
        }

        var fromDirectory = skipDirectory ? [] : TryReadFromDirectory();

        // Известные порталу люди добавляются всегда: они точно существуют
        // и точно имеют доступ — иначе не смогли бы войти.
        var known = await _db.Set<UserSeenState>()
            .Select(s => new { s.UserName, s.DisplayName })
            .ToListAsync(cancellationToken);

        var merged = new Dictionary<string, DirectoryUser>(StringComparer.OrdinalIgnoreCase);

        foreach (var person in known)
        {
            if (!string.IsNullOrWhiteSpace(person.UserName))
            {
                merged[person.UserName] = new DirectoryUser(
                    person.UserName,
                    string.IsNullOrWhiteSpace(person.DisplayName) ? person.UserName : person.DisplayName);
            }
        }

        // Данные каталога главнее: там имя актуальнее, чем запомненное
        // при последнем входе.
        foreach (var person in fromDirectory)
        {
            merged[person.UserName] = person;
        }

        var result = merged.Values.ToList();

        lock (CacheLock)
        {
            _cached = result;
            _cachedAt = now;
        }

        return result;
    }

    /// <summary>
    /// Спрашивает у контроллера домена участников группы доступа.
    /// При любой неудаче возвращает пустой список и пишет причину в журнал:
    /// раздел переписки должен работать даже при недоступном каталоге.
    /// </summary>
    private List<DirectoryUser> TryReadFromDirectory()
    {
        var controllers = _offices.Items
            .SelectMany(o => o.DomainControllers)
            .Concat(_ad.FallbackDomainControllers)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (controllers.Count == 0)
        {
            return [];
        }

        foreach (var controller in controllers)
        {
            try
            {
                return ReadFrom(controller);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex,
                    "Не удалось получить список сотрудников с контроллера {Controller}.", controller);
            }
        }

        lock (CacheLock)
        {
            _failedAt = _time.GetUtcNow();
        }

        _logger.LogWarning(
            "Список сотрудников из Active Directory получить не удалось. " +
            "Показываем только тех, кто уже входил в портал; следующая попытка " +
            "не раньше чем через {Minutes} мин.", RetryAfterFailure.TotalMinutes);

        return [];
    }

    private List<DirectoryUser> ReadFrom(string domainController)
    {
        var identifier = new LdapDirectoryIdentifier(
            domainController, _ad.Port, fullyQualifiedDnsHostName: false, connectionless: false);

        using var connection = new LdapConnection(identifier)
        {
            AuthType = AuthType.Negotiate,
            AutoBind = false,
            Timeout = TimeSpan.FromSeconds(_ad.TimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (_ad.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (_ad.UseSigningAndSealing && OperatingSystem.IsWindows())
        {
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
        }

        // Bind без указания имени и пароля — от имени процесса,
        // то есть учётной записи пула приложений IIS.
        connection.Bind();

        var group = LdapEscaper.EscapeFilterValue(_ad.AccessGroup);

        // Сначала находим саму группу — нам нужен её полный путь (DN),
        // потому что членство ищется именно по нему.
        var groupRequest = new SearchRequest(
            _ad.BaseDn,
            $"(&(objectClass=group)(sAMAccountName={group}))",
            SearchScope.Subtree,
            "distinguishedName");

        var groupResponse = (SearchResponse)connection.SendRequest(groupRequest);

        if (groupResponse.Entries.Count == 0)
        {
            _logger.LogWarning("Группа доступа {Group} в каталоге не найдена.", _ad.AccessGroup);
            return [];
        }

        var groupDn = LdapEscaper.EscapeFilterValue(groupResponse.Entries[0].DistinguishedName);

        // 1.2.840.113556.1.4.1941 — правило «в том числе через вложенные группы».
        // Без него человек, состоящий в WebUsers через другую группу, в список не попадёт.
        var filter = _ad.IncludeNestedGroups
            ? $"(&(objectCategory=person)(objectClass=user)(memberOf:1.2.840.113556.1.4.1941:={groupDn}))"
            : $"(&(objectCategory=person)(objectClass=user)(memberOf={groupDn}))";

        var request = new SearchRequest(
            _ad.BaseDn, filter, SearchScope.Subtree,
            "sAMAccountName", "displayName", "cn", "userAccountControl");

        var response = (SearchResponse)connection.SendRequest(request);
        var result = new List<DirectoryUser>();

        foreach (SearchResultEntry entry in response.Entries)
        {
            var login = First(entry, "sAMAccountName");

            if (string.IsNullOrWhiteSpace(login))
            {
                continue;
            }

            // Отключённые учётные записи пропускаем: писать уволенному
            // сотруднику незачем. Второй бит признака как раз означает
            // «учётная запись отключена».
            var flags = First(entry, "userAccountControl");

            if (int.TryParse(flags, out var value) && (value & 0x0002) != 0)
            {
                continue;
            }

            var name = First(entry, "displayName");

            if (string.IsNullOrWhiteSpace(name))
            {
                name = First(entry, "cn");
            }

            result.Add(new DirectoryUser(
                login, string.IsNullOrWhiteSpace(name) ? login : name));
        }

        _logger.LogInformation(
            "Список сотрудников получен с {Controller}: {Count} человек.", domainController, result.Count);

        return result;
    }

    private static string First(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) && entry.Attributes[attribute].Count > 0
            ? entry.Attributes[attribute][0]?.ToString() ?? ""
            : "";

    /// <summary>Забыть запомненный список — например, после того как список групп поменяли.</summary>
    public static void ResetCache()
    {
        lock (CacheLock)
        {
            _cached = null;
            _failedAt = default;
        }
    }
}
