using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using System.Text.Encodings.Web;
using System.Text.Unicode;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.WebEncoders;
using Portal.Web.Data;
using Portal.Web.Configuration;
using Portal.Web.Security;
using Portal.Web.Services;
using Portal.Web.Services.ActiveDirectory;
using Portal.Web.Services.Notifications;
using Portal.Web.Services.Offices;
using Portal.Web.Services.Storage;
using Portal.Web.Services.Text;

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

builder.Services.Configure<StorageOptions>(
    builder.Configuration.GetSection(StorageOptions.SectionName));

// Часть настроек нужна прямо здесь, при сборке конвейера, а не через DI.
var security = builder.Configuration
    .GetSection(SecurityOptions.SectionName).Get<SecurityOptions>() ?? new SecurityOptions();

var adOptions = builder.Configuration
    .GetSection(ActiveDirectoryOptions.SectionName).Get<ActiveDirectoryOptions>() ?? new ActiveDirectoryOptions();

var storageOptions = builder.Configuration
    .GetSection(StorageOptions.SectionName).Get<StorageOptions>() ?? new StorageOptions();

// ---------------------------------------------------------------------------
// 1а. Ограничения на размер запроса
//
// Загрузка файла — это обычный HTTP-запрос, и по умолчанию ASP.NET Core
// разрешает не больше 30 МБ. Поднимаем предел до общего верхнего значения
// хранилища. Настроить надо в двух местах — Kestrel (когда приложение
// запускают напрямую) и IIS (когда оно работает внутри рабочего процесса IIS).
//
// ТРЕТЬЕ место — файл web.config, атрибут maxAllowedContentLength.
// Там ограничение самого IIS, и оно срабатывает РАНЬШЕ приложения:
// если его не поднять, крупный файл оборвётся с невнятной ошибкой 404.13
// и никакого понятного сообщения пользователь не увидит.
// ---------------------------------------------------------------------------

var maxRequestBytes = (long)storageOptions.AbsoluteMaxFileSizeMb * 1024 * 1024
                      + 4 * 1024 * 1024;   // запас на служебные части multipart-запроса

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBytes);

builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = maxRequestBytes);

builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit = maxRequestBytes;

    // Загружать можно несколько файлов сразу; предел по умолчанию (128 полей)
    // при массовой загрузке легко упереться.
    options.ValueCountLimit = 1024;
});

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
    // Публиковать объявления могут «издатели» и администраторы.
    // RequireRole с несколькими значениями означает «любая из перечисленных групп».
    .AddPolicy(PortalPolicies.PublishAnnouncements, policy => policy
        .RequireAuthenticatedUser()
        .RequireRole(adOptions.PublisherGroup, adOptions.AdminGroup))
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

// Форматирование текста объявлений. Синглтон: состояния нет, только логика.
// Кодировщик HtmlEncoder ему подставит контейнер — тот самый, что настроен
// выше на вывод кириллицы как есть.
builder.Services.AddSingleton<PlainTextFormatter>();
builder.Services.AddSingleton<LoginThrottle>();
builder.Services.AddScoped<IAdAuthenticationService, LdapAdAuthenticationService>();

// ---------------------------------------------------------------------------
// 5б. Файловое хранилище
// ---------------------------------------------------------------------------

// Работа с диском состояния не имеет — достаточно одного экземпляра.
builder.Services.AddSingleton<FileStorage>();
builder.Services.AddSingleton<UploadValidator>();

// Дерево папок читается из базы один раз за запрос, поэтому Scoped.
builder.Services.AddScoped<FolderTree>();
builder.Services.AddScoped<AuditLog>();
builder.Services.AddScoped<NotificationService>();

// AuditLog нужно знать, кто выполняет действие и с какого адреса.
builder.Services.AddHttpContextAccessor();

// Фоновая уборка: автоочистка папок по сроку и вычистка корзины.
builder.Services.AddHostedService<StorageCleanupService>();

