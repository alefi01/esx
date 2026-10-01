using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Tasks;

/// <summary>
/// Личные настройки человека.
///
/// Хранится в базе, а не в браузере: такие настройки про человека,
/// и настроенное в кабинете должно работать и с другой машины.
/// Облегчённый режим, наоборот, живёт в браузере — см. theme.js.
///
/// Сейчас настройка здесь всего одна (и та служебная), но таблица
/// «имя — значение» остаётся: настройки такого рода заводятся и отменяются
/// чаще, чем выходят обновления, и колонка на каждую означала бы миграцию
/// базы ради одной строки.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>
    /// Звук новых сообщений.
    ///
    /// Он ОДИН и не выбирается. Выбор из нескольких сигналов — ровно та
    /// настройка, которую открывают один раз из любопытства и больше
    /// не трогают, а место в интерфейсе она занимает постоянно.
    /// Сигнал короткий и негромкий; тем, кому он мешает, звук отключают
    /// в самой Windows — для одной вкладки это делается в два нажатия
    /// и не требует настроек в портале.
    /// </summary>
    public const string MessageSound = "soft";

    private readonly PortalDbContext _db;

    public UserPreferences(PortalDbContext db) => _db = db;

    /// <summary>Все настройки человека разом: их единицы, и запрос на каждую не нужен.</summary>
    public async Task<Dictionary<string, string>> AllAsync(
        string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return [];
        }

        return await _db.Preferences
            .Where(p => p.UserName.ToLower() == userName.ToLower())
            .ToDictionaryAsync(p => p.Name, p => p.Value, cancellationToken);
    }

    /// <summary>
    /// Запоминает значение. Пустое — забывает настройку совсем, а не пишет
    /// пустую строку: «не задано» и «задано пустым» должны отличаться,
    /// иначе потом не понять, откуда берётся значение по умолчанию.
    /// </summary>
    public async Task SetAsync(
        string userName, string name, string? value, CancellationToken cancellationToken)
    {
        var existing = await _db.Preferences.AsTracking()
            .FirstOrDefaultAsync(
                p => p.UserName.ToLower() == userName.ToLower() && p.Name == name,
                cancellationToken);

        if (string.IsNullOrEmpty(value))
        {
            if (existing is not null)
            {
                _db.Preferences.Remove(existing);
                await _db.SaveChangesAsync(cancellationToken);
            }

            return;
        }

        if (existing is null)
        {
            _db.Preferences.Add(new UserPreference
            {
                UserName = userName,
                Name = name,
                Value = value
            });
        }
        else
        {
            existing.Value = value;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
