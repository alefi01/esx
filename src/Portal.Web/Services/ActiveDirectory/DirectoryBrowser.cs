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
/// Что произошло при обращении к ОДНОМУ контроллеру — для страницы
/// диагностики. Разбивает «дерево пустое» на понятные шаги: дошли ли
/// до контроллера, приняли ли нас, сколько записей он вернул.
/// </summary>
/// <param name="Controller">Контроллер домена.</param>
/// <param name="Reachable">Отозвался ли на своём порту.</param>
/// <param name="BoundAs">Под какой учётной записью работает приложение.</param>
/// <param name="Found">Сколько записей вернул обычный запрос обзора.</param>
/// <param name="Error">Текст ошибки, если запрос не удался.</param>
/// <param name="NamingContext">
/// Настоящий корень домена — тот, который называет сам контроллер
/// (defaultNamingContext из RootDSE). Сравнение с настроенным BaseDn
/// сразу показывает опечатку в настройках.
/// </param>
/// <param name="BaseFound">Существует ли объект, указанный в BaseDn.</param>
/// <param name="AnyChildren">
/// Сколько вообще объектов лежит на первом уровне BaseDn — запросом
/// без всякого отбора. Отличается от <paramref name="Found"/> только
/// в одном случае: каталог читается, но наш отбор ничего не выбирает.
/// </param>
public sealed record BrowseProbe(
    string Controller,
    bool Reachable,
    string BoundAs,
    int Found,
    string? Error,
    string NamingContext = "",
    bool BaseFound = false,
    int AnyChildren = 0);

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
        // Пустой путь означает «корень домена». Каким именно он окажется,
        // решается уже на контроллере: если BaseDn не задан или задан
        // неверно, корень спрашивается у самого каталога — см. RootFor.
        var baseDn = string.IsNullOrWhiteSpace(dn) ? RootDn : dn.Trim();

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

    /// <summary>
    /// Настоящий корень поиска.
    ///
    /// Если заданный путь существует — берём его. Если нет (или он вовсе
    /// не задан) — спрашиваем контроллер, что он считает корнем домена,
    /// и работаем оттуда. Портал при этом продолжает работать даже
    /// с опечаткой в настройках, а несоответствие видно на странице
    /// «Диагностика» и в журнале приложения.
    /// </summary>
    private string RootFor(LdapConnection connection, string baseDn)
    {
        if (!string.IsNullOrWhiteSpace(baseDn) && Exists(connection, baseDn))
        {
            return baseDn;
        }

        var discovered = DefaultNamingContext(connection);

        if (string.IsNullOrWhiteSpace(discovered))
        {
            return baseDn;
        }

        _logger.LogWarning(
            "Корень поиска «{Configured}» в каталоге не найден, используем «{Discovered}». "
            + "Проверьте настройку ActiveDirectory:BaseDn.",
            baseDn, discovered);

        return discovered;
    }

    private static bool Exists(LdapConnection connection, string dn)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(
                dn, "(objectClass=*)", SearchScope.Base, "distinguishedName"));

            return response.Entries.Count > 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static string DefaultNamingContext(LdapConnection connection)
    {
        try
        {
            var response = (SearchResponse)connection.SendRequest(new SearchRequest(
                "", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));

            return response.Entries.Count > 0
                ? First(response.Entries[0], "defaultNamingContext")
                : "";
        }
        catch (Exception)
        {
            return "";
        }
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

    /// <summary>
    /// Проверка обзора каталога по ВСЕМ контроллерам, для диагностики.
    ///
    /// В отличие от обычного обзора не останавливается на первом удачном:
    /// когда «дерево пустое», важно видеть картину целиком — до кого дошли,
    /// кто нас принял и сколько записей отдал. Пустой ответ без ошибки
    /// означает, что каталог нас принял, но ничего не показал: либо корень
    /// поиска (BaseDn) указывает не туда, либо учётной записи, под которой
    /// работает пул приложений, не разрешено читать каталог.
    /// </summary>
    public IReadOnlyList<BrowseProbe> Probe()
    {
        var identity = ProcessAccount();
        var result = new List<BrowseProbe>();

        foreach (var controller in _offices.GetDomainControllerOrder(null))
        {
            if (!Answers(controller))
            {
                result.Add(new BrowseProbe(controller, false, identity, 0,
                    $"не отвечает на порту {_ad.Port}"));

                continue;
            }

            result.Add(ProbeOne(controller, identity));
        }

        return result;
    }

    /// <summary>
    /// Разбор по шагам на одном контроллере.
    ///
    /// Шагов три, и вместе они отвечают на вопрос «почему дерево пустое»
    /// без гадания:
    ///
    /// 1. Что контроллер САМ считает корнем домена (defaultNamingContext).
    ///    Если это не то, что записано в BaseDn, — дальше можно не смотреть:
    ///    ищем не там. Ошибки при этом не будет: поиск в несуществующей
    ///    ветке иногда просто возвращает пустоту.
    /// 2. Существует ли объект, указанный в BaseDn.
    /// 3. Сколько объектов лежит на первом уровне — БЕЗ отбора и с нашим
    ///    отбором. Разница между этими двумя числами означает, что каталог
    ///    читается, а не выбирается ничего, — то есть дело в самом отборе,
    ///    а не в правах и не в связи.
    /// </summary>
    private BrowseProbe ProbeOne(string controller, string identity)
    {
        var namingContext = "";
        var baseFound = false;
        var anyChildren = 0;

        try
        {
            using var connection = Connect(controller);

            // Шаг 1. RootDSE — единственная ветка, которую отдают всем
            // и всегда. Если и она пуста, дело не в правах на дерево.
            try
            {
                var rootDse = (SearchResponse)connection.SendRequest(new SearchRequest(
                    "", "(objectClass=*)", SearchScope.Base, "defaultNamingContext"));

                if (rootDse.Entries.Count > 0)
                {
                    namingContext = First(rootDse.Entries[0], "defaultNamingContext");
                }
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "RootDSE на {Controller} прочитать не удалось.", controller);
            }

            // Шаг 2. Существует ли то, что записано в BaseDn.
            baseFound = !string.IsNullOrWhiteSpace(RootDn) && Exists(connection, RootDn);

            // Шаг 3. Первый уровень без отбора — по тому корню, которым
            // портал и пользуется (с поправкой на неверный BaseDn).
            var effective = RootFor(connection, RootDn);

            try
            {
                var all = (SearchResponse)connection.SendRequest(new SearchRequest(
                    effective, "(objectClass=*)", SearchScope.OneLevel, "distinguishedName")
                {
                    SizeLimit = MaxNodes
                });

                anyChildren = all.Entries.Count;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Обзор первого уровня на {Controller} не удался.", controller);
            }
        }
        catch (Exception ex)
        {
            return new BrowseProbe(controller, true, identity, 0, ex.Message, namingContext);
        }

        try
        {
            var nodes = Read(controller, RootDn, "");

            return new BrowseProbe(
                controller, true, identity, nodes.Count, null, namingContext, baseFound, anyChildren);
        }
        catch (Exception ex)
        {
            return new BrowseProbe(
                controller, true, identity, 0, ex.Message, namingContext, baseFound, anyChildren);
        }
    }

    /// <summary>Под какой учётной записью работает процесс — им же портал и представляется каталогу.</summary>
    private static string ProcessAccount()
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                return System.Security.Principal.WindowsIdentity.GetCurrent().Name;
            }
        }
        catch (Exception)
        {
            // Не смогли узнать — это само по себе не ошибка портала.
        }

        return Environment.UserName;
    }

    /// <summary>
    /// Соединение с контроллером, готовое к запросам. Одно на все обращения —
    /// и обзор, и проверка настроены одинаково, иначе диагностика проверяла бы
    /// не то, чем портал пользуется.
    /// </summary>
    private LdapConnection Connect(string controller)
    {
        var identifier = new LdapDirectoryIdentifier(
            controller, _ad.Port, fullyQualifiedDnsHostName: false, connectionless: false);

        var connection = new LdapConnection(identifier)
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

        return connection;
    }

    private IReadOnlyList<DirectoryNode> Read(string controller, string baseDn, string needle)
    {
        using var connection = Connect(controller);

        // Корень уточняем у самого каталога. Опечатка в BaseDn (или лишний
        // пробел, или корень от прошлого домена) не даёт НИКАКОЙ ошибки:
        // поиск в несуществующей ветке просто ничего не находит, и окно
        // выбора группы выглядит пустым без объяснений. Дешевле один
        // короткий запрос, чем такая тишина.
        baseDn = RootFor(connection, baseDn);

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
