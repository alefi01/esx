using System.DirectoryServices.Protocols;
using System.Net;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Services.Offices;

namespace Portal.Web.Services.ActiveDirectory;

/// <summary>
/// Аутентификация в Active Directory по протоколу LDAP.
///
/// Как это работает, по шагам:
///
/// 1. Логин приводится к короткому виду (sAMAccountName): из "DOMEN\ivanov" и
///    "ivanov@domen.pro" получается "ivanov".
///
/// 2. Строится список контроллеров домена в порядке «сначала свой офис».
///    Это важно: у вас между офисами канал с урезанным MTU, и гонять туда
///    аутентификацию каждого входа незачем.
///
/// 3. К первому доступному контроллеру выполняется bind — операция «представься».
///    Именно она и проверяет пароль: если пароль неверный, контроллер вернёт ошибку 49.
///    Никакой «своей» проверки паролей у нас нет и быть не должно —
///    портал вообще не хранит пароли.
///
/// 4. По тому же соединению, уже от имени вошедшего пользователя, читаются его
///    атрибуты (ФИО, почта) и список групп. Отдельная «сервисная учётка» с паролем
///    в конфиге не нужна — это сознательное решение: один секрет, которого нет,
///    это один секрет, который нельзя украсть.
///
/// 5. Имена групп возвращаются наверх и становятся ролями в cookie.
/// </summary>
public sealed class LdapAdAuthenticationService : IAdAuthenticationService
{
    // OID серверного правила LDAP_MATCHING_RULE_IN_CHAIN.
    // Заставляет контроллер домена самостоятельно раскрыть вложенные группы
    // (группа входит в группу входит в группу) за один запрос.
    private const string MatchingRuleInChain = "1.2.840.113556.1.4.1941";

    // Код результата LDAP «invalidCredentials» (RFC 4511, п. 4.1.9).
    // В перечислении ResultCode этого значения нет, поэтому задаём числом.
    private const int InvalidCredentialsResultCode = 49;

    // Из сообщения об ошибке AD вида "... comment: AcceptSecurityContext error, data 52e, v4563"
    // достаём код "52e" — по нему понятно, что именно не так с учёткой.
    private static readonly Regex SubErrorCodeRegex =
        new(@"data\s+(?<code>[0-9a-fA-F]+)", RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private readonly ActiveDirectoryOptions _options;
    private readonly IOfficeResolver _offices;
    private readonly ILogger<LdapAdAuthenticationService> _logger;

    public LdapAdAuthenticationService(
        IOptions<ActiveDirectoryOptions> options,
        IOfficeResolver offices,
        ILogger<LdapAdAuthenticationService> logger)
    {
        _options = options.Value;
        _offices = offices;
        _logger = logger;
    }

    public Task<AdAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        string? officeCode,
        CancellationToken cancellationToken = default)
    {
        // Библиотека System.DirectoryServices.Protocols синхронная: асинхронного Bind в ней нет.
        // Чтобы не занимать поток, обслуживающий HTTP-запрос, на время сетевого ожидания,
        // выносим работу в пул потоков. При 20 пользователях это с запасом.
        return Task.Run(() => Authenticate(userName, password, officeCode), cancellationToken);
    }

    private AdAuthenticationResult Authenticate(string rawUserName, string password, string? officeCode)
    {
        var samAccountName = NormalizeUserName(rawUserName);

        if (string.IsNullOrWhiteSpace(samAccountName) || string.IsNullOrEmpty(password))
        {
            // Пустой пароль отсекаем здесь. LDAP на пустой пароль отвечает
            // «успешно» (это так называемый unauthenticated bind) — и без этой
            // проверки в портал можно было бы войти вообще без пароля.
            return AdAuthenticationResult.Failure(
                AdAuthenticationStatus.InvalidCredentials, null, "Пустой логин или пароль.");
        }

        var domainControllers = _offices.GetDomainControllerOrder(officeCode);

        if (domainControllers.Count == 0)
        {
            return AdAuthenticationResult.Failure(
                AdAuthenticationStatus.ServerUnavailable, null,
                "В конфигурации не указан ни один контроллер домена " +
                "(секции Offices:Items:DomainControllers и ActiveDirectory:FallbackDomainControllers пусты).");
        }

        AdAuthenticationResult? lastTransportFailure = null;

        foreach (var dc in domainControllers)
        {
            try
            {
                return AuthenticateAgainst(dc, samAccountName, password);
            }
            catch (LdapException ex) when (ex.ErrorCode == InvalidCredentialsResultCode)
            {
                // 49 — «неверные учётные данные». Ответ авторитетный: обходить остальные
                // контроллеры смысла нет, при неверном пароле DC сам сходит к владельцу
                // роли PDC-эмулятора и проверит, не сменили ли пароль только что.
                var status = ClassifyCredentialError(ex);

                _logger.LogInformation(
                    "Отказ во входе для {User} на {Dc}: {Status}",
                    samAccountName, dc, status);

                return AdAuthenticationResult.Failure(status, dc, ex.ServerErrorMessage ?? ex.Message);
            }
            catch (Exception ex) when (ex is LdapException or DirectoryOperationException or InvalidOperationException)
            {
                // Сеть, таймаут, DNS, недоступный контроллер — пробуем следующий.
                _logger.LogWarning(ex,
                    "Контроллер домена {Dc} недоступен, пробую следующий из списка.", dc);

                lastTransportFailure = AdAuthenticationResult.Failure(
                    AdAuthenticationStatus.ServerUnavailable, dc, ex.Message);
            }
        }

        _logger.LogError(
            "Ни один контроллер домена не ответил. Пробовали: {Dcs}",
            string.Join(", ", domainControllers));

        return lastTransportFailure ?? AdAuthenticationResult.Failure(
            AdAuthenticationStatus.ServerUnavailable, null, "Ни один контроллер домена не ответил.");
    }

