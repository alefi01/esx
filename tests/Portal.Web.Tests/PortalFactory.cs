using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Portal.Web.Data;
using Portal.Web.Services.ActiveDirectory;

namespace Portal.Web.Tests;

/// <summary>
/// Группы, которые заглушка вернёт при входе.
///
/// Состояние намеренно НЕ статическое, а привязано к конкретной фабрике:
/// xUnit выполняет разные классы тестов параллельно, и общее статическое поле
/// они бы перетирали друг у друга. Отладка таких «плавающих» падений —
/// худшее, что можно оставить в проекте.
/// </summary>
public sealed class FakeAdState
{
    public List<string> Groups { get; set; } = ["WebUsers"];

    /// <summary>
    /// Кто «есть в каталоге» для переписок.
    ///
    /// Настоящий справочник ходит в Active Directory, и в тестах это
    /// означало бы ожидание ответа от несуществующего контроллера домена
    /// на каждый запрос. Поэтому здесь простой список.
    /// </summary>
    public List<DirectoryUser> People { get; set; } =
    [
        new("ivanov", "Иванов Иван"),
        new("petrov", "Петров Пётр"),
        new("sidorov", "Сидоров Сидор"),
        new("boss", "Начальников Начальник")
    ];
}

/// <summary>Справочник сотрудников без обращения к Active Directory.</summary>
public sealed class FakeUserDirectory : IUserDirectory
{
    private readonly FakeAdState _state;

    public FakeUserDirectory(FakeAdState state) => _state = state;

    public Task<IReadOnlyList<DirectoryUser>> SearchAsync(
        string? query, string exceptUserName, int take, CancellationToken cancellationToken)
    {
        var needle = (query ?? "").Trim();

        IReadOnlyList<DirectoryUser> found = _state.People
            .Where(p => !string.Equals(p.UserName, exceptUserName, StringComparison.OrdinalIgnoreCase))
            .Where(p => needle.Length == 0
                        || p.DisplayName.Contains(needle, StringComparison.OrdinalIgnoreCase)
                        || p.UserName.Contains(needle, StringComparison.OrdinalIgnoreCase))
            .Take(take)
            .ToList();

        return Task.FromResult(found);
    }

    public Task<string> DisplayNameAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(_state.People
            .FirstOrDefault(p => string.Equals(p.UserName, userName, StringComparison.OrdinalIgnoreCase))
            ?.DisplayName ?? userName);

    public Task<bool> ExistsAsync(string userName, CancellationToken cancellationToken) =>
        Task.FromResult(_state.People
            .Any(p => string.Equals(p.UserName, userName, StringComparison.OrdinalIgnoreCase)));
}

/// <summary>
/// Заглушка вместо настоящего Active Directory.
///
/// Благодаря ей тесты проходят на любой машине — без домена и без сети.
/// Проверяется вся логика портала выше уровня LDAP: выдача cookie,
/// разграничение прав по группам, выход, защита форм от подделки.
/// Сам обмен по LDAP тут не проверяется — для него нужен живой контроллер домена
/// (это делается вручную по инструкции docs/04-проверка-этапа-1.md).
/// </summary>
public sealed class FakeAdAuthenticationService : IAdAuthenticationService
{
    /// <summary>Единственный «правильный» пароль в тестах.</summary>
    public const string CorrectPassword = "good";

    private readonly FakeAdState _state;

    public FakeAdAuthenticationService(FakeAdState state) => _state = state;

    public Task<AdAuthenticationResult> AuthenticateAsync(
        string userName, string password, string? officeCode, CancellationToken ct = default)
    {
        if (password != CorrectPassword)
        {
            return Task.FromResult(AdAuthenticationResult.Failure(
                AdAuthenticationStatus.InvalidCredentials, "192.168.96.3", "тестовая заглушка"));
        }

        var user = new AdUserInfo(
            userName, $"Пользователь {userName}", $"{userName}@domen.pro",
            $"CN={userName},DC=domen,DC=pro", _state.Groups);

        return Task.FromResult(AdAuthenticationResult.Success(user, "192.168.96.3"));
    }
}

