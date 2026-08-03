using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.WebEncoders;
using Portal.Web.Configuration;
using Portal.Web.Security;
using Portal.Web.Services;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Offices;

var builder = WebApplication.CreateBuilder(args);

// ---------------------------------------------------------------------------
// 1. Конфигурация
//
// Настройки читаются из appsettings.json, поверх него — appsettings.<Environment>.json,
// поверх — переменные окружения. На боевом сервере окружение задаётся переменной
// ASPNETCORE_ENVIRONMENT=Production в web.config.
// ---------------------------------------------------------------------------

builder.Services.Configure<ActiveDirectoryOptions>(
    builder.Configuration.GetSection(ActiveDirectoryOptions.SectionName));

builder.Services.Configure<OfficesOptions>(
    builder.Configuration.GetSection(OfficesOptions.SectionName));

builder.Services.Configure<SecurityOptions>(
    builder.Configuration.GetSection(SecurityOptions.SectionName));

// Часть настроек нужна прямо здесь, при сборке конвейера, а не через DI.
var security = builder.Configuration
    .GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

var adOptions = builder.Configuration
    .GetSection(ActiveDirectoryOptions.SectionName).Get<ActiveDirectoryOptions>() ?? new ActiveDirectoryOptions();

// ---------------------------------------------------------------------------
// 2. Защита данных (Data Protection)
//
// Этим механизмом ASP.NET Core шифрует и подписывает cookie аутентификации.
// По умолчанию ключи лежат в профиле пользователя, под которым работает пул IIS.
// У пула с ApplicationPoolIdentity профиль часто не загружается — тогда ключи
// создаются заново при каждом перезапуске, и все пользователи разом
// «разлогиниваются». Поэтому явно кладём ключи в папку рядом с приложением.
//
// Папку App_Data надо будет один раз выдать в запись пулу IIS — это есть в инструкции
// по развёртыванию. Содержимое папки — секрет: доступ к ней означает возможность
// подделать cookie любого пользователя.
// ---------------------------------------------------------------------------

var keysDirectory = new DirectoryInfo(Path.Combine(builder.Environment.ContentRootPath, "App_Data", "keys"));
keysDirectory.Create();

builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(keysDirectory)
    // Имя приложения участвует в вычислении ключей. Фиксируем его, чтобы cookie
    // не переставали работать после переустановки приложения в другую папку.
    .SetApplicationName("DomenPortal");

// ---------------------------------------------------------------------------
// 3. Аутентификация: cookie
//
// Схема простая: пользователь один раз доказал знание пароля контроллеру домена,
// после чего мы выдаём подписанную cookie с его логином и списком групп.
// Пароль нигде не сохраняется, к AD на каждый запрос мы не ходим.
//
// Обратная сторона: изменения в группах AD доедут до пользователя только
// при следующем входе (или через SessionHours, когда cookie протухнет).
// Для 20 человек это приемлемо; если понадобится мгновенный отзыв прав —
// добавим периодическую перепроверку в событии OnValidatePrincipal.
// ---------------------------------------------------------------------------

builder.Services
    .AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name = "Portal.Auth";

        // Cookie недоступна из JavaScript — даже если на страницу как-то попадёт
        // чужой скрипт, украсть сессию он не сможет.
        options.Cookie.HttpOnly = true;

        // Lax: cookie не отправляется при межсайтовых POST-запросах.
        // Это защита от CSRF в дополнение к antiforgery-токенам Razor Pages.
        options.Cookie.SameSite = SameSiteMode.Lax;

        // Вот здесь и заложен переход на HTTPS: пока RequireHttps = false,
        // cookie ходит и по HTTP; после переключения — только по HTTPS.
        options.Cookie.SecurePolicy = security.RequireHttps
            ? CookieSecurePolicy.Always
            : CookieSecurePolicy.SameAsRequest;

        options.LoginPath = "/Account/Login";
        options.LogoutPath = "/Account/Logout";
        options.AccessDeniedPath = "/Account/AccessDenied";
        options.ReturnUrlParameter = "returnUrl";

        options.ExpireTimeSpan = TimeSpan.FromHours(security.SessionHours);

        // Скользящий срок: пока человек работает, сессия продлевается.
        options.SlidingExpiration = true;
    });

