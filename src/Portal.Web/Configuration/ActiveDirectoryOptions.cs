namespace Portal.Web.Configuration;

/// <summary>
/// Способ, которым приложение подтверждает пароль пользователя на контроллере домена.
/// </summary>
public enum LdapAuthMode
{
    /// <summary>
    /// SASL/GSS-SPNEGO (Kerberos, при невозможности — NTLM).
    /// Пароль по сети НЕ передаётся: контроллер домена проверяет знание пароля
    /// криптографически. Работает «из коробки» в домене, сертификаты не нужны.
    /// Это режим по умолчанию и рекомендуемый для вашей сети.
    /// </summary>
    Negotiate = 0,

    /// <summary>
    /// LDAP simple bind — логин и пароль уходят на контроллер домена в открытом виде.
    /// Использовать ТОЛЬКО вместе с UseSsl=true (LDAPS, порт 636), иначе пароль
    /// можно перехватить снифером в локальной сети.
    /// Плюс режима: AD возвращает подробную причину отказа (пароль истёк,
    /// учётка заблокирована и т.п.), у Negotiate ответ всегда обезличенный «49».
    /// </summary>
    Basic = 1
}

/// <summary>
/// Настройки подключения к Active Directory. Секция "ActiveDirectory" в appsettings.json.
/// </summary>
public sealed class ActiveDirectoryOptions
{
    public const string SectionName = "ActiveDirectory";

    /// <summary>FQDN домена, например "domen.pro". Используется при формировании учётных данных для bind.</summary>
    public string DomainFqdn { get; set; } = "domen.pro";

    /// <summary>
    /// NetBIOS-имя домена (короткое, например "DOMEN").
    /// Нужно только для того, чтобы принять логин в формате DOMEN\ivanov и отрезать префикс.
    /// Если оставить пустым — префикс всё равно отрежется, просто без проверки, что домен «наш».
    /// </summary>
    public string DomainNetBios { get; set; } = "";

    /// <summary>
    /// Корень поиска в каталоге. Для домена domen.pro это "DC=domen,DC=pro".
    /// Можно сузить до конкретного OU, если все учётки лежат в одном месте —
    /// поиск будет быстрее, но пользователи вне этого OU войти не смогут.
    /// </summary>
    public string BaseDn { get; set; } = "DC=domen,DC=pro";

    /// <summary>
    /// Группа AD, без членства в которой на портал не пускают вообще.
    /// Указывается по sAMAccountName (короткое имя группы, как в оснастке ADUC).
    /// </summary>
    public string AccessGroup { get; set; } = "WebUsers";

    /// <summary>Группа AD, дающая административные права в портале.</summary>
    public string AdminGroup { get; set; } = "WebAdmins";

    /// <summary>
    /// Группа AD, члены которой могут публиковать объявления.
    /// Читать ленту могут все, у кого есть доступ к порталу;
    /// писать — только эта группа и администраторы.
    /// </summary>
    public string PublisherGroup { get; set; } = "WebPublishers";

    /// <summary>См. <see cref="LdapAuthMode"/>.</summary>
    public LdapAuthMode AuthMode { get; set; } = LdapAuthMode.Negotiate;

    /// <summary>Порт LDAP. 389 — обычный, 636 — LDAPS (тогда UseSsl=true).</summary>
    public int Port { get; set; } = 389;

    /// <summary>Включить TLS (LDAPS). Требует, чтобы на контроллерах домена был сертификат.</summary>
    public bool UseSsl { get; set; } = false;

    /// <summary>
    /// Подписывать и шифровать LDAP-трафик средствами Kerberos/NTLM (только для AuthMode=Negotiate,
    /// только на Windows). Даёт защиту трафика без сертификатов. При UseSsl=true не применяется —
    /// одновременно включать sealing и TLS нельзя.
    /// </summary>
    public bool UseSigningAndSealing { get; set; } = true;

    /// <summary>
    /// Таймаут на подключение и на каждый LDAP-запрос, секунды.
    /// Держим небольшим: если контроллер домена соседнего офиса недоступен,
    /// пользователь не должен ждать 30 секунд — мы просто пойдём к следующему.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 10;

    /// <summary>
    /// Учитывать вложенные группы (группа в группе) через серверное правило
    /// LDAP_MATCHING_RULE_IN_CHAIN. Если false — считаются только прямые членства.
    /// </summary>
    public bool IncludeNestedGroups { get; set; } = true;

    /// <summary>
    /// Контроллеры домена, к которым идти, если офис пользователя определить не удалось
    /// (например, вход с VPN-адреса, не входящего ни в одну известную подсеть).
    /// Порядок важен: перебираются сверху вниз.
    /// </summary>
    public string[] FallbackDomainControllers { get; set; } = [];
}
