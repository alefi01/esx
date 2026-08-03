namespace Portal.Web.Security;

/// <summary>
/// Имена политик авторизации. Вынесены в константы, чтобы не искать опечатки
/// в строковых литералах по всему проекту.
/// </summary>
public static class PortalPolicies
{
    /// <summary>Базовый доступ к порталу: членство в группе ActiveDirectory:AccessGroup.</summary>
    public const string Access = "PortalAccess";

    /// <summary>Административные права: членство в группе ActiveDirectory:AdminGroup.</summary>
    public const string Admin = "PortalAdmin";

    /// <summary>
    /// Право публиковать объявления: членство в группе ActiveDirectory:PublisherGroup
    /// либо в группе администраторов. Читать ленту могут все, у кого есть доступ к порталу.
    /// </summary>
    public const string PublishAnnouncements = "PublishAnnouncements";
}

/// <summary>Собственные типы claim'ов, которых нет в стандартном наборе.</summary>
public static class PortalClaimTypes
{
    /// <summary>Код офиса, определённый по IP на момент входа, например "office1".</summary>
    public const string Office = "portal:office";

    /// <summary>Отображаемое имя офиса, например "Офис 1".</summary>
    public const string OfficeName = "portal:office_name";

    /// <summary>Контроллер домена, который фактически подтвердил пароль. Нужно для диагностики.</summary>
    public const string AuthenticatedBy = "portal:auth_dc";
}