// ---------------------------------------------------------------------------
// 4. Авторизация: роли — это группы Active Directory
//
// Никакой собственной таблицы ролей нет и не планируется. Роль в портале —
// это буквально короткое имя группы AD, в которой состоит пользователь.
// Управление доступом остаётся в оснастке «Пользователи и компьютеры».
// ---------------------------------------------------------------------------

// Политика базового доступа. Собирается отдельно, потому что используется дважды:
// как именованная политика и как политика по умолчанию.
var accessPolicy = new AuthorizationPolicyBuilder()
    .RequireAuthenticatedUser()
    .RequireRole(adOptions.AccessGroup)
    .Build();

builder.Services.AddAuthorizationBuilder()
    .AddPolicy(PortalPolicies.Access, accessPolicy)
    .AddPolicy(PortalPolicies.Admin, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(adOptions.AdminGroup))
    // Политика по умолчанию для всех страниц, где явно не сказано иное.
    // Так безопаснее: забыть закрыть новую страницу нельзя — она закрыта сама,
    // открывать надо осознанно, атрибутом AllowAnonymous.
    .SetFallbackPolicy(accessPolicy);

// ---------------------------------------------------------------------------
// 5. Свои сервисы
// ---------------------------------------------------------------------------

// TimeProvider — штатная абстракция времени из .NET 8. Нужна, чтобы логику
// таймаутов входа можно было проверить в тестах, не выжидая реальные минуты.
builder.Services.AddSingleton(TimeProvider.System);

builder.Services.AddSingleton<IOfficeResolver, OfficeResolver>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<IAdAuthenticationService, LdapAdAuthenticationService>();

// По умолчанию Razor кодирует все символы вне латиницы числовыми ссылками:
// «Вход» превращается в «&#x412;&#x445;&#x43E;&#x434;». Работает это правильно,
// но русский текст в ответе распухает примерно втрое, а исходный код страницы
// становится нечитаемым. Разрешаем выводить кириллицу как есть.
// Безопасность не страдает: экранирование HTML-спецсимволов (< > & ") остаётся.
builder.Services.Configure<WebEncoderOptions>(options =>
{
    options.TextEncoderSettings = new TextEncoderSettings(
        UnicodeRanges.BasicLatin,
        UnicodeRanges.Cyrillic);
});

builder.Services.AddRazorPages(options =>
{
    // Страницы, доступные без входа. Всё остальное закрыто политикой по умолчанию.
    options.Conventions.AllowAnonymousToPage("/Account/Login");
    options.Conventions.AllowAnonymousToPage("/Account/AccessDenied");
    options.Conventions.AllowAnonymousToPage("/Error");

    // Раздел администратора — отдельная политика поверх базовой.
    options.Conventions.AuthorizeFolder("/Admin", PortalPolicies.Admin);
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// 6. Конвейер обработки запроса. Порядок middleware здесь имеет значение.
// ---------------------------------------------------------------------------

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

if (security.UseHsts)
{
    app.UseHsts();
}

if (security.RequireHttps)
{
    app.UseHttpsRedirection();
}

// Заголовки безопасности. Дёшево, без зависимостей, и убирает целый класс проблем.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;

    // Не угадывать тип содержимого — иначе загруженный «текстовый» файл
    // браузер может исполнить как скрипт. Это пригодится на этапе файлохранилища.
    headers["X-Content-Type-Options"] = "nosniff";

    // Запрет встраивания портала в чужой iframe (защита от кликджекинга).
    headers["X-Frame-Options"] = "DENY";

    // Не утекать полный адрес страницы во внешние ссылки.
    headers["Referrer-Policy"] = "same-origin";

    // Минимальный CSP: скрипты и стили только свои, никаких внешних источников.
    // Это заодно страхует от случайной ссылки на CDN, которая в вашей сети
    // всё равно не загрузится.
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'none'; base-uri 'self'";

    await next();
});

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();   // разбирает cookie и наполняет HttpContext.User
app.UseAuthorization();    // проверяет политики

app.MapRazorPages();

// Простая точка проверки живости — открывается без входа.
// Удобна для мониторинга и для проверки, что сайт вообще поднялся.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.Run();

// Program при использовании «инструкций верхнего уровня» получается internal-классом.
// Эта строка делает его видимым снаружи — без неё автотесты не смогут поднять
// приложение в памяти. На работу самого приложения никак не влияет.
public partial class Program;
