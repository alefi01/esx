using System.ComponentModel.DataAnnotations;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Offices;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Аварийная проверка входа — «разбить стекло в случае пожара».
///
/// ЗАЧЕМ ОНА НУЖНА
///
/// Обычная страница диагностики закрыта правами администратора, а права
/// берутся из групп AD — то есть требуют успешного входа. Получается замкнутый
/// круг: если войти не может никто, посмотреть причину тоже нельзя.
/// Эта страница круг разрывает.
///
/// Она делает ровно то же, что форма входа, но вместо обезличенного
/// «неверный логин или пароль» показывает, на каком именно шаге всё встало:
/// не отвечает контроллер домена, не подошёл пароль, учётка заблокирована,
/// или пароль подошёл, но пользователя не видно в заданном BaseDn.
/// Заодно показывает настройки, с которыми приложение реально работает —
/// это сразу выявляет случай, когда appsettings.json затёрли при обновлении.
///
/// ПОЧЕМУ ЭТО НЕ ДЫРА В БЕЗОПАСНОСТИ
///
/// Страница открывается только при одном из двух условий:
///   * запрос пришёл с самого веб-сервера (адрес обратной петли), или
///   * пользователь уже вошёл и состоит в группе администраторов портала.
///
/// Первое условие означает, что человек уже сидит за консолью сервера или
/// подключён к нему по RDP, — то есть и так может всё. Всем остальным
/// страница отвечает «404 не найдено», а не «доступ запрещён»:
/// незачем даже подтверждать, что она существует.
///
/// Пароль, введённый здесь, никуда не сохраняется и в журнал не пишется.
/// Счётчик неудачных попыток работает и здесь — чтобы проверкой нельзя было
/// заблокировать доменную учётную запись.
/// </summary>
public class LoginTestModel : PageModel
{
    private readonly IAdAuthenticationService _authentication;
    private readonly IOfficeResolver _offices;
    private readonly Services.LoginThrottle _throttle;
    private readonly ActiveDirectoryOptions _ad;
    private readonly ILogger<LoginTestModel> _logger;