    private AdAuthenticationResult AuthenticateAgainst(string domainController, string samAccountName, string password)
    {
        using var connection = CreateConnection(domainController);

        connection.Bind(BuildCredential(samAccountName, password));   // ← вот здесь проверяется пароль

        // Дальше все запросы идут по этому же соединению от имени пользователя.
        var user = ReadUser(connection, samAccountName);

        if (user is null)
        {
            // Ситуация редкая: пароль подошёл, но учётку не видно в заданном BaseDn.
            // Обычно это значит, что BaseDn сужен до OU, в котором пользователя нет.
            return AdAuthenticationResult.Failure(
                AdAuthenticationStatus.Error, domainController,
                $"Bind прошёл, но учётная запись '{samAccountName}' не найдена в {_options.BaseDn}. " +
                "Проверьте настройку ActiveDirectory:BaseDn.");
        }

        _logger.LogInformation(
            "Вход выполнен: {User} через {Dc}, групп получено: {GroupCount}",
            user.SamAccountName, domainController, user.Groups.Count);

        return AdAuthenticationResult.Success(user, domainController);
    }

    /// <summary>Настраивает LDAP-соединение, но ещё не подключается — подключение произойдёт при Bind.</summary>
    private LdapConnection CreateConnection(string domainController)
    {
        var identifier = new LdapDirectoryIdentifier(
            domainController,
            _options.Port,
            fullyQualifiedDnsHostName: false,
            connectionless: false);   // connectionless=true — это LDAP поверх UDP (CLDAP);
                                      // на канале с урезанным MTU он как раз и теряет пакеты, нам он не нужен

        var connection = new LdapConnection(identifier)
        {
            AuthType = _options.AuthMode == LdapAuthMode.Basic ? AuthType.Basic : AuthType.Negotiate,
            AutoBind = false,
            Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds)
        };

        connection.SessionOptions.ProtocolVersion = 3;

        // Не ходить по «отсылкам» (referrals). Без этого запрос к контроллеру своего офиса
        // может незаметно уехать в другой офис по узкому каналу и подвиснуть.
        connection.SessionOptions.ReferralChasing = ReferralChasingOptions.None;

        if (_options.UseSsl)
        {
            connection.SessionOptions.SecureSocketLayer = true;
        }
        else if (_options.AuthMode == LdapAuthMode.Negotiate
                 && _options.UseSigningAndSealing
                 && OperatingSystem.IsWindows())
        {
            // Подпись и шифрование LDAP-трафика ключами Kerberos/NTLM.
            // Даёт защиту канала без сертификатов; свойства доступны только на Windows.
            connection.SessionOptions.Signing = true;
            connection.SessionOptions.Sealing = true;
        }

