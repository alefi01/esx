using Microsoft.EntityFrameworkCore;

namespace Portal.Web.Data;

/// <summary>
/// Точка доступа к базе данных.
///
/// «Контекст» в Entity Framework — это объект, через который выполняются все
/// запросы и сохранения. Каждое свойство DbSet&lt;T&gt; соответствует таблице.
///
/// Контекст живёт ровно один HTTP-запрос: ASP.NET Core создаёт его в начале
/// и уничтожает в конце. Хранить его в статическом поле или в синглтоне нельзя —
/// он не рассчитан на одновременное использование из нескольких потоков.
/// </summary>
public class PortalDbContext : DbContext
{
    public PortalDbContext(DbContextOptions<PortalDbContext> options)
        : base(options)
    {
    }

    public DbSet<Announcement> Announcements => Set<Announcement>();
    public DbSet<AnnouncementFile> AnnouncementFiles => Set<AnnouncementFile>();

    public DbSet<StorageFolder> Folders => Set<StorageFolder>();
    public DbSet<FolderPermission> FolderPermissions => Set<FolderPermission>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();
    public DbSet<UserSeenState> SeenStates => Set<UserSeenState>();

    public DbSet<Favorite> Favorites => Set<Favorite>();

    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationParticipant> Participants => Set<ConversationParticipant>();
    public DbSet<Message> Messages => Set<Message>();
    public DbSet<MessageFile> MessageFiles => Set<MessageFile>();

    // Синхронизация между филиалами.
    public DbSet<SyncOutboxEntry> SyncOutbox => Set<SyncOutboxEntry>();
    public DbSet<SyncPeerState> SyncPeers => Set<SyncPeerState>();
    public DbSet<PortalSetting> Settings => Set<PortalSetting>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.Entity<Announcement>(entity =>
        {
            // Лента всегда сортируется по дате публикации по убыванию.
            // Индекс нужен, чтобы база не перебирала всю таблицу при каждом открытии.
            // На нынешних объёмах разницы не будет, но и стоит он копейки.
            entity.HasIndex(a => a.CreatedAt);

            // Отдельный индекс по автору: по нему строится проверка
            // «моё это объявление или чужое», а в будущем — фильтр «мои объявления».
            entity.HasIndex(a => a.AuthorUserName);
        });