    public LoginTestModel(
        IAdAuthenticationService authentication,
        IOfficeResolver offices,
        Services.LoginThrottle throttle,
        IOptions<ActiveDirectoryOptions> ad,
        ILogger<LoginTestModel> logger)
    {
        _authentication = authentication;
        _offices = offices;
        _throttle = throttle;
        _ad = ad.Value;
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

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

    public ActiveDirectoryOptions AdOptions => _ad;

    public IReadOnlyList<string> DomainControllerOrder { get; private set; } = [];

    public string? RemoteIp { get; private set; }

    public string DetectedOffice { get; private set; } = "не определён";

    // Результат проверки — заполняется после отправки формы.
    public AdAuthenticationResult? Result { get; private set; }
    public bool HasAccessGroup { get; private set; }
    public bool HasAdminGroup { get; private set; }
    public bool HasPublisherGroup { get; private set; }
    public string? Blocked { get; private set; }

    public IActionResult OnGet()
    {
        if (!IsAllowed())
        {
            return NotFound();
        }

        FillContext();

        return Page();
    }

    public async Task<IActionResult> OnPostAsync(CancellationToken cancellationToken)
    {
        if (!IsAllowed())
        {
            return NotFound();
        }

        FillContext();

        if (!ModelState.IsValid)
        {
            return Page();
        }

        var throttleKey = Services.LoginThrottle.BuildKey(Input.UserName, RemoteIp);
        var lockout = _throttle.GetRemainingLockout(throttleKey);

        if (lockout is not null)
        {
            Blocked = $"Попытки для этого логина приостановлены ещё на " +
                      $"{Math.Ceiling(lockout.Value.TotalMinutes)} мин. " +
                      "Это защита доменной учётной записи от блокировки.";

            return Page();
        }

        var office = _offices.ResolveByAddress(HttpContext.Connection.RemoteIpAddress);

        var result = await _authentication.AuthenticateAsync(
            Input.UserName, Input.Password, office?.Code, cancellationToken);

        if (result.Status == AdAuthenticationStatus.Success)
        {
            _throttle.RegisterSuccess(throttleKey);
        }
        else
        {
            _throttle.RegisterFailure(throttleKey);
        }

        Result = result;

        if (result.User is not null)
        {
            HasAccessGroup = result.User.Groups.Contains(_ad.AccessGroup, StringComparer.OrdinalIgnoreCase);
            HasAdminGroup = result.User.Groups.Contains(_ad.AdminGroup, StringComparer.OrdinalIgnoreCase);
            HasPublisherGroup = result.User.Groups.Contains(_ad.PublisherGroup, StringComparer.OrdinalIgnoreCase);
        }

        _logger.LogInformation(
            "Выполнена аварийная проверка входа для {User}. Итог: {Status}.",
            Input.UserName, result.Status);

        return Page();
    }

    /// <summary>Кто вправе открывать страницу — см. пояснение в начале класса.</summary>
    private bool IsAllowed()
    {
        var address = HttpContext.Connection.RemoteIpAddress;

        if (address is not null && System.Net.IPAddress.IsLoopback(address))
        {
            return true;
        }

        return User.Identity?.IsAuthenticated == true && User.IsInRole(_ad.AdminGroup);
    }

    private void FillContext()
    {
        var address = HttpContext.Connection.RemoteIpAddress;

        RemoteIp = address?.ToString();

        var office = _offices.ResolveByAddress(address);

        DetectedOffice = office is null ? "не определён" : $"{office.Name} ({office.Code})";
        DomainControllerOrder = _offices.GetDomainControllerOrder(office?.Code);
    }

    /// <summary>Человеческое объяснение итога — то, ради чего страница и сделана.</summary>
    public (string Title, string Explanation, string Badge) Verdict()
    {
        if (Result is null)
        {
            return ("", "", "");
        }

        return Result.Status switch
        {
            AdAuthenticationStatus.Success when HasAccessGroup =>
                ("Вход возможен",
                 "Пароль проверен, пользователь найден в каталоге и состоит в группе доступа. " +
                 "Если через обычную форму войти всё равно не выходит — дело не в Active Directory.",
                 "badge--ok"),

            AdAuthenticationStatus.Success =>
                ($"Пароль верный, но нет доступа к порталу",
                 $"Пользователь не состоит в группе «{_ad.AccessGroup}». Добавьте его в эту группу " +
                 "в оснастке «Пользователи и компьютеры». Учтите, что членство в новой группе " +
                 "применяется не мгновенно и должно реплицироваться между контроллерами доменов.",
                 "badge--warn"),

            AdAuthenticationStatus.InvalidCredentials =>
                ("Неверный логин или пароль",
                 "Контроллер домена отклонил учётные данные. Проверьте раскладку клавиатуры и " +
                 "то же самое имя с паролем на любом доменном компьютере. " +
                 "Если пароль точно правильный, а отказ приходит для ВСЕХ пользователей — " +
                 "смотрите значение ActiveDirectory:DomainFqdn: при неверном имени домена " +
                 "контроллер отвечает именно так.",
                 "badge--error"),

            AdAuthenticationStatus.AccountRestricted =>
                ("Проблема с самой учётной записью",
                 "Пароль верный, но учётная запись заблокирована, отключена, просрочена " +
                 "или требует смены пароля. Смотрите её свойства в оснастке AD.",
                 "badge--warn"),

            AdAuthenticationStatus.ServerUnavailable =>
                ("Контроллеры домена не отвечают",
                 "Ни один из перечисленных ниже контроллеров не ответил за отведённое время. " +
                 "Это сеть или межсетевой экран, а не портал. Проверьте: " +
                 "Test-NetConnection <адрес> -Port " + _ad.Port,
                 "badge--error"),

            _ =>
                ("Ошибка настройки портала",
                 "Пароль проверен успешно, но дальше что-то пошло не так — чаще всего " +
                 "пользователя не удаётся найти в каталоге по заданному корню поиска. " +
                 "Сверьте значение ActiveDirectory:BaseDn ниже с реальным расположением " +
                 "учётных записей. Частая причина: при обновлении портала командой " +
                 "dotnet publish файл appsettings.json перезаписался значениями из репозитория, " +
                 "и все правки, сделанные на сервере, пропали.",
                 "badge--error")
        };
    }
}
