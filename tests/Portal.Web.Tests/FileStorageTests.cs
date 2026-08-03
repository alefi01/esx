using System.Net;
using System.Net.Http.Headers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Сквозные проверки файлового хранилища: загрузка, скачивание,
/// разграничение доступа, корзина и автоочистка.
/// </summary>
public class FileStorageTests : IDisposable
{
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), $"portal-storage-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private PortalFactory CreateFactory()
    {
        var factory = new PortalFactory();
        factory.StorageRootPath = _storageRoot;

        return factory;
    }

    private static async Task<HttpResponseMessage> UploadAsync(
        HttpClient client, int folderId, string fileName, byte[] content)
    {
        var token = await client.GetTokenAsync($"/Files?id={folderId}");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };

        var file = new ByteArrayContent(content);
        file.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(file, "uploads", fileName);

        return await client.PostAsync($"/Files?handler=Upload&folderId={folderId}", form);
    }

    private static int SeedFolder(PortalFactory factory, string name, params (string Group, FolderAccess Access)[] rights)
    {
        var id = 0;

        factory.Seed(db =>
        {
            var folder = new StorageFolder
            {
                Name = name,
                CreatedAt = DateTime.UtcNow,
                CreatedByUserName = "admin"
            };

            foreach (var (group, access) in rights)
            {
                folder.Permissions.Add(new FolderPermission { GroupName = group, Access = access });
            }

            db.Folders.Add(folder);
            db.SaveChanges();

            id = folder.Id;
        });

        return id;
    }

    [Fact]
    public async Task Файл_загружается_и_скачивается()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Договоры", ("Yuristy", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Yuristy");

        var content = "Содержимое договора"u8.ToArray();
        var upload = await UploadAsync(client, folderId, "договор.pdf", content);

        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);

        var saved = factory.Query(db => db.Files.Single());

        Assert.Equal("договор.pdf", saved.OriginalName);
        Assert.Equal(content.Length, saved.SizeBytes);
        Assert.Equal("application/pdf", saved.ContentType);
        Assert.Equal("ivanov", saved.UploadedByUserName);

        // Файл физически лежит на диске под обезличенным именем.
        var onDisk = Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories);
        Assert.Single(onDisk);
        Assert.DoesNotContain("договор", Path.GetFileName(onDisk[0]));

        var download = await client.GetAsync($"/Files?handler=Download&fileId={saved.Id}");

        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
        Assert.Equal(content, await download.Content.ReadAsByteArrayAsync());
    }

    [Fact]
    public async Task Без_права_чтения_файл_не_скачать()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Договоры", ("Yuristy", FolderAccess.Write));

        var author = await factory.LoginAsAsync("ivanov", "WebUsers", "Yuristy");
        await UploadAsync(author, folderId, "договор.pdf", "тайна"u8.ToArray());

        var fileId = factory.Query(db => db.Files.Single().Id);

        // Другой пользователь: доступ к порталу есть, прав на папку нет.
        var stranger = await factory.LoginAsAsync("sidorov", "WebUsers");

        var download = await stranger.GetAsync($"/Files?handler=Download&fileId={fileId}");

        // Именно 404, а не «запрещено»: не подтверждаем, что такой файл есть.
        Assert.Equal(HttpStatusCode.NotFound, download.StatusCode);
    }

    [Fact]
    public async Task Папка_без_прав_не_открывается()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Приказы по кадрам", ("Kadry", FolderAccess.Read));

        var stranger = await factory.LoginAsAsync("sidorov", "WebUsers");

        var page = await stranger.GetAsync($"/Files?id={folderId}");

        Assert.Equal(HttpStatusCode.NotFound, page.StatusCode);
    }

    [Fact]
    public async Task Право_чтения_не_даёт_загружать()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Договоры", ("Yuristy", FolderAccess.Read));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Yuristy");

        var upload = await UploadAsync(client, folderId, "договор.pdf", "текст"u8.ToArray());

        Assert.Equal(HttpStatusCode.Redirect, upload.StatusCode);
        Assert.Contains("AccessDenied", upload.Headers.Location!.ToString());
        Assert.Equal(0, factory.Query(db => db.Files.Count()));
    }

    [Fact]
    public async Task Опасный_файл_не_загружается_и_на_диск_не_попадает()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");

        await UploadAsync(client, folderId, "полезная_программа.exe", "MZ"u8.ToArray());

        Assert.Equal(0, factory.Query(db => db.Files.Count()));

        var onDisk = Directory.Exists(_storageRoot)
            ? Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories)
            : [];

        Assert.Empty(onDisk);
    }

    [Fact]
    public async Task Файл_больше_предела_папки_отклоняется()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        factory.Seed(db =>
        {
            db.Folders.First(f => f.Id == folderId).MaxFileSizeMb = 1;
        });

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");

        await UploadAsync(client, folderId, "большой.bin", new byte[2 * 1024 * 1024]);

        Assert.Equal(0, factory.Query(db => db.Files.Count()));
    }

    [Fact]
    public async Task Удаление_отправляет_файл_в_корзину_а_не_стирает()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "черновик.txt", "текст"u8.ToArray());

        var fileId = factory.Query(db => db.Files.Single().Id);

        var token = await client.GetTokenAsync($"/Files?id={folderId}");
        await client.PostFormAsync($"/Files?handler=DeleteFile&fileId={fileId}", token, []);

        var file = factory.Query(db => db.Files.Single());

        Assert.NotNull(file.DeletedAt);
        Assert.Equal("ivanov", file.DeletedByUserName);

        // Файл всё ещё на диске — иначе восстанавливать было бы нечего.
        Assert.Single(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));

        // И в списке папки он больше не показывается. Проверяем по ссылке
        // скачивания, а не по имени: имя есть ещё и в сообщении «перемещён в корзину».
        var page = await client.GetStringAsync($"/Files?id={folderId}");
        Assert.DoesNotContain($"handler=Download&amp;fileId={fileId}", page);
    }

    [Fact]
    public async Task Файл_из_корзины_восстанавливается()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "нужный.txt", "текст"u8.ToArray());

        var fileId = factory.Query(db => db.Files.Single().Id);

        var token = await client.GetTokenAsync($"/Files?id={folderId}");
        await client.PostFormAsync($"/Files?handler=DeleteFile&fileId={fileId}", token, []);

        var trashToken = await client.GetTokenAsync("/Files/Trash");
        await client.PostFormAsync($"/Files/Trash?handler=Restore&fileId={fileId}", trashToken, []);

        Assert.Null(factory.Query(db => db.Files.Single().DeletedAt));
    }

    [Fact]
    public async Task Непустую_папку_удалить_нельзя()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Manage));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "файл.txt", "текст"u8.ToArray());

        var token = await client.GetTokenAsync($"/Files?id={folderId}");
        await client.PostFormAsync($"/Files?handler=DeleteFolder&folderId={folderId}", token, []);

        Assert.Equal(1, factory.Query(db => db.Folders.Count()));
    }

    [Fact]
    public async Task Пустая_папка_удаляется()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Пустая", ("Vse", FolderAccess.Manage));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");

        var token = await client.GetTokenAsync($"/Files?id={folderId}");
        await client.PostFormAsync($"/Files?handler=DeleteFolder&folderId={folderId}", token, []);

        Assert.Equal(0, factory.Query(db => db.Folders.Count()));
    }

    [Fact]
    public async Task Действия_записываются_в_журнал()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "договор.pdf", "текст"u8.ToArray());

        var fileId = factory.Query(db => db.Files.Single().Id);
        await client.GetAsync($"/Files?handler=Download&fileId={fileId}");

        var entries = factory.Query(db => db.AuditEntries.OrderBy(e => e.Id).ToList());

        Assert.Contains(entries, e => e.Action == AuditAction.Upload && e.Target == "договор.pdf");
        Assert.Contains(entries, e => e.Action == AuditAction.Download && e.Target == "договор.pdf");
        Assert.All(entries, e => Assert.Equal("ivanov", e.UserName));
    }

    [Fact]
    public async Task Автоочистка_отправляет_старые_файлы_в_корзину()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Временное", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "старый.txt", "текст"u8.ToArray());
        await UploadAsync(client, folderId, "новый.txt", "текст"u8.ToArray());

        factory.Seed(db =>
        {
            // Срок хранения — 7 дней; один файл «состарим».
            db.Folders.First(f => f.Id == folderId).RetentionDays = 7;
            db.Files.First(f => f.OriginalName == "старый.txt").UploadedAt = DateTime.UtcNow.AddDays(-30);
        });

        var cleanup = factory.Services.GetRequiredService<IEnumerable<IHostedService>>()
            .OfType<StorageCleanupService>()
            .Single();

        await cleanup.RunOnceAsync(CancellationToken.None);

        Assert.NotNull(factory.Query(db => db.Files.Single(f => f.OriginalName == "старый.txt").DeletedAt));
        Assert.Null(factory.Query(db => db.Files.Single(f => f.OriginalName == "новый.txt").DeletedAt));

        // И это попало в журнал — вопрос «куда делся файл» не должен оставаться без ответа.
        Assert.Contains(
            factory.Query(db => db.AuditEntries.ToList()),
            e => e.Action == AuditAction.RetentionCleanup && e.Target == "старый.txt");
    }

    [Fact]
    public async Task Автоочистка_не_трогает_папки_без_настройки()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Постоянное", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "древний.txt", "текст"u8.ToArray());

        factory.Seed(db =>
        {
            db.Files.Single().UploadedAt = DateTime.UtcNow.AddYears(-5);
        });

        var cleanup = factory.Services.GetRequiredService<IEnumerable<IHostedService>>()
            .OfType<StorageCleanupService>()
            .Single();

        await cleanup.RunOnceAsync(CancellationToken.None);

        // Автоочистка по умолчанию выключена — файл на месте.
        Assert.Null(factory.Query(db => db.Files.Single().DeletedAt));
    }

    [Fact]
    public async Task Корзина_вычищается_по_истечении_срока()
    {
        using var factory = CreateFactory();
        var folderId = SeedFolder(factory, "Обмен", ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        await UploadAsync(client, folderId, "удалённый.txt", "текст"u8.ToArray());

        factory.Seed(db =>
        {
            var file = db.Files.Single();
            file.DeletedAt = DateTime.UtcNow.AddDays(-60);   // срок корзины по умолчанию 30 дней
            file.DeletedByUserName = "ivanov";
        });

        var cleanup = factory.Services.GetRequiredService<IEnumerable<IHostedService>>()
            .OfType<StorageCleanupService>()
            .Single();

        await cleanup.RunOnceAsync(CancellationToken.None);

        Assert.Equal(0, factory.Query(db => db.Files.Count()));
        Assert.Empty(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));
    }
}
