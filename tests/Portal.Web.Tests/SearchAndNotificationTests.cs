using System.Net;
using System.Text.Json;
using Portal.Web.Data;

namespace Portal.Web.Tests;

/// <summary>
/// Поиск по папке и колокольчик уведомлений.
///
/// У поиска есть неочевидная опасность: он ходит по всему дереву сразу,
/// и если забыть проверку прав, он превращается в способ узнать, что лежит
/// в закрытых папках. Проверки здесь именно про это.
/// </summary>
public class SearchAndNotificationTests
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    private static PortalFactory PrepareTree()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"search-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        var factory = new PortalFactory { StorageRootPath = storage };

        factory.Seed(db =>
        {
            var open = new StorageFolder
            {
                Name = "Общие", CreatedAt = DateTime.UtcNow, InheritPermissions = true
            };

            db.Folders.Add(open);
            db.SaveChanges();

            // Права по умолчанию закрыты, поэтому доступ на чтение
            // приходится выдать явно — иначе папки не увидит никто,
            // кроме администратора.
            db.FolderPermissions.Add(new FolderPermission
            {
                FolderId = open.Id,
                GroupName = Users,
                Access = FolderAccess.Read
            });

            db.SaveChanges();

            var secret = new StorageFolder
            {
                Name = "Кадры",
                ParentId = open.Id,
                CreatedAt = DateTime.UtcNow,
                // Наследование выключено и прав никому не выдано —
                // папка не видна никому, кроме администратора.
                InheritPermissions = false
            };

            db.Folders.Add(secret);
            db.SaveChanges();

            db.Files.Add(new StoredFile
            {
                FolderId = open.Id,
                OriginalName = "договор аренды.pdf",
                StorageName = "a",
                SizeBytes = 10,
                ContentType = "application/pdf",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = "ivanov",
                UploadedByDisplayName = "ivanov"
            });

            db.Files.Add(new StoredFile
            {
                FolderId = secret.Id,
                OriginalName = "договор с Петровым.pdf",
                StorageName = "b",
                SizeBytes = 10,
                ContentType = "application/pdf",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = "ivanov",
                UploadedByDisplayName = "ivanov"
            });

            db.SaveChanges();
        });

        return factory;
    }

    [Fact]
    public async Task Поиск_находит_файл_в_доступной_папке()
    {
        using var factory = PrepareTree();

        var client = await factory.LoginAsAsync("petrov", Users);

        var html = await client.GetStringAsync("/Files?id=1&q=договор");

        Assert.Contains("договор аренды.pdf", html);
    }

    [Fact]
    public async Task Поиск_не_показывает_файлы_из_закрытой_папки()
    {
        using var factory = PrepareTree();

        var client = await factory.LoginAsAsync("petrov", Users);

        var html = await client.GetStringAsync("/Files?id=1&q=договор");

        // Ни имени файла, ни даже намёка на существование папки.
        Assert.DoesNotContain("договор с Петровым", html);
        Assert.DoesNotContain("Кадры", html);
    }

    [Fact]
    public async Task Администратор_в_поиске_видит_всё()
    {
        using var factory = PrepareTree();

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var html = await client.GetStringAsync("/Files?id=1&q=договор");

        Assert.Contains("договор аренды.pdf", html);
        Assert.Contains("договор с Петровым.pdf", html);
    }

    [Fact]
    public async Task Первый_вход_не_выдаёт_старые_объявления_за_новые()
    {
        using var factory = new PortalFactory();

        factory.Seed(db =>
        {
            for (var i = 0; i < 5; i++)
            {
                db.Announcements.Add(new Announcement
                {
                    Title = $"Старое {i}",
                    Body = "текст",
                    AuthorUserName = "petrov",
                    AuthorDisplayName = "petrov",
                    CreatedAt = DateTime.UtcNow.AddDays(-i)
                });
            }
        });

        var client = await factory.LoginAsAsync("ivanov", Users);

        var summary = await ReadSummaryAsync(client);

        Assert.Equal(0, summary.Unread);
    }

    [Fact]
    public async Task Новое_объявление_попадает_в_непрочитанное()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("ivanov", Users);

        // Первый запрос заводит отметку «всё видел».
        await ReadSummaryAsync(client);

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Отключение воды",
            Body = "текст",
            AuthorUserName = "petrov",
            AuthorDisplayName = "Пользователь petrov",
            CreatedAt = DateTime.UtcNow
        }));

        var summary = await ReadSummaryAsync(client);

        Assert.Equal(1, summary.Unread);
        Assert.Equal("Отключение воды", summary.Items[0].Title);
    }

    [Fact]
    public async Task Отметка_прочитано_обнуляет_счётчик()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("ivanov", Users);
        await ReadSummaryAsync(client);

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Новое",
            Body = "текст",
            AuthorUserName = "petrov",
            AuthorDisplayName = "petrov",
            CreatedAt = DateTime.UtcNow
        }));

        Assert.Equal(1, (await ReadSummaryAsync(client)).Unread);

        var response = await client.PostAsync("/api/notifications/seen", null);
        response.EnsureSuccessStatusCode();

        Assert.Equal(0, (await ReadSummaryAsync(client)).Unread);
    }

    [Fact]
    public async Task Открытие_ленты_отмечает_объявления_прочитанными()
    {
        using var factory = new PortalFactory();

        var client = await factory.LoginAsAsync("ivanov", Users);
        await ReadSummaryAsync(client);

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Новое",
            Body = "текст",
            AuthorUserName = "petrov",
            AuthorDisplayName = "petrov",
            CreatedAt = DateTime.UtcNow
        }));

        await client.GetStringAsync("/Announcements");

        Assert.Equal(0, (await ReadSummaryAsync(client)).Unread);
    }

    [Fact]
    public async Task Уведомления_закрыты_для_неавторизованных()
    {
        using var factory = new PortalFactory();

        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/api/notifications");

        // Портал закрыт по умолчанию: гостя отправляют на вход.
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    private static async Task<Summary> ReadSummaryAsync(HttpClient client)
    {
        var json = await client.GetStringAsync("/api/notifications");

        return JsonSerializer.Deserialize<Summary>(json, JsonOptions)!;
    }

    private static readonly JsonSerializerOptions JsonOptions =
        new() { PropertyNameCaseInsensitive = true };

    private sealed record Summary(int Unread, List<Item> Items);

    private sealed record Item(string Kind, int Id, string Title, string Author, DateTime At, string Url);
}
