using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>Человек, которого можно назначить начальником отдела.</summary>
/// <param name="UserName">Логин.</param>
/// <param name="DisplayName">Как показывать.</param>
public sealed record HeadCandidate(string UserName, string DisplayName);

/// <summary>
/// Отделы: папка верхнего уровня с начальниками, объявлениями
/// и двумя заготовленными подпапками.
///
/// ПОЧЕМУ ЭТО ОТДЕЛЬНАЯ СЛУЖБА
///
/// Правил немного, но они разбросаны: создание отдела трогает три записи
/// в базе, назначение начальника — права на всю ветку, а список
/// кандидатов берётся не оттуда, откуда кажется. Повтори это в странице
/// создания папки и в странице настроек — и однажды они разойдутся.
/// </summary>
public sealed class Departments
{
    /// <summary>Названия подпапок, которые заводятся в новом отделе.</summary>
    public const string PublicFolderName = "Общая";

    public const string InternalFolderName = "Внутренняя работа";

    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public Departments(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>
    /// Кого можно назначить начальником.
    ///
    /// Это те, кто состоит в группе начальников И хотя бы раз входил
    /// в портал: членство запоминается при входе (см. UserSeenState.IsHead).
    /// Спрашивать домен прямо здесь было бы правильнее по смыслу, но тогда
    /// окно выбора переставало бы работать ровно в ту минуту, когда
    /// контроллер домена недоступен.
    /// </summary>
    public async Task<IReadOnlyList<HeadCandidate>> CandidatesAsync(CancellationToken cancellationToken)
    {
        var rows = await _db.Set<UserSeenState>()
            .Where(state => state.IsHead)
            .Select(state => new { state.UserName, state.DisplayName })
            .ToListAsync(cancellationToken);

        return rows
            .Select(row => new HeadCandidate(
                row.UserName,
                string.IsNullOrWhiteSpace(row.DisplayName) ? row.UserName : row.DisplayName))
            .OrderBy(candidate => candidate.DisplayName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// Превращает папку верхнего уровня в отдел: заводит подпапки
    /// и назначает начальников.
    ///
    /// Подпапки НЕ получают никаких прав, и это главное в их устройстве:
    /// «Общая» остаётся ненастроенной, то есть открытой всем по общему
    /// правилу портала. Права на «Внутреннюю работу» расставит начальник,
    /// и уже это выключит у неё наследование.
    /// </summary>
    public async Task MakeDepartmentAsync(
        StorageFolder folder,
        IEnumerable<string> headUserNames,
        string byUserName,
        CancellationToken cancellationToken)
    {
        if (folder.ParentId is not null)
        {
            throw new InvalidOperationException("Отделом может быть только папка верхнего уровня.");
        }

        var now = _time.GetUtcNow().UtcDateTime;

        folder.IsDepartment = true;

        var existing = await _db.Folders
            .Where(child => child.ParentId == folder.Id)
            .Select(child => new { child.Id, child.Name, child.DepartmentRole })
            .ToListAsync(cancellationToken);

        // Заводим только то, чего нет: папку могли сделать отделом задним
        // числом, и «Общая» в ней уже может лежать, созданная руками.
        await EnsureSubfolderAsync(folder, existing.Select(e => e.Name),
            PublicFolderName, DepartmentRoles.Public, byUserName, now, cancellationToken);

        await EnsureSubfolderAsync(folder, existing.Select(e => e.Name),
            InternalFolderName, DepartmentRoles.Internal, byUserName, now, cancellationToken);

        await SetHeadsAsync(folder.Id, headUserNames, byUserName, cancellationToken);
    }

    private async Task EnsureSubfolderAsync(
        StorageFolder parent,
        IEnumerable<string> siblingNames,
        string name,
        string role,
        string byUserName,
        DateTime now,
        CancellationToken cancellationToken)
    {
        var taken = await _db.Folders
            .AnyAsync(child => child.ParentId == parent.Id && child.DepartmentRole == role, cancellationToken);

        if (taken)
        {
            return;
        }

        // Имя может быть занято обычной подпапкой — тогда подбираем
        // свободное, а не падаем с ошибкой уникальности.
        var finalName = name;
        var suffix = 2;
        var names = siblingNames.ToHashSet(StringComparer.CurrentCultureIgnoreCase);

        while (names.Contains(finalName))
        {
            finalName = $"{name} {suffix++}";
        }

        _db.Folders.Add(new StorageFolder
        {
            Name = finalName,
            ParentId = parent.Id,
            DepartmentRole = role,
            InheritPermissions = true,
            CreatedAt = now,
            CreatedByUserName = byUserName
        });

        await _db.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Задаёт список начальников отдела целиком: кого нет — добавляет,
    /// кого убрали — снимает.
    /// </summary>
    public async Task SetHeadsAsync(
        int folderId,
        IEnumerable<string> headUserNames,
        string byUserName,
        CancellationToken cancellationToken)
    {
        var wanted = headUserNames
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        var current = await _db.FolderHeads
            .AsTracking()
            .Where(head => head.FolderId == folderId)
            .ToListAsync(cancellationToken);

        foreach (var head in current)
        {
            if (!wanted.Contains(head.UserName, StringComparer.OrdinalIgnoreCase))
            {
                _db.FolderHeads.Remove(head);
            }
        }

        var names = await NamesAsync(wanted, cancellationToken);

        foreach (var userName in wanted)
        {
            if (current.Any(head => string.Equals(head.UserName, userName, StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            _db.FolderHeads.Add(new FolderHead
            {
                FolderId = folderId,
                UserName = userName,
                DisplayName = names.GetValueOrDefault(userName, userName),
                AssignedAt = _time.GetUtcNow().UtcDateTime,
                AssignedByUserName = byUserName
            });
        }

        await _db.SaveChangesAsync(cancellationToken);
    }

    private async Task<Dictionary<string, string>> NamesAsync(
        IReadOnlyCollection<string> userNames, CancellationToken cancellationToken)
    {
        if (userNames.Count == 0)
        {
            return new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        }

        var lowered = userNames.Select(name => name.ToLowerInvariant()).ToList();

        var rows = await _db.Set<UserSeenState>()
            .Where(state => lowered.Contains(state.UserName.ToLower()))
            .Select(state => new { state.UserName, state.DisplayName })
            .ToListAsync(cancellationToken);

        return rows.ToDictionary(
            row => row.UserName,
            row => string.IsNullOrWhiteSpace(row.DisplayName) ? row.UserName : row.DisplayName,
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Отделы без начальника. Нужны администратору: такие папки остаются
    /// на нём, и молча копиться они не должны.
    /// </summary>
    public async Task<IReadOnlyList<StorageFolder>> WithoutHeadsAsync(CancellationToken cancellationToken) =>
        await _db.Folders
            .Where(folder => folder.IsDepartment && !folder.Heads.Any())
            .OrderBy(folder => folder.Name)
            .ToListAsync(cancellationToken);
}
