using System.Net;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Messaging;

namespace Portal.Web.Tests;

/// <summary>
/// Переписки: доступ, группы, вложения, непрочитанное.
///
/// Упор на отказы. Переписка — это то, что человек считает личным,
/// и цена ошибки здесь выше, чем в файловом хранилище: увидеть чужой
/// документ неприятно, а прочитать чужой разговор — это уже совсем другое.
/// </summary>
public class MessagingTests : IDisposable
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), $"messages-{Guid.NewGuid():N}");

    public MessagingTests() => Directory.CreateDirectory(_storageRoot);

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private PortalFactory CreateFactory() => new() { StorageRootPath = _storageRoot };

    /// <summary>
    /// Заводит беседу напрямую в базе.
    /// Через интерфейс это делалось бы дольше, а проверяем мы не создание.
    /// </summary>
    private static int AddConversation(
        PortalFactory factory, bool isGroup, string title, params string[] members)
    {
        var id = 0;

        factory.Seed(db =>
        {
            var conversation = new Conversation
            {
                IsGroup = isGroup,
                Title = title,
                PairKey = isGroup ? "" : ConversationService.PairKeyFor(members[0], members[1]),
                CreatedByUserName = members[0],
                CreatedAt = DateTime.UtcNow,
                LastMessageAt = DateTime.UtcNow
            };

            for (var i = 0; i < members.Length; i++)
            {
                conversation.Participants.Add(new ConversationParticipant
                {
                    UserName = members[i],
                    DisplayName = "Пользователь " + members[i],
                    IsOwner = i == 0,
                    JoinedAt = DateTime.UtcNow
                });
            }

            db.Conversations.Add(conversation);
            db.SaveChanges();

            id = conversation.Id;
        });

        return id;
    }

    /// <param name="tokenFrom">
    /// Откуда взять токен защиты форм. По умолчанию — со страницы самой беседы,
    /// но постороннему она недоступна (и это правильно), поэтому в проверках
    /// «чужой пытается написать» токен берётся с общего списка переписок.
    /// </param>
    private static async Task SendAsync(
        HttpClient client, int conversationId, string text, string? tokenFrom = null)
    {
        var token = await client.GetTokenAsync(tokenFrom ?? $"/Messages?id={conversationId}");

        await client.PostFormAsync(
            $"/Messages?handler=Send&id={conversationId}", token, new Dictionary<string, string>
            {
                ["Body"] = text
            });
    }

    // ------------------------------------------------------------------
    // Доступ
    // ------------------------------------------------------------------

    [Fact]
    public async Task Участник_видит_переписку()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(client, id, "Здравствуйте, Пётр");

        var html = await client.GetStringAsync($"/Messages?id={id}");

        Assert.Contains("Здравствуйте, Пётр", html);
    }

    [Fact]
    public async Task Посторонний_не_видит_чужую_переписку()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Разговор двоих");

        var outsider = await factory.LoginAsAsync("sidorov", Users);

        var response = await outsider.GetAsync($"/Messages?id={id}");

        // Именно «не найдено»: подтверждать, что такая переписка существует,
        // постороннему незачем.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Посторонний_не_может_написать_в_чужую_переписку()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var outsider = await factory.LoginAsAsync("sidorov", Users);
        await SendAsync(outsider, id, "влезаю в разговор", tokenFrom: "/Messages");

        Assert.Equal(0, factory.Query(db => db.Messages.Count()));
    }

    [Fact]
    public async Task Администратор_читает_чужую_переписку_но_не_пишет_в_неё()
    {
        // Полный доступ администратора — сознательное решение заказчика.
        // Но писать от своего имени в чужой беседе он не должен: это подлог.
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Служебная переписка");

        var admin = await factory.LoginAsAsync("boss", Users, Admins);

        var html = await admin.GetStringAsync($"/Messages?id={id}");

        Assert.Contains("Служебная переписка", html);

        await SendAsync(admin, id, "а я тут пишу");

        Assert.Equal(1, factory.Query(db => db.Messages.Count()));
    }

    // ------------------------------------------------------------------
    // Переписка двоих
    // ------------------------------------------------------------------

    [Fact]
    public void Ключ_пары_не_зависит_от_порядка()
    {
        // Именно на этом держится то, что «Иванов → Петров» и «Петров → Иванов» —
        // одна беседа, а не две параллельные.
        Assert.Equal(
            ConversationService.PairKeyFor("ivanov", "petrov"),
            ConversationService.PairKeyFor("petrov", "ivanov"));

        Assert.Equal(
            ConversationService.PairKeyFor("Ivanov", "PETROV"),
            ConversationService.PairKeyFor("petrov", "ivanov"));

        Assert.NotEqual(
            ConversationService.PairKeyFor("ivanov", "petrov"),
            ConversationService.PairKeyFor("ivanov", "sidorov"));
    }

    [Fact]
    public async Task Повторное_обращение_к_тому_же_человеку_не_плодит_бесед()
    {
        using var factory = CreateFactory();

        factory.Seed(db =>
        {
            db.SeenStates.Add(new UserSeenState { UserName = "petrov", DisplayName = "Пётр Петров" });
            db.SeenStates.Add(new UserSeenState { UserName = "ivanov", DisplayName = "Иван Иванов" });
        });

        var client = await factory.LoginAsAsync("ivanov", Users);
        var token = await client.GetTokenAsync("/Messages");

        await client.PostFormAsync("/Messages?handler=Start", token,
            new Dictionary<string, string> { ["withUserName"] = "petrov" });

        await client.PostFormAsync("/Messages?handler=Start", token,
            new Dictionary<string, string> { ["withUserName"] = "petrov" });

        Assert.Equal(1, factory.Query(db => db.Conversations.Count()));
    }

    // ------------------------------------------------------------------
    // Группы
    // ------------------------------------------------------------------

    [Fact]
    public async Task Создатель_группы_добавляет_и_убирает_людей()
    {
        using var factory = CreateFactory();

        factory.Seed(db =>
        {
            db.SeenStates.Add(new UserSeenState { UserName = "sidorov", DisplayName = "Сидоров" });
        });

        var id = AddConversation(factory, true, "Отдел снабжения", "ivanov", "petrov");

        var owner = await factory.LoginAsAsync("ivanov", Users);
        var token = await owner.GetTokenAsync($"/Messages?id={id}");

        await owner.PostFormAsync($"/Messages?handler=AddMember&id={id}", token,
            new Dictionary<string, string> { ["memberUserName"] = "sidorov" });

        Assert.Equal(3, factory.Query(db => db.Participants.Count(p => p.ConversationId == id)));

        await owner.PostFormAsync($"/Messages?handler=RemoveMember&id={id}", token,
            new Dictionary<string, string> { ["memberUserName"] = "sidorov" });

        Assert.Equal(2, factory.Query(db => db.Participants.Count(p => p.ConversationId == id)));
    }

    [Fact]
    public async Task Обычный_участник_не_меняет_состав_группы()
    {
        using var factory = CreateFactory();

        factory.Seed(db =>
        {
            db.SeenStates.Add(new UserSeenState { UserName = "sidorov", DisplayName = "Сидоров" });
        });

        var id = AddConversation(factory, true, "Отдел", "ivanov", "petrov");

        var member = await factory.LoginAsAsync("petrov", Users);
        var token = await member.GetTokenAsync($"/Messages?id={id}");

        await member.PostFormAsync($"/Messages?handler=AddMember&id={id}", token,
            new Dictionary<string, string> { ["memberUserName"] = "sidorov" });

        Assert.Equal(2, factory.Query(db => db.Participants.Count(p => p.ConversationId == id)));
    }

    [Fact]
    public async Task Любой_участник_может_выйти_из_группы_сам()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, true, "Отдел", "ivanov", "petrov");

        var member = await factory.LoginAsAsync("petrov", Users);
        var token = await member.GetTokenAsync($"/Messages?id={id}");

        await member.PostFormAsync($"/Messages?handler=RemoveMember&id={id}", token,
            new Dictionary<string, string> { ["memberUserName"] = "petrov" });

        Assert.Equal(1, factory.Query(db => db.Participants.Count(p => p.ConversationId == id)));
    }

    [Fact]
    public async Task Уход_создателя_передаёт_группу_следующему()
    {
        // Иначе группа осталась бы без управления: ни переименовать,
        // ни добавить человека было бы уже некому.
        using var factory = CreateFactory();
        var id = AddConversation(factory, true, "Отдел", "ivanov", "petrov");

        var owner = await factory.LoginAsAsync("ivanov", Users);
        var token = await owner.GetTokenAsync($"/Messages?id={id}");

        await owner.PostFormAsync($"/Messages?handler=RemoveMember&id={id}", token,
            new Dictionary<string, string> { ["memberUserName"] = "ivanov" });

        var remaining = factory.Query(db =>
            db.Participants.Where(p => p.ConversationId == id).ToList());

        Assert.Single(remaining);
        Assert.True(remaining[0].IsOwner);
        Assert.Equal("petrov", remaining[0].UserName);
    }

    // ------------------------------------------------------------------
    // Сообщения
    // ------------------------------------------------------------------

    [Fact]
    public async Task Текст_сообщения_не_становится_разметкой()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(client, id, "<script>alert(1)</script>");

        var html = await client.GetStringAsync($"/Messages?id={id}");

        Assert.DoesNotContain("<script>alert", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public async Task Удаление_сообщения_видно_обоим()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Сказанное сгоряча");

        var messageId = factory.Query(db => db.Messages.Single().Id);
        var token = await author.GetTokenAsync($"/Messages?id={id}");

        await author.PostFormAsync($"/Messages?handler=DeleteMessage&id={id}", token,
            new Dictionary<string, string> { ["messageId"] = messageId.ToString() });

        var other = await factory.LoginAsAsync("petrov", Users);
        var html = await other.GetStringAsync($"/Messages?id={id}");

        Assert.DoesNotContain("Сказанное сгоряча", html);
        Assert.Contains("сообщение удалено", html);
    }

    [Fact]
    public async Task Чужое_сообщение_удалить_нельзя()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Моё сообщение");

        var messageId = factory.Query(db => db.Messages.Single().Id);

        var other = await factory.LoginAsAsync("petrov", Users);
        var token = await other.GetTokenAsync($"/Messages?id={id}");

        await other.PostFormAsync($"/Messages?handler=DeleteMessage&id={id}", token,
            new Dictionary<string, string> { ["messageId"] = messageId.ToString() });

        Assert.Null(factory.Query(db => db.Messages.Single().DeletedAt));
    }

    // ------------------------------------------------------------------
    // Вложения
    // ------------------------------------------------------------------

    [Fact]
    public async Task Вложение_прикладывается_и_скачивается_участником()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);

        await UploadAsync(client, id, "смета.pdf", "содержимое сметы");

        var file = factory.Query(db => db.MessageFiles.Single());

        Assert.Equal("смета.pdf", file.OriginalName);

        var other = await factory.LoginAsAsync("petrov", Users);
        var response = await other.GetAsync($"/Messages?handler=Attachment&fileId={file.Id}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("содержимое сметы", await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Вложение_не_отдаётся_постороннему()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await UploadAsync(client, id, "тайна.pdf", "секрет");

        var fileId = factory.Query(db => db.MessageFiles.Single().Id);

        var outsider = await factory.LoginAsAsync("sidorov", Users);
        var response = await outsider.GetAsync($"/Messages?handler=Attachment&fileId={fileId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Опасное_расширение_не_приложить()
    {
        // Переписка не должна становиться обходным путём для того,
        // что запрещено в файловом хранилище.
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await UploadAsync(client, id, "вирус.exe", "MZ");

        Assert.Equal(0, factory.Query(db => db.MessageFiles.Count()));
    }

    [Fact]
    public async Task Удаление_сообщения_стирает_вложения_с_диска()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var client = await factory.LoginAsAsync("ivanov", Users);
        await UploadAsync(client, id, "черновик.pdf", "текст");

        Assert.Single(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));

        var messageId = factory.Query(db => db.Messages.Single().Id);
        var token = await client.GetTokenAsync($"/Messages?id={id}");

        await client.PostFormAsync($"/Messages?handler=DeleteMessage&id={id}", token,
            new Dictionary<string, string> { ["messageId"] = messageId.ToString() });

        Assert.Empty(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));
    }

    // ------------------------------------------------------------------
    // Непрочитанное
    // ------------------------------------------------------------------

    [Fact]
    public async Task Новое_сообщение_попадает_в_колокольчик_получателю()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var recipient = await factory.LoginAsAsync("petrov", Users);

        // Первое обращение заводит отметку «всё видел».
        await recipient.GetStringAsync("/api/notifications");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Посмотрите смету, пожалуйста");

        var json = await recipient.GetStringAsync("/api/notifications");

        Assert.Contains("\"unread\":1", json.Replace(" ", ""));
        Assert.Contains("message", json);
    }

    [Fact]
    public async Task Своё_сообщение_непрочитанным_не_считается()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await author.GetStringAsync("/api/notifications");

        await SendAsync(author, id, "Моё собственное сообщение");

        var json = await author.GetStringAsync("/api/notifications");

        Assert.Contains("\"unread\":0", json.Replace(" ", ""));
    }

    [Fact]
    public async Task Открытие_беседы_обнуляет_непрочитанное()
    {
        using var factory = CreateFactory();
        var id = AddConversation(factory, false, "", "ivanov", "petrov");

        var recipient = await factory.LoginAsAsync("petrov", Users);
        await recipient.GetStringAsync("/api/notifications");

        var author = await factory.LoginAsAsync("ivanov", Users);
        await SendAsync(author, id, "Первое");
        await SendAsync(author, id, "Второе");

        Assert.Contains("\"unread\":2",
            (await recipient.GetStringAsync("/api/notifications")).Replace(" ", ""));

        await recipient.GetStringAsync($"/Messages?id={id}");

        Assert.Contains("\"unread\":0",
            (await recipient.GetStringAsync("/api/notifications")).Replace(" ", ""));
    }

    [Fact]
    public async Task Переписки_закрыты_для_неавторизованных()
    {
        using var factory = CreateFactory();

        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/Messages");

        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task UploadAsync(
        HttpClient client, int conversationId, string fileName, string content)
    {
        var token = await client.GetTokenAsync($"/Messages?id={conversationId}");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" },
            { new StringContent(""), "Body" },
            { new StringContent(content), "attachments", fileName }
        };

        await client.PostAsync($"/Messages?handler=Send&id={conversationId}", form);
    }
}
