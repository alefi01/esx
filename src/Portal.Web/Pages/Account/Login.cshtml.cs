using System.ComponentModel.DataAnnotations;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Security;
using Portal.Web.Services;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Notifications;
using Portal.Web.Services.Offices;

namespace Portal.Web.Pages.Account;

/// <summary>
/// Страница входа.
///
/// В Razor Pages файл .cshtml — это разметка, а этот класс («code-behind») —
/// её обработчик. Метод OnGet вызывается при открытии страницы,
/// OnPostAsync — при отправке формы. Свойства с атрибутом [BindProperty]
/// автоматически заполняются данными из формы по совпадению имён.
/// </summary>
public class LoginModel : PageModel
{
    private readonly IAdAuthenticationService _authentication;
    private readonly IOfficeResolver _offices;
    private readonly LoginThrottle _throttle;
    private readonly ActiveDirectoryOptions _adOptions;
    private readonly NotificationService _notifications;
    private readonly ILogger<LoginModel> _logger;

    public LoginModel(
        IAdAuthenticationService authentication,
        IOfficeResolver offices,
        LoginThrottle throttle,
        IOptions<ActiveDirectoryOptions> adOptions,
        NotificationService notifications,
        ILogger<LoginModel> logger)
    {
        _authentication = authentication;
        _offices = offices;
        _throttle = throttle;
        _adOptions = adOptions.Value;
        _notifications = notifications;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    /// <summary>Сообщение об ошибке для пользователя. Намеренно неконкретное — см. комментарий ниже.</summary>
    public string? ErrorMessage { get; private set; }

    /// <summary>Подсказка формата логина, например "DOMEN\ivanov". Берётся из конфига.</summary>
    public string LoginHint =>
        string.IsNullOrWhiteSpace(_adOptions.DomainNetBios)
            ? "ivanov"
            : $@"{_adOptions.DomainNetBios}\ivanov";

    public sealed class InputModel
    {
        [Required(ErrorMessage = "Введите логин")]
        [Display(Name = "Логин")]
        public string UserName { get; set; } = "";

        [Required(ErrorMessage = "Введите пароль")]
        [DataType(DataType.Password)]
        [Display(Name = "Пароль")]
        public string Password { get; set; } = "";
    }

    public IActionResult OnGet(string? returnUrl = null)
    {
        // Если человек уже вошёл, показывать ему форму входа незачем.
        if (User.Identity?.IsAuthenticated == true)
        {
            return RedirectToPage("/Index");
        }

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(string? returnUrl = null, CancellationToken cancellationToken = default)
    {
        if (!ModelState.IsValid)
        {
            return Page();
        }

        var remoteIp = HttpContext.Connection.RemoteIpAddress;
        var throttleKey = LoginThrottle.BuildKey(Input.UserName, remoteIp?.ToString());

        // Проверка «не исчерпаны ли попытки» — ДО обращения к контроллеру домена.
        // Иначе смысл теряется: доменная учётка успела бы заблокироваться.
        var lockout = _throttle.GetRemainingLockout(throttleKey);

        if (lockout is not null)
        {
            _logger.LogWarning(
                "Попытки входа для {User} с адреса {Ip} временно приостановлены.",
                Input.UserName, remoteIp);

            ErrorMessage =
                $"Слишком много неудачных попыток. Повторите через {Math.Ceiling(lockout.Value.TotalMinutes)} мин.";

            return Page();
        }

        // Определяем офис по IP: от этого зависит, к какому контроллеру домена идти первым.
        var office = _offices.ResolveByAddress(remoteIp);

        var result = await _authentication.AuthenticateAsync(
            Input.UserName, Input.Password, office?.Code, cancellationToken);

        if (result.Status != AdAuthenticationStatus.Success || result.User is null)
        {
            _throttle.RegisterFailure(throttleKey);

            // Пользователю показываем одно и то же сообщение для «нет такого логина»
            // и «неверный пароль». Если различать, форма входа превращается в удобный
            // способ выяснить, какие учётные записи существуют в домене.
            // Подробности — только в журнал.
            ErrorMessage = result.Status switch
            {
                AdAuthenticationStatus.AccountRestricted =>
                    "Вход невозможен: учётная запись заблокирована, отключена или требует смены пароля. " +
                    "Обратитесь к администратору.",
                AdAuthenticationStatus.ServerUnavailable =>
                    "Контроллер домена сейчас недоступен. Попробуйте позже или сообщите администратору.",

                // Отдельная формулировка для технического сбоя.
                // Раньше здесь тоже говорилось «неверный логин или пароль», и это
                // сбивало с толку: при неправильно настроенном BaseDn пароль
                // проверяется УСПЕШНО, а пользователя не находят в каталоге —
                // и администратор сутки ищет проблему в паролях.
                // Пользователю подробности по-прежнему не показываем,
                // но говорим честно, что дело не в нём.
                AdAuthenticationStatus.Error =>
                    "Вход не удался из-за ошибки настройки портала. Пароль здесь ни при чём — " +
                    "сообщите администратору, подробности записаны в журнал.",

                _ => "Неверный логин или пароль."
            };

            // Технический сбой — это не «пользователь ошибся», а «сломана настройка».
            // Уровень записи в журнале должен это отражать, иначе такая строка
            // теряется среди обычных опечаток в паролях.
            if (result.Status == AdAuthenticationStatus.Error)
            {
                _logger.LogError(
                    "ОШИБКА НАСТРОЙКИ при входе {User} с {Ip}. Контроллер: {Dc}. Подробности: {Detail}",
                    Input.UserName, remoteIp, result.DomainController, result.TechnicalDetail);
            }
            else
            {
                _logger.LogWarning(
                    "Неудачный вход {User} с {Ip}: {Status}. Подробности: {Detail}",
                    Input.UserName, remoteIp, result.Status, result.TechnicalDetail);
            }

            return Page();
        }

        var user = result.User;

        // Проверка базового доступа. Делаем её здесь, а не только политикой авторизации,
        // чтобы не выдавать cookie тому, кому вход всё равно запрещён,
        // и чтобы показать понятную причину вместо «Доступ запрещён» на пустой странице.
        if (!user.Groups.Contains(_adOptions.AccessGroup, StringComparer.OrdinalIgnoreCase))
        {
            _logger.LogWarning(
                "Пользователь {User} прошёл проверку пароля, но не состоит в группе {Group}.",
                user.SamAccountName, _adOptions.AccessGroup);

            ErrorMessage =
                $"Пароль верный, но у вас нет доступа к порталу. " +
                $"Нужно членство в группе «{_adOptions.AccessGroup}». Обратитесь к администратору.";

            return Page();
        }

        _throttle.RegisterSuccess(throttleKey);

        await SignInAsync(user, office, result.DomainController);

        // Запоминаем, как показывать этого человека. Пригодится в переписках,
        // если контроллер домена окажется недоступен: список собеседников
        // тогда собирается из тех, кто уже входил в портал.
        //
        // Ошибку глотаем намеренно: недоступная база не должна мешать войти
        // в портал — файлы и раздел диагностики работают и без неё.
        try
        {
            await _notifications.RememberUserAsync(
                user.SamAccountName, user.DisplayName, cancellationToken);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Не удалось запомнить имя пользователя {User}.", user.SamAccountName);
        }

        _logger.LogInformation(
            "Пользователь {User} вошёл с {Ip} (офис: {Office}).",
            user.SamAccountName, remoteIp, office?.Code ?? "не определён");

        // LocalRedirect отвергает абсолютные адреса. Без этой проверки параметр returnUrl
        // превращается в «открытый редирект»: ссылку на портал, которая уводит на чужой сайт.
        if (!string.IsNullOrEmpty(returnUrl) && Url.IsLocalUrl(returnUrl))
        {
            return LocalRedirect(returnUrl);
        }

        return RedirectToPage("/Index");
    }

    /// <summary>Складывает данные пользователя в cookie аутентификации.</summary>
    private async Task SignInAsync(AdUserInfo user, OfficeDefinition? office, string? domainController)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.SamAccountName),
            new(ClaimTypes.NameIdentifier, user.DistinguishedName),
            new(PortalClaimTypes.AuthenticatedBy, domainController ?? "")
        };

