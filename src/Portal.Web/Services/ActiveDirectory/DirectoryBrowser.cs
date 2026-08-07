using System.DirectoryServices.Protocols;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.ActiveDirectory;

/// <summary>Узел каталога: подразделение, группа или человек.</summary>
/// <param name="Kind">«ou» — подразделение, «group» — группа, «user» — человек.</param>
/// <param name="Name">Что видит человек.</param>
/// <param name="Dn">Полный путь в каталоге — по нему открывается вложенное.</param>
/// <param name="Account">Логин или короткое имя группы — то, что запишется в права. У подразделений пусто.</param>
public sealed record DirectoryNode(string Kind, string Name, string Dn, string Account);

/// <summary>
/// Ответ обзора: что нашлось и — если ничего — почему.
///
/// Причина нужна ровно потому, что пустое дерево ничего не объясняет.
/// «Не задан BaseDn», «ни один контроллер не ответил» и «в этой ветке
/// действительно пусто» выглядели одинаково: пустое окно. Разбираться
/// приходилось по журналу приложения на сервере.
/// </summary>
/// <param name="Nodes">Найденные узлы.</param>
/// <param name="Problem">Почему список пуст. null — всё в порядке.</param>
public sealed record DirectoryLevel(IReadOnlyList<DirectoryNode> Nodes, string? Problem = null);

/// <summary>
/// Обзор каталога домена: подразделения, вложенные подразделения, группы
/// и люди — по одному уровню за раз.
///
/// ЗАЧЕМ
///
/// Права на папку раньше выдавались вводом имени группы руками. Это работало,
/// пока имена групп помнили наизусть; на практике их подсматривают у коллег,
/// ошибаются в раскладке и получают «группа не найдена» без объяснений.
/// Здесь то же самое выбирается из дерева — как в оснастке «Пользователи
/// и компьютеры Active Directory», к которой люди привыкли.
///
/// ПОЧЕМУ ПО ОДНОМУ УРОВНЮ
///
/// В домене на несколько тысяч учётных записей запрос «дай всё дерево»
/// возвращает мегабайты и выполняется секунды. Раскрывают же обычно
/// две-три ветки. Поэтому каждый уровень запрашивается отдельно и только
/// когда его раскрыли, а ответ на минуту кладётся в память: по дереву
/// ходят вверх-вниз, и один и тот же уровень открывают по нескольку раз.
///
/// ЧТО ЗДЕСЬ НЕ ДЕЛАЕТСЯ
///
/// Ничего не меняется в домене — только чтение. Портал вообще не имеет
/// права писать в каталог, и заводить такое право ради удобной формы
/// было бы несоразмерно.
/// </summary>
public sealed class DirectoryBrowser
{
    /// <summary>Сколько узлов отдаём за раз. Больше в списке всё равно не выбрать глазами.</summary>
    private const int MaxNodes = 300;

    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Сколько ждём отклика ОДНОГО контроллера на уровне сети.
    ///
    /// Проверка отдельная и короткая, потому что таймаут самой LdapConnection
    /// считает время ЗАПРОСА, а не подключения: если контроллер выключен или
    /// имя не разрешается, соединение висит минутами, и окно выбора группы
    /// просто не открывается — без ошибки, без объяснения, без конца.
    /// Именно так это и выглядело: «дерево не подгружается».
    /// </summary>
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Общий предел на весь обзор. Даже если контроллеров в списке десяток,
    /// человек у окна ждёт секунды, а не минуты: не ответили — покажем
    /// причину и оставим ввод имени руками.
    /// </summary>
    private static readonly TimeSpan TotalBudget = TimeSpan.FromSeconds(12);

    private readonly ActiveDirectoryOptions _ad;
    private readonly Portal.Web.Services.Offices.IOfficeResolver _offices;
    private readonly IMemoryCache _cache;
    private readonly ILogger<DirectoryBrowser> _logger;

    public DirectoryBrowser(
        IOptions<ActiveDirectoryOptions> ad,
        Portal.Web.Services.Offices.IOfficeResolver offices,
        IMemoryCache cache,
        ILogger<DirectoryBrowser> logger)
    {
        _ad = ad.Value;
        _offices = offices;
        _cache = cache;
        _logger = logger;
    }

