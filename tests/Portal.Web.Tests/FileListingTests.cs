using System.IO.Compression;
using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Portal.Web.Data;
using Portal.Web.Pages.Shared;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Список файлов: сортировка, цвета типов, предпросмотр архива.
///
/// Проверяем не то, «красиво ли получилось», а то, что видно снаружи
/// и от чего зависит поведение: в каком порядке идут строки, что уходит
/// наружу при предпросмотре и что портал наотрез отказывается показывать.
/// </summary>
public class FileListingTests
{
    private const string Users = "WebUsers";
    private const string Admins = "WebAdmins";

    /// <summary>Папка с набором файлов разного размера, возраста и типа.</summary>
    private static PortalFactory PrepareFolder()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"listing-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        var factory = new PortalFactory { StorageRootPath = storage };

        factory.Seed(db =>
        {
            var folder = new StorageFolder
            {
                Name = "Документы",
                CreatedAt = DateTime.UtcNow.AddDays(-10),
                InheritPermissions = true
            };

            db.Folders.Add(folder);
            db.SaveChanges();

            // Порядок добавления намеренно ни на что не похож: если бы
            // сортировки не было, тест проходил бы случайно.
            Add(db, folder.Id, "берёза.txt", 300, DateTime.UtcNow.AddDays(-1));
            Add(db, folder.Id, "Акт.pdf", 100, DateTime.UtcNow.AddDays(-5));
            Add(db, folder.Id, "ёлка.docx", 200, DateTime.UtcNow.AddDays(-3));

            db.SaveChanges();
        });

        return factory;

