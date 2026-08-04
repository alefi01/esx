using System.Net;
using System.Net.Http.Headers;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Копирование, перемещение и групповое удаление файлов — то, что в интерфейсе
/// делается через Ctrl+C / Ctrl+X / Ctrl+V, перетаскивание и клавишу Delete.
///
/// Проверяется именно серверная часть: нажатия в браузере — это удобство,
/// а запрет должен работать и тогда, когда запрос отправили в обход страницы.
/// </summary>
public class FileClipboardTests : IDisposable
{
    private readonly string _storageRoot =
        Path.Combine(Path.GetTempPath(), $"portal-clip-{Guid.NewGuid():N}");

    public void Dispose()
    {
        if (Directory.Exists(_storageRoot))
        {
            Directory.Delete(_storageRoot, recursive: true);
        }

        GC.SuppressFinalize(this);
    }

    private PortalFactory CreateFactory() => new() { StorageRootPath = _storageRoot };

    private static int AddFolder(
        PortalFactory factory, string name, int? parentId = null,
        params (string Group, FolderAccess Access)[] rights)
    {
        var id = 0;

        factory.Seed(db =>
        {
            var folder = new StorageFolder
            {
                Name = name,
                ParentId = parentId,
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

    private static async Task<int> UploadAsync(
        PortalFactory factory, HttpClient client, int folderId, string fileName, int sizeBytes = 32)
    {
        var token = await client.GetTokenAsync($"/Files?id={folderId}");

        using var form = new MultipartFormDataContent
        {
            { new StringContent(token), "__RequestVerificationToken" }
        };

        var content = new ByteArrayContent(new byte[sizeBytes]);
        content.Headers.ContentType = new MediaTypeHeaderValue("application/octet-stream");
        form.Add(content, "uploads", fileName);

        await client.PostAsync($"/Files?handler=Upload&folderId={folderId}", form);

        return factory.Query(db => db.Files.Single(f => f.OriginalName == fileName && f.FolderId == folderId).Id);
    }

    private static Task<HttpResponseMessage> PostIdsAsync(
        HttpClient client, string url, string token, int targetFolderId, params int[] fileIds)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("targetFolderId", targetFolderId.ToString())
        };

        fields.AddRange(fileIds.Select(id => new KeyValuePair<string, string>("fileIds", id.ToString())));

        return client.PostAsync(url, new FormUrlEncodedContent(fields));
    }

    /// <summary>
    /// Копирования между папками портала больше нет.
    ///
    /// Оно жило только вместе с собственным буфером обмена (Ctrl+C — Ctrl+V),
    /// который люди принимали за буфер обмена Windows. Обработчик убран,
    /// и запрос к нему не должен ничего делать — иначе получилась бы
    /// возможность, о которой в интерфейсе нет ни следа.
    /// </summary>
    [Fact]
    public async Task Обработчика_копирования_больше_нет()
    {
        using var factory = CreateFactory();
        var source = AddFolder(factory, "Исходная", null, ("Vse", FolderAccess.Write));
        var target = AddFolder(factory, "Целевая", null, ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var fileId = await UploadAsync(factory, client, source, "договор.pdf");

        var token = await client.GetTokenAsync($"/Files?id={target}");

        await PostIdsAsync(client, "/Files?handler=Copy", token, target, fileId);

        // Файл остался один и там же, где был.
        var files = factory.Query(db => db.Files.ToList());

        Assert.Single(files);
        Assert.Equal(source, files[0].FolderId);
    }

    [Fact]
    public async Task Файл_перемещается_и_из_исходной_папки_пропадает()
    {
        using var factory = CreateFactory();
        var source = AddFolder(factory, "Исходная", null, ("Vse", FolderAccess.Write));
        var target = AddFolder(factory, "Целевая", null, ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var fileId = await UploadAsync(factory, client, source, "договор.pdf");

        var token = await client.GetTokenAsync($"/Files?id={target}");
        await PostIdsAsync(client, "/Files?handler=Move", token, target, fileId);

        var file = factory.Query(db => db.Files.Single());

        Assert.Equal(target, file.FolderId);

        // На диске файл ровно один — перемещение не должно плодить копии.
        Assert.Single(Directory.GetFiles(_storageRoot, "*", SearchOption.AllDirectories));

        // И он лежит в папке целевого каталога, а не исходного.
        var download = await client.GetAsync($"/Files?handler=Download&fileId={fileId}");
        Assert.Equal(HttpStatusCode.OK, download.StatusCode);
    }

    [Fact]
    public async Task Без_права_записи_в_целевую_папку_перемещение_запрещено()
    {
        using var factory = CreateFactory();
        var source = AddFolder(factory, "Исходная", null, ("Vse", FolderAccess.Write));
        var target = AddFolder(factory, "Только чтение", null, ("Vse", FolderAccess.Read));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var fileId = await UploadAsync(factory, client, source, "договор.pdf");

        var token = await client.GetTokenAsync($"/Files?id={source}");
        var response = await PostIdsAsync(client, "/Files?handler=Move", token, target, fileId);

        Assert.Contains("AccessDenied", response.Headers.Location!.ToString());
        Assert.Equal(1, factory.Query(db => db.Files.Count()));
    }

    [Fact]
    public async Task Без_права_читать_исходную_папку_файл_не_забрать()
    {
        // Попытка «вытащить» файл из закрытой папки, зная его номер.
        using var factory = CreateFactory();
        var secret = AddFolder(factory, "Секретно", null, ("Direktsiya", FolderAccess.Write));
        var mine = AddFolder(factory, "Моя", null, ("Vse", FolderAccess.Write));

        var insider = await factory.LoginAsAsync("director", "WebUsers", "Direktsiya");
        var fileId = await UploadAsync(factory, insider, secret, "тайна.pdf");

        var outsider = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var token = await outsider.GetTokenAsync($"/Files?id={mine}");

        await PostIdsAsync(outsider, "/Files?handler=Move", token, mine, fileId);

        // Ничего не вышло: файл по-прежнему один и лежит в закрытой папке.
        var files = factory.Query(db => db.Files.ToList());
        Assert.Single(files);
        Assert.Equal(secret, files[0].FolderId);
    }

    [Fact]
    public async Task Ограничения_целевой_папки_действуют_и_при_перемещении()
    {
        // Иначе перетаскиванием файла на папку можно было бы обойти
        // и предел размера, и квоту папки.
        using var factory = CreateFactory();
        var source = AddFolder(factory, "Исходная", null, ("Vse", FolderAccess.Write));
        var target = AddFolder(factory, "Целевая", null, ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var fileId = await UploadAsync(factory, client, source, "большой.bin", 300_000);

        factory.Seed(db =>
        {
            // В целевой папке предел 1 КБ — файл на 300 КБ туда не поместится.
            db.Folders.First(f => f.Id == target).MaxFileSizeMb = 1;
            db.Files.First(f => f.Id == fileId).SizeBytes = 5L * 1024 * 1024;
        });

        var token = await client.GetTokenAsync($"/Files?id={target}");
        await PostIdsAsync(client, "/Files?handler=Move", token, target, fileId);

        // Файл остался в исходной папке: предел целевой его не пустил.
        Assert.Equal(source, factory.Query(db => db.Files.Single().FolderId));
    }

    [Fact]
    public async Task Перемещение_чужого_файла_без_прав_управления_запрещено()
    {
        using var factory = CreateFactory();
        var source = AddFolder(factory, "Общая", null, ("Vse", FolderAccess.Write));
        var target = AddFolder(factory, "Моя", null, ("Vse", FolderAccess.Write));

        var author = await factory.LoginAsAsync("petrov", "WebUsers", "Vse");
        var fileId = await UploadAsync(factory, author, source, "чужой.pdf");

        // У ivanov есть право записи, но файл загрузил не он,
        // а управления папкой у него нет.
        var other = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var token = await other.GetTokenAsync($"/Files?id={target}");

        await PostIdsAsync(other, "/Files?handler=Move", token, target, fileId);

        Assert.Equal(source, factory.Query(db => db.Files.Single().FolderId));
    }

    [Fact]
    public async Task Несколько_файлов_удаляются_разом()
    {
        using var factory = CreateFactory();
        var folder = AddFolder(factory, "Обмен", null, ("Vse", FolderAccess.Write));

        var client = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");

        var first = await UploadAsync(factory, client, folder, "первый.txt");
        var second = await UploadAsync(factory, client, folder, "второй.txt");
        var third = await UploadAsync(factory, client, folder, "третий.txt");

        var token = await client.GetTokenAsync($"/Files?id={folder}");

        var fields = new List<KeyValuePair<string, string>>
        {
            new("__RequestVerificationToken", token),
            new("fileIds", first.ToString()),
            new("fileIds", second.ToString())
        };

        await client.PostAsync($"/Files?handler=DeleteFiles&folderId={folder}", new FormUrlEncodedContent(fields));

        Assert.Equal(2, factory.Query(db => db.Files.Count(f => f.DeletedAt != null)));
        Assert.Null(factory.Query(db => db.Files.Single(f => f.Id == third).DeletedAt));
    }

    [Fact]
    public async Task Администратор_видит_объём_папки_вместе_с_вложенными()
    {
        using var factory = CreateFactory();
        var parent = AddFolder(factory, "Договоры", null, ("Vse", FolderAccess.Write));
        var child = AddFolder(factory, "2026", parent, ("Vse", FolderAccess.Write));

        var admin = await factory.LoginAsAsync("admin", "WebUsers", "WebAdmins", "Vse");

        await UploadAsync(factory, admin, parent, "верхний.bin", 1000);
        await UploadAsync(factory, admin, child, "вложенный.bin", 2000);

        // На странице верхнего уровня у папки «Договоры» должен быть показан
        // суммарный объём — свой файл плюс файл вложенной папки.
        var html = await admin.GetStringAsync("/Files");

        Assert.Contains("Договоры", html);
        Assert.Contains("2,9 КБ", html.Replace(' ', ' '));
    }

    [Fact]
    public async Task Обычному_пользователю_объёмы_папок_не_показываются()
    {
        using var factory = CreateFactory();
        var parent = AddFolder(factory, "Договоры", null, ("Vse", FolderAccess.Read));
        var child = AddFolder(factory, "2026", parent, ("Vse", FolderAccess.Read));

        var admin = await factory.LoginAsAsync("admin", "WebUsers", "WebAdmins");
        await UploadAsync(factory, admin, child, "файл.bin", 5000);

        var reader = await factory.LoginAsAsync("ivanov", "WebUsers", "Vse");
        var html = await reader.GetStringAsync("/Files");

        Assert.Contains("Договоры", html);
        // Размер папки — косвенный признак её содержимого; читателю он не нужен.
        Assert.DoesNotContain("4,9 КБ", html.Replace(' ', ' '));
    }
}