        modelBuilder.Entity<AnnouncementFile>(entity =>
        {
            entity.HasOne(f => f.Announcement)
                .WithMany(a => a.Files)
                .HasForeignKey(f => f.AnnouncementId)
                // Вложения — часть объявления, отдельно от него не нужны.
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StorageFolder>(entity =>
        {
            entity.HasOne(f => f.Parent)
                .WithMany(f => f.Children)
                .HasForeignKey(f => f.ParentId)
                // Удаление папки с подпапками запрещено на уровне базы данных.
                // Это подстраховка: в интерфейсе удалить непустую папку и так нельзя,
                // но правило, записанное в схеме, переживёт любую ошибку в коде.
                .OnDelete(DeleteBehavior.Restrict);

            // Имя папки уникально среди соседей: две папки «Договоры» рядом
            // сбивают с толку и делают бессмысленными ссылки на них.
            entity.HasIndex(f => new { f.ParentId, f.Name }).IsUnique();

            // Отдельный индекс для верхнего уровня.
            //
            // Индекс выше папки верхнего уровня НЕ защищает: там ParentId пуст,
            // а база считает два пустых значения РАЗНЫМИ — пара (NULL, «Договоры»)
            // не нарушает уникальность сама с собой. Это общее правило языка
            // запросов, а не особенность PostgreSQL.
            //
            // Поэтому здесь второй индекс — только по имени и только для строк
            // без родителя (HasFilter). Он и закрывает верхний уровень.
            entity.HasIndex(f => f.Name)
                .IsUnique()
                .HasFilter("\"ParentId\" IS NULL")
                .HasDatabaseName("IX_Folders_Name_Root");
        });

        modelBuilder.Entity<FolderPermission>(entity =>
        {
            entity.HasOne(p => p.Folder)
                .WithMany(f => f.Permissions)
                .HasForeignKey(p => p.FolderId)
                // Права — часть папки, отдельно от неё они не нужны.
                .OnDelete(DeleteBehavior.Cascade);

            // Одна группа — одна запись на папку. Иначе получится два разных
            // уровня доступа для одной группы, и поведение станет непредсказуемым.
            entity.HasIndex(p => new { p.FolderId, p.GroupName }).IsUnique();
        });

        modelBuilder.Entity<StoredFile>(entity =>
        {
            entity.HasOne(f => f.Folder)
                .WithMany(f => f.Files)
                .HasForeignKey(f => f.FolderId)
                // Папку с файлами удалить нельзя — сначала надо разобраться с файлами.
                .OnDelete(DeleteBehavior.Restrict);

            // Основной запрос: «покажи содержимое папки, кроме удалённого».
            entity.HasIndex(f => new { f.FolderId, f.DeletedAt });

            // Фоновая уборка ищет по дате удаления и по дате загрузки.
            entity.HasIndex(f => f.DeletedAt);
            entity.HasIndex(f => f.UploadedAt);

            // Вычисляемое свойство, в базе его быть не должно.
            entity.Ignore(f => f.IsDeleted);
        });

        modelBuilder.Entity<Favorite>(entity =>
        {
            // Отметка живёт вместе с тем, что отмечено: исчез файл —
            // исчезла и отметка. Иначе избранное со временем превратилось бы
            // в кладбище ссылок в никуда.
            entity.HasOne(f => f.File)
                .WithMany()
                .HasForeignKey(f => f.FileId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasOne(f => f.Folder)
                .WithMany()
                .HasForeignKey(f => f.FolderId)
                .OnDelete(DeleteBehavior.Cascade);

            // Один человек не может отметить один и тот же файл дважды.
            // Индексы раздельные и частичные по той же причине, что и у папок:
            // база считает два пустых значения РАЗНЫМИ, и общий индекс
            // по паре «логин + файл» не помешал бы дублям среди папок.
            entity.HasIndex(f => new { f.UserName, f.FileId })
                .IsUnique()
                .HasFilter("\"FileId\" IS NOT NULL");

            entity.HasIndex(f => new { f.UserName, f.FolderId })
                .IsUnique()
                .HasFilter("\"FolderId\" IS NOT NULL");
        });

        modelBuilder.Entity<UserSeenState>(entity =>
        {
            // Один человек — одна строка. Уникальность на уровне базы,
            // а не только в коде: две строки на одного пользователя
            // означали бы, что счётчик непрочитанного зависит от того,
            // какая из них попалась первой.
            entity.HasIndex(s => s.UserName).IsUnique();
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            // Список бесед всегда сортируется по времени последнего сообщения.
            entity.HasIndex(c => c.LastMessageAt);

            // Одна пара — одна переписка. Индекс частичный: у групп ключ пары
            // пуст, и без условия все группы конфликтовали бы друг с другом
            // одной и той же пустой строкой.
            entity.HasIndex(c => c.PairKey)
                .IsUnique()
                .HasFilter("\"PairKey\" <> ''")
                .HasDatabaseName("IX_Conversations_PairKey");
        });

        modelBuilder.Entity<ConversationParticipant>(entity =>
        {
            entity.HasOne(p => p.Conversation)
                .WithMany(c => c.Participants)
                .HasForeignKey(p => p.ConversationId)
                // Участники — часть беседы, отдельно от неё не нужны.
                .OnDelete(DeleteBehavior.Cascade);

            // Один человек в беседе ровно один раз.
            entity.HasIndex(p => new { p.ConversationId, p.UserName }).IsUnique();

            // Основной запрос: «покажи мои беседы».
            entity.HasIndex(p => p.UserName);
        });

        modelBuilder.Entity<Message>(entity =>
        {
            entity.HasOne(m => m.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(m => m.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);

            // «Покажи сообщения беседы по порядку» и «есть ли новее такого-то».
            entity.HasIndex(m => new { m.ConversationId, m.Id });

            // Фоновая уборка вложений ищет по дате.
            entity.HasIndex(m => m.CreatedAt);

            entity.Ignore(m => m.IsDeleted);
        });

        modelBuilder.Entity<MessageFile>(entity =>
        {
            entity.HasOne(f => f.Message)
                .WithMany(m => m.Files)
                .HasForeignKey(f => f.MessageId)
                .OnDelete(DeleteBehavior.Cascade);

            entity.HasIndex(f => f.PurgedAt);
        });

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasIndex(a => a.At);
            entity.HasIndex(a => a.UserName);
            entity.HasIndex(a => a.Action);
        });

        // ------------------------------------------------------------------
        // Синхронизация между филиалами
        // ------------------------------------------------------------------

        modelBuilder.Entity<SyncOutboxEntry>(entity =>
        {
            // Соседи спрашивают «что после номера N» — это основной и,
            // по сути, единственный запрос к журналу.
            entity.HasIndex(e => e.Id);

            // Уборка старых записей ищет по дате.
            entity.HasIndex(e => e.ChangedAt);
        });

        // По одной строке на соседа.
        modelBuilder.Entity<SyncPeerState>().HasIndex(e => e.PeerCode).IsUnique();

        modelBuilder.Entity<PortalSetting>().HasKey(s => s.Key);

        // Общий номер объекта уникален в пределах базы. Индекс нужен ещё
        // и потому, что применение изменения от соседа начинается именно
        // с поиска «есть ли уже такой объект».
        modelBuilder.Entity<Announcement>().HasIndex(a => a.GlobalId).IsUnique();
        modelBuilder.Entity<AnnouncementFile>().HasIndex(f => f.GlobalId).IsUnique();
        modelBuilder.Entity<StorageFolder>().HasIndex(f => f.GlobalId).IsUnique();
        modelBuilder.Entity<StoredFile>().HasIndex(f => f.GlobalId).IsUnique();
        modelBuilder.Entity<Conversation>().HasIndex(c => c.GlobalId).IsUnique();
        modelBuilder.Entity<Message>().HasIndex(m => m.GlobalId).IsUnique();
        modelBuilder.Entity<MessageFile>().HasIndex(f => f.GlobalId).IsUnique();
    }

    // ======================================================================
    // Общие номера объектов
    // ======================================================================

    public override int SaveChanges()
    {
        StampGlobalIds();

        return base.SaveChanges();
    }

    public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
    {
        StampGlobalIds();

        return base.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// Проставляет объявлению, файлу, папке или сообщению общий номер
    /// и время последнего изменения.
    ///
    /// ПОЧЕМУ ЗДЕСЬ, А НЕ В КАЖДОМ МЕСТЕ, ГДЕ ЧТО-ТО МЕНЯЕТСЯ
    ///
    /// Мест, где портал меняет объявления, переписку и файлы, около двадцати:
    /// публикация, правка, закрепление, отправка сообщения, загрузка файла,
    /// переименование, корзина, восстановление, автоочистка… Расставить
    /// вызов в каждом — значит однажды забыть про один. Здесь же место одно,
    /// и мимо него изменение пройти не может: сохранение в базу идёт
    /// только через этот метод.
    ///
    /// Номер (GlobalId) уникален в пределах базы и от неё не зависит:
    /// на него ссылаются ссылки на объявления и вложения, и он переживает
    /// перенос данных в другую базу, где счётчики начнутся заново.
    /// </summary>
    private void StampGlobalIds()
    {
        ChangeTracker.DetectChanges();

        var now = DateTime.UtcNow;

        foreach (var tracked in ChangeTracker.Entries<ISyncable>())
        {
            if (tracked.State is not (EntityState.Added or EntityState.Modified))
            {
                continue;
            }

            if (tracked.Entity.GlobalId == Guid.Empty)
            {
                tracked.Entity.GlobalId = Guid.NewGuid();
            }

            tracked.Entity.ChangedAt = now;
        }
    }
}
