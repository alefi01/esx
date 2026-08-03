using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Дерево папок, загруженное целиком, и вычисление прав по нему.
///
/// ПОЧЕМУ ЦЕЛИКОМ
///
/// Права на папку зависят от прав её родителей, а те — от своих родителей.
/// Вычислять это запросами к базе на каждом шаге означало бы десяток запросов
/// на одну страницу. Папок в корпоративном хранилище десятки, максимум сотни,
/// поэтому проще и быстрее один раз прочитать их все вместе с правами
/// и дальше работать в памяти.
///
/// Объект живёт один HTTP-запрос (зарегистрирован как Scoped) — значит,
/// в пределах страницы дерево читается из базы ровно один раз.
/// Если когда-нибудь папок станет тысячи, здесь и надо будет что-то менять;
/// до тех пор простота важнее.
/// </summary>
public sealed class FolderTree
{
    private readonly PortalDbContext _db;
    private readonly ActiveDirectoryOptions _ad;
    private readonly StorageOptions _storage;

    private Dictionary<int, StorageFolder>? _folders;

    public FolderTree(
        PortalDbContext db,
        IOptions<ActiveDirectoryOptions> ad,
        IOptions<StorageOptions> storage)
    {
        _db = db;
        _ad = ad.Value;
        _storage = storage.Value;
    }

    /// <summary>Читает дерево из базы, если оно ещё не прочитано.</summary>
    public async Task<IReadOnlyDictionary<int, StorageFolder>> LoadAsync(CancellationToken cancellationToken = default)
    {
        if (_folders is not null)
        {
            return _folders;
        }

        var list = await _db.Folders
            .Include(f => f.Permissions)
            .ToListAsync(cancellationToken);

        _folders = list.ToDictionary(f => f.Id);

        // Связываем родителей и детей вручную. EF сам этого не сделает:
        // мы не загружали навигационные свойства Parent и Children,
        // а «дозагружать» их по одному — это те самые лишние запросы.
        foreach (var folder in list)
        {
            folder.Children = [];
        }

        foreach (var folder in list)
        {
            if (folder.ParentId is { } parentId && _folders.TryGetValue(parentId, out var parent))
            {
                folder.Parent = parent;
                parent.Children.Add(folder);
            }
        }

        return _folders;
    }

    public StorageFolder? Get(int id) =>
        _folders is not null && _folders.TryGetValue(id, out var folder) ? folder : null;

    /// <summary>Папки верхнего уровня, отсортированные по названию.</summary>
    public IEnumerable<StorageFolder> RootFolders() =>
        (_folders?.Values ?? Enumerable.Empty<StorageFolder>())
        .Where(f => f.ParentId is null)
        .OrderBy(f => f.Name, StringComparer.CurrentCultureIgnoreCase);

    /// <summary>
    /// Уровень доступа пользователя к папке.
    ///
    /// Правила:
    ///   * администратор портала всегда получает Manage;
    ///   * права, назначенные разным группам, СКЛАДЫВАЮТСЯ — остаётся наибольший;
    ///   * если у папки включено наследование, к её собственным правам
    ///     добавляются права родителя, и так вверх до корня;
    ///   * как только встретилась папка с выключенным наследованием,
    ///     подъём вверх прекращается — дальше её собственных прав ничего нет.
    /// </summary>
    public FolderAccess AccessFor(ClaimsPrincipal user, StorageFolder folder)
    {
        if (user.IsInRole(_ad.AdminGroup))
        {
            return FolderAccess.Manage;
        }

        var result = FolderAccess.None;
        var current = folder;

        // Защита от зацикливания: если в базе каким-то образом окажется
        // папка, ссылающаяся сама на себя (или цикл из нескольких),
        // без этого счётчика запрос повис бы навсегда.
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            foreach (var permission in current.Permissions)
            {
                if (user.IsInRole(permission.GroupName) && permission.Access > result)
                {
                    result = permission.Access;
                }
            }

            if (!current.InheritPermissions)
            {
                break;
            }

            current = current.Parent;
        }

        return result;
    }

    public bool CanRead(ClaimsPrincipal user, StorageFolder folder) =>
        AccessFor(user, folder) >= FolderAccess.Read;

    public bool CanWrite(ClaimsPrincipal user, StorageFolder folder) =>
        AccessFor(user, folder) >= FolderAccess.Write;

    public bool CanManage(ClaimsPrincipal user, StorageFolder folder) =>
        AccessFor(user, folder) >= FolderAccess.Manage;

    /// <summary>
    /// Папка видна, если пользователь имеет к ней доступ ЛИБО имеет доступ
    /// к чему-то внутри неё. Иначе до вложенной разрешённой папки было бы
    /// не добраться: путь к ней шёл бы через невидимого родителя.
    /// </summary>
    public bool IsVisible(ClaimsPrincipal user, StorageFolder folder)
    {
        if (CanRead(user, folder))
        {
            return true;
        }

        return folder.Children.Any(child => IsVisible(user, child));
    }

    /// <summary>Путь от корня до папки — для «хлебных крошек» и журнала действий.</summary>
    public IReadOnlyList<StorageFolder> PathTo(StorageFolder folder)
    {
        var path = new List<StorageFolder>();
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            path.Add(current);
            current = current.Parent;
        }

        path.Reverse();

        return path;
    }

    /// <summary>Текстовый путь вида «Договоры / 2026 / Аренда».</summary>
    public string DisplayPath(StorageFolder folder) =>
        string.Join(" / ", PathTo(folder).Select(f => f.Name));

    /// <summary>
    /// Действующий предел размера одного файла, байты.
    /// Ищется у самой папки, затем у родителей, и в конце берётся
    /// общее значение по умолчанию из конфигурации.
    /// </summary>
    public long EffectiveMaxFileSizeBytes(StorageFolder folder) =>
        (long)(EffectiveSetting<int>(folder, f => f.MaxFileSizeMb, _storage.DefaultMaxFileSizeMb)
               ?? _storage.DefaultMaxFileSizeMb) * 1024 * 1024;

    /// <summary>Действующая квота на объём папки, байты. null — квоты нет.</summary>
    public long? EffectiveQuotaBytes(StorageFolder folder)
    {
        var quota = EffectiveSetting<int>(folder, f => f.QuotaMb, null);

        return quota is { } megabytes ? (long)megabytes * 1024 * 1024 : null;
    }

    /// <summary>
    /// Общий приём: ищем ближайшее заданное значение вверх по дереву.
    /// Наследование настроек не зависит от флага InheritPermissions —
    /// тот флаг касается только прав доступа.
    /// </summary>
    private static T? EffectiveSetting<T>(StorageFolder folder, Func<StorageFolder, T?> selector, T? fallback)
        where T : struct
    {
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (selector(current) is { } value)
            {
                return value;
            }

            current = current.Parent;
        }

        return fallback;
    }
}
