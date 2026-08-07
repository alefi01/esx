using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Настройки портала, которые администратор меняет из браузера.
///
/// ПОЧЕМУ В БАЗЕ, А НЕ В appsettings.json
///
/// Правка файла настроек означает доступ к серверу по RDP, права
/// на папку приложения и перезапуск пула — то есть заявку в ИТ ради
/// одного числа. Настройки, которые меняют по ходу работы, должны
/// меняться из портала; в файле остаётся то, что задаётся один раз
/// при установке: строка подключения, адреса контроллеров домена, пути.
///
/// Значение из базы ПЕРЕКРЫВАЕТ значение из файла. Пока в базе ничего
/// не задано, работает то, что в файле, — портал, только что поставленный
/// по инструкции, ведёт себя ровно как раньше.
///
/// Кэш на минуту: общий объём хранилища показывается в боковом меню
/// на каждой странице, и ходить за ним в базу на каждый переход незачем.
/// </summary>
public sealed class PortalSettings
{
    /// <summary>Общий объём хранилища, гигабайты. Хранится строкой — таблица общая.</summary>
    public const string TotalCapacityGbKey = "storage.totalCapacityGb";

    private const string CacheKey = "portal-settings";
    private static readonly TimeSpan CacheFor = TimeSpan.FromMinutes(1);

    private readonly PortalDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly StorageOptions _options;
    private readonly ILogger<PortalSettings> _logger;

    public PortalSettings(
        PortalDbContext db,
        IMemoryCache cache,
        IOptions<StorageOptions> options,
        ILogger<PortalSettings> logger)
    {
        _db = db;
        _cache = cache;
        _options = options.Value;
        _logger = logger;
    }

    /// <summary>
    /// Сколько всего отведено под хранилище, гигабайты.
    /// 0 — предел не задан, портал только показывает занятое.
    /// </summary>
    public async Task<int> TotalCapacityGbAsync(CancellationToken cancellationToken = default)
    {
        var stored = await ValueAsync(TotalCapacityGbKey, cancellationToken);

        return int.TryParse(stored, out var gigabytes) && gigabytes >= 0
            ? gigabytes
            : _options.TotalCapacityGb;
    }

    /// <summary>Записывает общий объём. 0 — снять предел.</summary>
    public async Task SetTotalCapacityGbAsync(
        int gigabytes, string userName, CancellationToken cancellationToken = default)
    {
        await SetAsync(
            TotalCapacityGbKey,
            gigabytes.ToString(System.Globalization.CultureInfo.InvariantCulture),
            userName,
            cancellationToken);
    }

    /// <summary>
    /// Числовая настройка из базы. Нет записи или мусор вместо числа —
    /// возвращается значение по умолчанию: испорченная строка в таблице
    /// не должна менять поведение портала.
    /// </summary>
    public async Task<int> IntAsync(
        string key, int fallback, CancellationToken cancellationToken = default)
    {
        var stored = await ValueAsync(key, cancellationToken);

        return int.TryParse(stored, out var value) && value >= 0 ? value : fallback;
    }

    /// <summary>Записывает числовую настройку.</summary>
    public Task SetIntAsync(
        string key, int value, string userName, CancellationToken cancellationToken = default) =>
        SetAsync(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture), userName, cancellationToken);

    private async Task<string?> ValueAsync(string key, CancellationToken cancellationToken)
    {
        var all = await AllAsync(cancellationToken);

        return all.GetValueOrDefault(key);
    }

    private async Task<IReadOnlyDictionary<string, string>> AllAsync(CancellationToken cancellationToken)
    {
        if (_cache.TryGetValue(CacheKey, out IReadOnlyDictionary<string, string>? cached) && cached is not null)
        {
            return cached;
        }

        Dictionary<string, string> settings;

        try
        {
            settings = await _db.Settings.ToDictionaryAsync(s => s.Key, s => s.Value, cancellationToken);
        }
        catch (Exception ex)
        {
            // База не ответила — работаем по файлу настроек. Портал
            // не должен падать из-за необязательной строки в таблице.
            _logger.LogWarning(ex, "Не удалось прочитать настройки портала из базы.");

            return new Dictionary<string, string>();
        }

        _cache.Set(CacheKey, (IReadOnlyDictionary<string, string>)settings, CacheFor);

        return settings;
    }

    private async Task SetAsync(string key, string value, string userName, CancellationToken cancellationToken)
    {
        var existing = await _db.Settings.AsTracking().FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (existing is null)
        {
            _db.Settings.Add(new PortalSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = DateTime.UtcNow,
                UpdatedByUserName = userName
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = DateTime.UtcNow;
            existing.UpdatedByUserName = userName;
        }

        await _db.SaveChangesAsync(cancellationToken);

        // Кэш сбрасываем сразу: администратор, поменявший число, должен
        // увидеть его на следующей же странице, а не через минуту.
        _cache.Remove(CacheKey);
        _cache.Remove(StorageUsage.CacheKey);
    }
}
