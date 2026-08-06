using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Sync;

/// <summary>
/// Фоновая задача: сама, по расписанию, спрашивает соседей.
///
/// ПОЧЕМУ ФОНОМ, А НЕ ПО ЗАПРОСУ СТРАНИЦЫ
///
/// Синхронизация не должна зависеть от того, зашёл ли кто-нибудь на портал.
/// Ночью в филиале никого нет, а объявление, опубликованное в головном
/// офисе в шесть вечера, к утру должно быть на месте.
///
/// ПОЧЕМУ ПРОМЕЖУТОК ЧИТАЕТСЯ КАЖДЫЙ РАЗ ЗАНОВО
///
/// Администратор меняет его в панели управления. Если прочитать значение
/// один раз при запуске, изменение подействовало бы только после
/// перезапуска пула приложений — то есть, на практике, никогда.
/// </summary>
public sealed class SyncBackgroundService : BackgroundService
{
    /// <summary>
    /// Задержка перед первым проходом. При запуске сервер занят применением
    /// миграций и прогревом, и лезть в сеть в ту же секунду незачем.
    /// </summary>
    private static readonly TimeSpan FirstDelay = TimeSpan.FromSeconds(45);

    private readonly IServiceProvider _services;
    private readonly SyncOptions _options;
    private readonly ILogger<SyncBackgroundService> _logger;

    public SyncBackgroundService(
        IServiceProvider services,
        IOptions<SyncOptions> options,
        ILogger<SyncBackgroundService> logger)
    {
        _services = services;
        _options = options.Value;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!ShouldRun())
        {
            return;
        }

        await SafeDelayAsync(FirstDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            var interval = TimeSpan.FromMinutes(Math.Clamp(_options.DefaultIntervalMinutes, 1, 1440));

            try
            {
                using var scope = _services.CreateScope();

                var settings = scope.ServiceProvider.GetRequiredService<SyncSettings>();

                interval = TimeSpan.FromMinutes(await settings.IntervalMinutesAsync(stoppingToken));

                if (await settings.PausedAsync(stoppingToken))
                {
                    _logger.LogDebug("Обмен приостановлен в панели администратора — проход пропущен.");
                }
                else
                {
                    var runner = scope.ServiceProvider.GetRequiredService<SyncRunner>();

                    await runner.RunAsync(stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // Обмен не должен ронять портал. Всё, что можно сделать
                // с неожиданной ошибкой, — записать её и попробовать снова
                // в следующий раз.
                _logger.LogError(ex, "Проход синхронизации завершился ошибкой.");
            }

            await SafeDelayAsync(interval, stoppingToken);
        }
    }

    /// <summary>
    /// Настроено ли всё, что нужно для обмена. Каждая проверка —
    /// про ошибку, которую легко допустить при развёртывании филиала,
    /// поэтому в журнал пишется не «не работает», а что именно не задано.
    /// </summary>
    private bool ShouldRun()
    {
        if (!_options.Enabled)
        {
            _logger.LogInformation(
                "Синхронизация между филиалами выключена (Sync:Enabled = false). Портал работает сам по себе.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.BranchCode))
        {
            _logger.LogError(
                "Синхронизация включена, но не задан код филиала (Sync:BranchCode). Обмен не начат: "
                + "без кода изменения этого сервера невозможно отличить от чужих.");

            return false;
        }

        if (string.IsNullOrWhiteSpace(_options.Key))
        {
            _logger.LogError(
                "Синхронизация включена, но не задан пароль обмена (Sync:Key). Обмен не начат: "
                + "отдавать содержимое портала без проверки нельзя.");

            return false;
        }

        return true;
    }

    private static async Task SafeDelayAsync(TimeSpan delay, CancellationToken token)
    {
        try
        {
            await Task.Delay(delay, token);
        }
        catch (OperationCanceledException)
        {
            // Остановка службы — не ошибка.
        }
    }
}
