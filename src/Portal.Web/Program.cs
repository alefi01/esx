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
using Portal.Web.Services.Messaging;
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

// Как портал называется и чем подписан — см. BrandingOptions.
builder.Services.Configure<BrandingOptions>(
    builder.Configuration.GetSection(BrandingOptions.SectionName));

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
//
// Storage:AbsoluteMaxFileSizeMb = 0 означает «без ограничения». Тогда всем
// трём местам говорим «предела нет» (значение null), и остаётся только
// ограничение IIS из web.config плюс место на диске.
// ---------------------------------------------------------------------------

long? maxRequestBytes = storageOptions.FileSizeUnlimited
    ? null
    : (long)storageOptions.AbsoluteMaxFileSizeMb * 1024 * 1024
      + 4 * 1024 * 1024;   // запас на служебные части multipart-запроса

builder.WebHost.ConfigureKestrel(options => options.Limits.MaxRequestBodySize = maxRequestBytes);

builder.Services.Configure<IISServerOptions>(options => options.MaxRequestBodySize = maxRequestBytes);

builder.Services.Configure<FormOptions>(options =>
{
    // У формы предел задаётся числом, «нет предела» здесь выражается
    // наибольшим возможным значением.
    options.MultipartBodyLengthLimit = maxRequestBytes ?? long.MaxValue;

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

        // -------------------------------------------------------------------
        // Что отвечать, когда пользователь не признан вошедшим
        //
        // По умолчанию cookie-аутентификация перенаправляет на страницу входа.
        // Для обычного перехода это правильно. Но код страницы обращается
        // к порталу и в фоне — за уведомлениями и за содержимым предпросмотра, —
        // и для таких запросов перенаправление вредно: браузер послушно идёт
        // по нему, получает разметку страницы входа с кодом 200, и портал
        // показывает её вместо документа. Человек видит невнятную ошибку
        // и не догадывается, что просто истёк вход.
        //
        // Поэтому фоновым запросам отвечаем честным кодом и коротким
        // объяснением, которое страница умеет показать словами.
        // -------------------------------------------------------------------

        static bool IsBackgroundRequest(HttpRequest request) =>
            request.Path.StartsWithSegments("/api")
            || string.Equals(request.Headers.XRequestedWith, "XMLHttpRequest", StringComparison.OrdinalIgnoreCase);

        options.Events.OnRedirectToLogin = context =>
        {
            RecordAuthFailure(context.HttpContext, "вход не признан: cookie отсутствует, истекла или не расшифровалась");

            if (IsBackgroundRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                context.Response.ContentType = "application/json; charset=utf-8";

                return context.Response.WriteAsync("{\"reason\":\"signed-out\"}");
            }

            context.Response.Redirect(context.RedirectUri);

            return Task.CompletedTask;
        };

        options.Events.OnRedirectToAccessDenied = context =>
        {
            RecordAuthFailure(context.HttpContext, "вход признан, но прав на этот раздел нет");

            if (IsBackgroundRequest(context.Request))
            {
                context.Response.StatusCode = StatusCodes.Status403Forbidden;
                context.Response.ContentType = "application/json; charset=utf-8";

                return context.Response.WriteAsync("{\"reason\":\"forbidden\"}");
            }

            context.Response.Redirect(context.RedirectUri);

            return Task.CompletedTask;
        };
    });

// Записывает отказ в список последних — его видно на странице «Диагностика».
// Отдельной функцией, потому что вызывается из двух мест выше.
static void RecordAuthFailure(HttpContext context, string reason)
{
    var diagnostics = context.RequestServices.GetService<AuthDiagnostics>();

    if (diagnostics is null)
    {
        return;
    }

    var cookie = context.Request.Cookies["Portal.Auth"];

    diagnostics.Record(new AuthFailure(
        DateTime.Now,
        context.Request.Path + context.Request.QueryString,
        reason,
        cookie is not null,
        cookie?.Length ?? 0,
        context.Connection.RemoteIpAddress?.ToString() ?? "",
        context.Request.Headers.UserAgent.ToString()));

    context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("Portal.Web.Security.Access")
        .LogWarning(
            "Отказ в доступе: {Path}. Причина: {Reason}. Cookie входа: {Cookie}. Адрес: {Ip}.",
            context.Request.Path + context.Request.QueryString,
            reason,
            cookie is null ? "не пришла" : $"пришла, {cookie.Length} симв.",
            context.Connection.RemoteIpAddress);
}

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
builder.Services.AddSingleton<AuthDiagnostics>();
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

// Шина «у вас что-то новое». Одна на весь процесс: она держит открытые
// соединения страниц, а они живут дольше любого запроса.
builder.Services.AddSingleton<NotificationHub>();

