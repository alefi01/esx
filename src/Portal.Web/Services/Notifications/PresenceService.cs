using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Portal.Web.Data;

namespace Portal.Web.Services.Notifications;

/// <summary>Кто в сети и когда его видели в последний раз.</summary>
/// <param name="Online">Человек шевелился только что.</param>
/// <param name="LastActiveAt">Когда видели в последний раз, местное время. null — не заходил ни разу.</param>
/// <param name="SignedOut">Человек вышел из портала сам — значит его точно нет.</param>
public sealed record Presence(bool Online, DateTime? LastActiveAt, bool SignedOut = false);

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
    /// <summary>
    /// Сколько человек считается «в сети» после последнего действия.
    ///
    /// Две минуты, а не пять. Пока портал открыт, страница сама
    /// спрашивает сервер каждые несколько секунд, поэтому отметка
    /// у работающего человека свежая всегда. А вот закрытая вкладка
    /// или ушедший с машины человек должны переставать числиться
    /// в сети быстро: пять минут «в сети» после ухода — это пять минут,
    /// в течение которых коллега ждёт мгновенного ответа и не получает
    /// его. Особенно заметно на сменном компьютере, где за одним столом
    /// работают по очереди.
    /// </summary>
    public static readonly TimeSpan OnlineFor = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Сколько после этого человек считается «нет на месте».
    ///
    /// Между «в сети» и «не в сети» есть середина, и она самая частая:
    /// портал открыт, но человек вышел с чайником. Зелёный кружок в этот
    /// момент обманывает — написавший ждёт ответа «сейчас». Серый тоже
    /// обманывает — человек за столом через минуту. Поэтому третий цвет.
    /// </summary>
    public static readonly TimeSpan AwayFor = TimeSpan.FromMinutes(30);

    /// <summary>
    /// Состояние для кружка на аватарке: online, away, offline.
    ///
    /// Строкой, а не перечислением: это значение идёт прямо в разметку
    /// классом (dot--online), и превращать его там обратно в строку
    /// пришлось бы в каждом месте, где рисуется аватарка.
    /// </summary>
    public static string StatusOf(Presence presence, DateTime nowLocal)
    {
        // Вышел сам — значит его точно нет, и «нет на месте» здесь
        // неуместно: это состояние для того, кто отошёл, не закрывая
        // портал.
        if (presence.SignedOut)
        {
            return "offline";
        }

        if (presence.Online)
        {
            return "online";
        }

        return presence.LastActiveAt is { } at && nowLocal - at < AwayFor ? "away" : "offline";
    }

    /// <summary>Подпись к кружку — для подсказки при наведении.</summary>
    public static string StatusWord(string status) => status switch
    {
        "online" => "в сети",
        "away" => "нет на месте",
        _ => "не в сети"
    };

    /// <summary>
    /// Как часто отметка доходит до базы.
    ///
    /// Реже, чем длится «в сети», но заметно реже и чем опрос страницы:
    /// писать на каждый запрос — это запись на каждую картинку
    /// и каждый переход.
    /// </summary>
    private static readonly TimeSpan WriteEvery = TimeSpan.FromSeconds(40);

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

        // Человек снова что-то делает — значит он вернулся: отметка
        // о выходе больше не действует. Снимаем её ДО проверки частоты
        // записи, иначе вернувшийся целую минуту числился бы ушедшим.
        _cache.Remove(SignedOutKey(userName));

        var key = TouchKey(userName);

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

    /// <summary>
    /// «Этот человек больше не в портале»: вызывается при выходе и когда
    /// на той же машине входит кто-то другой.
    ///
    /// ЗАЧЕМ ЭТО ВООБЩЕ НУЖНО
    ///
    /// «В сети» означает «что-то делал в последние пять минут». Без этой
    /// отметки вышедший человек ещё пять минут числится в сети — и на
    /// сменном компьютере, где сотрудники входят друг за другом, в сети
    /// оказываются сразу оба: тот, кто только что вышел, и тот, кто
    /// вошёл. Выглядит как ошибка, и по сути ею и является: про первого
    /// порталу ТОЧНО известно, что он уже не здесь.
    ///
    /// Отметка не стирается, а отодвигается на границу: «последний раз
    /// в сети» должно остаться — это полезнее пустоты.
    /// </summary>
    public Task MarkOfflineAsync(string userName, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return Task.CompletedTask;
        }

        // Снимаем запрет на запись: иначе следующий вход в течение минуты
        // не обновил бы время активности, и вернувшийся человек остался бы
        // «не в сети».
        _cache.Remove(TouchKey(userName));

        // Сам факт выхода держим в памяти, а не в базе, и ВРЕМЯ АКТИВНОСТИ
        // не трогаем: «последний раз в сети в 17:40» должно остаться правдой.
        // Если двигать его назад, подпись начнёт врать на пять минут.
        //
        // Памяти для этого достаточно: портал живёт одним процессом
        // (см. docs/13), а сама отметка нужна ровно до тех пор, пока
        // человек не перестал бы считаться «в сети» и без неё.
        _cache.Set(SignedOutKey(userName), true, AwayFor);

        return Task.CompletedTask;
    }

    private static string TouchKey(string userName) => "presence:" + userName.ToLowerInvariant();

    private static string SignedOutKey(string userName) => "signedout:" + userName.ToLowerInvariant();

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
                var signedOut = _cache.TryGetValue(SignedOutKey(state.UserName), out _);

                result[state.UserName] = new Presence(
                    !signedOut && state.LastActiveAt is { } at && at >= border,
                    state.LastActiveAt?.ToLocalTime(),
                    signedOut);
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
        if (presence.Online && !presence.SignedOut)
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
