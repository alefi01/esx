using System.Collections.Concurrent;
using System.Threading.Channels;

namespace Portal.Web.Services.Notifications;

/// <summary>
/// Раздача «у вас что-то новое» открытым страницам — без опроса.
///
/// ЗАЧЕМ ЭТО ПОНАДОБИЛОСЬ
///
/// Страница спрашивает сервер по таймеру. Пока на неё смотрят, это
/// работает. Но стоит свернуть браузер и уйти в другую программу —
/// а это ровно тот случай, ради которого уведомления и нужны, — как
/// браузер придушивает таймеры фоновых вкладок: вместо раза в семь
/// секунд они срабатывают раз в минуту, а то и реже. Сообщение приходило
/// молча, звук раздавался с опозданием на минуту или не раздавался вовсе.
///
/// Приходящие по сети данные браузер так не придерживает: их он
/// обрабатывает сразу, в каком бы состоянии ни была вкладка. Поэтому
/// страница держит открытым одно соединение (см. /api/notifications/stream),
/// а сервер пишет в него строчку, когда человеку действительно что-то
/// пришло. Страница на это тут же спрашивает подробности обычным запросом
/// и показывает звук, мигание вкладки и окошко системы.
///
/// ПОЧЕМУ ЭТО НЕ ПРОТИВОРЕЧИТ «НИКАКИХ ПОСТОЯННЫХ СОЕДИНЕНИЙ»
///
/// Раньше в портале их избегали намеренно: канал между офисами узкий
/// и рвётся, а оборванное соединение выглядит как «портал завис». Здесь
/// это учтено. Соединение обычное, HTTP: его пропускают любые прокси
/// и фильтры, в отличие от WebSocket. Рвётся оно без последствий —
/// браузер соединяется заново сам, а на время разрыва остаётся опрос
/// по таймеру, который никуда не делся и работает как прежде. То есть
/// соединение здесь — ускорение, а не единственный путь.
///
/// ПОЧЕМУ В ПАМЯТИ, А НЕ ЧЕРЕЗ БАЗУ
///
/// Сервер один — это решённый вопрос (см. docs/13-этап-9-один-сервер.md).
/// Значит, все открытые страницы подключены к этому же процессу, и шине
/// достаточно жить в его памяти. Появится второй сервер — понадобится
/// общая очередь; пока её заводить незачем.
/// </summary>
public sealed class NotificationHub
{
    /// <summary>
    /// Открытые страницы: на каждого человека — по каналу на вкладку.
    /// Вкладок у одного человека бывает несколько, и разбудить нужно все.
    /// </summary>
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<byte>>> _listeners =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly ILogger<NotificationHub> _logger;

    public NotificationHub(ILogger<NotificationHub> logger) => _logger = logger;

    /// <summary>Сколько сейчас открытых соединений — для страницы диагностики.</summary>
    public int Connections => _listeners.Values.Sum(tabs => tabs.Count);

    /// <summary>
    /// Подписка одной вкладки.
    ///
    /// Канал ёмкостью в одно сообщение и с режимом DropWrite: если вкладка
    /// почему-то не успевает читать, десять накопившихся «проверь почту»
    /// не нужны — хватит одного. Это не очередь сообщений, а звонок
    /// в дверь.
    /// </summary>
    public (Guid Id, ChannelReader<byte> Reader) Subscribe(string userName)
    {
        var channel = Channel.CreateBounded<byte>(new BoundedChannelOptions(1)
        {
            FullMode = BoundedChannelFullMode.DropWrite,
            SingleReader = true,
            SingleWriter = false
        });

        var id = Guid.NewGuid();

        _listeners.GetOrAdd(userName, _ => new ConcurrentDictionary<Guid, Channel<byte>>())[id] = channel;

        return (id, channel.Reader);
    }

    public void Unsubscribe(string userName, Guid id)
    {
        if (!_listeners.TryGetValue(userName, out var tabs))
        {
            return;
        }

        tabs.TryRemove(id, out _);

        // Пустую запись убираем, чтобы словарь не рос именами тех,
        // кто давно ушёл.
        if (tabs.IsEmpty)
        {
            _listeners.TryRemove(userName, out _);
        }
    }

    /// <summary>Разбудить страницы названных людей.</summary>
    public void Notify(IEnumerable<string> userNames)
    {
        foreach (var userName in userNames)
        {
            if (string.IsNullOrEmpty(userName) || !_listeners.TryGetValue(userName, out var tabs))
            {
                continue;
            }

            foreach (var channel in tabs.Values)
            {
                // Писать некуда — значит вкладка ещё не прочитала прошлый
                // звонок, и второй ей ни к чему.
                channel.Writer.TryWrite(1);
            }
        }
    }

    /// <summary>Разбудить всех, у кого открыт портал. Нужно для объявлений.</summary>
    public void NotifyAll()
    {
        foreach (var tabs in _listeners.Values)
        {
            foreach (var channel in tabs.Values)
            {
                channel.Writer.TryWrite(1);
            }
        }
    }
}