// Личные настройки (звук уведомлений) и задачи.
builder.Services.AddScoped<Portal.Web.Services.Tasks.UserPreferences>();
builder.Services.AddScoped<Portal.Web.Services.Tasks.TaskService>();

// Кто в сети. Обращается к базе — Scoped.
builder.Services.AddScoped<PresenceService>();

// Занятое место для карточки в боковом меню. Само значение общее для всех
// и лежит в памяти минуту (см. StorageUsage), но берётся из базы — Scoped.
builder.Services.AddMemoryCache();
builder.Services.AddScoped<StorageUsage>();

// Настройки, которые администратор меняет из браузера (общий объём
// хранилища). Лежат в базе — Scoped.
builder.Services.AddScoped<PortalSettings>();

// Уборка портала: сроки хранения переписки, объявлений, корзины и журнала.
builder.Services.AddScoped<PortalCleanup>();

// Список фоновых тем — картинок из папки wwwroot\img\themes. К базе
// не обращается, читает папку раз в минуту, поэтому Singleton.
builder.Services.AddSingleton<Portal.Web.Services.Appearance.BackgroundCatalog>();

// Личные отметки «в избранном». Обращаются к базе — Scoped.
builder.Services.AddScoped<FavoriteService>();

// Переписки. Справочник сотрудников и служба бесед живут один запрос:
// оба обращаются к базе, а контекст базы существует ровно столько же.
// Хранилище вложений состояния не имеет, поэтому одно на всё приложение.
builder.Services.AddSingleton<MessageStorage>();
builder.Services.AddSingleton<Portal.Web.Services.Announcements.AnnouncementStorage>();
builder.Services.AddScoped<IUserDirectory, UserDirectory>();

// Обзор дерева домена для окна прав на папку. К базе не обращается,
// ответы держит в памяти минуту — Singleton.
builder.Services.AddSingleton<DirectoryBrowser>();
builder.Services.AddScoped<ConversationService>();

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

// То же самое, но для ответов в формате JSON: их отдают окно «Свойства»
// и колокольчик уведомлений. По умолчанию сериализатор экранирует всё,
// кроме латиницы, и «Размер» уезжает как «Размер» —
// вшестеро больше байт на каждую букву. На канале между офисами это заметно,
// а в отладке такой ответ вдобавок нечитаем.
//
// Набор разрешённых символов тот же, что и у страниц: латиница и кириллица.
// Экранирование опасных для разметки символов сериализатор сохраняет.
var jsonEncoder = JavaScriptEncoder.Create(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic);

builder.Services.ConfigureHttpJsonOptions(options =>
{
    // Точки /api/... — они отвечают через минимальные обработчики.
    options.SerializerOptions.Encoder = jsonEncoder;
});

