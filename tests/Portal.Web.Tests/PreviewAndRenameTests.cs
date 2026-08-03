using System.Net;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Предпросмотр, свойства и переименование.
///
/// Главное здесь — не «показалось ли красиво», а два вопроса:
/// отдаём ли мы наружу то, что отдавать нельзя, и можно ли переименованием
/// обойти проверки, которые действуют при загрузке.
/// </summary>
public class PreviewAndRenameTests
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    /// <summary>Заводит папку с одним файлом и настоящим содержимым на диске.</summary>
    private static (PortalFactory Factory, string Storage) Prepare(
        string fileName, string content = "содержимое", string owner = "ivanov")
    {
        var storage = Path.Combine(Path.GetTempPath(), $"preview-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        var factory = new PortalFactory { StorageRootPath = storage };

        factory.Seed(db =>
        {
            var folder = new StorageFolder
            {
                Name = "Документы",
                CreatedAt = DateTime.UtcNow,
                InheritPermissions = true
            };

            db.Folders.Add(folder);
            db.SaveChanges();

            db.Files.Add(new StoredFile
            {
                FolderId = folder.Id,
                OriginalName = fileName,
                StorageName = "storage-name",
                SizeBytes = content.Length,
                ContentType = "application/octet-stream",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = owner,
                UploadedByDisplayName = owner
            });

            db.SaveChanges();

            var directory = Path.Combine(storage, folder.Id.ToString("D6"));
            Directory.CreateDirectory(directory);
            File.WriteAllText(Path.Combine(directory, "storage-name"), content);
        });

        return (factory, storage);
    }

    [Theory]
    [InlineData("схема.png", "image/png")]
    [InlineData("инструкция.pdf", "application/pdf")]
    [InlineData("отчёт.txt", "text/plain; charset=utf-8")]
    public async Task Предпросмотр_отдаёт_тип_из_белого_списка(string name, string expected)
    {
        var (factory, _) = Prepare(name);
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var response = await client.GetAsync("/Files?handler=Preview&fileId=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(expected, response.Content.Headers.ContentType?.ToString());

        // «Показать», а не «сохранить» — иначе панель предпросмотра
        // превратится в кнопку скачивания.
        Assert.Equal("inline", response.Content.Headers.ContentDisposition?.DispositionType);
    }

    [Theory]
    [InlineData("рисунок.svg")]      // картинка, но внутри может быть код
    [InlineData("страница.html")]
    [InlineData("письмо.docx")]
    public async Task Предпросмотр_отказывает_всему_что_не_в_списке(string name)
    {
        var (factory, _) = Prepare(name);
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var response = await client.GetAsync("/Files?handler=Preview&fileId=1");

        // Именно «не найдено»: подтверждать существование файла тому,
        // кому его не покажут, незачем.
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Предпросмотр_закрыт_для_чужой_папки()
    {
        var (factory, _) = Prepare("схема.png");
        using var _factory = factory;

        // Закрываем папку: наследование выключено, прав никому не выдано.
        factory.Seed(db =>
        {
            var folder = db.Folders.First();
            folder.InheritPermissions = false;
        });

        var client = await factory.LoginAsAsync("petrov", Users);

        var response = await client.GetAsync("/Files?handler=Preview&fileId=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public async Task Переименование_не_даёт_подсунуть_запрещённое_расширение()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);
        var token = await client.GetTokenAsync("/Files?id=1");

        await client.PostFormAsync("/Files?handler=RenameFile", token, new Dictionary<string, string>
        {
            ["fileId"] = "1",
            ["newName"] = "вирус.exe"
        });

        var stored = factory.Query(db => db.Files.First().OriginalName);

        Assert.Equal("отчёт.txt", stored);
    }

    [Fact]
    public async Task Переименование_меняет_имя_и_тип_содержимого()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);
        var token = await client.GetTokenAsync("/Files?id=1");

        await client.PostFormAsync("/Files?handler=RenameFile", token, new Dictionary<string, string>
        {
            ["fileId"] = "1",
            ["newName"] = "итоговый отчёт.txt"
        });

        var file = factory.Query(db => db.Files.First());

        Assert.Equal("итоговый отчёт.txt", file.OriginalName);
        Assert.StartsWith("text/plain", file.ContentType);
    }

    [Fact]
    public async Task Переименование_чужого_файла_без_прав_запрещено()
    {
        var (factory, _) = Prepare("отчёт.txt", owner: "sidorov");
        using var _factory = factory;

        // Читатель: видит файл, но ни управлять папкой, ни писать в неё не может.
        factory.Seed(db =>
        {
            var folder = db.Folders.First();
            folder.InheritPermissions = false;
            db.FolderPermissions.Add(new FolderPermission
            {
                FolderId = folder.Id,
                GroupName = Users,
                Access = FolderAccess.Read
            });
        });

        var client = await factory.LoginAsAsync("petrov", Users);
        var token = await client.GetTokenAsync("/Files?id=1");

        var response = await client.PostFormAsync(
            "/Files?handler=RenameFile", token, new Dictionary<string, string>
            {
                ["fileId"] = "1",
                ["newName"] = "чужое.txt"
            });

        Assert.Equal("отчёт.txt", factory.Query(db => db.Files.First().OriginalName));
        Assert.NotEqual(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Переименование_папки_не_допускает_двух_одинаковых_имён()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        factory.Seed(db => db.Folders.Add(new StorageFolder
        {
            Name = "Приказы",
            CreatedAt = DateTime.UtcNow,
            InheritPermissions = true
        }));

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);
        var token = await client.GetTokenAsync("/Files");

        await client.PostFormAsync("/Files?handler=RenameFolder", token, new Dictionary<string, string>
        {
            ["folderId"] = "2",
            ["newName"] = "Документы"
        });

        Assert.Equal("Приказы", factory.Query(db => db.Folders.Single(f => f.Id == 2).Name));
    }

    /// <summary>
    /// Две папки с одним именем на верхнем уровне.
    ///
    /// Проверка стояла с самого начала, но не работала: на верхнем уровне
    /// у папки нет родителя, а сравнение с пустым значением в запросе
    /// к базе не даёт истины никогда. Ошибка тихая — портал просто заводил
    /// вторую папку с тем же именем.
    /// </summary>
    [Fact]
    public async Task Две_папки_с_одним_именем_на_верхнем_уровне_не_создаются()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);
        var token = await client.GetTokenAsync("/Files");

        await client.PostFormAsync("/Files?handler=CreateFolder", token, new Dictionary<string, string>
        {
            ["NewFolderName"] = "Документы"
        });

        Assert.Equal(1, factory.Query(db => db.Folders.Count(f => f.Name == "Документы")));
    }

    [Fact]
    public async Task Ответ_в_формате_JSON_не_экранирует_кириллицу()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var body = await client.GetStringAsync("/Files?handler=Properties&fileId=1");

        // По умолчанию сериализатор превращает «Размер» в «Р...» —
        // ответ распухает вшестеро, а на канале между офисами это заметно.
        Assert.DoesNotContain("\\u04", body);
    }

    [Fact]
    public async Task Свойства_файла_отдаются_читателю()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var body = await client.GetStringAsync("/Files?handler=Properties&fileId=1");

        Assert.Contains("отчёт.txt", body);
        Assert.Contains("Размер", body);
    }

    [Fact]
    public async Task Свойства_закрытой_папки_не_отдаются_постороннему()
    {
        var (factory, _) = Prepare("отчёт.txt");
        using var _factory = factory;

        factory.Seed(db => db.Folders.First().InheritPermissions = false);

        var client = await factory.LoginAsAsync("petrov", Users);

        var response = await client.GetAsync("/Files?handler=Properties&folderId=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    [Fact]
    public void Белый_список_предпросмотра_не_содержит_опасного()
    {
        // SVG — картинка, но может нести в себе код. HTML — тем более.
        Assert.Equal(PreviewKind.None, PreviewSupport.KindOf("картинка.svg"));
        Assert.Equal(PreviewKind.None, PreviewSupport.KindOf("страница.html"));
        Assert.Null(PreviewSupport.ContentTypeFor("страница.htm"));

        // А json и xml показываем, но обычным текстом, а не как разметку.
        Assert.StartsWith("text/plain", PreviewSupport.ContentTypeFor("данные.json"));
        Assert.StartsWith("text/plain", PreviewSupport.ContentTypeFor("данные.xml"));
    }
}
