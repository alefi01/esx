using System.Net;
using Portal.Web.Data;

namespace Portal.Web.Tests;

/// <summary>
/// Лента активности.
///
/// Главный вопрос — не «красиво ли выглядит», а «кому она видна».
/// По такой ленте понятно, кто чем занимается, и обычному сотруднику
/// этого знать незачем: раздел закрыт и не показывается в меню.
/// </summary>
public class ActivityTests
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    private static PortalFactory PrepareEvents()
    {
        var factory = new PortalFactory();

        factory.Seed(db =>
        {
            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTime.UtcNow.AddMinutes(-5),
                UserName = "ivanov",
                UserDisplayName = "Иванов Иван",
                Action = AuditAction.Upload,
                Target = "Договор аренды.docx",
                Details = "папка «Договоры», 1,2 МБ"
            });

            db.AuditEntries.Add(new AuditEntry
            {
                At = DateTime.UtcNow.AddDays(-2),
                UserName = "petrov",
                UserDisplayName = "Петров Пётр",
                Action = AuditAction.MoveToTrash,
                Target = "Старый акт.pdf"
            });

            db.SaveChanges();
        });

        return factory;
    }

    [Fact]
    public async Task Администратор_видит_ленту_событий()
    {
        using var factory = PrepareEvents();
        var client = await factory.LoginAsAsync("boss", Users, Admins);

        var html = await client.GetStringAsync("/Admin/Activity");

        Assert.Contains("Иванов Иван", html);
        Assert.Contains("Договор аренды.docx", html);
        Assert.Contains("Старый акт.pdf", html);

        // События сгруппированы по дням: сегодняшнее и позавчерашнее
        // не должны идти сплошным списком.
        Assert.Contains("Сегодня", html);
    }

    [Fact]
    public async Task Обычному_сотруднику_лента_недоступна()
    {
        using var factory = PrepareEvents();
        var client = await factory.LoginAsAsync("ivanov", Users);

        var response = await client.GetAsync("/Admin/Activity");

        // При входе по cookie отказ — это перенаправление на страницу
        // «доступ запрещён», а не код 403.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location?.ToString());
    }

    /// <summary>
    /// Пункта меню у обычного сотрудника нет вовсе — не спрятан, а отсутствует.
    /// Ссылка, ведущая в отказ, только раздражает.
    /// </summary>
    [Fact]
    public async Task Пункт_меню_показывается_только_администратору()
    {
        using var factory = PrepareEvents();

        var user = await factory.LoginAsAsync("ivanov", Users);
        var userHtml = await user.GetStringAsync("/");

        Assert.DoesNotContain("/Admin/Activity", userHtml);

        var admin = await factory.LoginAsAsync("boss", Users, Admins);
        var adminHtml = await admin.GetStringAsync("/");

        Assert.Contains("/Admin/Activity", adminHtml);
    }
}