/// <summary>
/// Поднимает приложение целиком в памяти — без IIS и без сетевого порта.
///
/// Подменяются ровно две вещи:
///   * Active Directory — на заглушку выше;
///   * PostgreSQL — на SQLite во временном файле.
///
/// Всё остальное настоящее: та же конфигурация, те же политики авторизации,
/// тот же конвейер обработки запроса, те же страницы.
///
/// Почему SQLite, а не «база в памяти» от EF: SQLite ведёт себя как настоящая
/// реляционная база — проверяет типы, ограничения длины, уникальность.
/// Провайдер «в памяти» этого не делает, и тест на нём проходит там,
/// где настоящая база отказала бы.
/// </summary>
public sealed class PortalFactory : WebApplicationFactory<Program>
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"portal-tests-{Guid.NewGuid():N}.db");

    /// <summary>Группы пользователя, который войдёт в этот экземпляр приложения.</summary>
    public FakeAdState Ad { get; } = new();

    /// <summary>
    /// Куда складывать файлы в этом тесте. Задавать нужно ДО первого обращения
    /// к приложению: после того как хост поднят, настройка уже прочитана.
    /// </summary>
    public string? StorageRootPath { get; set; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Миграции написаны под PostgreSQL и на SQLite не применятся.
        // Схему создаём отдельно, методом EnsureCreated (см. CreateHost).
        builder.UseSetting("Database:ApplyMigrationsOnStartup", "false");

        if (StorageRootPath is not null)
        {
            builder.UseSetting("Storage:RootPath", StorageRootPath);
        }

        builder.ConfigureServices(services =>
        {
            services.AddSingleton(Ad);
            services.Replace(ServiceDescriptor.Scoped<IAdAuthenticationService, FakeAdAuthenticationService>());
            services.Replace(ServiceDescriptor.Scoped<IUserDirectory, FakeUserDirectory>());

            // Тестовый сервер работает без сети и адрес клиента не заполняет,
            // а от него зависят определение офиса и доступ к аварийной странице.
            // Подставляем адрес: по умолчанию «с самого сервера», а если тест
            // прислал заголовок X-Test-Remote-Ip — указанный в нём.
            services.AddSingleton<IStartupFilter, TestRemoteIpStartupFilter>();

            RemovePostgreSqlRegistrations(services);

            services.AddDbContext<PortalDbContext>(options => options
                .UseSqlite($"Data Source={_databasePath}")
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking));
        });
    }

    /// <summary>
    /// Убирает регистрации, оставшиеся от AddDbContext с провайдером PostgreSQL.
    /// Без этого в контейнере окажутся два набора настроек контекста,
    /// и EF пожалуется, что для него настроено сразу два провайдера.
    /// </summary>
    private static void RemovePostgreSqlRegistrations(IServiceCollection services)
    {
        var toRemove = services
            .Where(descriptor =>
                descriptor.ServiceType == typeof(DbContextOptions<PortalDbContext>)
                || descriptor.ServiceType == typeof(DbContextOptions)
                || descriptor.ServiceType == typeof(PortalDbContext)
                || (descriptor.ServiceType.IsGenericType
                    && descriptor.ServiceType.GetGenericArguments().Contains(typeof(PortalDbContext))))
            .ToList();

        foreach (var descriptor in toRemove)
        {
            services.Remove(descriptor);
        }
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);

        using var scope = host.Services.CreateScope();
        scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database.EnsureCreated();

        return host;
    }

    /// <summary>
    /// Выполнить действие с базой напрямую: положить данные для теста
    /// или поправить уже имеющиеся.
    ///
    /// Отслеживание изменений включается явно. По умолчанию контекст портала
    /// настроен на запросы без отслеживания (так быстрее для страниц-читалок),
    /// и без этой строки правка вида db.Folders.First(...).X = 1 молча
    /// не сохранилась бы — тест «проходил» бы, ничего не проверив.
    /// </summary>
    public void Seed(Action<PortalDbContext> action)
    {
        using var scope = Services.CreateScope();
        var db = scope.ServiceProvider.GetRequiredService<PortalDbContext>();

        db.ChangeTracker.QueryTrackingBehavior = QueryTrackingBehavior.TrackAll;

        action(db);
        db.SaveChanges();
    }

    /// <summary>
    /// Убирает базу целиком — чтобы проверить, как портал ведёт себя,
    /// когда PostgreSQL недоступен. Такое случается, и портал не должен
    /// от этого падать: файлы и переписки живут своей жизнью.
    /// </summary>
    public void DropDatabase()
    {
        using var scope = Services.CreateScope();

        scope.ServiceProvider.GetRequiredService<PortalDbContext>().Database.EnsureDeleted();
    }

    /// <summary>Прочитать что-нибудь из базы напрямую — для проверки результата.</summary>
    public T Query<T>(Func<PortalDbContext, T> query)
    {
        using var scope = Services.CreateScope();

        return query(scope.ServiceProvider.GetRequiredService<PortalDbContext>());
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);

        if (disposing && File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }
}

/// <summary>
/// Подставляет адрес клиента в тестовых запросах.
///
/// TestServer сетевого соединения не имеет и RemoteIpAddress оставляет пустым,
/// а от этого адреса в портале зависят две вещи: определение офиса
/// и доступ к аварийной странице проверки входа. Без подстановки такие
/// проверки написать нельзя.
///
/// IStartupFilter позволяет вклиниться в самое начало конвейера обработки
/// запроса, не переписывая конвейер приложения.
/// </summary>
public sealed class TestRemoteIpStartupFilter : IStartupFilter
{
    /// <summary>Заголовок, которым тест задаёт «откуда» пришёл запрос.</summary>
    public const string HeaderName = "X-Test-Remote-Ip";

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) =>
        app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                var value = context.Request.Headers[HeaderName].ToString();

                context.Connection.RemoteIpAddress =
                    !string.IsNullOrEmpty(value) && System.Net.IPAddress.TryParse(value, out var address)
                        ? address
                        : System.Net.IPAddress.Loopback;

                await nextMiddleware();
            });

            next(app);
        };
}
