namespace Portal.Web.Configuration;

/// <summary>
/// Секция "Security". Здесь собрано всё, что переключается при переходе с HTTP на HTTPS,
/// чтобы это была одна правка в конфиге, а не поиск по коду.
/// </summary>
public sealed class SecurityOptions
{
    public const string SectionName = "Security";

    /// <summary>
    /// true — включается принудительный редирект на HTTPS и флаг Secure на cookie
    /// (cookie перестаёт отправляться по HTTP вообще).
    /// На время разработки по HTTP держим false.
    /// ВАЖНО: включать только после того, как HTTPS реально заработал,
    /// иначе получите бесконечный редирект и невозможность войти.
    /// </summary>
    public bool RequireHttps { get; set; } = false;

    /// <summary>
    /// HSTS — заголовок, которым сайт говорит браузеру «ко мне только по HTTPS».
    /// Браузер это запоминает надолго, поэтому включать ТОЛЬКО после RequireHttps=true
    /// и после проверки, что сертификат валидный. Ошибку здесь тяжело откатить:
    /// браузеры пользователей будут помнить запрет ещё HstsMaxAgeDays дней.
    /// </summary>
    public bool UseHsts { get; set; } = false;

    /// <summary>Срок действия HSTS. На первое время после включения разумно поставить 1–7 дней.</summary>
    public int HstsMaxAgeDays { get; set; } = 1;

    /// <summary>Сколько живёт сессия. Скользящая: активность продлевает срок.</summary>
    public int SessionHours { get; set; } = 8;

    /// <summary>
    /// Сколько неудачных попыток входа разрешаем с одного IP по одному логину,
    /// прежде чем на LoginLockoutMinutes перестанем даже обращаться к контроллеру домена.
    /// Это защита не столько от подбора, сколько от блокировки доменных учёток:
    /// без неё чужой скрипт с формой входа заблокирует ваших пользователей в AD.
    /// </summary>
    public int MaxFailedLoginAttempts { get; set; } = 5;

    /// <summary>На сколько минут включается пауза после исчерпания попыток.</summary>
    public int LoginLockoutMinutes { get; set; } = 5;
}
