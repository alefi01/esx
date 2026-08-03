using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Запись действий с файлами в журнал.
///
/// Пишет и в таблицу базы данных, и в обычный журнал приложения.
/// Дублирование намеренное: таблица удобна для поиска и переживает
/// перезапуски, а текстовый журнал остаётся единственным свидетелем,
/// если в этот момент недоступна как раз база.
///
/// Правило: запись в журнал НИКОГДА не должна ронять само действие.
/// Если не удалось записать, что файл скачали, — файл всё равно надо отдать.
/// </summary>
public sealed class AuditLog
{
    private readonly PortalDbContext _db;
    private readonly IHttpContextAccessor _httpContext;
    private readonly TimeProvider _time;
    private readonly ILogger<AuditLog> _logger;

    public AuditLog(
        PortalDbContext db,
        IHttpContextAccessor httpContext,
        TimeProvider time,
        ILogger<AuditLog> logger)
    {
        _db = db;
        _httpContext = httpContext;
        _time = time;
        _logger = logger;
    }

    /// <summary>
    /// Добавляет запись в контекст, но НЕ сохраняет её.
    /// Сохранение произойдёт вместе с самим действием — одним SaveChangesAsync.
    /// Так запись в журнале и изменение данных попадут в одну транзакцию:
    /// не будет ни записи о несделанном, ни сделанного без записи.
    /// </summary>
    public void Add(AuditAction action, string target, string? details = null)
    {
        var user = _httpContext.HttpContext?.User;

        _db.AuditEntries.Add(new AuditEntry
        {
            At = _time.GetUtcNow().UtcDateTime,
            UserName = user?.Identity?.Name ?? "система",
            UserDisplayName = user?.FindFirstValue(ClaimTypes.GivenName) ?? user?.Identity?.Name ?? "система",
            RemoteIp = _httpContext.HttpContext?.Connection.RemoteIpAddress?.ToString(),
            Action = action,
            Target = Trim(target, 600),
            Details = details is null ? null : Trim(details, 1000)
        });

        _logger.LogInformation(
            "{Action}: {Target} (пользователь {User}) {Details}",
            action, target, user?.Identity?.Name ?? "система", details);
    }

    /// <summary>
    /// Записывает действие отдельно, своим сохранением.
    /// Нужно там, где основное действие ничего в базе не меняет, —
    /// например при скачивании файла.
    /// </summary>
    public async Task WriteAsync(AuditAction action, string target, string? details = null,
        CancellationToken cancellationToken = default)
    {
        Add(action, target, details);

        try
        {
            await _db.SaveChangesAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // Осознанно проглатываем: недоступная база не повод не отдать файл
            // человеку, который имеет на него право. Событие уже попало
            // в текстовый журнал строкой выше.
            _logger.LogError(ex, "Не удалось сохранить запись журнала действий ({Action}).", action);
        }
    }

    /// <summary>Запись от имени фоновой задачи — HTTP-контекста в этот момент нет.</summary>
    public static AuditEntry SystemEntry(DateTime at, AuditAction action, string target, string? details) =>
        new()
        {
            At = at,
            UserName = "система",
            UserDisplayName = "Автоматическая очистка",
            Action = action,
            Target = Trim(target, 600),
            Details = details is null ? null : Trim(details, 1000)
        };

    private static string Trim(string value, int max) =>
        value.Length <= max ? value : value[..max];
}
