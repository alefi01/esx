namespace Portal.Web.Configuration;

/// <summary>Секция "Database" в appsettings.json.</summary>
public sealed class DatabaseOptions
{
    public const string SectionName = "Database";

    /// <summary>
    /// Приводить схему базы к нужному виду при запуске приложения.
    ///
    /// Когда true, при старте выполняются все ещё не применённые миграции —
    /// то есть создаются недостающие таблицы и колонки. Это избавляет от
    /// необходимости ставить на сервер отдельный инструмент (dotnet-ef),
    /// который в сети без интернета пришлось бы отдельно переносить.
    ///
    /// Так можно делать, потому что веб-сервер один. Если когда-нибудь
    /// появится второй, два экземпляра приложения могут начать применять
    /// миграции одновременно — тогда это надо выключить и накатывать
    /// изменения вручную SQL-скриптом.
    /// </summary>
    public bool ApplyMigrationsOnStartup { get; set; } = true;

    /// <summary>
    /// Сколько секунд ждать ответа базы данных, прежде чем считать запрос неудачным.
    /// </summary>
    public int CommandTimeoutSeconds { get; set; } = 30;

    /// <summary>Сколько объявлений показывать на одной странице ленты.</summary>
    public int AnnouncementsPageSize { get; set; } = 20;
}
