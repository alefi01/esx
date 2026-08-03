using System.Collections.Concurrent;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services;

/// <summary>
/// Счётчик неудачных попыток входа.
///
/// Главная задача — НЕ дать заблокировать доменные учётные записи.
/// Если в домене настроена политика блокировки (а она обычно настроена),
/// то любой, кто дотянется до формы входа, сможет перебором из пяти запросов
/// заблокировать любого сотрудника в AD. Поэтому мы считаем неудачи сами
/// и перестаём обращаться к контроллеру домена раньше, чем сработает
/// доменная блокировка.
///
/// Хранилище — в памяти процесса. Этого достаточно: сервер один, и потеря
/// счётчиков при перезапуске приложения не страшна. Если когда-нибудь появится
/// второй веб-сервер, счётчик надо будет переносить в общее хранилище.
/// </summary>
public sealed class LoginThrottle
{
    private sealed class Entry
    {
        public int FailureCount;
        public DateTimeOffset BlockedUntil;
        public DateTimeOffset LastActivity;
    }

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SecurityOptions _options;
    private readonly TimeProvider _time;

    // Чтобы словарь не рос вечно, раз в 5 минут выкидываем протухшие записи.
    private DateTimeOffset _nextCleanup = DateTimeOffset.MinValue;
    private static readonly TimeSpan CleanupInterval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromHours(1);

    public LoginThrottle(IOptions<SecurityOptions> options, TimeProvider time)
    {
        _options = options.Value;
        _time = time;
    }

    /// <summary>
    /// Ключ намеренно составной: логин + IP. Иначе один человек, забывший пароль,
    /// заблокировал бы форму входа для всех, кто сидит за тем же адресом.
    /// </summary>
    public static string BuildKey(string userName, string? remoteIp) =>
        $"{userName.Trim().ToLowerInvariant()}|{remoteIp ?? "unknown"}";

    /// <summary>Сколько ещё ждать, если попытки исчерпаны. null — можно пробовать.</summary>
    public TimeSpan? GetRemainingLockout(string key)
    {
        CleanupIfDue();

        if (!_entries.TryGetValue(key, out var entry))
        {
            return null;
        }

        var remaining = entry.BlockedUntil - _time.GetUtcNow();

        return remaining > TimeSpan.Zero ? remaining : null;
    }

    /// <summary>Зафиксировать неудачную попытку. При исчерпании лимита включает паузу.</summary>
    public void RegisterFailure(string key)
    {
        var now = _time.GetUtcNow();

        var entry = _entries.GetOrAdd(key, _ => new Entry());

        // Записей на ключ мало и они короткоживущие, поэтому обычной блокировки
        // на объекте достаточно — без неё два одновременных запроса могут потерять инкремент.
        lock (entry)
        {
            entry.LastActivity = now;
            entry.FailureCount++;

            if (entry.FailureCount >= _options.MaxFailedLoginAttempts)
            {
                entry.BlockedUntil = now.AddMinutes(_options.LoginLockoutMinutes);
                entry.FailureCount = 0;   // после паузы счёт начинается заново
            }
        }
    }

    /// <summary>Сбросить счётчик после успешного входа.</summary>
    public void RegisterSuccess(string key) => _entries.TryRemove(key, out _);

    private void CleanupIfDue()
    {
        var now = _time.GetUtcNow();

        if (now < _nextCleanup)
        {
            return;
        }

        _nextCleanup = now.Add(CleanupInterval);

        foreach (var pair in _entries)
        {
            if (now - pair.Value.LastActivity > EntryLifetime && pair.Value.BlockedUntil < now)
            {
                _entries.TryRemove(pair.Key, out _);
            }
        }
    }
}