        if (!string.IsNullOrWhiteSpace(user.DisplayName))
        {
            claims.Add(new Claim(ClaimTypes.GivenName, user.DisplayName));
        }

        if (!string.IsNullOrWhiteSpace(user.Email))
        {
            claims.Add(new Claim(ClaimTypes.Email, user.Email));
        }

        if (office is not null)
        {
            claims.Add(new Claim(PortalClaimTypes.Office, office.Code));
            claims.Add(new Claim(PortalClaimTypes.OfficeName, office.Name));
        }

        // Каждая группа AD становится ролью. Благодаря этому в коде можно писать
        // User.IsInRole("WebUsers") и [Authorize(Roles = "...")] с настоящими именами групп,
        // без промежуточной таблицы соответствий.
        //
        // Ограничение, о котором стоит знать: всё это едет в cookie на каждый запрос.
        // Если пользователь состоит в сотнях групп, cookie распухнет (ASP.NET Core
        // разрежет её на части автоматически, но трафик вырастет). При 20 пользователях
        // это не проблема; если станет — здесь надо будет оставить только нужные порталу группы.
        foreach (var group in user.Groups)
        {
            claims.Add(new Claim(ClaimTypes.Role, group));
        }

        var identity = new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme);

        await HttpContext.SignInAsync(
            CookieAuthenticationDefaults.AuthenticationScheme,
            new ClaimsPrincipal(identity),
            new AuthenticationProperties
            {
                // false — сессионная cookie: закрыл браузер, значит вышел.
                // Для портала с документами это правильное поведение по умолчанию.
                IsPersistent = false,
                IssuedUtc = DateTimeOffset.UtcNow
            });
    }
}
