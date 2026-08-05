using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>Сколько места занято во всём хранилище и сколько всего отведено.</summary>
/// <param name="UsedBytes">Занято живыми файлами (без корзины).</param>
/// <param name="TrashBytes">Занято тем, что лежит в корзине и ещё не стёрто с диска.</param>
/// <param name="TotalBytes">Сколько всего отведено под хранилище. 0 — предел не задан.</param>
/// <param name="Known">Удалось ли вообще посчитать. false — база не ответила.</param>
public sealed record StorageUsageInfo(long UsedBytes, long TrashBytes, long TotalBytes, bool Known = true)
{
    /// <summary>Занятая доля от 0 до 1. Если предел не задан — 0.</summary>
    public double Fraction =>
        TotalBytes <= 0 ? 0 : Math.Clamp((double)(UsedBytes + TrashBytes) / TotalBytes, 0, 1);

    /// <summary>Показывать ли полосу заполнения: без предела и без чисел она бессмысленна.</summary>
    public bool ShowBar => Known && TotalBytes > 0;

    /// <summary>Заполнено больше 90% — пора вмешаться.</summary>
    public bool IsNearlyFull => ShowBar && Fraction >= .9;

    /// <summary>Подпись под полосой: «12,4 ГБ из 200 ГБ».</summary>
    public string Caption =>
        !Known
            ? "нет данных"
            : TotalBytes <= 0
                ? $"{UploadValidator.Format(UsedBytes + TrashBytes)} — предел не задан"
                : $"{UploadValidator.Format(UsedBytes + TrashBytes)} из {UploadValidator.Format(TotalBytes)}";
}

/// <summary>
/// Считает занятое место для карточки в боковом меню.
///
/// ПОЧЕМУ С КЭШЕМ
///
/// Карточка показывается на КАЖДОЙ странице портала. Без кэша каждый переход
/// по сайту гнал бы в базу запрос «просуммируй размеры всех файлов» —
/// а это полный проход по таблице. При двадцати пользователях это не сломает
/// сервер, но и смысла в такой честности нет: цифра «занято 12 ГБ из 200»
/// не портится оттого, что ей полминуты.
///
/// Поэтому результат держится в памяти минуту, один на весь портал:
/// значение общее для всех, у пользователей оно не различается. Сразу после
/// загрузки или удаления файла человек может увидеть прежнее число —
/// это ожидаемо и не является ошибкой.
/// </summary>
public sealed class StorageUsage
{
    private const string CacheKey = "storage-usage";
    private static readonly TimeSpan CacheFor = TimeSpan.FromSeconds(60);

    /// <summary>Насколько запоминаем неудачу — см. комментарий в GetAsync.</summary>
    private static readonly TimeSpan RememberFailureFor = TimeSpan.FromSeconds(15);

    private readonly IMemoryCache _cache;
    private readonly PortalDbContext _db;
    private readonly StorageOptions _options;
    private readonly ILogger<StorageUsage> _logger;

    public StorageUsage(
        IMemoryCache cache,
        PortalDbContext db,
        IOptions<StorageOptions> options,
        ILogger<StorageUsage> logger)
    {
        _cache = cache;
        _db = db;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<StorageUsageInfo> GetAsync(CancellationToken cancellationToken = default)
    {
        if (_cache.TryGetValue(CacheKey, out StorageUsageInfo? cached) && cached is not null)
        {
            return cached;
        }

        var total = (long)_options.TotalCapacityGb * 1024 * 1024 * 1024;

        try
        {
            // Две суммы: живое и то, что ждёт в корзине.
            // Суммируем как long?, а не long: по пустой таблице сумма
            // не определена, и на обычном long запрос вернул бы ошибку.
            var used = await _db.Files
                .Where(f => f.DeletedAt == null)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

            var trash = await _db.Files
                .Where(f => f.DeletedAt != null)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

            var info = new StorageUsageInfo(used, trash, total);

            _cache.Set(CacheKey, info, CacheFor);

            return info;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // База недоступна — это НЕ повод обрушить страницу.
            //
            // Карточка «сколько занято» выводится в боковом меню, то есть
            // на каждой странице портала. Если пустить исключение дальше,
            // упадёт всё разом, включая страницу диагностики — ту самую,
            // которая и должна сообщить, что база не отвечает.
            //
            // Поэтому: пишем в журнал, показываем «нет данных» и ненадолго
            // это запоминаем. Иначе каждая страница заново ждала бы ответа
            // от лежащей базы, и портал стал бы мучительно медленным.
            _logger.LogWarning(ex, "Не удалось посчитать занятое место в хранилище.");

            var unknown = new StorageUsageInfo(0, 0, total, Known: false);

            _cache.Set(CacheKey, unknown, RememberFailureFor);

            return unknown;
        }
    }

    /// <summary>
    /// Сбросить запомненное значение — вызывается после загрузки и удаления,
    /// чтобы цифра в меню обновилась сразу, а не через минуту.
    /// </summary>
    public void Invalidate() => _cache.Remove(CacheKey);
}
