using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Sync;

/// <summary>
/// Настройки синхронизации, которые меняются в панели администратора.
///
/// Почему не в appsettings.json: правка файла требует доступа к серверу
/// и перезапуска пула приложений IIS. Для того, что администратор
/// подкручивает по ходу дела — «спрашивать соседей раз в 15 минут
/// или раз в час» — это слишком тяжело.
///
/// Адреса филиалов и пароль обмена, наоборот, остаются в файле:
/// это часть устройства сети, а не повседневная настройка, и менять
/// их через веб-страницу было бы опаснее, чем удобнее.
/// </summary>
public sealed class SyncSettings
{
    /// <summary>Ключи в таблице настроек.</summary>
    public const string IntervalKey = "sync.interval.minutes";

    public const string PausedKey = "sync.paused";

    private readonly PortalDbContext _db;
    private readonly SyncOptions _options;
    private readonly TimeProvider _time;

    public SyncSettings(PortalDbContext db, IOptions<SyncOptions> options, TimeProvider time)
    {
        _db = db;
        _options = options.Value;
        _time = time;
    }

    /// <summary>Как часто спрашивать соседей, минуты.</summary>
    public async Task<int> IntervalMinutesAsync(CancellationToken cancellationToken)
    {
        var stored = await ValueAsync(IntervalKey, cancellationToken);

        return int.TryParse(stored, out var minutes) && minutes is >= 1 and <= 1440
            ? minutes
            : Math.Clamp(_options.DefaultIntervalMinutes, 1, 1440);
    }

    /// <summary>Приостановлен ли обмен вручную.</summary>
    public async Task<bool> PausedAsync(CancellationToken cancellationToken) =>
        await ValueAsync(PausedKey, cancellationToken) == "1";

    public Task SetIntervalAsync(int minutes, string userName, CancellationToken cancellationToken) =>
        SetAsync(IntervalKey, Math.Clamp(minutes, 1, 1440).ToString(), userName, cancellationToken);

    public Task SetPausedAsync(bool paused, string userName, CancellationToken cancellationToken) =>
        SetAsync(PausedKey, paused ? "1" : "0", userName, cancellationToken);

    private async Task<string?> ValueAsync(string key, CancellationToken cancellationToken) =>
        await _db.Settings
            .Where(s => s.Key == key)
            .Select(s => s.Value)
            .FirstOrDefaultAsync(cancellationToken);

    private async Task SetAsync(string key, string value, string userName, CancellationToken cancellationToken)
    {
        var existing = await _db.Settings
            .AsTracking()
            .FirstOrDefaultAsync(s => s.Key == key, cancellationToken);

        if (existing is null)
        {
            _db.Settings.Add(new PortalSetting
            {
                Key = key,
                Value = value,
                UpdatedAt = _time.GetUtcNow().UtcDateTime,
                UpdatedByUserName = userName
            });
        }
        else
        {
            existing.Value = value;
            existing.UpdatedAt = _time.GetUtcNow().UtcDateTime;
            existing.UpdatedByUserName = userName;
        }

        await _db.SaveChangesAsync(cancellationToken);
    }
}
