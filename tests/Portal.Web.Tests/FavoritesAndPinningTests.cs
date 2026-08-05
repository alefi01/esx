using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Portal.Web.Data;

namespace Portal.Web.Tests;

/// <summary>
/// Избранное и закрепление объявлений.
///
/// Главные вопросы здесь два. Первый: не стало ли избранное обходным путём
/// к закрытым папкам — отметил, пока пускали, видишь и после. Второй:
/// действительно ли закреплённое объявление встаёт наверх ленты,
/// а не просто получает пометку.
/// </summary>
public class FavoritesAndPinningTests
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    /// <summary>Две папки: общая и закрытая, с файлом в каждой.</summary>
    private static PortalFactory PrepareFolders()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"fav-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        var factory = new PortalFactory { StorageRootPath = storage };

        factory.Seed(db =>
        {
            var open = new StorageFolder
            {
                Name = "Общая",
                CreatedAt = DateTime.UtcNow,
                InheritPermissions = true
            };

            var closed = new StorageFolder
            {
                Name = "Закрытая",
                CreatedAt = DateTime.UtcNow,
                // Наследование выключено, своих прав для WebUsers нет —
                // значит, обычному сотруднику папка не видна вовсе.
                InheritPermissions = false
            };

            db.Folders.AddRange(open, closed);
            db.SaveChanges();

            db.FolderPermissions.Add(new FolderPermission
            {
                FolderId = open.Id,
                GroupName = Users,
                Access = FolderAccess.Write
            });

            db.Files.Add(new StoredFile
            {
                FolderId = open.Id,
                OriginalName = "открытый.txt",
                StorageName = "s1",
                SizeBytes = 10,
                ContentType = "text/plain",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = "ivanov",
                UploadedByDisplayName = "Иванов Иван"
            });

            db.Files.Add(new StoredFile
            {
                FolderId = closed.Id,
                OriginalName = "секретный.txt",
                StorageName = "s2",
                SizeBytes = 10,
                ContentType = "text/plain",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = "boss",
                UploadedByDisplayName = "Начальников Начальник"
            });

            db.SaveChanges();
        });

        return factory;
    }

    private static async Task<HttpResponseMessage> ToggleAsync(
        HttpClient client, string query)
    {
        var token = await client.GetTokenAsync("/Files");

        return await client.PostFormAsync(
            "/Files?handler=Favorite&" + query, token, new Dictionary<string, string>());
    }

    [Fact]
    public async Task Звёздочка_ставится_и_снимается_одной_кнопкой()
    {
        using var factory = PrepareFolders();
        var client = await factory.LoginAsAsync("ivanov", Users);

        var first = await ToggleAsync(client, "fileId=1");
        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Contains("true", await first.Content.ReadAsStringAsync());

        var second = await ToggleAsync(client, "fileId=1");
        Assert.Contains("false", await second.Content.ReadAsStringAsync());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        Assert.Empty(db.Favorites.ToList());
    }

    /// <summary>
    /// Отметить чужое нельзя. Иначе избранное превратилось бы в способ
    /// узнать, существует ли файл с определённым номером.
    /// </summary>
    [Fact]
    public async Task Нельзя_отметить_файл_из_недоступной_папки()
    {
        using var factory = PrepareFolders();
        var client = await factory.LoginAsAsync("ivanov", Users);

        var response = await ToggleAsync(client, "fileId=2");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// Права проверяются заново при каждом показе избранного. Если доступ
    /// к папке пропал, отметка остаётся в базе, но в список не попадает —
    /// иначе избранное пережило бы отзыв прав.
    /// </summary>
    [Fact]
    public async Task Избранное_не_показывает_то_к_чему_доступ_пропал()
    {
        using var factory = PrepareFolders();

        // Отметку ставит администратор — ему видно всё.
        var admin = await factory.LoginAsAsync("boss", Users, Admins);
        await ToggleAsync(admin, "fileId=2");

        // А смотрит в избранное обычный сотрудник. У него та же отметка
        // в базе не появится, но проверим и другое: даже если бы появилась,
        // страница не показала бы файл из закрытой папки.
        factory.Seed(db => db.Favorites.Add(new Favorite
        {
            UserName = "ivanov",
            FileId = 2,
            AddedAt = DateTime.UtcNow
        }));

        var client = await factory.LoginAsAsync("ivanov", Users);

        var html = await client.GetStringAsync("/Files/Favorites");

        Assert.DoesNotContain("секретный.txt", html);
        Assert.Contains("В избранном пусто", html);
    }

    [Fact]
    public async Task Отмеченный_файл_виден_в_избранном()
    {
        using var factory = PrepareFolders();
        var client = await factory.LoginAsAsync("ivanov", Users);

        await ToggleAsync(client, "fileId=1");

        var html = await client.GetStringAsync("/Files/Favorites");

        Assert.Contains("открытый.txt", html);
    }

    /// <summary>Избранное личное: чужие отметки видеть нельзя.</summary>
    [Fact]
    public async Task Избранное_у_каждого_своё()
    {
        using var factory = PrepareFolders();

        var ivanov = await factory.LoginAsAsync("ivanov", Users);
        await ToggleAsync(ivanov, "fileId=1");

        var petrov = await factory.LoginAsAsync("petrov", Users);
        var html = await petrov.GetStringAsync("/Files/Favorites");

        Assert.Contains("В избранном пусто", html);
    }

    /// <summary>Папка с двумя объявлениями: старое и новое.</summary>
    private static PortalFactory PrepareAnnouncements()
    {
        var factory = new PortalFactory();

        factory.Seed(db =>
        {
            db.Announcements.Add(new Announcement
            {
                Title = "Старое объявление",
                Body = "текст",
                AuthorUserName = "boss",
                AuthorDisplayName = "Начальников Начальник",
                CreatedAt = DateTime.UtcNow.AddDays(-10)
            });

            db.Announcements.Add(new Announcement
            {
                Title = "Свежее объявление",
                Body = "текст",
                AuthorUserName = "boss",
                AuthorDisplayName = "Начальников Начальник",
                CreatedAt = DateTime.UtcNow
            });

            db.SaveChanges();
        });

        return factory;
    }

    [Fact]
    public async Task Закреплённое_объявление_встаёт_наверх_ленты()
    {
        using var factory = PrepareAnnouncements();
        var client = await factory.LoginAsAsync("boss", Users, Admins);

        // До закрепления сверху свежее.
        var before = await client.GetStringAsync("/Announcements");

        Assert.True(
            before.IndexOf("Свежее объявление", StringComparison.Ordinal)
            < before.IndexOf("Старое объявление", StringComparison.Ordinal));

        var token = await client.GetTokenAsync("/Announcements");

        var response = await client.PostFormAsync(
            "/Announcements?handler=Pin&id=1", token, new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        // После — сверху закреплённое, хотя оно и старее на десять дней.
        var after = await client.GetStringAsync("/Announcements");

        Assert.True(
            after.IndexOf("Старое объявление", StringComparison.Ordinal)
            < after.IndexOf("Свежее объявление", StringComparison.Ordinal));

        Assert.Contains("Закреплено", after);
    }

    /// <summary>
    /// Закрепление не должно превращаться в правку: подпись «изменено»
    /// после него сбивала бы с толку — текст-то не менялся.
    /// </summary>
    [Fact]
    public async Task Закрепление_не_помечает_объявление_изменённым()
    {
        using var factory = PrepareAnnouncements();
        var client = await factory.LoginAsAsync("boss", Users, Admins);

        var token = await client.GetTokenAsync("/Announcements");

        await client.PostFormAsync(
            "/Announcements?handler=Pin&id=1", token, new Dictionary<string, string>());

        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        var announcement = db.Announcements.Single(a => a.Id == 1);

        Assert.True(announcement.IsPinned);
        Assert.Null(announcement.UpdatedAt);
    }

    /// <summary>
    /// Кнопки закрепления нет у того, кто не может править объявление,
    /// но главное — сам запрос от него не проходит: скрытая кнопка
    /// это удобство интерфейса, а не защита.
    /// </summary>
    [Fact]
    public async Task Чужое_объявление_закрепить_нельзя()
    {
        using var factory = PrepareAnnouncements();
        var client = await factory.LoginAsAsync("ivanov", Users);

        var token = await client.GetTokenAsync("/Announcements");

        var response = await client.PostFormAsync(
            "/Announcements?handler=Pin&id=1", token, new Dictionary<string, string>());

        // При входе по cookie отказ в правах — это НЕ код 403, а перенаправление
        // на страницу «доступ запрещён». Так устроен ASP.NET Core: код 403
        // человеку показывать нечего, ему нужна страница с объяснением.
        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Contains("AccessDenied", response.Headers.Location?.ToString());

        // Главное — объявление осталось незакреплённым.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        Assert.False(db.Announcements.Single(a => a.Id == 1).IsPinned);
    }

    [Fact]
    public async Task Важное_объявление_помечается_в_ленте()
    {
        using var factory = PrepareAnnouncements();
        var client = await factory.LoginAsAsync("boss", Users, Admins);

        var token = await client.GetTokenAsync("/Announcements");

        await client.PostFormAsync(
            "/Announcements?handler=Important&id=2", token, new Dictionary<string, string>());

        var html = await client.GetStringAsync("/Announcements");

        Assert.Contains("Важное", html);
        Assert.Contains("post--important", html);
    }
}
