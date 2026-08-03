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
    }
}
