using System.Net;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Portal.Web.Services.ActiveDirectory;

namespace Portal.Web.Tests;

/// <summary>
/// Заглушка вместо настоящего Active Directory.
///
/// Благодаря ей тесты проходят на любой машине — без домена и без сети.
/// Проверяется вся логика портала выше уровня LDAP: выдача cookie,
/// разграничение прав по группам, выход, защита форм от подделки.
/// Сам обмен по LDAP тут не проверяется — для него нужен живой контроллер домена
/// (это делается вручную по инструкции docs/05-проверка-этапа-1.md).
/// </summary>
public sealed class FakeAdAuthenticationService : IAdAuthenticationService
{
    /// <summary>Группы, которые «вернёт» AD при следующем входе. Тест задаёт их перед вызовом.</summary>
    public static List<string> Groups { get; set; } = ["WebUsers"];

    /// <summary>Единственный «правильный» пароль в тестах.</summary>
    public const string CorrectPassword = "good";

    public Task<AdAuthenticationResult> AuthenticateAsync(
        string userName, string password, string? officeCode, CancellationToken ct = default)
    {
        if (password != CorrectPassword)
        {
            return Task.FromResult(AdAuthenticationResult.Failure(
                AdAuthenticationStatus.InvalidCredentials, "192.168.96.3", "тестовая заглушка"));
        }

        var user = new AdUserInfo(
            userName, "Иван Иванов", "ivanov@domen.pro", "CN=Ivanov,DC=domen,DC=pro", Groups);

        return Task.FromResult(AdAuthenticationResult.Success(user, "192.168.96.3"));
    }
}

/// <summary>
/// Поднимает приложение целиком в памяти — без IIS и без сетевого порта —
/// и подменяет только сервис аутентификации. Всё остальное настоящее:
/// та же конфигурация, те же политики, тот же конвейер обработки запроса.
/// </summary>
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    protected override void ConfigureWebHost(IWebHostBuilder builder) =>
        builder.ConfigureServices(services =>
            services.Replace(ServiceDescriptor.Scoped<IAdAuthenticationService, FakeAdAuthenticationService>()));
}

public class AuthFlowTests
{
    // Скрытое поле формы с antiforgery-токеном. Без него POST-запросы отклоняются.
    private static readonly Regex TokenRegex =
        new(@"name=""__RequestVerificationToken""[^>]*value=""(?<token>[^""]+)""", RegexOptions.Compiled);

    private static HttpClient CreateClient(PortalFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Перенаправления не выполняем автоматически: нам важно проверить,
            // КУДА именно приложение отправляет пользователя.
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    private static async Task<string> GetTokenAsync(HttpClient client, string page) =>
        TokenRegex.Match(await client.GetStringAsync(page)).Groups["token"].Value;

    private static Task<HttpResponseMessage> LoginAsync(
        HttpClient client, string token, string userName, string password, string url = "/Account/Login") =>
        client.PostAsync(url, new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["__RequestVerificationToken"] = token,
            ["Input.UserName"] = userName,
            ["Input.Password"] = password
        }));

    [Fact]
    public async Task Успешный_вход_приводит_на_главную_с_именем_и_группами()
    {
        FakeAdAuthenticationService.Groups = ["WebUsers", "Domain Users"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        var login = await LoginAsync(client, token, "ivanov", FakeAdAuthenticationService.CorrectPassword);

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.ToString());

        var home = await client.GetAsync("/");
        var html = await home.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Иван Иванов", html);
        Assert.Contains("WebUsers", html);
    }

    [Fact]
    public async Task Неверный_пароль_не_пускает_и_не_раскрывает_причину()
    {
        FakeAdAuthenticationService.Groups = ["WebUsers"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        var login = await LoginAsync(client, token, "ivanov", "неверный");
        var html = await login.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("Неверный логин или пароль", html);

        // Cookie не выдана — на главную по-прежнему не пускают.
        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
    }

    [Fact]
    public async Task Пользователь_вне_группы_доступа_не_получает_сессию()
    {
        // Пароль верный, но членства в WebUsers нет.
        FakeAdAuthenticationService.Groups = ["Domain Users"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        var login = await LoginAsync(client, token, "sidorov", FakeAdAuthenticationService.CorrectPassword);
        var html = await login.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, login.StatusCode);
        Assert.Contains("нет доступа к порталу", html);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Account/Login", home.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Обычный_пользователь_не_попадает_в_раздел_администратора()
    {
        FakeAdAuthenticationService.Groups = ["WebUsers"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        await LoginAsync(client, token, "ivanov", FakeAdAuthenticationService.CorrectPassword);

        var page = await client.GetAsync("/Admin/Diagnostics");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Администратор_видит_раздел_диагностики()
    {
        FakeAdAuthenticationService.Groups = ["WebUsers", "WebAdmins"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        await LoginAsync(client, token, "admin", FakeAdAuthenticationService.CorrectPassword);

        var page = await client.GetAsync("/Admin/Diagnostics");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Диагностика подключения", html);
        // Контроллеры домена из appsettings.json должны попасть в таблицу.
        Assert.Contains("192.168.96.3", html);
    }

    [Fact]
    public async Task Выход_завершает_сессию()
    {
        FakeAdAuthenticationService.Groups = ["WebUsers"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        await LoginAsync(client, token, "ivanov", FakeAdAuthenticationService.CorrectPassword);

        // Токен для выхода берём с главной: форма выхода живёт в шапке страницы.
        var logoutToken = await GetTokenAsync(client, "/");

        var logout = await client.PostAsync("/Account/Logout", new FormUrlEncodedContent(
            new Dictionary<string, string> { ["__RequestVerificationToken"] = logoutToken }));

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Account/Login", home.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Параметр_returnUrl_не_уводит_на_чужой_сайт()
    {
        // Защита от «открытого редиректа»: ссылка вида
        // http://web.domen.pro/Account/Login?returnUrl=https://зловред/
        // после успешного входа не должна уводить пользователя наружу.
        FakeAdAuthenticationService.Groups = ["WebUsers"];

        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var token = await GetTokenAsync(client, "/Account/Login");
        var login = await LoginAsync(
            client, token, "ivanov", FakeAdAuthenticationService.CorrectPassword,
            "/Account/Login?returnUrl=https://evil.example/");

        Assert.Equal("/", login.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Отправка_формы_без_antiforgery_токена_отклоняется()
    {
        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        await client.GetAsync("/Account/Login");

        var response = await client.PostAsync("/Account/Login", new FormUrlEncodedContent(
            new Dictionary<string, string>
            {
                ["Input.UserName"] = "ivanov",
                ["Input.Password"] = FakeAdAuthenticationService.CorrectPassword
            }));

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
    }

    [Fact]
    public async Task Проверка_живости_открыта_без_входа()
    {
        using var factory = new PortalFactory();
        var client = CreateClient(factory);

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }
}
