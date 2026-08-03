using System.Net;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Offices;

/// <summary>
/// Определение офиса пользователя по IP-адресу.
///
/// На этапе 1 это нужно, чтобы аутентифицировать человека через контроллер домена
/// его собственного офиса, а не гонять LDAP через межофисный канал.
/// На этапе 6 та же самая логика будет выбирать, с какой реплики DFS-R отдавать файл.
/// Поэтому сервис появляется сразу — переписывать потом ничего не придётся.
/// </summary>
public interface IOfficeResolver
{
    /// <summary>Офис, которому принадлежит адрес, или null, если адрес не попал ни в одну подсеть.</summary>
    OfficeDefinition? ResolveByAddress(IPAddress? address);

    /// <summary>Офис по его коду, или null.</summary>
    OfficeDefinition? GetByCode(string? code);

    /// <summary>
    /// Контроллеры домена в порядке обхода: сначала «свои» для указанного офиса,
    /// затем контроллеры остальных офисов, затем запасной список из конфига.
    /// Дубликаты убраны, порядок сохранён.
    /// </summary>
    IReadOnlyList<string> GetDomainControllerOrder(string? officeCode);

    /// <summary>Все настроенные офисы — для страницы диагностики.</summary>
    IReadOnlyList<OfficeDefinition> All { get; }
}
