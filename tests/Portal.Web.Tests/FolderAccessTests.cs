using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Вычисление прав на папку — сердце файлового хранилища.
///
/// Ошибка здесь означает либо что человек видит чужие документы, либо что
/// он не видит своих. Первое хуже, поэтому проверок на «не должен видеть»
/// здесь больше, чем на «должен».
/// </summary>
public class FolderAccessTests : IDisposable
{
    private readonly PortalDbContext _db;
    private readonly string _databasePath = Path.Combine(Path.GetTempPath(), $"acl-{Guid.NewGuid():N}.db");

    public FolderAccessTests()
    {
        var options = new DbContextOptionsBuilder<PortalDbContext>()
            .UseSqlite($"Data Source={_databasePath}")
            .Options;

        _db = new PortalDbContext(options);
        _db.Database.EnsureCreated();
    }

    public void Dispose()
    {
        _db.Dispose();

        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        GC.SuppressFinalize(this);
    }

    private static ClaimsPrincipal UserWithGroups(params string[] groups) =>
        new(new ClaimsIdentity(
            [new Claim(ClaimTypes.Name, "ivanov"), .. groups.Select(g => new Claim(ClaimTypes.Role, g))],
            "test"));

    private async Task<FolderTree> TreeAsync()
    {
        var tree = new FolderTree(
            _db,
            Options.Create(new ActiveDirectoryOptions { AdminGroup = "WebAdmins" }),
            Options.Create(new StorageOptions { DefaultMaxFileSizeMb = 50 }));

        await tree.LoadAsync();

        return tree;
    }

    private StorageFolder AddFolder(string name, int? parentId = null, bool inherit = true)
    {
        var folder = new StorageFolder
        {
            Name = name,
            ParentId = parentId,
            InheritPermissions = inherit,
            CreatedAt = DateTime.UtcNow,
            CreatedByUserName = "admin"
        };

        _db.Folders.Add(folder);
        _db.SaveChanges();

        return folder;
    }

    private void Grant(int folderId, string group, FolderAccess access)
    {
        _db.FolderPermissions.Add(new FolderPermission { FolderId = folderId, GroupName = group, Access = access });
        _db.SaveChanges();
    }

