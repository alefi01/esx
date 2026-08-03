using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Mvc.Testing;

namespace Portal.Web.Tests;

/// <summary>
/// Общие для всех тестов действия: создать клиента, войти в портал,
/// вытащить antiforgery-токен из формы.
/// </summary>
public static class TestClientExtensions
{
    // Скрытое поле формы с antiforgery-токеном. Без него POST-запросы отклоняются.
    private static readonly Regex TokenRegex =
        new(@"name=""__RequestVerificationToken""[^>]*value=""(?<token>[^""]+)""", RegexOptions.Compiled);

    public static HttpClient CreateTestClient(this PortalFactory factory) =>
        factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            // Перенаправления не выполняем автоматически: нам важно проверить,
            // КУДА именно приложение отправляет пользователя.
            AllowAutoRedirect = false,
            HandleCookies = true
        });

    public static async Task<string> GetTokenAsync(this HttpClient client, string page)
    {
        var html = await client.GetStringAsync(page);

        return TokenRegex.Match(html).Groups["token"].Value;
    }

    public static Task<HttpResponseMessage> PostFormAsync(
        this HttpClient client, string url, string token, Dictionary<string, string> fields)
    {
        fields["__RequestVerificationToken"] = token;

        return client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    public static async Task<HttpResponseMessage> LoginAsync(
        this HttpClient client,
        string userName,
        string password = FakeAdAuthenticationService.CorrectPassword,
        string url = "/Account/Login")
    {
        var token = await client.GetTokenAsync("/Account/Login");

        return await client.PostFormAsync(url, token, new Dictionary<string, string>
        {
            ["Input.UserName"] = userName,
            ["Input.Password"] = password
        });
    }

    /// <summary>Войти пользователем с заданным набором групп AD.</summary>
    public static async Task<HttpClient> LoginAsAsync(
        this PortalFactory factory, string userName, params string[] groups)
    {
        factory.Ad.Groups = [.. groups];

        var client = factory.CreateTestClient();
        await client.LoginAsync(userName);

        return client;
    }
}