// ---------------------------------------------------------------------------
// 5а. База данных (PostgreSQL)
//
// Строка подключения содержит пароль, поэтому её НЕТ в appsettings.json,
// который лежит в репозитории. Она берётся из appsettings.Production.json
// (этот файл создаётся на сервере вручную и в репозиторий не попадает)
// либо из переменной окружения ConnectionStrings__Portal.
// Подробности — в docs/07-этап-2-объявления.md.
// ---------------------------------------------------------------------------

var databaseOptions = builder.Configuration
    .GetSection(DatabaseOptions.SectionName).Get<DatabaseOptions>() ?? new DatabaseOptions();

builder.Services.Configure<DatabaseOptions>(
    builder.Configuration.GetSection(DatabaseOptions.SectionName));

// Состояние базы на момент запуска — одно на всё приложение.
builder.Services.AddSingleton<DatabaseStatus>();

builder.Services.AddDbContext<PortalDbContext>(options =>
{
    var connectionString = builder.Configuration.GetConnectionString("Portal");

    options.UseNpgsql(connectionString, npgsql =>
    {
        npgsql.CommandTimeout(databaseOptions.CommandTimeoutSeconds);

        // Повтор при кратковременных сбоях связи с базой. Полезно, если
        // PostgreSQL перезапускается или сеть моргнула: пользователь увидит
        // задержку вместо ошибки.
        npgsql.EnableRetryOnFailure(maxRetryCount: 3, maxRetryDelay: TimeSpan.FromSeconds(5), errorCodesToAdd: null);
    });

    // Запросы только на чтение (лента) не нужно отслеживать на изменения —
    // так EF не строит лишние структуры в памяти.
    options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
});

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

    // Единственное исключение: аварийная проверка входа. Права администратора
    // берутся из групп AD, то есть требуют успешного входа, — а эта страница
    // нужна ровно тогда, когда войти не может никто. Замкнутый круг разрывается
    // здесь, а доступ ограничивается внутри самой страницы: она открывается
    // только с адреса обратной петли (то есть с консоли самого сервера)
    // либо уже вошедшему администратору. Всем прочим отвечает «404».
    options.Conventions.AllowAnonymousToPage("/Admin/LoginTest");

    // Ленту объявлений читают все, у кого есть доступ к порталу (политика по умолчанию).
    // А вот писать, править и удалять — только публикаторы и администраторы.
    // Внутри страниц правки есть ещё одна проверка: своё объявление или чужое.
    options.Conventions.AuthorizePage("/Announcements/Create", PortalPolicies.PublishAnnouncements);
    options.Conventions.AuthorizePage("/Announcements/Edit", PortalPolicies.PublishAnnouncements);
    options.Conventions.AuthorizePage("/Announcements/Delete", PortalPolicies.PublishAnnouncements);
});

var app = builder.Build();

// ---------------------------------------------------------------------------
// 6. Приведение схемы базы данных к нужному виду
//
// Выполняются миграции, которых ещё нет в базе: создаются недостающие
// таблицы и колонки. Так на сервере не нужен отдельный инструмент dotnet-ef,
// который в сети без интернета пришлось бы переносить руками.
//
// Отдельно оговорим поведение при недоступной базе: приложение НЕ падает.
// Вход в портал работает через Active Directory и от PostgreSQL не зависит,
// поэтому правильнее пустить людей внутрь и показать администратору,
// что именно сломалось, чем не запуститься вовсе.
// ---------------------------------------------------------------------------

