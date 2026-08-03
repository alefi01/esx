namespace Portal.Web.Services.ActiveDirectory;

/// <summary>Чем закончилась попытка входа.</summary>
public enum AdAuthenticationStatus
{
    /// <summary>Пароль верный, данные пользователя получены.</summary>
    Success,

    /// <summary>Логин или пароль неверны.</summary>
    InvalidCredentials,

    /// <summary>Пароль верный, но с учётной записью что-то не так: истёк пароль, учётка отключена или заблокирована.</summary>
    AccountRestricted,

    /// <summary>Ни один контроллер домена не ответил. Это проблема сети/инфраструктуры, а не пользователя.</summary>
    ServerUnavailable,

    /// <summary>Прочая ошибка — смотреть журнал.</summary>
    Error
}

/// <summary>Данные пользователя, вычитанные из каталога после успешного bind.</summary>
/// <param name="SamAccountName">Короткий логин, например "ivanov". Это наш основной идентификатор пользователя.</param>
/// <param name="DisplayName">Отображаемое имя из атрибута displayName (если пусто — подставим логин).</param>
/// <param name="Email">Атрибут mail, может отсутствовать.</param>
/// <param name="DistinguishedName">Полный DN учётки в каталоге.</param>
/// <param name="Groups">Короткие имена всех групп, в которых состоит пользователь (с учётом вложенности).</param>
public sealed record AdUserInfo(
    string SamAccountName,
    string DisplayName,
    string? Email,
    string DistinguishedName,
    IReadOnlyList<string> Groups);

/// <summary>Результат аутентификации.</summary>
/// <param name="Status">Итог попытки.</param>
/// <param name="User">Данные пользователя — заполнены только при Status = Success.</param>
/// <param name="DomainController">Контроллер домена, который обработал запрос (для журнала и диагностики).</param>
/// <param name="TechnicalDetail">Техническая подробность для журнала. Пользователю НЕ показываем.</param>
public sealed record AdAuthenticationResult(
    AdAuthenticationStatus Status,
    AdUserInfo? User,
    string? DomainController,
    string? TechnicalDetail)
{
    public static AdAuthenticationResult Success(AdUserInfo user, string dc) =>
        new(AdAuthenticationStatus.Success, user, dc, null);

    public static AdAuthenticationResult Failure(AdAuthenticationStatus status, string? dc, string? detail) =>
        new(status, null, dc, detail);
}
