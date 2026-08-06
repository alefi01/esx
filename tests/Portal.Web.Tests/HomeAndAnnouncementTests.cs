using System.Net;
using Portal.Web.Data;
using Portal.Web.Services.Messaging;

namespace Portal.Web.Tests;

/// <summary>
/// Главная страница, вложения к объявлениям, поиск по перепискам
/// и очистка журнала.
/// </summary>
public class HomeAndAnnouncementTests : IDisposable
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), $"home-{Guid.NewGuid():N}");

    public HomeAndAnnouncementTests() => Directory.CreateDirectory(_storageRoot);

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private PortalFactory CreateFactory() => new() { StorageRootPath = _storageRoot };

    private static void AddAnnouncement(PortalFactory factory, string title, string body)
    {
        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = title,
            Body = body,
            AuthorUserName = "petrov",
            AuthorDisplayName = "Петров Пётр",
            CreatedAt = DateTime.UtcNow
        }));
    }

    // ------------------------------------------------------------------
    // Главная
    // ------------------------------------------------------------------

    [Fact]
    public async Task Главная_показывает_объявления()
    {
        using var factory = CreateFactory();
        AddAnnouncement(factory, "Отключение воды", "Подробности внутри.");

        var client = await factory.LoginAsAsync("ivanov", Users);

        var html = await client.GetStringAsync("/");

        Assert.Contains("Отключение воды", html);
        Assert.Contains("Подробности внутри.", html);
    }

    [Fact]
    public async Task Открытие_главной_отмечает_объявления_прочитанными()
    {
        // Главная и есть лента: открыв её, человек всё новое увидел.
        using var factory = CreateFactory();

        var client = await factory.LoginAsAsync("ivanov", Users);
        await client.GetStringAsync("/api/notifications");

        AddAnnouncement(factory, "Новое", "текст");

        await client.GetStringAsync("/");

        var json = await client.GetStringAsync("/api/notifications");

        Assert.Contains("\"unread\":0", json.Replace(" ", ""));
    }

    [Fact]
    public async Task Недоступная_база_не_роняет_главную()
    {
        // Файлы и переписки живут своей жизнью, и портал должен открываться,
        // даже если объявления прочитать не удалось.
        using var factory = CreateFactory();

        var client = await factory.LoginAsAsync("ivanov", Users);

        factory.DropDatabase();

        var response = await client.GetAsync("/");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("недоступны", await response.Content.ReadAsStringAsync());
    }

    // ------------------------------------------------------------------
    // Вложения объявлений
    // ------------------------------------------------------------------

    [Fact]
    public async Task Картинка_прикладывается_к_объявлению_и_показывается()
    {
        using var factory = CreateFactory();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        await PublishAsync(client, "С картинкой", "текст", "схема.png", "содержимое картинки");

        var file = factory.Query(db => db.AnnouncementFiles.Single());

        Assert.True(file.IsImage);

        var html = await client.GetStringAsync("/");

        // Картинка показывается прямо в ленте, а не ссылкой.
        Assert.Contains("ann-image", html);
    }

    [Fact]
    public async Task Обычный_файл_прикладывается_ссылкой()
    {
        using var factory = CreateFactory();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        await PublishAsync(client, "С файлом", "текст", "форма.txt", "образец");

        var file = factory.Query(db => db.AnnouncementFiles.Single());

        Assert.False(file.IsImage);

        var response = await client.GetAsync($"/Announcements?handler=Attachment&fileId={file.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("образец", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Опасное_расширение_к_объявлению_не_приложить()
    {
        // Объявление видно всем сотрудникам сразу — послаблений здесь быть не может.
        using var factory = CreateFactory();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        await PublishAsync(client, "Заголовок", "текст", "вирус.exe", "MZ");

        Assert.Equal(0, factory.Query(db => db.AnnouncementFiles.Count()));

        // Само объявление при этом опубликовано: текст не должен пропадать
        // из-за неудачного вложения.
        Assert.Equal(1, factory.Query(db => db.Announcements.Count()));
    }

    [Fact]
    public async Task SVG_не_показывается_картинкой()
    {
        // SVG — картинка, но внутри неё может быть код, а объявление
        // видит вся организация.
        using var factory = CreateFactory();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        await PublishAsync(client, "Заголовок", "текст", "рисунок.svg", "<svg></svg>");

        var file = factory.Query(db => db.AnnouncementFiles.SingleOrDefault());

        // Либо не принят вовсе, либо принят как обычный файл — но не как картинка.
        Assert.True(file is null || !file.IsImage);
    }

    // ------------------------------------------------------------------
    // Поиск по перепискам
    // ------------------------------------------------------------------

    [Fact]
    public async Task Поиск_находит_сообщение_в_своей_переписке()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(client, id, "Посмотрите смету по аренде");

        var html = await client.GetStringAsync("/Messages?q=смет");

        // Найденное слово обёрнуто подсветкой, поэтому фраза целиком
        // в разметке не встречается — проверяем части.
        Assert.Contains("Посмотрите ", html);
        Assert.Contains("<mark>смет</mark>", html);
        Assert.Contains("у по аренде", html);
    }

    [Fact]
    public async Task Поиск_не_заглядывает_в_чужие_переписки()
    {
        // Иначе строка поиска стала бы способом читать чужие разговоры.
        using var factory = CreateFactory();
        var id = AddConversation(factory, "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Совершенно секретная смета");

        var outsider = await factory.LoginAsAsync("sidorov", Users);

        var html = await outsider.GetStringAsync("/Messages?q=смета");

        Assert.DoesNotContain("Совершенно секретная", html);
    }

    [Fact]
    public async Task Поиск_по_одной_букве_ничего_не_ищет()
    {
        // Находится всё подряд, толку ноль, нагрузка есть.
        using var factory = CreateFactory();
        var id = AddConversation(factory, "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(client, id, "Смета готова");

        var html = await client.GetStringAsync("/Messages?q=с");

        Assert.Contains("Найдено в сообщениях: 0", html);
    }

    // ------------------------------------------------------------------
    // Журнал
    // ------------------------------------------------------------------

    [Fact]
    public async Task Полная_очистка_журнала_оставляет_запись_о_себе()
    {
        // Иначе очистка стала бы способом скрыть свои следы.
        using var factory = CreateFactory();

        factory.Seed(db =>
        {
            for (var i = 0; i < 5; i++)
            {
                db.AuditEntries.Add(new AuditEntry
                {
                    At = DateTime.UtcNow.AddDays(-i),
                    UserName = "petrov",
                    Action = AuditAction.Download,
                    Target = $"файл{i}.pdf"
                });
            }
        });

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);
        var token = await client.GetTokenAsync("/Admin/Audit");

        await client.PostFormAsync("/Admin/Audit?handler=PurgeAll", token, []);

        var entries = factory.Query(db => db.AuditEntries.ToList());

        Assert.Single(entries);
        Assert.Equal(AuditAction.PurgeAuditLog, entries[0].Action);
        Assert.Equal("ivanov", entries[0].UserName);
    }

    [Fact]
    public async Task Журнал_закрыт_для_обычного_пользователя()
    {
        using var factory = CreateFactory();

        var client = await factory.LoginAsAsync("petrov", Users);

        var response = await client.GetAsync("/Admin/Audit");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    // ------------------------------------------------------------------
    // Вспомогательное
    // ------------------------------------------------------------------

    private static async Task PublishAsync(
        HttpClient client, string title, string body, string fileName, string content)
    {
        var token = await client.GetTokenAsync("/Announcements/Create");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(title), "Input.Title" },
            { new StringContent(body), "Input.Body" },
            { new StringContent(content), "attachments", fileName }
        };

        await client.PostAsync("/Announcements/Create", form);
    }

    private static int AddConversation(PortalFactory factory, params string[] members)
    {
        var id = 0;

        factory.Seed(db =>
        {
            var conversation = new Conversation
            {
                IsGroup = false,
                PairKey = ConversationService.PairKeyFor(members[0], members[1]),
                CreatedByUserName = members[0],
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow
            };

            foreach (var member in members)
            {
                conversation.Participants.Add(new ConversationParticipant
                {
                    UserName = member,
                    DisplayName = "Пользователь " + member,
                    JoinedAt = DateTime.UtcNow
                });
            }

            db.Conversations.Add(conversation);
            db.SaveChanges();

            id = conversation.Id;
        });

        return id;
    }

    private static async Task SendAsync(HttpClient client, int conversationId, string text)
    {
        var token = await client.GetTokenAsync($"/Messages?id={conversationId}");

        await client.PostFormAsync(
            $"/Messages?handler=Send&id={conversationId}", token,
            new Dictionary<string, string> { ["Body"] = text });
    }
}