using (var scope = app.Services.CreateScope())
{
    var status = scope.ServiceProvider.GetRequiredService<DatabaseStatus>();
    var logger = scope.ServiceProvider.GetRequiredService<ILogger<Program>>();

    try
    {
        var database = scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database;

        if (databaseOptions.ApplyMigrationsOnStartup)
        {
            var pending = (await database.GetPendingMigrationsAsync()).ToList();

            if (pending.Count > 0)
            {
                logger.LogInformation(
                    "Применяю миграции базы данных: {Migrations}", string.Join(", ", pending));

                await database.MigrateAsync();
            }
        }
        else
        {
            // Миграции отключены — проверим хотя бы, что база отвечает.
            await database.CanConnectAsync();
        }

        status.MarkReady();
        logger.LogInformation("База данных готова к работе.");
    }
    catch (Exception ex)
    {
        status.MarkFailed(ex.Message);

        logger.LogCritical(ex,
            "База данных недоступна. Портал запустится, вход будет работать, " +
            "но разделы, которым нужна база (объявления), выдадут ошибку. " +
            "Проверьте службу PostgreSQL и строку подключения ConnectionStrings:Portal.");
    }
}

// ---------------------------------------------------------------------------
// 7. Конвейер обработки запроса. Порядок middleware здесь имеет значение.
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

    // Запрет встраивания портала в ЧУЖОЙ iframe (защита от кликджекинга).
    //
    // Именно «в чужой», а не «в любой»: предпросмотр файлов показывает PDF
    // во встроенном окне на нашей же странице, и полный запрет (DENY)
    // ломал бы его. SAMEORIGIN разрешает встраивание только с нашего адреса —
    // защита от кликджекинга при этом сохраняется.
    headers["X-Frame-Options"] = "SAMEORIGIN";

    // Не утекать полный адрес страницы во внешние ссылки.
    headers["Referrer-Policy"] = "same-origin";

    // Минимальный CSP: скрипты и стили только свои, никаких внешних источников.
    // Это заодно страхует от случайной ссылки на CDN, которая в вашей сети
    // всё равно не загрузится.
    //
    // frame-ancestors 'self' — та же причина, что и у X-Frame-Options выше.
    // Этот заголовок современнее и в спорных случаях главнее, поэтому оба
    // должны говорить одно и то же, иначе поведение зависит от браузера.
    headers["Content-Security-Policy"] =
        "default-src 'self'; img-src 'self' data:; object-src 'none'; frame-ancestors 'self'; base-uri 'self'";

    await next();
});

app.UseStaticFiles();

app.UseRouting();

app.UseAuthentication();   // разбирает cookie и наполняет HttpContext.User
app.UseAuthorization();    // проверяет политики

app.MapRazorPages();

// ---------------------------------------------------------------------------
// Уведомления. Страница раз в минуту спрашивает, нет ли нового,
// и показывает колокольчик со счётчиком плюс всплывающее сообщение.
//
// Обе точки закрыты обычной политикой доступа к порталу — она действует
// по умолчанию для всего, где не сказано иное.
// ---------------------------------------------------------------------------

app.MapGet("/api/notifications", async (
    NotificationService notifications, HttpContext http, CancellationToken cancellationToken) =>
{
    var user = http.User.Identity?.Name;

    if (string.IsNullOrEmpty(user))
    {
        return Results.Unauthorized();
    }

    return Results.Ok(await notifications.GetAsync(user, cancellationToken));
});

app.MapPost("/api/notifications/seen", async (
    NotificationService notifications, HttpContext http, CancellationToken cancellationToken) =>
{
    var user = http.User.Identity?.Name;

    if (string.IsNullOrEmpty(user))
    {
        return Results.Unauthorized();
    }

    await notifications.MarkAllSeenAsync(user, cancellationToken);

    return Results.NoContent();
});

// Простая точка проверки живости — открывается без входа.
// Удобна для мониторинга и для проверки, что сайт вообще поднялся.
app.MapGet("/healthz", () => Results.Text("ok")).AllowAnonymous();

app.Run();

// Program при использовании «инструкций верхнего уровня» получается internal-классом.
// Эта строка делает его видимым снаружи — без неё автотесты не смогут поднять
// приложение в памяти. На работу самого приложения никак не влияет.
public partial class Program;