builder.Services.Configure<Microsoft.AspNetCore.Mvc.JsonOptions>(options =>
{
    // JsonResult со страниц — окно «Свойства», отчёт о загрузке файлов.
    options.JsonSerializerOptions.Encoder = jsonEncoder;
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

// ---------------------------------------------------------------------------
// Сжатие ответов.
//
// ЗАЧЕМ. Стили и код страницы весят вместе около трёхсот килобайт. В одной
// сети с сервером это незаметно, а на канале между офисами — секунды
// ожидания при каждом первом заходе. Сжатие уменьшает их примерно вчетверо.
//
// IIS сжимает статику сам, но только ту, которую отдаёт сам же. Файлы
// портала отдаёт приложение, и до них встроенное сжатие IIS не доходит —
// поэтому оно включается здесь.
//
// EnableForHttps включён намеренно. Общее правило «не сжимать под HTTPS»
// защищает от атаки BREACH: она позволяет по размеру ответа подбирать
// секреты в теле страницы, но для этого нужен злоумышленник, который
// умеет заставлять браузер жертвы слать запросы и при этом видит их
// размеры, — то есть уже находится внутри сети. Для внутреннего портала
// это несоразмерно тому, что без сжатия по HTTPS каждая страница снова
// станет весить втрое больше.
builder.Services.AddResponseCompression(options =>
{
    options.EnableForHttps = true;

    options.MimeTypes =
    [
        "text/html", "text/css", "text/plain", "text/xml",
        "application/javascript", "text/javascript",
        "application/json", "image/svg+xml"
    ];
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

    // Страницы портала не кладём в кэш браузера.
    //
    // Иначе получается обман: человек возвращается кнопкой «назад», браузер
    // достаёт страницу из кэша, она выглядит рабочей — а вход к этому моменту
    // уже истёк, и всё, что страница спрашивает у сервера, отвечает отказом.
    // Выглядит как «портал сломался», хотя надо просто войти заново.
    //
    // Правило вешаем только на разметку: стили, код страниц и сами файлы
    // кэшировать по-прежнему можно и нужно — на канале между офисами это
    // экономит заметную долю трафика.
    context.Response.OnStarting(() =>
    {
        if (context.Response.ContentType?.StartsWith("text/html", StringComparison.OrdinalIgnoreCase) == true)
        {
            headers.CacheControl = "no-store, no-cache, must-revalidate";
            headers.Pragma = "no-cache";
        }

        return Task.CompletedTask;
    });

    await next();
});

// Сжатие стоит ДО отдачи файлов: иначе сжимать будет уже нечего.
app.UseResponseCompression();

app.UseStaticFiles(new StaticFileOptions
{
    OnPrepareResponse = context =>
    {
        // Стили, код страниц и значки подключаются с отпечатком в адресе
        // (asp-append-version), то есть при любой правке адрес меняется.
        // Значит, старый можно кэшировать надолго и не спрашивать о нём
        // вовсе: на канале между офисами это убирает с каждого перехода
        // по странице добрую сотню килобайт.
        //
        // Год и immutable — обычная практика для файлов с отпечатком.
        // Файлы БЕЗ отпечатка (их открывают по прямой ссылке) кэшируются
        // на час: ошибиться на час не страшно, а трафик всё равно экономит.
        var versioned = context.Context.Request.Query.ContainsKey("v");

        context.Context.Response.Headers.CacheControl = versioned
            ? "public, max-age=31536000, immutable"
            : "public, max-age=3600";
    }
});

app.UseRouting();

app.UseAuthentication();   // разбирает cookie и наполняет HttpContext.User
app.UseAuthorization();    // проверяет политики

// ---------------------------------------------------------------------------
// Отметка «человек в портале» — для статуса «в сети» в переписках.
//
// Стоит ПОСЛЕ проверки входа (иначе имени ещё нет) и ловит любой запрос
// страницы, включая опрос колокольчика раз в минуту: пока вкладка открыта,
// отметка обновляется сама. В базу при этом пишется не чаще раза в минуту
// на человека — подробности в PresenceService.
//
// Ошибку здесь не пропускаем дальше: «в сети» не та вещь, ради которой
// стоит показать человеку страницу ошибки вместо портала.
// ---------------------------------------------------------------------------
app.Use(async (context, next) =>
{
    var userName = context.User.Identity?.Name;

    if (!string.IsNullOrEmpty(userName) && context.User.Identity?.IsAuthenticated == true)
    {
        var presence = context.RequestServices.GetRequiredService<PresenceService>();

        try
        {
            await presence.TouchAsync(userName, context.RequestAborted);
        }
        catch (Exception)
        {
            // Уже записано в журнал внутри службы.
        }
    }

    await next();
});

app.MapRazorPages();

// ---------------------------------------------------------------------------
// Уведомления. Страница раз в минуту спрашивает, нет ли нового,
// и показывает колокольчик со счётчиком плюс всплывающее сообщение.
//
// Обе точки закрыты обычной политикой доступа к порталу — она действует
// по умолчанию для всего, где не сказано иное.
// ---------------------------------------------------------------------------

app.MapGet("/api/notifications", async (
    NotificationService notifications,
    Portal.Web.Services.Tasks.TaskService tasks,
    HttpContext http,
    CancellationToken cancellationToken) =>
{
    var user = http.User.Identity?.Name;

    if (string.IsNullOrEmpty(user))
    {
        return Results.Unauthorized();
    }

    var summary = await notifications.GetAsync(user, cancellationToken);

    // Число задач в работе берётся здесь, а не внутри службы уведомлений:
    // задачи к уведомлениям отношения не имеют, им просто по дороге —
    // страница и так спрашивает этот адрес раз в несколько секунд,
    // и отдельный запрос ради одного числа был бы лишним.
    var active = await tasks.ActiveCountAsync(user, cancellationToken);

    // Задачи, у которых срок завтра или уже прошёл, — о них портал
    // напоминает один раз за день, когда человек открывает его.
    var urgent = await tasks.UrgentAsync(user, cancellationToken);

    var today = DateTime.Now.Date;

    return Results.Ok(new
    {
        summary.Unread,
        summary.Announcements,
        summary.Messages,
        summary.Items,
        Tasks = active,
        UrgentTasks = urgent.Select(t => new
        {
            t.Id,
            t.Title,
            Due = Portal.Web.Pages.Tasks.IndexModel.DueText(t.DueOn!.Value),
            Late = t.DueOn!.Value.Date < today
        })
    });
});

// ---------------------------------------------------------------------------
// Поток событий: сервер сам сообщает странице, что ей пора обновиться.
//
// Нужен для одного случая, но важного: браузер свёрнут, человек работает
// в другой программе. В этом состоянии браузер придушивает таймеры фоновых
// вкладок до одного срабатывания в минуту, и опрос перестаёт быть опросом.
// Приходящие по сети данные он так не придерживает — поэтому о новом
// сообщении страница узнаёт отсюда мгновенно.
//
// Формат — обычный text/event-stream: его понимает сам браузер (EventSource),
// и ничего, кроме HTTP, здесь не используется. Соединение рвётся — браузер
// переподключается сам, а опрос по таймеру остаётся запасным путём.
// ---------------------------------------------------------------------------

app.MapGet("/api/notifications/stream", async (
    NotificationHub hub, HttpContext http, CancellationToken cancellationToken) =>
{
    var user = http.User.Identity?.Name;

    if (string.IsNullOrEmpty(user))
    {
        return Results.Unauthorized();
    }

    var response = http.Response;

    response.ContentType = "text/event-stream";
    response.Headers.CacheControl = "no-cache, no-store";

    // Просьба к посредникам не копить ответ в буфере: соединение именно
    // в том и состоит, что строчки уходят по одной. IIS и nginx понимают
    // этот заголовок, остальные его просто не заметят.
    response.Headers["X-Accel-Buffering"] = "no";

    var (id, reader) = hub.Subscribe(user);

    try
    {
        // Первая строка уходит сразу: по ней браузер понимает, что
        // соединение установлено, а посредники — что ответ начался.
        await response.WriteAsync(": открыто\n\n", cancellationToken);
        await response.Body.FlushAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            // Ждём либо события, либо четверти минуты. Молчащее соединение
            // закрывают и прокси, и сам IIS, поэтому в тишине отправляем
            // двоеточие — для протокола это пустая строка-комментарий,
            // которая ничего не значит, но держит канал живым.
            using var tick = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            tick.CancelAfter(TimeSpan.FromSeconds(25));

            var woken = false;

            try
            {
                woken = await reader.WaitToReadAsync(tick.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Прошли двадцать пять секунд без событий — это не ошибка.
            }

            if (woken)
            {
                while (reader.TryRead(out _)) { /* звонок один, сколько бы ни нажали */ }

                await response.WriteAsync("event: new\ndata: 1\n\n", cancellationToken);
            }
            else
            {
                await response.WriteAsync(": тишина\n\n", cancellationToken);
            }

            await response.Body.FlushAsync(cancellationToken);
        }
    }
    catch (OperationCanceledException)
    {
        // Человек закрыл вкладку или ушёл со страницы — обычное дело.
    }
    finally
    {
        hub.Unsubscribe(user, id);
    }

    return Results.Empty;
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

// ---------------------------------------------------------------------------
// Значок вкладки браузера.
//
// Рисуется КОДОМ, а не лежит файлом: тогда он сам меняется вслед
// за названием портала и фирменным цветом из секции Branding, и класть
// на сервер ещё одну картинку не нужно. Если у организации есть готовый
// логотип, укажите его в Branding:LogoFile — тогда значком станет он
// (см. _Layout.cshtml), а этот адрес просто останется невостребованным.
//
// Открывается БЕЗ входа: значок браузер запрашивает раньше, чем человек
// успевает войти, и на странице входа он тоже должен быть.
// ---------------------------------------------------------------------------
app.MapGet("/favicon.svg", (Microsoft.Extensions.Options.IOptions<BrandingOptions> branding) =>
{
    var value = branding.Value;

    var letter = string.IsNullOrWhiteSpace(value.Title)
        ? "П"
        : value.Title.TrimStart()[..1].ToUpperInvariant();

    // Цвет и буква подставляются в разметку, поэтому кодируются: значение
    // приходит из файла настроек, но правило «всё, что подставляется,
    // кодируется» не должно знать исключений.
    var color = System.Net.WebUtility.HtmlEncode(value.AccentColor);
    var text = System.Net.WebUtility.HtmlEncode(letter);

    var svg =
        "<svg xmlns=\"http://www.w3.org/2000/svg\" viewBox=\"0 0 64 64\">" +
        $"<rect width=\"64\" height=\"64\" rx=\"14\" fill=\"{color}\"/>" +
        "<text x=\"32\" y=\"44\" text-anchor=\"middle\" fill=\"#fff\" " +
        $"font-family=\"Segoe UI, Arial, sans-serif\" font-size=\"38\" font-weight=\"700\">{text}</text>" +
        "</svg>";

    return Results.Text(svg, "image/svg+xml");
}).AllowAnonymous();

app.Run();

// Program при использовании «инструкций верхнего уровня» получается internal-классом.
// Эта строка делает его видимым снаружи — без неё автотесты не смогут поднять
// приложение в памяти. На работу самого приложения никак не влияет.
public partial class Program;
