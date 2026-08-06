using System.Net;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Sync;

namespace Portal.Web.Tests;

/// <summary>
/// Синхронизация между филиалами.
///
/// Проверяется то, из-за чего филиалы могут молча разойтись между собой,
/// а это самое неприятное, что может случиться с такой схемой: всё
/// работает, просто в одном офисе чего-то нет, и никто об этом не узнает.
///
/// Поэтому упор на три вещи:
///   * изменение непременно попадает в журнал (иначе оно не уедет);
///   * чужое изменение НЕ попадает в журнал повторно (иначе оно
///     закольцуется между филиалами);
///   * отложенная запись не теряется и не «перепрыгивается».
///
/// Настоящий обмен по сети здесь не проверяется — для него нужны два
/// поднятых портала. Он проверялся вручную, по инструкции
/// docs/13-этап-8-филиалы.md.
/// </summary>
public class SyncTests
{
    private const string Branch = "office1";

    /// <summary>
    /// Портал с включённой синхронизацией. Пароль и адреса соседей не нужны:
    /// проверяется работа с журналом, а не разговор по сети.
    /// </summary>
    private static PortalFactory CreateFactory(string branchCode = Branch)
    {
        var factory = new PortalFactory();

        factory.SyncBranchCode = branchCode;

        return factory;
    }

    // ==================================================================
    // Журнал изменений
    // ==================================================================

