using System.Net;
using Portal.Web.Data;

namespace Portal.Web.Tests;

/// <summary>
/// Проверка ленты объявлений: кто может читать, кто писать,
/// и главное — кто НЕ может править чужое.
/// </summary>
public class AnnouncementsTests
{
    private const string Access = "WebUsers";
    private const string Publisher = "WebPublishers";
    private const string Admin = "WebAdmins";

    private static Announcement SampleAnnouncement(string author = "petrov", string title = "Отключение воды") =>
        new()
        {
            Title = title,
            Body = "Завтра с 9:00 до 12:00 в здании отключат воду.",
            AuthorUserName = author,
            AuthorDisplayName = $"Пользователь {author}",
            CreatedAt = DateTime.UtcNow.AddHours(-1)
        };

    [Fact]
    public async Task Ленту_видят_все_у_кого_есть_доступ_к_порталу()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement()));

        var client = await factory.LoginAsAsync("ivanov", Access);

        var page = await client.GetAsync("/Announcements");
        var html = await page.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.OK, page.StatusCode);
        Assert.Contains("Отключение воды", html);
    }

    [Fact]
    public async Task Читателю_не_показывают_кнопку_публикации()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("ivanov", Access);
        var html = await client.GetStringAsync("/Announcements");

        Assert.DoesNotContain("Написать объявление", html);
    }

    [Fact]
    public async Task Читатель_не_может_открыть_форму_публикации()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("ivanov", Access);

        var page = await client.GetAsync("/Announcements/Create");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Публикатор_создаёт_объявление_и_оно_появляется_в_ленте()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);

        var token = await client.GetTokenAsync("/Announcements/Create");

        var response = await client.PostFormAsync("/Announcements/Create", token, new Dictionary<string, string>
        {
            ["Input.Title"] = "Собрание в пятницу",
            ["Input.Body"] = "Начало в 15:00, переговорная на втором этаже."
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var saved = factory.Query(db => db.Announcements.Single());

        Assert.Equal("Собрание в пятницу", saved.Title);
        // Автор берётся из cookie, а не из формы — подделать его нельзя.
        Assert.Equal("petrov", saved.AuthorUserName);
        Assert.Null(saved.UpdatedAt);

        var html = await client.GetStringAsync("/Announcements");
        Assert.Contains("Собрание в пятницу", html);
    }

    [Fact]
    public async Task Автора_нельзя_подделать_через_форму()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);
        var token = await client.GetTokenAsync("/Announcements/Create");

        // Пытаемся дослать поля, которых в форме нет.
        await client.PostFormAsync("/Announcements/Create", token, new Dictionary<string, string>
        {
            ["Input.Title"] = "Объявление от чужого имени",
            ["Input.Body"] = "текст",
            ["Input.AuthorUserName"] = "director",
            ["AuthorUserName"] = "director",
            ["Announcement.AuthorUserName"] = "director"
        });

        var saved = factory.Query(db => db.Announcements.Single());

        Assert.Equal("petrov", saved.AuthorUserName);
    }

    [Fact]
    public async Task Скрипт_в_тексте_объявления_не_выполняется()
    {
        using var factory = new PortalFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Обычный заголовок",
            Body = "<script>alert('xss')</script> и ещё <b>жирный</b> текст",
            AuthorUserName = "petrov",
            AuthorDisplayName = "Пользователь petrov",
            CreatedAt = DateTime.UtcNow
        }));

        var client = await factory.LoginAsAsync("ivanov", Access);
        var html = await client.GetStringAsync("/Announcements");

        // Тег не должен попасть в разметку как тег — только как видимый текст.
        Assert.DoesNotContain("<script>", html);
        Assert.DoesNotContain("<b>жирный</b>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Публикатор_не_может_править_чужое_объявление()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "sidorov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        // petrov состоит в группе публикаторов, но автор объявления — sidorov.
        var client = await factory.LoginAsAsync("petrov", Access, Publisher);

        var page = await client.GetAsync($"/Announcements/Edit/{id}");

        Assert.Equal(HttpStatusCode.Redirect, page.StatusCode);
        Assert.Contains("AccessDenied", page.Headers.Location!.ToString());
    }

    [Fact]
    public async Task Публикатор_не_может_переписать_чужое_объявление_прямым_POST()
    {
        // Даже если обойти страницу и отправить форму напрямую —
        // права проверяются ещё раз при сохранении.
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "sidorov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);

        // Токен берём со своей формы создания — она этому пользователю доступна.
        var token = await client.GetTokenAsync("/Announcements/Create");

        var response = await client.PostFormAsync($"/Announcements/Edit/{id}", token, new Dictionary<string, string>
        {
            ["Input.Id"] = id.ToString(),
            ["Input.Title"] = "Подменённый заголовок",
            ["Input.Body"] = "Подменённый текст"
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());

        var unchanged = factory.Query(db => db.Announcements.Single());
        Assert.Equal("Отключение воды", unchanged.Title);
    }

    [Fact]
    public async Task Автор_правит_своё_объявление()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "petrov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);

        var token = await client.GetTokenAsync($"/Announcements/Edit/{id}");

        var response = await client.PostFormAsync($"/Announcements/Edit/{id}", token, new Dictionary<string, string>
        {
            ["Input.Id"] = id.ToString(),
            ["Input.Title"] = "Отключение воды перенесено",
            ["Input.Body"] = "Перенесено на понедельник."
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var updated = factory.Query(db => db.Announcements.Single());

        Assert.Equal("Отключение воды перенесено", updated.Title);
        Assert.NotNull(updated.UpdatedAt);
    }

    [Fact]
    public async Task Администратор_правит_чужое_объявление()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "sidorov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        var client = await factory.LoginAsAsync("admin", Access, Admin);

        var token = await client.GetTokenAsync($"/Announcements/Edit/{id}");

        var response = await client.PostFormAsync($"/Announcements/Edit/{id}", token, new Dictionary<string, string>
        {
            ["Input.Id"] = id.ToString(),
            ["Input.Title"] = "Исправлено администратором",
            ["Input.Body"] = "Текст исправлен."
        });

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("Исправлено администратором", factory.Query(db => db.Announcements.Single().Title));
    }

    [Fact]
    public async Task Автор_удаляет_своё_объявление()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "petrov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);

        var token = await client.GetTokenAsync($"/Announcements/Delete/{id}");

        var response = await client.PostFormAsync($"/Announcements/Delete/{id}", token, []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal(0, factory.Query(db => db.Announcements.Count()));
    }

    [Fact]
    public async Task Чужое_объявление_удалить_нельзя()
    {
        using var factory = new PortalFactory();
        factory.Seed(db => db.Announcements.Add(SampleAnnouncement(author: "sidorov")));

        var id = factory.Query(db => db.Announcements.Single().Id);

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);
        var token = await client.GetTokenAsync("/Announcements/Create");

        var response = await client.PostFormAsync($"/Announcements/Delete/{id}", token, []);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        Assert.Equal(1, factory.Query(db => db.Announcements.Count()));
    }

    [Fact]
    public async Task Пустой_заголовок_не_сохраняется()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("petrov", Access, Publisher);
        var token = await client.GetTokenAsync("/Announcements/Create");

        var response = await client.PostFormAsync("/Announcements/Create", token, new Dictionary<string, string>
        {
            ["Input.Title"] = "",
            ["Input.Body"] = "Текст есть, заголовка нет."
        });

        // Остаёмся на форме с сообщением об ошибке, ничего не сохранив.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("Введите заголовок", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, factory.Query(db => db.Announcements.Count()));
    }

    [Fact]
    public async Task Лента_разбивается_на_страницы_и_новые_объявления_сверху()
    {
        using var factory = new PortalFactory();

        // Размер страницы в appsettings.json — 20.
        factory.Seed(db =>
        {
            for (var i = 1; i <= 25; i++)
            {
                db.Announcements.Add(new Announcement
                {
                    Title = $"Объявление номер {i}",
                    Body = "текст",
                    AuthorUserName = "petrov",
                    AuthorDisplayName = "Пользователь petrov",
                    CreatedAt = DateTime.UtcNow.AddMinutes(i)
                });
            }
        });

        var client = await factory.LoginAsAsync("ivanov", Access);

        var first = await client.GetStringAsync("/Announcements");

        // Самое свежее — сверху, самое старое на первую страницу не попало.
        Assert.Contains("Объявление номер 25", first);
        Assert.DoesNotContain("Объявление номер 1<", first);
        Assert.Contains("Страница 1 из 2", first);

        var second = await client.GetStringAsync("/Announcements?pageNumber=2");

        Assert.Contains("Объявление номер 1<", second);
        Assert.Contains("Страница 2 из 2", second);
    }
}