        return connection;
    }

    private NetworkCredential BuildCredential(string samAccountName, string password) =>
        _options.AuthMode == LdapAuthMode.Basic
            // simple bind: имя пользователя должно быть UPN или полным DN
            ? new NetworkCredential($"{samAccountName}@{_options.DomainFqdn}", password)
            // Negotiate: имя и домен передаются раздельно
            : new NetworkCredential(samAccountName, password, _options.DomainFqdn);

    private AdUserInfo? ReadUser(LdapConnection connection, string samAccountName)
    {
        var searchTimeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);

        // objectCategory=person + objectClass=user — стандартный способ найти именно учётку
        // пользователя, не задев компьютеры и контакты. objectCategory индексирован, поиск быстрый.
        var userFilter =
            $"(&(objectCategory=person)(objectClass=user)(sAMAccountName={LdapEscaper.EscapeFilterValue(samAccountName)}))";

        var userRequest = new SearchRequest(
            _options.BaseDn,
            userFilter,
            SearchScope.Subtree,
            "distinguishedName", "displayName", "mail", "sAMAccountName");

        var userResponse = (SearchResponse)connection.SendRequest(userRequest, searchTimeout);

        if (userResponse.Entries.Count == 0)
        {
            return null;
        }

        var entry = userResponse.Entries[0];

        var distinguishedName = GetAttribute(entry, "distinguishedName") ?? entry.DistinguishedName;
        var actualSam = GetAttribute(entry, "sAMAccountName") ?? samAccountName;
        var displayName = GetAttribute(entry, "displayName");
        var email = GetAttribute(entry, "mail");

        var groups = ReadGroups(connection, distinguishedName, searchTimeout);

        return new AdUserInfo(
            actualSam,
            string.IsNullOrWhiteSpace(displayName) ? actualSam : displayName,
            email,
            distinguishedName,
            groups);
    }

    private List<string> ReadGroups(LdapConnection connection, string userDn, TimeSpan searchTimeout)
    {
        var escapedDn = LdapEscaper.EscapeFilterValue(userDn);

        // Вариант с MatchingRuleInChain перекладывает раскрытие вложенных групп на контроллер домена:
        // один запрос вместо рекурсивного обхода с нашей стороны.
        var groupFilter = _options.IncludeNestedGroups
            ? $"(&(objectCategory=group)(member:{MatchingRuleInChain}:={escapedDn}))"
            : $"(&(objectCategory=group)(member={escapedDn}))";

        var request = new SearchRequest(
            _options.BaseDn,
            groupFilter,
            SearchScope.Subtree,
            "sAMAccountName");

        var response = (SearchResponse)connection.SendRequest(request, searchTimeout);

        var groups = new List<string>(response.Entries.Count);

        foreach (SearchResultEntry groupEntry in response.Entries)
        {
            var name = GetAttribute(groupEntry, "sAMAccountName");

            if (!string.IsNullOrWhiteSpace(name))
            {
                groups.Add(name);
            }
        }

        // Замечание на будущее: сюда НЕ попадает «основная группа» пользователя
        // (обычно Domain Users) — в AD она хранится отдельно, атрибутом primaryGroupID,
        // а не записью в member. Права на портале мы на Domain Users не выдаём,
        // так что это не мешает; но если однажды понадобится — знайте, где искать.
        return groups;
    }

    /// <summary>Безопасно достаёт строковое значение атрибута: у отсутствующих атрибутов возвращает null.</summary>
    private static string? GetAttribute(SearchResultEntry entry, string attributeName)
    {
        if (!entry.Attributes.Contains(attributeName))
        {
            return null;
        }

        var values = entry.Attributes[attributeName].GetValues(typeof(string));

        return values.Length > 0 ? values[0] as string : null;
    }

    /// <summary>
    /// Разбирает «подкод» ошибки 49, чтобы отличить неверный пароль от проблем с самой учёткой.
    /// Подкод AD присылает только при simple bind (AuthMode=Basic); при Negotiate его нет,
    /// и всё сводится к «неверные учётные данные».
    /// </summary>
    private static AdAuthenticationStatus ClassifyCredentialError(LdapException ex)
    {
        var message = ex.ServerErrorMessage ?? ex.Message ?? string.Empty;
        var match = SubErrorCodeRegex.Match(message);

        if (!match.Success)
        {
            return AdAuthenticationStatus.InvalidCredentials;
        }

        return match.Groups["code"].Value.ToLowerInvariant() switch
        {
            "525" => AdAuthenticationStatus.InvalidCredentials,  // пользователь не найден
            "52e" => AdAuthenticationStatus.InvalidCredentials,  // неверный пароль
            "530" => AdAuthenticationStatus.AccountRestricted,   // вход запрещён в это время суток
            "531" => AdAuthenticationStatus.AccountRestricted,   // вход запрещён с этой рабочей станции
            "532" => AdAuthenticationStatus.AccountRestricted,   // истёк срок действия пароля
            "533" => AdAuthenticationStatus.AccountRestricted,   // учётная запись отключена
            "701" => AdAuthenticationStatus.AccountRestricted,   // истёк срок действия учётной записи
            "773" => AdAuthenticationStatus.AccountRestricted,   // требуется смена пароля
            "775" => AdAuthenticationStatus.AccountRestricted,   // учётная запись заблокирована
            _ => AdAuthenticationStatus.InvalidCredentials
        };
    }

    /// <summary>
    /// Приводит логин к формату sAMAccountName: "DOMEN\ivanov" и "ivanov@domen.pro" → "ivanov".
    /// Пользователи вводят логин по-разному, и заставлять их помнить один правильный формат незачем.
    /// </summary>
    private string NormalizeUserName(string userName)
    {
        var value = (userName ?? string.Empty).Trim();

        var backslash = value.IndexOf('\\');

        if (backslash >= 0)
        {
            value = value[(backslash + 1)..];
        }

        var at = value.IndexOf('@');

        if (at >= 0)
        {
            value = value[..at];
        }

        return value;
    }
}