    /// <summary>Корень обзора — то, что задано в ActiveDirectory:BaseDn.</summary>
    public string RootDn => _ad.BaseDn ?? "";

    /// <summary>
    /// Что лежит непосредственно внутри указанного пути.
    ///
    /// Пустой путь означает корень (BaseDn). Порядок: сначала подразделения,
    /// потом группы, потом люди — сверху то, что раскрывают, снизу то,
    /// что выбирают.
    /// </summary>
    /// <summary>
    /// То же, что <see cref="Children"/>, но с ОБЩИМ пределом ожидания.
    ///
    /// Библиотека каталога синхронная и отменять начатое подключение
    /// не умеет, поэтому работа уходит в отдельный поток, а страница ждёт
    /// его не дольше отведённого. Зависший поток при этом досчитает сам
    /// и никого не задержит — а окно уже покажет, что каталог не ответил.
    /// </summary>
    public async Task<DirectoryLevel> ChildrenAsync(
        string? dn, string? query, CancellationToken cancellationToken)
    {
        try
        {
            return await Task.Run(() => Children(dn, query), cancellationToken)
                .WaitAsync(TotalBudget, cancellationToken);
        }
        catch (TimeoutException)
        {
            _logger.LogWarning("Обзор каталога не уложился в {Seconds} с.", TotalBudget.TotalSeconds);

            return new DirectoryLevel([],
                $"Каталог домена не ответил за {TotalBudget.TotalSeconds:0} с. "
                + "Имя группы можно вписать руками.");
        }
    }

    public DirectoryLevel Children(string? dn, string? query = null)
    {
        var baseDn = string.IsNullOrWhiteSpace(dn) ? RootDn : dn.Trim();

        if (string.IsNullOrWhiteSpace(baseDn))
        {
            return new DirectoryLevel([],
                "В настройках не задан корень каталога (ActiveDirectory:BaseDn). "
                + "Имя группы можно вписать руками.");
        }

        // Поиск идёт по ВСЕМУ поддереву, обзор — только по одному уровню:
        // человек, который начал печатать, ищет конкретное имя и не хочет
        // сам обходить ветки.
        var needle = (query ?? "").Trim();
        var key = "dirbrowse:" + baseDn + "\u0001" + needle.ToLowerInvariant();

        if (_cache.TryGetValue(key, out DirectoryLevel? cached) && cached is not null)
        {
            return cached;
        }

        var result = ReadAnyController(baseDn, needle);

        // В памяти держим только удачные ответы. Иначе минутная неудача
        // («контроллер перезагружался») запоминалась бы на минуту и после
        // восстановления связи окно всё равно оставалось бы пустым.
        if (result.Problem is null)
        {
            _cache.Set(key, result, CacheFor);
        }

        return result;
    }

    /// <summary>
    /// Обходит контроллеры домена по очереди, как это делает вход в портал.
    ///
    /// Список берётся ИЗ ТОГО ЖЕ места, что и при входе (офисы плюс общий
    /// запасной список). Раньше здесь читался только запасной список — и в
    /// сети, где контроллеры расписаны по офисам, обзор каталога не работал
    /// вовсе, хотя вход по тем же самым контроллерам работал прекрасно.
    /// </summary>
    private DirectoryLevel ReadAnyController(string baseDn, string needle)
    {
        var controllers = _offices.GetDomainControllerOrder(null);

        if (controllers.Count == 0)
        {
            return new DirectoryLevel([],
                "В настройках не указан ни один контроллер домена "
                + "(Offices:Items:DomainControllers и ActiveDirectory:FallbackDomainControllers пусты).");
        }

        string? lastError = null;

        foreach (var controller in controllers)
        {
            // Сначала короткая проверка «отзывается ли вообще», и только
            // потом настоящий запрос. Без неё один недоступный контроллер
            // в списке съедал всё ожидание.
            if (!Answers(controller))
            {
                lastError = $"{controller} не отвечает на порту {_ad.Port}";
                continue;
            }

            try
            {
                return new DirectoryLevel(Read(controller, baseDn, needle));
            }
            catch (Exception ex)
            {
                // Каталог недоступен — окно прав должно продолжать работать:
                // имя группы всегда можно вписать руками, как и раньше.
                _logger.LogWarning(ex,
                    "Не удалось прочитать каталог по пути {Dn} через контроллер {Controller}.",
                    baseDn, controller);

                lastError = ex.Message;
            }
        }

        return new DirectoryLevel([],
            "Каталог домена не ответил ни на одном контроллере"
            + (lastError is null ? "." : ": " + lastError));
    }

