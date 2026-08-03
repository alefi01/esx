using System.Net;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Offices;

/// <inheritdoc cref="IOfficeResolver"/>
public sealed class OfficeResolver : IOfficeResolver
{
    private readonly OfficesOptions _offices;
    private readonly ActiveDirectoryOptions _ad;
    private readonly ILogger<OfficeResolver> _logger;

    // Подсети разобраны один раз при старте: разбирать строку "192.168.96.0/24"
    // на каждый запрос не нужно, а кривая запись в конфиге должна всплыть сразу,
    // а не в момент чьего-то входа.
    private readonly List<(IPNetwork Network, OfficeDefinition Office)> _networks = [];

    public OfficeResolver(
        IOptions<OfficesOptions> offices,
        IOptions<ActiveDirectoryOptions> ad,
        ILogger<OfficeResolver> logger)
    {
        _offices = offices.Value;
        _ad = ad.Value;
        _logger = logger;

        foreach (var office in _offices.Items)
        {
            foreach (var subnet in office.Subnets)
            {
                // IPNetwork появился в .NET 8 — свой разбор масок писать не нужно.
                if (IPNetwork.TryParse(subnet, out var network))
                {
                    _networks.Add((network, office));
                }
                else
                {
                    _logger.LogError(
                        "Подсеть '{Subnet}' офиса '{Office}' записана неверно и будет пропущена. " +
                        "Ожидается формат CIDR, например 192.168.96.0/24.",
                        subnet, office.Code);
                }
            }
        }
    }

    public IReadOnlyList<OfficeDefinition> All => _offices.Items;

    public OfficeDefinition? ResolveByAddress(IPAddress? address)
    {
        if (address is null)
        {
            return GetByCode(_offices.DefaultOfficeCode);
        }

        // IIS отдаёт IPv4-адрес клиента в виде IPv6-совместимой записи ::ffff:192.168.96.15.
        // Приводим к обычному IPv4, иначе сравнение с подсетью 192.168.96.0/24 не сработает.
        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (var (network, office) in _networks)
        {
            if (network.Contains(address))
            {
                return office;
            }
        }

        // Адрес не из известных подсетей: VPN, проброс, тестовый запуск с localhost.
        return GetByCode(_offices.DefaultOfficeCode);
    }

    public OfficeDefinition? GetByCode(string? code) =>
        string.IsNullOrWhiteSpace(code)
            ? null
            : _offices.Items.FirstOrDefault(o => string.Equals(o.Code, code, StringComparison.OrdinalIgnoreCase));

    public IReadOnlyList<string> GetDomainControllerOrder(string? officeCode)
    {
        // HashSet со сравнением без учёта регистра — чтобы один и тот же контроллер,
        // записанный в двух местах конфига, не опрашивался дважды.
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordered = new List<string>();

        void Add(IEnumerable<string> candidates)
        {
            foreach (var candidate in candidates)
            {
                var value = candidate?.Trim();

                if (!string.IsNullOrEmpty(value) && seen.Add(value))
                {
                    ordered.Add(value);
                }
            }
        }

        // 1. Контроллеры своего офиса — они и должны отвечать в 99% случаев.
        var own = GetByCode(officeCode);

        if (own is not null)
        {
            Add(own.DomainControllers);
        }

        // 2. Контроллеры остальных офисов — запасной вариант, если свой лежит.
        foreach (var office in _offices.Items)
        {
            Add(office.DomainControllers);
        }

        // 3. Явно заданный запасной список.
        Add(_ad.FallbackDomainControllers);

        return ordered;
    }
}
