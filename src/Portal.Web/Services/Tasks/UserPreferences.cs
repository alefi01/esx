using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Tasks;

/// <summary>
/// Личные настройки человека: звук новых сообщений — общий и по беседам.
///
/// Хранится в базе, а не в браузере, намеренно: «у меня от начальника
/// звонок, от рассылки тишина» — это про человека, а не про компьютер,
/// и настроенное в кабинете должно работать и с другой машины.
/// Облегчённый режим, наоборот, живёт в браузере — см. theme.js.
/// </summary>
public sealed class UserPreferences
{
    /// <summary>Звук новых сообщений по умолчанию.</summary>
    public const string MessageSound = "sound";

    /// <summary>Что считается «звука нет». Пустое значение означает то же самое.</summary>
    public const string Silent = "none";

    /// <summary>
    /// Звуки, которые портал умеет проигрывать.
    ///
    /// Это НЕ файлы. Звук собирается в браузере из нескольких тонов
    /// (см. sound.js) по одной простой причине: в вашей сети нет интернета,
    /// а класть в репозиторий набор mp3 ради трёх сигналов — лишние
    /// мегабайты, которые ещё и не у всех браузеров проиграются одинаково.
    /// Код даёт одинаковый звук везде и весит строчки.
    /// </summary>
    public static readonly IReadOnlyList<(string Id, string Name)> Sounds =
    [
        ("soft", "Мягкий"),
        ("ping", "Звонкий"),
        ("double", "Двойной"),
        ("low", "Низкий"),
        (Silent, "Без звука")
    ];

    public static bool IsKnown(string? id) =>
        !string.IsNullOrEmpty(id) && Sounds.Any(s => s.Id == id);

    /// <summary>Имя настройки для конкретной беседы.</summary>
    public static string SoundFor(int conversationId) => "sound:" + conversationId;

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
