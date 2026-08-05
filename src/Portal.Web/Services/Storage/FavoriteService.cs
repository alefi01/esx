using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Избранное: личные отметки на файлах и папках.
///
/// Отметка своя у каждого человека — то, что положил в избранное Иванов,
/// Петрова не касается. Поэтому все методы принимают пользователя, и «чужого»
/// избранного здесь не бывает в принципе.
///
/// Права при этом никуда не деваются: отметка сама по себе доступа не даёт.
/// Страница избранного заново проверяет, видна ли человеку папка, и молча
/// пропускает то, к чему доступ пропал, — см. LoadAsync там.
/// </summary>
public sealed class FavoriteService
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public FavoriteService(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>
    /// Номера файлов, отмеченных этим человеком, — чтобы страница знала,
    /// у каких плиток зажечь звёздочку.
    ///
    /// Одним запросом на всю страницу, а не по запросу на плитку:
    /// по одному запросу на строку списка — классический способ
    /// незаметно посадить страницу.
    /// </summary>
    public async Task<HashSet<int>> FileIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var name = user.Identity?.Name;

        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        var ids = await _db.Favorites
            .Where(f => f.UserName == name && f.FileId != null)
            .Select(f => f.FileId!.Value)
            .ToListAsync(cancellationToken);

        return [.. ids];
    }

    /// <summary>Номера папок, отмеченных этим человеком.</summary>
    public async Task<HashSet<int>> FolderIdsAsync(ClaimsPrincipal user, CancellationToken cancellationToken)
    {
        var name = user.Identity?.Name;

        if (string.IsNullOrEmpty(name))
        {
            return [];
        }

        var ids = await _db.Favorites
            .Where(f => f.UserName == name && f.FolderId != null)
            .Select(f => f.FolderId!.Value)
            .ToListAsync(cancellationToken);

        return [.. ids];
    }

    /// <summary>
    /// Поставить или снять отметку. Возвращает новое состояние:
    /// true — теперь в избранном.
    ///
    /// Одна кнопка на оба действия, а не «добавить» и «убрать» отдельно:
    /// звёздочка в интерфейсе одна, и разделять её на две операции значило бы
    /// заставлять страницу помнить, какая из них сейчас нужна.
    /// </summary>
    public async Task<bool> ToggleAsync(
        ClaimsPrincipal user, int? fileId, int? folderId, CancellationToken cancellationToken)
    {
        var name = user.Identity?.Name;

        if (string.IsNullOrEmpty(name) || (fileId is null) == (folderId is null))
        {
            // Ровно одно из двух должно быть заполнено. Иначе это ошибка вызова,
            // а не пользовательская ситуация.
            throw new ArgumentException("Укажите либо файл, либо папку.");
        }

        var existing = await _db.Favorites.AsTracking()
            .FirstOrDefaultAsync(
                f => f.UserName == name && f.FileId == fileId && f.FolderId == folderId,
                cancellationToken);

        if (existing is not null)
        {
            _db.Favorites.Remove(existing);
            await _db.SaveChangesAsync(cancellationToken);

            return false;
        }

        _db.Favorites.Add(new Favorite
        {
            UserName = name,
            FileId = fileId,
            FolderId = folderId,
            AddedAt = _time.GetUtcNow().UtcDateTime
        });

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
