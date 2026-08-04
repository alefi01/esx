using System.Net;

namespace Portal.Web.Tests;

public class AuthFlowTests
{
    [Fact]
    public async Task Успешный_вход_приводит_на_главную_с_именем_и_группами()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers", "Domain Users"];
        var client = factory.CreateTestClient();

        var login = await client.LoginAsync("ivanov");

        Assert.Equal(HttpStatusCode.Redirect, login.StatusCode);
        Assert.Equal("/", login.Headers.Location!.ToString());

        var home = await client.GetAsync("/");
        var html = await home.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, home.StatusCode);
        Assert.Contains("Пользователь ivanov", html);
        Assert.Contains("WebUsers", html);
    }

    [Fact]
    public async Task Неверный_пароль_не_пускает_и_не_раскрывает_причину()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers"];
        var client = factory.CreateTestClient();

        var login = await client.LoginAsync("ivanov", "неверный");
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
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["Domain Users"];
        var client = factory.CreateTestClient();

        var login = await client.LoginAsync("sidorov");
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
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers"];
        var client = factory.CreateTestClient();

        await client.LoginAsync("ivanov");

        var page = await client.GetAsync("/Admin/Diagnostics");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Администратор_видит_раздел_диагностики()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers", "WebAdmins"];
        var client = factory.CreateTestClient();

        await client.LoginAsync("admin");

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
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers"];
        var client = factory.CreateTestClient();

        await client.LoginAsync("ivanov");

        // Токен для выхода берём с главной: форма выхода живёт в шапке страницы.
        var logoutToken = await client.GetTokenAsync("/");

        var logout = await client.PostFormAsync("/Account/Logout", logoutToken, []);

        Assert.Equal(HttpStatusCode.Redirect, logout.StatusCode);

        var home = await client.GetAsync("/");
        Assert.Equal(HttpStatusCode.Redirect, home.StatusCode);
        Assert.Contains("/Account/Login", home.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Параметр_returnUrl_не_уводит_на_чужой_сайт()
    {
        // Защита от «открытого редиректа»: ссылка вида
        // http://ftp.domen.pro/Account/Login?returnUrl=https://зловред/
        // после успешного входа не должна уводить пользователя наружу.
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers"];
        var client = factory.CreateTestClient();

        var login = await client.LoginAsync(
            "ivanov",
            FakeAdAuthenticationService.CorrectPassword,
            "/Account/Login?returnUrl=https://evil.example/");

        Assert.Equal("/", login.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Отправка_формы_без_antiforgery_токена_отклоняется()
    {
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

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
        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/healthz");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("ok", await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Фоновый запрос от уже открытой страницы не должен получать
    /// разметку страницы входа.
    ///
    /// Так и было: cookie-аутентификация перенаправляла на вход, браузер
    /// послушно шёл по перенаправлению и получал HTML с кодом 200 — портал
    /// показывал страницу входа вместо документа, а человек видел невнятную
    /// ошибку и не догадывался, что просто истёк вход.
    /// </summary>
    [Fact]
    public async Task Фоновый_запрос_без_входа_получает_401_а_не_страницу_входа()
    {
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

        var request = new HttpRequestMessage(HttpMethod.Get, "/Files?handler=Preview&fileId=1");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");

        var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Contains("signed-out", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Точка_уведомлений_без_входа_отвечает_401()
    {
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/api/notifications");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Обычный_переход_без_входа_по_прежнему_ведёт_на_страницу_входа()
    {
        // Послабление выше касается только фоновых запросов. Человек,
        // открывший адрес в браузере, должен попасть на форму входа,
        // а не увидеть голый код ошибки.
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/Files");

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("/Account/Login", response.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Страницы_портала_не_кладутся_в_кэш_браузера()
    {
        // Иначе человек возвращается кнопкой «назад», браузер достаёт
        // страницу из кэша, она выглядит рабочей — а вход уже истёк,
        // и всё, что она спрашивает у сервера, отвечает отказом.
        using var factory = new PortalFactory();
        var client = await factory.LoginAsAsync("ivanov", "WebUsers");

        var response = await client.GetAsync("/Files");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("no-store", response.Headers.CacheControl!.ToString());
    }
}
