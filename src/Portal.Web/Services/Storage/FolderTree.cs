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
            // Начальники отдела читаются вместе с деревом: от них зависит
            // уровень доступа, то есть вопрос возникает на каждой папке.
            .Include(f => f.Heads)
            // Состав отдела и список тех, кому разрешено менять:
            // от них зависит доступ к каждой папке внутри отдела,
            // то есть вопрос возникает на каждой папке.
            .Include(f => f.Members)
            .Include(f => f.Editors)
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
    ///   * тот, кто ЗАВЁЛ папку, тоже получает Manage — на неё и на всё
    ///     вложенное, пока наследование не оборвано;
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

        // ПАПКИ ОТДЕЛА ЖИВУТ ПО СВОИМ ПРАВИЛАМ.
        //
        // Они не настраиваются правами вовсе — ни начальником,
        // ни администратором:
        //
        //   «Общая» и всё в ней        — видят ВСЕ сотрудники портала;
        //   «Внутренняя работа» и всё  — видят ТОЛЬКО сотрудники отдела.
        //
        // Это не настройка, а устройство: отдел заводят ровно затем,
        // чтобы у него были эти две части. Будь это галочкой, «внутреннее»
        // однажды перестало бы быть внутренним — а человек, который туда
        // писал, узнал бы об этом последним.
        if (DepartmentAccess(user, folder) is { } inDepartment)
        {
            return inDepartment;
        }

        // Начальник отдела управляет своей папкой и всем, что в ней.
        //
        // Через обычные права это сделать нельзя: выдача права выключает
        // наследование, и папка отдела из «открытой всем» молча стала бы
        // закрытой. Поэтому начальство — отдельное правило, как и права
        // администратора, а не строка в таблице прав.
        if (IsHead(user, folder))
        {
            return FolderAccess.Manage;
        }

        var result = FolderAccess.None;
        var current = folder;

        // Настроена ли папка вообще хоть как-нибудь: есть ли по цепочке
        // назначенные права или выключенное наследование. Если ничего
        // этого нет, папку никто не закрывал — и она открыта на чтение
        // всем, у кого есть доступ к порталу (см. в конце метода).
        var configured = false;

        // Защита от зацикливания: если в базе каким-то образом окажется
        // папка, ссылающаяся сама на себя (или цикл из нескольких),
        // без этого счётчика запрос повис бы навсегда.
        var guard = 0;

        var userName = user.Identity?.Name;

        while (current is not null && guard++ < 64)
        {
            // СВОЯ ПАПКА — своя и по правам.
            //
            // Тот, кто завёл папку, распоряжается ею полностью: может
            // раздать доступ коллегам, закрыть её, поменять предел размера.
            // Иначе выходило странно: человеку разрешено создать папку
            // и сложить туда документы, а открыть их коллеге он не может —
            // нужно писать заявку администратору, и так по каждой папке.
            //
            // Право распространяется и на вложенное — теми же правилами,
            // что и обычные права: пока наследование включено, оно идёт
            // вниз, а папка с выключенным наследованием обрывает его
            // на себе (проверка ниже по циклу).
            //
            // Выше Manage прав нет, поэтому дальше вверх идти незачем.
            if (!string.IsNullOrEmpty(userName)
                && string.Equals(current.CreatedByUserName, userName, StringComparison.OrdinalIgnoreCase))
            {
                return FolderAccess.Manage;
            }

            if (current.Permissions.Count > 0)
            {
                configured = true;
            }

            foreach (var permission in current.Permissions)
            {
                // Право выдано либо ГРУППЕ (проверяем членство), либо
                // конкретному ЧЕЛОВЕКУ (сравниваем логин). Одна проверка
                // вместо двух не годится: логин в списке ролей не лежит.
                var granted = permission.IsUser
                    ? !string.IsNullOrEmpty(userName)
                      && string.Equals(permission.GroupName, userName, StringComparison.OrdinalIgnoreCase)
                    : user.IsInRole(permission.GroupName);

                if (granted && permission.Access > result)
                {
                    result = permission.Access;
                }
            }

            if (!current.InheritPermissions)
            {
                // Выключенное наследование — это тоже настройка: папку
                // сделали самостоятельной намеренно, и «по умолчанию всем»
                // к ней уже не относится.
                configured = true;

                break;
            }

            current = current.Parent;
        }

        // ПАПКА, КОТОРУЮ НИКТО НЕ НАСТРАИВАЛ, ОТКРЫТА ВСЕМ.
        //
        // Портал — общее хранилище организации, и обычная папка в нём
        // не секрет. Раньше свежесозданный раздел не видел никто, кроме
        // администратора: прав на нём нет, значит доступа нет. На практике
        // это означало, что после каждой новой папки идут в администраторы
        // «выдайте всем доступ».
        //
        // Уровень зависит от глубины (см. DefaultAccess). Как только кто-то
        // выдал первое право или закрыл наследование, папка становится
        // обычной — действуют только заданные права.
        if (!configured)
        {
            var byDefault = DefaultAccess(folder);

            if (byDefault > result)
            {
                return byDefault;
            }
        }

        return result;
    }

    /// <summary>
    /// Доступ к папке, лежащей в отделе. null — папка не в отделе,
    /// решают обычные правила.
    ///
    /// КТО ВИДИТ
    ///
    /// «Общая» — все сотрудники портала. «Внутренняя работа» — только
    /// состав отдела (и его начальники). Правило жёсткое: прав на эти
    /// папки не существует, менять его некому.
    ///
    /// КТО МЕНЯЕТ
    ///
    /// Тот, кто видит, — если папка не помечена «только для чтения»
    /// и если у неё не задан список тех, кому разрешено менять.
    /// Как только список задан, остальные папку видят, но не трогают:
    /// это мягче, чем закрыть её совсем, — документ остаётся на виду
    /// у отдела, даже если ведёт его один человек.
    ///
    /// Начальник отдела и администратор портала не ограничиваются
    /// ни тем, ни другим: иначе папку, помеченную «только для чтения»,
    /// не мог бы поправить уже никто.
    /// </summary>
    private FolderAccess? DepartmentAccess(ClaimsPrincipal user, StorageFolder folder)
    {
        var department = DepartmentOf(folder);

        if (department is null)
        {
            return null;
        }

        // Сама папка отдела: внутри неё только две заготовленные части,
        // и распоряжается ими администратор. Начальник видит её, чтобы
        // пройти внутрь и вести состав.
        if (folder.Id == department.Id)
        {
            return IsHead(user, folder) ? FolderAccess.Manage : FolderAccess.Read;
        }

        var inPublic = IsInPublicPart(folder);
        var member = IsMemberOf(user, department);

        // Внутренняя часть — только своим.
        if (!inPublic && !member && !IsHead(user, folder))
        {
            return FolderAccess.None;
        }

        if (IsHead(user, folder))
        {
            return FolderAccess.Manage;
        }

        // Папка только для чтения — меняет её лишь начальник отдела.
        if (IsReadOnly(folder))
        {
            return FolderAccess.Read;
        }

        // Список тех, кому разрешено менять, действует и на вложенное:
        // поручили человеку папку — поручили и всё, что он в ней заведёт.
        return CanEditHere(user, folder) ? FolderAccess.Write : FolderAccess.Read;
    }

    /// <summary>Человек состоит в этом отделе.</summary>
    public bool IsMemberOf(ClaimsPrincipal user, StorageFolder department)
    {
        var userName = user.Identity?.Name;

        if (string.IsNullOrEmpty(userName))
        {
            return false;
        }

        return department.Members
            .Any(m => string.Equals(m.UserName, userName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Папка только для чтения — сама или потому, что такова папка выше.
    /// Запрет идёт вниз: внутри закрытой на правку папки правка
    /// невозможна и в подпапках.
    /// </summary>
    public bool IsReadOnly(StorageFolder folder)
    {
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (current.ReadOnly)
            {
                return true;
            }

            if (current.IsDepartment)
            {
                break;
            }

            current = current.Parent;
        }

        return false;
    }

    /// <summary>
    /// Человеку разрешено менять содержимое этой папки.
    ///
    /// Список ищется снизу вверх: у самой папки, потом у той, что выше.
    /// Пустые списки пропускаются — «не задано» означает «меняют все,
    /// кому видно», а не «никто».
    /// </summary>
    private bool CanEditHere(ClaimsPrincipal user, StorageFolder folder)
    {
        var userName = user.Identity?.Name;
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (current.Editors.Count > 0)
            {
                return !string.IsNullOrEmpty(userName)
                       && current.Editors.Any(e =>
                           string.Equals(e.UserName, userName, StringComparison.OrdinalIgnoreCase));
            }

            if (current.IsDepartment)
            {
                break;
            }

            current = current.Parent;
        }

        return true;
    }

    /// <summary>
    /// Что разрешено в папке, которую никто не настраивал: чтение и запись.
    ///
    /// Это рабочее место: смотреть, скачивать, складывать документы,
    /// заводить подпапки, убирать ненужное. Требовать на каждый рабочий
    /// файл отдельного права — значит вернуться к заявкам администратору
    /// по любому поводу.
    ///
    /// А вот УПРАВЛЕНИЕ по умолчанию не даётся никому и нигде. Именно оно
    /// и защищает устройство хранилища: разделы верхнего уровня —
    /// «Бухгалтерия», «Приказы», «Общее» — переносят, копируют, удаляют
    /// и переименовывают только те, кому это прямо разрешено, либо тот,
    /// кто раздел завёл. Случайно утащить такой раздел в соседний нельзя,
    /// хотя работать внутри него может каждый.
    ///
    /// Удаление ЧУЖИХ файлов по умолчанию не даётся: свой файл человек
    /// убирает сам, чужой — тот, кто управляет папкой (в отделе это
    /// начальник). Иначе «Общая» часть отдела, открытая всему порталу,
    /// стала бы местом, где любой может стереть чужой документ.
    /// </summary>
    public static FolderAccess DefaultAccess(StorageFolder folder) => FolderAccess.Write;

    /// <summary>
    /// Действуют ли в папке права «по умолчанию» — то есть её и всю цепочку
    /// над ней никто не настраивал.
    ///
    /// Нужно там, где правило зависит не только от уровня доступа: удалять
    /// чужие файлы можно в общей рабочей папке, но не в той, где права
    /// кто-то расставил руками.
    /// </summary>
    public bool UsesDefaultAccess(StorageFolder folder)
    {
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (current.Permissions.Count > 0 || !current.InheritPermissions)
            {
                return false;
            }

            current = current.Parent;
        }

        return true;
    }

    /// <summary>
    /// Папка верхнего уровня, в которой лежит эта. Для самой папки
    /// верхнего уровня — она сама.
    ///
    /// Нужна часто: отдел, корзина раздела и начальство — всё считается
    /// по верхнему уровню, а не по той папке, в которой человек стоит.
    /// </summary>
    public StorageFolder RootOf(StorageFolder folder)
    {
        var current = folder;
        var guard = 0;

        while (current.Parent is not null && guard++ < 64)
        {
            current = current.Parent;
        }

        return current;
    }

    /// <summary>Отдел, которому принадлежит папка. null — папка не в отделе.</summary>
    public StorageFolder? DepartmentOf(StorageFolder folder)
    {
        var root = RootOf(folder);

        return root.IsDepartment ? root : null;
    }

    /// <summary>Начальники отдела, в котором лежит папка.</summary>
    public IReadOnlyList<FolderHead> HeadsOf(StorageFolder folder) =>
        DepartmentOf(folder)?.Heads ?? [];

    /// <summary>
    /// Человек — начальник отдела, которому принадлежит эта папка.
    ///
    /// Проверяется по логину, а не по членству в группе: группа WebHeads
    /// отвечает только на вопрос «кого вообще можно назначить», а кем он
    /// назначен — решает администратор в настройках конкретной папки.
    /// </summary>
    public bool IsHead(ClaimsPrincipal user, StorageFolder folder)
    {
        var userName = user.Identity?.Name;

        if (string.IsNullOrEmpty(userName))
        {
            return false;
        }

        return HeadsOf(folder)
            .Any(head => string.Equals(head.UserName, userName, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Папка лежит в «Общей» части отдела (или сама ею является).
    ///
    /// По ней отличается общая часть от внутренней: в общей работают все
    /// сотрудники портала, и удаление чужих файлов там ограничено.
    /// </summary>
    public bool IsInPublicPart(StorageFolder folder)
    {
        var current = folder;
        var guard = 0;

        while (current is not null && guard++ < 64)
        {
            if (current.DepartmentRole == DepartmentRoles.Public)
            {
                return true;
            }

            current = current.Parent;
        }

        return false;
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

    /// <summary>
    /// Сколько элементов лежит в каждой из перечисленных папок: видимые
    /// подпапки плюс файлы самой папки.
    ///
    /// Нужно для подписи под плиткой — «пусто» или «7 элем.». Считать одни
    /// подпапки нельзя: папка с документами, но без вложенных папок,
    /// подписывалась бы «пусто», и в неё просто не стали бы заходить.
    ///
    /// Файлы считаются одним запросом на весь список, а не запросом на папку:
    /// по запросу на строку списка — это классический способ незаметно
    /// посадить страницу. Файлы учитываются только там, куда человеку
    /// разрешено смотреть: количество файлов в закрытой папке — тоже сведения.
    /// </summary>
    public async Task<IReadOnlyDictionary<int, int>> ChildCountsAsync(
        ClaimsPrincipal user,
        IReadOnlyCollection<StorageFolder> folders,
        CancellationToken cancellationToken = default)
    {
        var counts = new Dictionary<int, int>();

        if (folders.Count == 0)
        {
            return counts;
        }

        var readable = folders.Where(f => CanRead(user, f)).Select(f => f.Id).ToList();

        var files = readable.Count == 0
            ? []
            : await _db.Files
                .Where(f => readable.Contains(f.FolderId) && f.DeletedAt == null)
                .GroupBy(f => f.FolderId)
                .Select(g => new { FolderId = g.Key, Count = g.Count() })
                .ToDictionaryAsync(x => x.FolderId, x => x.Count, cancellationToken);

        foreach (var folder in folders)
        {
            counts[folder.Id] =
                folder.Children.Count(child => IsVisible(user, child))
                + files.GetValueOrDefault(folder.Id);
        }

        return counts;
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
    /// Действующий предел размера одного файла, байты. <b>0 — без ограничения.</b>
    ///
    /// Ищется у самой папки, затем у родителей, и в конце берётся
    /// общее значение по умолчанию из конфигурации. Поверх всего действует
    /// потолок портала (Storage:AbsoluteMaxFileSizeMb): папке нельзя разрешить
    /// больше, чем принимает сам сервер, — иначе загрузка обрывалась бы
    /// на полпути без внятного объяснения.
    /// </summary>
    public long EffectiveMaxFileSizeBytes(StorageFolder folder)
    {
        var portalCap = _storage.FileSizeUnlimited
            ? 0
            : (long)_storage.AbsoluteMaxFileSizeMb * 1024 * 1024;

        var megabytes = EffectiveSetting<int>(folder, f => f.MaxFileSizeMb, _storage.DefaultMaxFileSizeMb)
                        ?? _storage.DefaultMaxFileSizeMb;

        // У папки предел снят — остаётся только потолок портала.
        if (megabytes <= 0)
        {
            return portalCap;
        }

        var bytes = (long)megabytes * 1024 * 1024;

        return portalCap > 0 && bytes > portalCap ? portalCap : bytes;
    }

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
