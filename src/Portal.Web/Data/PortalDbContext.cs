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
    public PortalDbContext(DbContextOptions<PortalDbContext> options) : base(options)
    {
    }

    public DbSet<Announcement> Announcements => Set<Announcement>();

    public DbSet<StorageFolder> Folders => Set<StorageFolder>();
    public DbSet<FolderPermission> FolderPermissions => Set<FolderPermission>();
    public DbSet<StoredFile> Files => Set<StoredFile>();
    public DbSet<AuditEntry> AuditEntries => Set<AuditEntry>();

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

        modelBuilder.Entity<AuditEntry>(entity =>
        {
            entity.HasIndex(a => a.At);
            entity.HasIndex(a => a.UserName);
            entity.HasIndex(a => a.Action);
        });
    }
}
