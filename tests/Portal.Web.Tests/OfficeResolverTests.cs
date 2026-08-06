using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Services.Offices;

namespace Portal.Web.Tests;

/// <summary>
/// Проверка определения офиса по IP-адресу.
///
/// Тесты здесь появились после реальной ошибки: в конфиге стояла маска /24,
/// а в сети используется 255.255.240.0, то есть /20. Внешне всё работало —
/// пока проверяли с адресов вида 192.168.96.x. Но компьютер с адресом
/// 192.168.105.20 в офис уже не попадал, и аутентификация уходила
/// на контроллер домена соседнего офиса через межофисный канал.
///
/// Такие ошибки не видны при беглой проверке, поэтому границы подсетей
/// зафиксированы тестами.
/// </summary>
public class OfficeResolverTests
{
    /// <summary>
    /// Реальные подсети: 255.255.240.0 = /20.
    ///   192.168.96.0/20  → 192.168.96.0  – 192.168.111.255
    ///   192.168.112.0/20 → 192.168.112.0 – 192.168.127.255
    /// </summary>
    private static OfficeResolver CreateResolver()
    {
        var offices = new OfficesOptions
        {
            Items =
            [
                new OfficeDefinition
                {
                    Code = "office1",
                    Name = "Офис 1",
                    Subnets = ["192.168.96.0/20"],
                    DomainControllers = ["192.168.96.3"]
                },
                new OfficeDefinition
                {
                    Code = "office2",
                    Name = "Офис 2",
                    Subnets = ["192.168.112.0/20"],
                    DomainControllers = ["192.168.112.2"]
                }
            ]
        };

        var ad = new ActiveDirectoryOptions
        {
            FallbackDomainControllers = ["192.168.96.3", "192.168.112.2"]
        };

        return new OfficeResolver(
            Options.Create(offices),
            Options.Create(ad),
            NullLogger<OfficeResolver>.Instance);
    }

    [Theory]
    // Первый офис: начало, середина и самый конец диапазона /20
    [InlineData("192.168.96.1", "office1")]
    [InlineData("192.168.96.255", "office1")]
    [InlineData("192.168.105.20", "office1")]    // при маске /24 сюда бы не попал
    [InlineData("192.168.111.254", "office1")]
    // Второй офис
    [InlineData("192.168.112.2", "office2")]
    [InlineData("192.168.120.7", "office2")]     // при маске /24 сюда бы не попал
    [InlineData("192.168.127.254", "office2")]
    public void Адрес_попадает_в_свой_офис(string address, string expectedOffice)
    {
        var resolver = CreateResolver();

        var office = resolver.ResolveByAddress(IPAddress.Parse(address));

        Assert.NotNull(office);
        Assert.Equal(expectedOffice, office.Code);
    }

    [Theory]
    [InlineData("192.168.128.1")]   // сразу за границей второго офиса
    [InlineData("192.168.95.254")]  // сразу перед границей первого
    [InlineData("10.8.0.5")]        // адрес VPN
    [InlineData("127.0.0.1")]
    public void Адрес_вне_известных_подсетей_даёт_неизвестный_офис(string address)
    {
        var resolver = CreateResolver();

        Assert.Null(resolver.ResolveByAddress(IPAddress.Parse(address)));
    }

    [Fact]
    public void Адрес_в_IPv6_совместимой_записи_распознаётся()
    {
        // IIS отдаёт клиентский IPv4 в виде ::ffff:192.168.105.20.
        // Без приведения к IPv4 сравнение с подсетью не сработало бы.
        var resolver = CreateResolver();

        var office = resolver.ResolveByAddress(IPAddress.Parse("::ffff:192.168.105.20"));

        Assert.NotNull(office);
        Assert.Equal("office1", office.Code);
    }

    [Fact]
    public void Контроллеры_своего_офиса_опрашиваются_первыми()
    {
        var resolver = CreateResolver();

        Assert.Equal(["192.168.112.2", "192.168.96.3"], resolver.GetDomainControllerOrder("office2"));
        Assert.Equal(["192.168.96.3", "192.168.112.2"], resolver.GetDomainControllerOrder("office1"));
    }

    [Fact]
    public void При_неизвестном_офисе_перебираются_все_контроллеры_без_повторов()
    {
        var resolver = CreateResolver();

        var order = resolver.GetDomainControllerOrder(null);

        Assert.Equal(["192.168.96.3", "192.168.112.2"], order);
    }

    [Fact]
    public void Кривая_запись_подсети_не_роняет_приложение()
    {
        // Опечатка в конфиге должна попасть в журнал и быть пропущена,
        // а не обрушить запуск портала целиком.
        var offices = new OfficesOptions
        {
            Items =
            [
                new OfficeDefinition
                {
                    Code = "office1",
                    Subnets = ["192.168.96.0/20", "это не подсеть"],
                    DomainControllers = ["192.168.96.3"]
                }
            ]
        };

        var resolver = new OfficeResolver(
            Options.Create(offices),
            Options.Create(new ActiveDirectoryOptions()),
            NullLogger<OfficeResolver>.Instance);

        Assert.Equal("office1", resolver.ResolveByAddress(IPAddress.Parse("192.168.100.1"))?.Code);
    }

    /// <summary>
    /// Страховка от рассогласования: подсети и контроллеры домена в настоящем
    /// appsettings.json должны быть согласованы между собой. Контроллер офиса
    /// обязан попадать в подсеть этого же офиса — иначе где-то опечатка.
    /// </summary>
    [Fact]
    public void Настоящий_appsettings_согласован()
    {
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        var offices = configuration.GetSection(OfficesOptions.SectionName).Get<OfficesOptions>();

        Assert.NotNull(offices);
        Assert.NotEmpty(offices.Items);

        var ad = configuration.GetSection(ActiveDirectoryOptions.SectionName).Get<ActiveDirectoryOptions>();

        Assert.NotNull(ad);

        var resolver = new OfficeResolver(
            Options.Create(offices),
            Options.Create(new ActiveDirectoryOptions()),
            NullLogger<OfficeResolver>.Instance);

        foreach (var office in offices.Items)
        {
            Assert.NotEmpty(office.Subnets);

            // Свой контроллер домена есть не у каждого офиса: в третьем его
            // адрес пока не назван. Такой офис работает через запасной
            // список — вход идёт к контроллерам соседних офисов по каналу
            // между площадками. Медленнее, но работает. А вот если пуст
            // и запасной список, войти из этого офиса будет некуда,
            // и это уже ошибка настройки.
            if (office.DomainControllers.Length == 0)
            {
                Assert.True(
                    ad.FallbackDomainControllers.Length > 0,
                    $"У офиса '{office.Code}' не задан контроллер домена, " +
                    "и запасной список ActiveDirectory:FallbackDomainControllers тоже пуст. " +
                    "Войти из этого офиса будет не через что.");

                continue;
            }

            foreach (var dc in office.DomainControllers)
            {
                // Контроллеры заданы адресами, а не именами — значит можно проверить.
                if (!IPAddress.TryParse(dc, out var dcAddress))
                {
                    continue;
                }

                var resolved = resolver.ResolveByAddress(dcAddress);

                Assert.True(
                    resolved?.Code == office.Code,
                    $"Контроллер {dc} назначен офису '{office.Code}', " +
                    $"но по подсетям попадает в '{resolved?.Code ?? "неизвестный"}'. " +
                    "Проверьте маски в секции Offices.");
            }
        }
    }
}