    /// <summary>Отзывается ли контроллер на своём порту за отведённые секунды.</summary>
    private bool Answers(string controller)
    {
        try
        {
            using var client = new System.Net.Sockets.TcpClient();

            return client.ConnectAsync(controller, _ad.Port).Wait(ProbeTimeout);
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Контроллер {Controller} не отозвался.", controller);

            return false;
        }
    }

    private IReadOnlyList<DirectoryNode> Read(string controller, string baseDn, string needle)
    {
        var identifier = new LdapDirectoryIdentifier(
            controller, _ad.Port, fullyQualifiedDnsHostName: false, connectionless: false);

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

        // Bind от имени процесса — учётной записи пула приложений IIS.
        connection.Bind();

        var searching = needle.Length > 0;
        var scope = searching ? SearchScope.Subtree : SearchScope.OneLevel;

        var escaped = LdapEscaper.EscapeFilterValue(needle);

        // Подразделения при поиске не показываем: ищут кого-то конкретного,
        // а не ветку. При обзоре — наоборот, ветки и нужны.
        var filter = searching
            ? "(&(|(objectClass=group)(&(objectCategory=person)(objectClass=user)))"
              + $"(|(sAMAccountName=*{escaped}*)(displayName=*{escaped}*)(cn=*{escaped}*)))"
            : "(|(objectClass=organizationalUnit)(objectClass=container)(objectClass=group)"
              + "(&(objectCategory=person)(objectClass=user)))";

        var request = new SearchRequest(
            baseDn, filter, scope,
            "objectClass", "distinguishedName", "sAMAccountName", "displayName", "cn", "ou",
            "userAccountControl")
        {
            SizeLimit = MaxNodes
        };

        var response = (SearchResponse)connection.SendRequest(request);

        var nodes = new List<DirectoryNode>();

        foreach (SearchResultEntry entry in response.Entries)
        {
            var classes = Values(entry, "objectClass");

            var isOu = classes.Contains("organizationalUnit", StringComparer.OrdinalIgnoreCase)
                       || classes.Contains("container", StringComparer.OrdinalIgnoreCase);

            var isGroup = classes.Contains("group", StringComparer.OrdinalIgnoreCase);
            var isUser = classes.Contains("user", StringComparer.OrdinalIgnoreCase) && !isGroup;

            if (!isOu && !isGroup && !isUser)
            {
                continue;
            }

            // Отключённые учётные записи пропускаем: выдавать доступ
            // уволенному незачем, а в списке он только мешает.
            if (isUser
                && int.TryParse(First(entry, "userAccountControl"), out var flags)
                && (flags & 0x0002) != 0)
            {
                continue;
            }

            var account = First(entry, "sAMAccountName");

            if (!isOu && string.IsNullOrWhiteSpace(account))
            {
                continue;
            }

            var name = First(entry, "displayName");

            if (string.IsNullOrWhiteSpace(name))
            {
                name = First(entry, "ou");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = First(entry, "cn");
            }

            if (string.IsNullOrWhiteSpace(name))
            {
                name = account;
            }

            nodes.Add(new DirectoryNode(
                isOu ? "ou" : isGroup ? "group" : "user",
                name,
                entry.DistinguishedName ?? "",
                account));
        }

        // Сначала ветки, потом группы, потом люди — и всё по алфавиту.
        var order = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["ou"] = 0,
            ["group"] = 1,
            ["user"] = 2
        };

        return nodes
            .OrderBy(n => order.GetValueOrDefault(n.Kind, 3))
            .ThenBy(n => n.Name, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private static string First(SearchResultEntry entry, string attribute) =>
        entry.Attributes.Contains(attribute) && entry.Attributes[attribute].Count > 0
            ? entry.Attributes[attribute][0]?.ToString() ?? ""
            : "";

    private static List<string> Values(SearchResultEntry entry, string attribute)
    {
        var result = new List<string>();

        if (!entry.Attributes.Contains(attribute))
        {
            return result;
        }

        foreach (var value in entry.Attributes[attribute])
        {
            result.Add(value?.ToString() ?? "");
        }

        return result;
    }
}
