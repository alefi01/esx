namespace Portal.Web.Configuration;

/// <summary>
/// Описание одного офиса: какие подсети ему принадлежат и какие контроллеры домена
/// в нём стоят. На этапе 6 сюда же добавится путь к локальной реплике файлового хранилища.
/// </summary>
public sealed class OfficeDefinition
{
    /// <summary>Короткий машинный код офиса, например "office1". Используется в claim и в конфиге.</summary>
    public string Code { get; set; } = "";

    /// <summary>Человекочитаемое название для интерфейса, например "Офис 1".</summary>
    public string Name { get; set; } = "";

    /// <summary>
    /// Подсети офиса в формате CIDR, например "192.168.96.0/20".
    /// Их может быть несколько — просто перечислите.
    /// </summary>
    public string[] Subnets { get; set; } = [];

    /// <summary>
    /// Контроллеры домена этого офиса (IP или DNS-имя).
    /// Пользователя из этого офиса аутентифицируем через них — чтобы LDAP-трафик
    /// не ходил через межофисный канал.
    /// </summary>
    public string[] DomainControllers { get; set; } = [];
}

/// <summary>Секция "Offices" в appsettings.json.</summary>
public sealed class OfficesOptions
{
    public const string SectionName = "Offices";

    /// <summary>
    /// Код офиса, который считаем «своим», если IP клиента не попал ни в одну подсеть.
    /// Пусто — значит офис остаётся неизвестным, и мы идём по FallbackDomainControllers.
    /// </summary>
    public string DefaultOfficeCode { get; set; } = "";

    public List<OfficeDefinition> Items { get; set; } = [];
}
