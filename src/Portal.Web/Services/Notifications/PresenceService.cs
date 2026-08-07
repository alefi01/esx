using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Portal.Web.Data;

namespace Portal.Web.Services.Notifications;

/// <summary>Кто в сети и когда его видели в последний раз.</summary>
/// <param name="Online">Человек шевелился только что.</param>
/// <param name="LastActiveAt">Когда видели в последний раз, местное время. null — не заходил ни разу.</param>
public sealed record Presence(bool Online, DateTime? LastActiveAt);

/// <summary>
/// Кто сейчас в портале.
///
/// КАК ЭТО УСТРОЕНО И ПОЧЕМУ НЕ ИНАЧЕ
///
/// «В сети» здесь означает не открытое соединение, а «что-то делал
/// в последние несколько минут». Портал засекает время при каждом запросе
/// страницы и при каждом опросе колокольчика — а тот идёт раз в минуту,
/// пока вкладка открыта. Значит, у человека с открытым порталом отметка
/// обновляется сама, даже если он просто читает.
///
/// Постоянное соединение на каждого (WebSocket) дало бы точность до
/// секунды, но между офисами канал узкий и рвущийся: висящее соединение
/// там регулярно обрывается, и человек выглядел бы то ушедшим,
/// то вернувшимся. Точность «плюс-минус минута» для строчки «в сети»
/// достаточна, а стоит она одного поля в таблице.
///
/// ПОЧЕМУ ЗАПИСЬ РЕДКАЯ
///
/// Писать в базу на каждый запрос — это запись на каждую картинку
/// и каждый переход. Поэтому отметка обновляется не чаще раза в минуту
/// на человека: между записями достаточно памяти процесса.
/// </summary>
public sealed class PresenceService
{
    /// <summary>Сколько человек считается «в сети» после последнего действия.</summary>
    public static readonly TimeSpan OnlineFor = TimeSpan.FromMinutes(5);

    /// <summary>Как часто отметка доходит до базы.</summary>
    private static readonly TimeSpan WriteEvery = TimeSpan.FromMinutes(1);

    private readonly PortalDbContext _db;
    private readonly IMemoryCache _cache;
    private readonly TimeProvider _time;
    private readonly ILogger<PresenceService> _logger;

    public PresenceService(
        PortalDbContext db,
        IMemoryCache cache,
        TimeProvider time,
        ILogger<PresenceService> logger)
    {
        _db = db;
        _cache = cache;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// «Этот человек только что что-то делал». Вызывается на каждый запрос;
    /// в базу пишет не чаще раза в минуту на человека.
    /// </summary>
    public async Task TouchAsync(string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return;
        }

        var key = "presence:" + userName.ToLowerInvariant();

        if (_cache.TryGetValue(key, out _))
        {
            return;
        }

        _cache.Set(key, true, WriteEvery);

        var now = _time.GetUtcNow().UtcDateTime;

        try
        {
            var state = await _db.Set<UserSeenState>().AsTracking()
                .FirstOrDefaultAsync(s => s.UserName == userName, cancellationToken);

            if (state is null)
            {
                // Отметки ещё нет — заведём её. Здесь ТОЛЬКО время активности:
                // границу прочитанных объявлений ставит колокольчик, и трогать
                // её отсюда нельзя, иначе первый же заход спрячет всю ленту.
                _db.Set<UserSeenState>().Add(new UserSeenState
                {
                    UserName = userName,
                    LastActiveAt = now,
                    UpdatedAt = now
                });
            }
            else
            {
                state.LastActiveAt = now;
            }

            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            // База не ответила — «в сети» не та вещь, ради которой стоит
            // ронять страницу. Отметка просто не обновится.
            _logger.LogWarning(ex, "Не удалось отметить активность {User}.", userName);
        }
    }

    /// <summary>Присутствие нескольких человек одним запросом.</summary>
    public async Task<IReadOnlyDictionary<string, Presence>> ForAsync(
        IReadOnlyCollection<string> userNames, CancellationToken cancellationToken = default)
    {
        var result = new Dictionary<string, Presence>(StringComparer.OrdinalIgnoreCase);

        if (userNames.Count == 0)
        {
            return result;
        }

        var lowered = userNames.Select(u => u.ToLower()).ToList();

        try
        {
            var states = await _db.Set<UserSeenState>()
                .Where(s => lowered.Contains(s.UserName.ToLower()))
                .Select(s => new { s.UserName, s.LastActiveAt })
                .ToListAsync(cancellationToken);

            var border = _time.GetUtcNow().UtcDateTime - OnlineFor;

            foreach (var state in states)
            {
                result[state.UserName] = new Presence(
                    state.LastActiveAt is { } at && at >= border,
                    state.LastActiveAt?.ToLocalTime());
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Не удалось получить, кто в сети.");
        }

        foreach (var userName in userNames)
        {
            result.TryAdd(userName, new Presence(false, null));
        }

        return result;
    }

    /// <summary>
    /// Подпись под именем: «в сети» либо «был в сети …».
    ///
    /// Слово «был» одинаково для всех: род собеседника порталу неизвестен,
    /// а угадывать его по имени — верный способ однажды ошибиться.
    /// Поэтому формулировка нейтральная — «последний раз в сети».
    /// </summary>
    public static string Describe(Presence presence, DateTime now)
    {
        if (presence.Online)
        {
            return "в сети";
        }

        if (presence.LastActiveAt is not { } at)
        {
            return "не заходил в портал";
        }

        var ago = now - at;

        if (ago < TimeSpan.FromMinutes(60))
        {
            return $"последний раз в сети {Math.Max(1, (int)ago.TotalMinutes)} мин. назад";
        }

        if (at.Date == now.Date)
        {
            return "последний раз в сети сегодня в " + at.ToString("HH:mm");
        }

        if (at.Date == now.Date.AddDays(-1))
        {
            return "последний раз в сети вчера в " + at.ToString("HH:mm");
        }

        return "последний раз в сети " + at.ToString("dd.MM.yyyy") + " в " + at.ToString("HH:mm");
    }
}