        static void Add(PortalDbContext db, int folderId, string name, long size, DateTime at) =>
            db.Files.Add(new StoredFile
            {
                FolderId = folderId,
                OriginalName = name,
                StorageName = "s-" + name,
                SizeBytes = size,
                ContentType = "application/octet-stream",
                UploadedAt = at,
                UploadedByUserName = "ivanov",
                UploadedByDisplayName = "Иванов Иван"
            });
    }

    /// <summary>Порядок имён файлов на странице — по тому, где они встречаются в разметке.</summary>
    private static List<string> OrderOnPage(string html, params string[] names) =>
        names
            .Select(name => new { name, at = html.IndexOf(name, StringComparison.Ordinal) })
            .Where(x => x.at >= 0)
            .OrderBy(x => x.at)
            .Select(x => x.name)
            .ToList();

    [Fact]
    public async Task По_умолчанию_файлы_идут_по_имени()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var html = await client.GetStringAsync("/Files?id=1");

        // По-русски «ё» стоит после «е», и «берёза» идёт раньше «ёлки».
        // Именно это и ломалось, когда сортировка отдавалась базе:
        // порядок зависел от локали, с которой её создали.
        Assert.Equal(
            ["Акт.pdf", "берёза.txt", "ёлка.docx"],
            OrderOnPage(html, "Акт.pdf", "берёза.txt", "ёлка.docx"));
    }

    [Fact]
    public async Task Сортировка_по_размеру_ставит_крупные_первыми()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var html = await client.GetStringAsync("/Files?id=1&sort=size");

        Assert.Equal(
            ["берёза.txt", "ёлка.docx", "Акт.pdf"],
            OrderOnPage(html, "Акт.pdf", "берёза.txt", "ёлка.docx"));
    }

    [Fact]
    public async Task Сортировка_по_дате_ставит_свежие_первыми()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var html = await client.GetStringAsync("/Files?id=1&sort=date");

        Assert.Equal(
            ["берёза.txt", "ёлка.docx", "Акт.pdf"],
            OrderOnPage(html, "Акт.pdf", "берёза.txt", "ёлка.docx"));
    }

    /// <summary>
    /// Чужая строка в адресе не должна ничего ломать: неизвестный порядок
    /// молча превращается в обычный, по имени.
    /// </summary>
    [Fact]
    public async Task Неизвестный_порядок_сортировки_не_ломает_страницу()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var response = await client.GetAsync("/Files?id=1&sort=' OR 1=1--");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Equal(
            ["Акт.pdf", "берёза.txt", "ёлка.docx"],
            OrderOnPage(html, "Акт.pdf", "берёза.txt", "ёлка.docx"));
    }

    /// <summary>
    /// Значок файла рисуется на сервере и красится по типу файла.
    /// Если цвет пропадёт, весь список станет одинаково серым, и по нему
    /// перестанет читаться, где таблица, а где документ.
    /// </summary>
    [Fact]
    public async Task Значок_файла_покрашен_по_типу()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var html = await client.GetStringAsync("/Files?id=1");

        Assert.Contains(FileIcons.Color("Акт.pdf"), html);      // красный
        Assert.Contains(FileIcons.Color("ёлка.docx"), html);    // синий
        Assert.Contains(FileIcons.Color("берёза.txt"), html);   // серый

        // И подпись с расширением на плашке значка.
        Assert.Contains(">PDF<", html);
        Assert.Contains(">DOCX<", html);
    }

    [Theory]
    [InlineData("снимок.png", "image")]
    [InlineData("смета.xlsx", "excel")]
    [InlineData("доклад.pptx", "ppt")]
    [InlineData("запись.mp4", "video")]
    [InlineData("звук.mp3", "audio")]
    [InlineData("архив.zip", "zip")]
    [InlineData("настройки.json", "code")]
    [InlineData("чертёж.dwg", "other")]
    public void Семейство_файла_определяется_по_расширению(string name, string expected) =>
        Assert.Equal(expected, FileKinds.Of(name));

    /// <summary>
    /// Видео и звук портал отдаёт браузеру целиком — значит, тип содержимого
    /// должен браться из белого списка, а не из того, что записано при загрузке.
    /// </summary>
    [Theory]
    [InlineData("запись.mp4", "video/mp4")]
    [InlineData("совещание.mp3", "audio/mpeg")]
    public void Видео_и_звук_отдаются_с_проверенным_типом(string name, string expected) =>
        Assert.Equal(expected, PreviewSupport.ContentTypeFor(name));

    /// <summary>
    /// Архив наружу целиком НЕ отдаётся: из него уходит только список
    /// содержимого, собранный на сервере. Иначе предпросмотр превратился бы
    /// в способ скачать файл в обход журнала.
    /// </summary>
    [Fact]
    public void Архив_нельзя_отдать_файлом() =>
        Assert.Null(PreviewSupport.ContentTypeFor("документы.zip"));

    [Fact]
    public async Task Предпросмотр_архива_показывает_список_содержимого()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"zip-{Guid.NewGuid():N}");
        Directory.CreateDirectory(storage);

        using var factory = new PortalFactory { StorageRootPath = storage };

        factory.Seed(db =>
        {
            var folder = new StorageFolder
            {
                Name = "Архивы",
                CreatedAt = DateTime.UtcNow,
                InheritPermissions = true
            };

            db.Folders.Add(folder);
            db.SaveChanges();

            db.Files.Add(new StoredFile
            {
                FolderId = folder.Id,
                OriginalName = "документы.zip",
                StorageName = "zip-file",
                SizeBytes = 1,
                ContentType = "application/zip",
                UploadedAt = DateTime.UtcNow,
                UploadedByUserName = "ivanov",
                UploadedByDisplayName = "Иванов Иван"
            });

            db.SaveChanges();

            var directory = Path.Combine(storage, folder.Id.ToString("D6"));
            Directory.CreateDirectory(directory);

            using var file = File.Create(Path.Combine(directory, "zip-file"));
            using var archive = new ZipArchive(file, ZipArchiveMode.Create);

            using (var writer = new StreamWriter(archive.CreateEntry("договор.txt").Open(), Encoding.UTF8))
            {
                writer.Write("текст договора");
            }

            archive.CreateEntry("вложенная папка/приложение.txt");
        });

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var response = await client.GetAsync("/Files?handler=ArchivePreview&fileId=1");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var html = await response.Content.ReadAsStringAsync();

        Assert.Contains("договор.txt", html);
        Assert.Contains("вложенная папка/приложение.txt", html);

        // Само содержимое файлов внутрь списка попадать не должно:
        // архив не распаковывается, читается только его оглавление.
        Assert.DoesNotContain("текст договора", html);
    }

    /// <summary>
    /// Обработчик архива не должен становиться лазейкой: попросить у него
    /// «предпросмотр» обычного документа нельзя.
    /// </summary>
    [Fact]
    public async Task Обработчик_архива_отказывает_не_архиву()
    {
        using var factory = PrepareFolder();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var response = await client.GetAsync("/Files?handler=ArchivePreview&fileId=1");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    /// <summary>
    /// В окне предпросмотра есть блок «Доступ». Он берёт данные у того же
    /// обработчика, что и окно свойств, поэтому список групп должен
    /// приходить вместе со сведениями о файле.
    /// </summary>
    [Fact]
    public async Task Сведения_о_файле_содержат_список_доступа()
    {
        using var factory = PrepareFolder();

        factory.Seed(db => db.FolderPermissions.Add(new FolderPermission
        {
            FolderId = 1,
            GroupName = "Бухгалтерия",
            Access = FolderAccess.Write
        }));

        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var json = await client.GetStringAsync("/Files?handler=Properties&fileId=1");

        // Разбираем ответ, а не ищем подстроку: тире в JSON уезжает
        // в вид \u2014 — кодировщик выпускает без экранирования только
        // латиницу и кириллицу. Для браузера это одно и то же, а тест
        // на подстроке из-за такой мелочи падал бы на ровном месте.
        using var document = JsonDocument.Parse(json);

        var access = document.RootElement.GetProperty("access")
            .EnumerateArray()
            .Select(item => item.GetString())
            .ToList();

        Assert.Contains("Бухгалтерия — запись", access);
    }

    /// <summary>Папка с двумя файлами в корзине: один удалил Иванов, другой — Петров.</summary>
    private static PortalFactory PrepareTrash()
    {
        var storage = Path.Combine(Path.GetTempPath(), $"trash-{Guid.NewGuid():N}");
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

            db.FolderPermissions.Add(new FolderPermission
            {
                FolderId = folder.Id,
                GroupName = Users,
                Access = FolderAccess.Write
            });

            foreach (var (name, who) in new[] { ("своё.txt", "ivanov"), ("чужое.txt", "petrov") })
            {
                db.Files.Add(new StoredFile
                {
                    FolderId = folder.Id,
                    OriginalName = name,
                    StorageName = "s-" + name,
                    SizeBytes = 10,
                    ContentType = "text/plain",
                    UploadedAt = DateTime.UtcNow.AddDays(-1),
                    UploadedByUserName = who,
                    UploadedByDisplayName = who,
                    DeletedAt = DateTime.UtcNow.AddHours(-1),
                    DeletedByUserName = who
                });
            }

            db.SaveChanges();
        });

        return factory;
    }

    [Fact]
    public async Task Администратор_очищает_корзину_целиком()
    {
        using var factory = PrepareTrash();
        var client = await factory.LoginAsAsync("ivanov", Users, Admins);

        var token = await client.GetTokenAsync("/Files/Trash");
        var response = await client.PostFormAsync(
            "/Files/Trash?handler=PurgeAll", token, new Dictionary<string, string>());

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);

        var html = await client.GetStringAsync("/Files/Trash");

        Assert.Contains("Корзина пуста", html);
    }

    /// <summary>
    /// «Очистить корзину» не должна становиться способом стереть чужое.
    /// Обычный сотрудник вычищает только то, что удалил сам, — ровно то же
    /// правило, что и при удалении по одному файлу.
    /// </summary>
    [Fact]
    public async Task Обычный_сотрудник_очищает_только_своё()
    {
        using var factory = PrepareTrash();
        var client = await factory.LoginAsAsync("ivanov", Users);

        var token = await client.GetTokenAsync("/Files/Trash");

        await client.PostFormAsync(
            "/Files/Trash?handler=PurgeAll", token, new Dictionary<string, string>());

        // Чужой файл остался в базе, хотя в корзине этому человеку он и не виден.
        using var scope = factory.Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        var left = db.Files.Select(f => f.OriginalName).ToList();

        Assert.Equal(["чужое.txt"], left);
    }
}
