namespace Portal.Web.Services.ActiveDirectory;

/// <summary>
/// Проверка доменной учётной записи и получение членства в группах.
/// Интерфейс нужен, чтобы страницу входа можно было тестировать без живого AD:
/// достаточно подсунуть другую реализацию.
/// </summary>
public interface IAdAuthenticationService
{
    /// <param name="userName">
    /// Логин в любом из привычных пользователю форматов: "ivanov", "DOMEN\ivanov" или "ivanov@domen.pro".
    /// </param>
    /// <param name="password">Пароль в открытом виде. Нигде не сохраняется и не логируется.</param>
    /// <param name="officeCode">
    /// Код офиса пользователя (определён по IP). Задаёт порядок обхода контроллеров домена:
    /// сначала «свои», потом остальные. Может быть null, если офис неизвестен.
    /// </param>
    Task<AdAuthenticationResult> AuthenticateAsync(
        string userName,
        string password,
        string? officeCode,
        CancellationToken cancellationToken = default);
}