    [Fact]
    public async Task Без_прав_папка_недоступна()
    {
        var folder = AddFolder("Договоры");

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.None, tree.AccessFor(UserWithGroups("WebUsers"), tree.Get(folder.Id)!));
    }

    [Fact]
    public async Task Право_выданное_группе_действует()
    {
        var folder = AddFolder("Договоры");
        Grant(folder.Id, "Yuristy", FolderAccess.Write);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Write,
            tree.AccessFor(UserWithGroups("WebUsers", "Yuristy"), tree.Get(folder.Id)!));

        Assert.Equal(FolderAccess.None,
            tree.AccessFor(UserWithGroups("WebUsers", "Buhgalteriya"), tree.Get(folder.Id)!));
    }

    [Fact]
    public async Task Права_разных_групп_складываются_остаётся_наибольшее()
    {
        var folder = AddFolder("Договоры");
        Grant(folder.Id, "Vse", FolderAccess.Read);
        Grant(folder.Id, "Yuristy", FolderAccess.Manage);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Manage,
            tree.AccessFor(UserWithGroups("Vse", "Yuristy"), tree.Get(folder.Id)!));
    }

    [Fact]
    public async Task Вложенная_папка_наследует_права_родителя()
    {
        var parent = AddFolder("Договоры");
        var child = AddFolder("2026", parent.Id);

        Grant(parent.Id, "Yuristy", FolderAccess.Write);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Write, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(child.Id)!));
    }

    [Fact]
    public async Task Наследование_работает_через_несколько_уровней()
    {
        var level1 = AddFolder("Договоры");
        var level2 = AddFolder("2026", level1.Id);
        var level3 = AddFolder("Аренда", level2.Id);

        Grant(level1.Id, "Yuristy", FolderAccess.Read);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Read, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(level3.Id)!));
    }

    [Fact]
    public async Task Выключенное_наследование_отрезает_права_родителя()
    {
        var parent = AddFolder("Договоры");
        var child = AddFolder("Секретно", parent.Id, inherit: false);

        Grant(parent.Id, "Yuristy", FolderAccess.Manage);

        var tree = await TreeAsync();

        // На родителе права есть...
        Assert.Equal(FolderAccess.Manage, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(parent.Id)!));

        // ...а в закрытую подпапку они не проходят.
        Assert.Equal(FolderAccess.None, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(child.Id)!));
    }

    [Fact]
    public async Task В_закрытой_папке_действуют_её_собственные_права()
    {
        var parent = AddFolder("Договоры");
        var child = AddFolder("Секретно", parent.Id, inherit: false);

        Grant(parent.Id, "Yuristy", FolderAccess.Manage);
        Grant(child.Id, "Direktsiya", FolderAccess.Read);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Read, tree.AccessFor(UserWithGroups("Direktsiya"), tree.Get(child.Id)!));
        Assert.Equal(FolderAccess.None, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(child.Id)!));
    }

    [Fact]
    public async Task Подпапка_закрытой_папки_наследует_уже_от_неё()
    {
        var root = AddFolder("Договоры");
        var closed = AddFolder("Секретно", root.Id, inherit: false);
        var inner = AddFolder("Черновики", closed.Id);

        Grant(root.Id, "Yuristy", FolderAccess.Manage);
        Grant(closed.Id, "Direktsiya", FolderAccess.Write);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Write, tree.AccessFor(UserWithGroups("Direktsiya"), tree.Get(inner.Id)!));
        Assert.Equal(FolderAccess.None, tree.AccessFor(UserWithGroups("Yuristy"), tree.Get(inner.Id)!));
    }

    [Fact]
    public async Task Администратор_портала_попадает_везде_включая_закрытые_папки()
    {
        var parent = AddFolder("Договоры");
        var closed = AddFolder("Секретно", parent.Id, inherit: false);

        var tree = await TreeAsync();

        Assert.Equal(FolderAccess.Manage, tree.AccessFor(UserWithGroups("WebAdmins"), tree.Get(closed.Id)!));
    }

    [Fact]
    public async Task Папка_видна_если_доступна_вложенная_в_неё()
    {
        // Иначе до разрешённой подпапки было бы не добраться:
        // путь к ней идёт через невидимого родителя.
        var parent = AddFolder("Общее");
        var child = AddFolder("Бухгалтерия", parent.Id);

        Grant(child.Id, "Buhgalteriya", FolderAccess.Read);

        var tree = await TreeAsync();

        var user = UserWithGroups("Buhgalteriya");

        Assert.True(tree.IsVisible(user, tree.Get(parent.Id)!));
        // Но читать содержимое самого родителя по-прежнему нельзя.
        Assert.False(tree.CanRead(user, tree.Get(parent.Id)!));
    }

    [Fact]
    public async Task Предел_размера_файла_наследуется_и_падает_к_значению_по_умолчанию()
    {
        var root = AddFolder("Договоры");
        var child = AddFolder("2026", root.Id);
        var grandchild = AddFolder("Аренда", child.Id);

        var tree = await TreeAsync();

        // Нигде не задано — берётся общее значение по умолчанию (50 МБ).
        Assert.Equal(50L * 1024 * 1024, tree.EffectiveMaxFileSizeBytes(tree.Get(grandchild.Id)!));

        // Задали на среднем уровне — действует и на вложенной папке.
        _db.Folders.First(f => f.Id == child.Id).MaxFileSizeMb = 10;
        _db.SaveChanges();

        var tree2 = await TreeAsync();
        Assert.Equal(10L * 1024 * 1024, tree2.EffectiveMaxFileSizeBytes(tree2.Get(grandchild.Id)!));

        // Собственное значение папки важнее унаследованного.
        _db.Folders.First(f => f.Id == grandchild.Id).MaxFileSizeMb = 100;
        _db.SaveChanges();

        var tree3 = await TreeAsync();
        Assert.Equal(100L * 1024 * 1024, tree3.EffectiveMaxFileSizeBytes(tree3.Get(grandchild.Id)!));
    }

    [Fact]
    public async Task Квота_не_задана_нигде_значит_её_нет()
    {
        var root = AddFolder("Договоры");
        var child = AddFolder("2026", root.Id);

        var tree = await TreeAsync();

        Assert.Null(tree.EffectiveQuotaBytes(tree.Get(child.Id)!));

        _db.Folders.First(f => f.Id == root.Id).QuotaMb = 500;
        _db.SaveChanges();

        var tree2 = await TreeAsync();
        Assert.Equal(500L * 1024 * 1024, tree2.EffectiveQuotaBytes(tree2.Get(child.Id)!));
    }

    [Fact]
    public async Task Путь_папки_собирается_от_корня()
    {
        var root = AddFolder("Договоры");
        var child = AddFolder("2026", root.Id);
        var grandchild = AddFolder("Аренда", child.Id);

        var tree = await TreeAsync();

        Assert.Equal("Договоры / 2026 / Аренда", tree.DisplayPath(tree.Get(grandchild.Id)!));
    }
}
