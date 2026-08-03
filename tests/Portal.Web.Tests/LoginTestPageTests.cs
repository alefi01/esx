using System.Net;

namespace Portal.Web.Tests;

/// <summary>
/// Проверки аварийной страницы /Admin/LoginTest.
///
/// Главное здесь — не то, что страница работает, а то, что посторонний
/// её не откроет. Страница показывает настройки подключения к домену
/// и подробные причины отказа, поэтому цена ошибки в правах высока.
///
/// В тестовой среде запросы приходят с адреса обратной петли, поэтому
/// «разрешающий» случай проверяется естественным образом; чтобы проверить
/// запрет, адрес клиента подменяется на внешний.
/// </summary>
public class LoginTestPageTests
{
    /// <summary>
    /// Подменяет адрес клиента: WebApplicationFactory по умолчанию
    /// присылает запросы с localhost, а нам нужен «чужой» адрес.
    /// </summary>
    private static HttpClient CreateRemoteClient(PortalFactory factory, string remoteIp)
    {
        var client = factory.CreateTestClient();

        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, remoteIp);

        return client;
    }

    [Fact]
    public async Task С_самого_сервера_страница_открывается_без_входа()
    {
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

        var page = await client.GetAsync("/Admin/LoginTest");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Проверка входа", html);
        // На странице должны быть видны фактические настройки домена.
        Assert.Contains("DC=domen,DC=pro", html);
    }

    [Fact]
    public async Task Проверка_показывает_подробную_причину_отказа()
    {
        using var factory = new PortalFactory();
        var client = factory.CreateTestClient();

        var token = await client.GetTokenAsync("/Admin/LoginTest");

        var response = await client.PostFormAsync("/Admin/LoginTest", token, new Dictionary<string, string>
        {
            ["Input.UserName"] = "ivanov",
            ["Input.Password"] = "заведомо неверный"
        });

        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, html.Length > 0 ? response.StatusCode : HttpStatusCode.OK);
        Assert.Contains("InvalidCredentials", html);
        Assert.Contains("Неверный логин или пароль", html);
    }

    [Fact]
    public async Task Успешная_проверка_показывает_группы_и_членство()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers", "Buhgalteriya"];

        var client = factory.CreateTestClient();
        var token = await client.GetTokenAsync("/Admin/LoginTest");

        var response = await client.PostFormAsync("/Admin/LoginTest", token, new Dictionary<string, string>
        {
            ["Input.UserName"] = "ivanov",
            ["Input.Password"] = FakeAdAuthenticationService.CorrectPassword
        });

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Вход возможен", html);
        Assert.Contains("Buhgalteriya", html);
        Assert.Contains("состоит", html);
    }

    [Fact]
    public async Task Пароль_верный_но_нет_группы_доступа_объясняется_отдельно()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["Domain Users"];

        var client = factory.CreateTestClient();
        var token = await client.GetTokenAsync("/Admin/LoginTest");

        var response = await client.PostFormAsync("/Admin/LoginTest", token, new Dictionary<string, string>
        {
            ["Input.UserName"] = "sidorov",
            ["Input.Password"] = FakeAdAuthenticationService.CorrectPassword
        });

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("Пароль верный, но нет доступа к порталу", html);
        Assert.Contains("НЕ состоит", html);
    }

    [Fact]
    public async Task Снаружи_страница_отвечает_404_а_не_отказом_в_доступе()
    {
        // 404, а не 403: посторонний не должен даже узнать, что такая страница есть.
        using var factory = new PortalFactory();
        var client = CreateRemoteClient(factory, "192.168.105.20");

        var page = await client.GetAsync("/Admin/LoginTest");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Снаружи_проверку_нельзя_выполнить_и_POST_ом()
    {
        using var factory = new PortalFactory();

        // Один и тот же клиент: сначала берём токен «с сервера», где страница
        // доступна, а потом отправляем форму, притворившись чужим адресом.
        // Так проверка antiforgery проходит и запрос доходит до самой страницы —
        // а значит, проверяется именно её защита, а не защита формы.
        var client = factory.CreateTestClient();

        var token = await client.GetTokenAsync("/Admin/LoginTest");

        client.DefaultRequestHeaders.Add(TestRemoteIpStartupFilter.HeaderName, "192.168.105.20");

        var response = await client.PostFormAsync("/Admin/LoginTest", token, new Dictionary<string, string>
        {
            ["Input.UserName"] = "ivanov",
            ["Input.Password"] = FakeAdAuthenticationService.CorrectPassword
        });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Обычному_пользователю_снаружи_страница_тоже_не_видна()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers"];

        var client = CreateRemoteClient(factory, "192.168.105.20");
        await client.LoginAsync("ivanov");

        var page = await client.GetAsync("/Admin/LoginTest");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Администратору_снаружи_страница_доступна()
    {
        using var factory = new PortalFactory();
        factory.Ad.Groups = ["WebUsers", "WebAdmins"];

        var client = CreateRemoteClient(factory, "192.168.105.20");
        await client.LoginAsync("admin");

        var page = await client.GetAsync("/Admin/LoginTest");

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
    }
}