    [Fact]
    public void Новое_объявление_попадает_в_журнал_изменений()
    {
        using var factory = CreateFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Пропускной режим",
            Body = "С понедельника вход по новым пропускам.",
            AuthorUserName = "ivanov",
            AuthorDisplayName = "Иванов Иван",
            CreatedAt = DateTime.UtcNow
        }));

        var entries = factory.Query(db => db.SyncOutbox.ToList());

        var entry = Assert.Single(entries);

        Assert.Equal(SyncKinds.Announcement, entry.Kind);
        Assert.Equal(Branch, entry.OriginBranch);
        Assert.NotEqual(Guid.Empty, entry.GlobalId);
        Assert.False(entry.Deleted);
    }

    [Fact]
    public void Правка_и_удаление_тоже_попадают_в_журнал()
    {
        using var factory = CreateFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Первое",
            Body = "Текст",
            CreatedAt = DateTime.UtcNow
        }));

        factory.Seed(db =>
        {
            var item = db.Announcements.Single();

            item.Title = "Первое (исправлено)";
        });

        factory.Seed(db => db.Announcements.Remove(db.Announcements.Single()));

        var entries = factory.Query(db => db.SyncOutbox.OrderBy(e => e.Id).ToList());

        Assert.Equal(3, entries.Count);
        Assert.False(entries[0].Deleted);
        Assert.False(entries[1].Deleted);

        // Удаление обязано попасть в журнал: иначе в соседнем филиале
        // объявление осталось бы висеть навсегда.
        Assert.True(entries[2].Deleted);

        // Общий номер один и тот же во всех трёх записях — это один объект.
        Assert.Single(entries.Select(e => e.GlobalId).Distinct());
    }

    [Fact]
    public void При_выключенной_синхронизации_журнал_не_ведётся()
    {
        // Пока филиал один, лишних строк в базе появляться не должно.
        using var factory = new PortalFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Объявление",
            Body = "Текст",
            CreatedAt = DateTime.UtcNow
        }));

        Assert.Empty(factory.Query(db => db.SyncOutbox.ToList()));
    }

    [Fact]
    public async Task Чужое_изменение_в_журнал_не_попадает()
    {
        // Самая важная проверка во всём наборе. Если её нарушить, каждое
        // изменение будет ходить между филиалами по кругу без конца.
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var result = await applier.ApplyAsync(new SyncEntry
        {
            Id = 1,
            Kind = SyncKinds.Announcement,
            GlobalId = Guid.NewGuid(),
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload
            {
                Title = "Из соседнего офиса",
                Body = "Текст",
                CreatedAt = DateTime.UtcNow
            }
        }, CancellationToken.None);

        Assert.Equal(SyncApplyResult.Applied, result);
        Assert.Equal("Из соседнего офиса", factory.Query(db => db.Announcements.Single().Title));
        Assert.Empty(factory.Query(db => db.SyncOutbox.ToList()));
    }

    // ==================================================================
    // Разбор спорных случаев
    // ==================================================================

    [Fact]
    public async Task Более_поздняя_правка_соседа_побеждает()
    {
        using var factory = CreateFactory();

        var globalId = Guid.NewGuid();
        var ourTime = DateTime.UtcNow.AddMinutes(-10);

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Наш заголовок",
            Body = "Наш текст",
            CreatedAt = ourTime,
            GlobalId = globalId,
            OriginBranch = Branch,
            ChangedAt = ourTime
        }));

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var result = await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Announcement,
            GlobalId = globalId,
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload { Title = "Заголовок соседа", Body = "Текст соседа" }
        }, CancellationToken.None);

        Assert.Equal(SyncApplyResult.Applied, result);
        Assert.Equal("Заголовок соседа", factory.Query(db => db.Announcements.Single().Title));
    }

    [Fact]
    public async Task Более_ранняя_правка_соседа_не_затирает_нашу()
    {
        using var factory = CreateFactory();

        var globalId = Guid.NewGuid();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Наш заголовок",
            Body = "Наш текст",
            CreatedAt = DateTime.UtcNow,
            GlobalId = globalId,
            OriginBranch = Branch,
            ChangedAt = DateTime.UtcNow
        }));

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var result = await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Announcement,
            GlobalId = globalId,
            OriginBranch = "office2",

            // Правка соседа СТАРЕЕ нашей на час.
            ChangedAt = DateTime.UtcNow.AddHours(-1),
            Payload = new SyncPayload { Title = "Устаревшее", Body = "Устаревшее" }
        }, CancellationToken.None);

        Assert.Equal(SyncApplyResult.SkippedOlder, result);
        Assert.Equal("Наш заголовок", factory.Query(db => db.Announcements.Single().Title));
    }

    // ==================================================================
    // Зависимости между объектами
    // ==================================================================

    [Fact]
    public async Task Файл_без_своей_папки_откладывается_а_не_теряется()
    {
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var folderGlobalId = Guid.NewGuid();

        var fileEntry = new SyncEntry
        {
            Kind = SyncKinds.File,
            GlobalId = Guid.NewGuid(),
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload
            {
                FolderGlobalId = folderGlobalId,
                OriginalName = "Приказ.pdf",
                SizeBytes = 1024,
                ContentType = "application/pdf",
                CreatedAt = DateTime.UtcNow
            }
        };

        // Папки ещё нет — файл положить некуда.
        Assert.Equal(SyncApplyResult.Deferred, await applier.ApplyAsync(fileEntry, CancellationToken.None));
        Assert.Empty(factory.Query(db => db.Files.ToList()));

        // Приходит папка…
        await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Folder,
            GlobalId = folderGlobalId,
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload { Title = "Приказы", CreatedAt = DateTime.UtcNow }
        }, CancellationToken.None);

        // …и теперь файл встаёт на место.
        Assert.Equal(SyncApplyResult.Applied, await applier.ApplyAsync(fileEntry, CancellationToken.None));
        Assert.Equal("Приказ.pdf", factory.Query(db => db.Files.Single().OriginalName));
    }

    [Fact]
    public async Task Файл_без_содержимого_попадает_в_список_на_докачку()
    {
        // Сведения о файле приходят одной записью, а само содержимое
        // забирается отдельно. Пока содержимого нет, имя на диске пустое,
        // и файл должен оказаться в списке недокачанных.
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var folderGlobalId = Guid.NewGuid();

        await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Folder,
            GlobalId = folderGlobalId,
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload { Title = "Приказы", CreatedAt = DateTime.UtcNow }
        }, CancellationToken.None);

        var fileGlobalId = Guid.NewGuid();

        await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.File,
            GlobalId = fileGlobalId,
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload
            {
                FolderGlobalId = folderGlobalId,
                OriginalName = "Приказ.pdf",
                SizeBytes = 1024,
                CreatedAt = DateTime.UtcNow
            }
        }, CancellationToken.None);

        Assert.Contains(applier.Pending, p => p.Kind == SyncKinds.File && p.GlobalId == fileGlobalId);
        Assert.Empty(factory.Query(db => db.Files.Single().StorageName));
    }

    [Fact]
    public async Task Сообщение_без_своей_беседы_откладывается()
    {
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var result = await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Message,
            GlobalId = Guid.NewGuid(),
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload
            {
                ConversationGlobalId = Guid.NewGuid(),
                Body = "Привет",
                CreatedAt = DateTime.UtcNow
            }
        }, CancellationToken.None);

        Assert.Equal(SyncApplyResult.Deferred, result);
        Assert.Empty(factory.Query(db => db.Messages.ToList()));
    }

    [Fact]
    public async Task Переписка_двоих_не_раздваивается()
    {
        // Иванов написал Петрову у себя, Петров Иванову — у себя.
        // Ключ пары одинаков, и должна получиться ОДНА переписка,
        // а не две параллельные с половиной сообщений в каждой.
        using var factory = CreateFactory();

        var pairKey = "ivanov|petrov";

        factory.Seed(db => db.Conversations.Add(new Conversation
        {
            IsGroup = false,
            PairKey = pairKey,
            CreatedByUserName = "ivanov",
            CreatedAt = DateTime.UtcNow,
            LastMessageAt = DateTime.UtcNow
        }));

        using var scope = factory.Services.CreateScope();
        var applier = scope.ServiceProvider.GetRequiredService<SyncApplier>();

        var result = await applier.ApplyAsync(new SyncEntry
        {
            Kind = SyncKinds.Conversation,

            // Номер ДРУГОЙ: у соседа беседа завелась своя.
            GlobalId = Guid.NewGuid(),
            OriginBranch = "office2",
            ChangedAt = DateTime.UtcNow,
            Payload = new SyncPayload
            {
                IsGroup = false,
                PairKey = pairKey,
                CreatedByUserName = "petrov",
                CreatedAt = DateTime.UtcNow,
                Participants =
                [
                    new SyncParticipant { UserName = "ivanov", DisplayName = "Иванов Иван" },
                    new SyncParticipant { UserName = "petrov", DisplayName = "Петров Пётр" }
                ]
            }
        }, CancellationToken.None);

        Assert.Equal(SyncApplyResult.Applied, result);
        Assert.Single(factory.Query(db => db.Conversations.ToList()));
        Assert.Equal(2, factory.Query(db => db.Participants.Count()));
    }

    // ==================================================================
    // Сборка пачки для соседа
    // ==================================================================

    [Fact]
    public async Task Соседу_отдаётся_нынешнее_состояние_а_не_стопка_правок()
    {
        using var factory = CreateFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Черновик",
            Body = "Текст",
            CreatedAt = DateTime.UtcNow
        }));

        factory.Seed(db => db.Announcements.Single().Title = "Второй вариант");
        factory.Seed(db => db.Announcements.Single().Title = "Окончательный вариант");

        // Три записи в журнале — но объект-то один.
        Assert.Equal(3, factory.Query(db => db.SyncOutbox.Count()));

        using var scope = factory.Services.CreateScope();
        var feed = scope.ServiceProvider.GetRequiredService<SyncFeed>();

        var batch = await feed.BuildAsync(0, null, CancellationToken.None);

        var entry = Assert.Single(batch.Entries);

        Assert.Equal("Окончательный вариант", entry.Payload?.Title);
        Assert.Equal(Branch, batch.Branch);
        Assert.Equal(3, batch.LastId);
    }

    [Fact]
    public async Task Удалённый_объект_отдаётся_как_удаление()
    {
        using var factory = CreateFactory();

        factory.Seed(db => db.Announcements.Add(new Announcement
        {
            Title = "Объявление",
            Body = "Текст",
            CreatedAt = DateTime.UtcNow
        }));

        factory.Seed(db => db.Announcements.Remove(db.Announcements.Single()));

        using var scope = factory.Services.CreateScope();
        var feed = scope.ServiceProvider.GetRequiredService<SyncFeed>();

        var batch = await feed.BuildAsync(0, null, CancellationToken.None);

        var entry = Assert.Single(batch.Entries);

        Assert.True(entry.Deleted);
        Assert.Null(entry.Payload);
    }

    [Fact]
    public async Task Отсчёт_ведётся_по_номеру_записи()
    {
        using var factory = CreateFactory();

        for (var i = 1; i <= 3; i++)
        {
            var number = i;

            factory.Seed(db => db.Announcements.Add(new Announcement
            {
                Title = $"Объявление {number}",
                Body = "Текст",
                CreatedAt = DateTime.UtcNow
            }));
        }

        using var scope = factory.Services.CreateScope();
        var feed = scope.ServiceProvider.GetRequiredService<SyncFeed>();

        // Сосед уже забрал первые две записи и спрашивает, что после них.
        var batch = await feed.BuildAsync(2, null, CancellationToken.None);

        var entry = Assert.Single(batch.Entries);

        Assert.Equal("Объявление 3", entry.Payload?.Title);
        Assert.False(batch.HasMore);
    }

    // ==================================================================
    // Доступ к адресам обмена
    // ==================================================================

    [Fact]
    public async Task Без_пароля_обмена_адреса_закрыты()
    {
        using var factory = CreateFactory();
        factory.SyncKey = "правильный-пароль";

        var client = factory.CreateTestClient();

        var response = await client.GetAsync("/api/sync/changes?after=0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task С_чужим_паролем_адреса_закрыты()
    {
        using var factory = CreateFactory();
        factory.SyncKey = "правильный-пароль";

        var client = factory.CreateTestClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            SyncClient.KeyHeader, SyncClient.Fingerprint("чужой-пароль"));

        var response = await client.GetAsync("/api/sync/changes?after=0");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task Со_своим_паролем_адреса_открыты()
    {
        using var factory = CreateFactory();
        factory.SyncKey = "правильный-пароль";

        var client = factory.CreateTestClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            SyncClient.KeyHeader, SyncClient.Fingerprint("правильный-пароль"));

        var response = await client.GetAsync("/api/sync/ping");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains(Branch, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Пока_пароль_не_задан_обмен_недоступен_совсем()
    {
        // Пустой пароль означает «обмен не настроен». Отдавать содержимое
        // портала кому угодно, кто дотянулся до порта, нельзя ни при какой
        // настройке — даже если Enabled = true.
        using var factory = CreateFactory();
        factory.SyncKey = "";

        var client = factory.CreateTestClient();

        client.DefaultRequestHeaders.TryAddWithoutValidation(
            SyncClient.KeyHeader, SyncClient.Fingerprint(""));

        var response = await client.GetAsync("/api/sync/ping");

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
    }

    [Fact]
    public void Отпечаток_пароля_годится_для_заголовка_HTTP()
    {
        // Пароль наберут по-русски, а в заголовках HTTP допустима только
        // латиница. Отпечаток — всегда 64 знака из «0123456789abcdef».
        var fingerprint = SyncClient.Fingerprint("пароль обмена между филиалами");

        Assert.Equal(64, fingerprint.Length);
        Assert.All(fingerprint, c => Assert.Contains(c, "0123456789abcdef"));
        Assert.NotEqual(fingerprint, SyncClient.Fingerprint("другой пароль"));
    }

    // ==================================================================
    // Настройки, меняемые в панели администратора
    // ==================================================================

    [Fact]
    public async Task Промежуток_между_обменами_сохраняется_и_читается()
    {
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SyncSettings>();

        // По умолчанию — значение из настроек файла.
        Assert.Equal(15, await settings.IntervalMinutesAsync(CancellationToken.None));

        await settings.SetIntervalAsync(45, "admin", CancellationToken.None);

        Assert.Equal(45, await settings.IntervalMinutesAsync(CancellationToken.None));

        // Заведомо неверные значения не принимаются: обмен раз в ноль минут
        // означал бы непрерывный поток запросов по каналу между офисами.
        await settings.SetIntervalAsync(0, "admin", CancellationToken.None);

        Assert.Equal(1, await settings.IntervalMinutesAsync(CancellationToken.None));
    }

    [Fact]
    public async Task Паузу_можно_поставить_и_снять()
    {
        using var factory = CreateFactory();

        using var scope = factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SyncSettings>();

        Assert.False(await settings.PausedAsync(CancellationToken.None));

        await settings.SetPausedAsync(true, "admin", CancellationToken.None);
        Assert.True(await settings.PausedAsync(CancellationToken.None));

        await settings.SetPausedAsync(false, "admin", CancellationToken.None);
        Assert.False(await settings.PausedAsync(CancellationToken.None));
    }

    // ==================================================================
    // Настоящий appsettings
    // ==================================================================

    [Fact]
    public void Настоящий_appsettings_не_содержит_пароля_обмена()
    {
        // Пароль должен задаваться переменной окружения Sync__Key.
        // Если он однажды окажется в файле, тот попадёт и в репозиторий,
        // и в резервные копии конфигурации.
        var configuration = new ConfigurationBuilder()
            .AddJsonFile(Path.Combine(AppContext.BaseDirectory, "appsettings.json"))
            .Build();

        var sync = configuration.GetSection(SyncOptions.SectionName).Get<SyncOptions>();

        Assert.NotNull(sync);
        Assert.True(string.IsNullOrEmpty(sync.Key),
            "В appsettings.json попал пароль обмена. Уберите его и задайте переменной окружения Sync__Key.");

        // Филиал не должен быть указан сам на себя.
        Assert.DoesNotContain(sync.Peers,
            p => string.Equals(p.Code, sync.BranchCode, StringComparison.OrdinalIgnoreCase));
    }
}
