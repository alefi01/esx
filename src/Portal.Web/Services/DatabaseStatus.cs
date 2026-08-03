namespace Portal.Web.Services;

/// <summary>
/// Что произошло с базой данных при запуске приложения.
///
/// Зачем это нужно. Вход в портал базу не использует — он работает через
/// Active Directory. Поэтому если PostgreSQL остановлен или недоступен,
/// правильное поведение портала не «упасть целиком», а продолжить работать
/// с тем, что не зависит от базы: пустить людей внутрь, показать диагностику,
/// дать администратору понять, что именно сломалось.
///
/// Этот класс и хранит результат: приложение записывает его при старте,
/// а страницы читают, чтобы показать понятное сообщение вместо
/// технической ошибки Npgsql.
/// </summary>
public sealed class DatabaseStatus
{
    /// <summary>true — схема базы в порядке, работать можно.</summary>
    public bool IsReady { get; private set; }

    /// <summary>Текст ошибки для администратора. Обычным пользователям не показываем.</summary>
    public string? Error { get; private set; }

    /// <summary>Когда состояние было записано.</summary>
    public DateTimeOffset CheckedAt { get; private set; }

    public void MarkReady()
    {
        IsReady = true;
        Error = null;
        CheckedAt = DateTimeOffset.UtcNow;
    }

    public void MarkFailed(string error)
    {
        IsReady = false;
        Error = error;
        CheckedAt = DateTimeOffset.UtcNow;
    }
}
