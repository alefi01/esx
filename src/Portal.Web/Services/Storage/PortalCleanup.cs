using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;
using Portal.Web.Services.Announcements;
using Portal.Web.Services.Messaging;

namespace Portal.Web.Services.Storage;

/// <summary>Сколько чего накопилось и что уйдёт при ближайшей уборке.</summary>
/// <param name="Name">Что именно: «Корзина», «Переписка» и так далее.</param>
/// <param name="Total">Сколько всего сейчас есть.</param>
/// <param name="Expired">Сколько из этого старше заданного срока — то есть уйдёт.</param>
/// <param name="Bytes">Сколько места освободится, байты. 0 — место не занимает.</param>
/// <param name="Days">Срок хранения в днях. 0 — уборка выключена, хранится вечно.</param>
public sealed record CleanupLine(string Name, int Total, int Expired, long Bytes, int Days);

/// <summary>
/// Уборка портала целиком: что копится и когда это убирать.
///
/// ЗАЧЕМ ОТДЕЛЬНАЯ СЛУЖБА
///
/// Портал копит четыре разных вещи, и каждая растёт сама по себе:
/// корзина, переписка с вложениями, объявления с вложениями и журнал
/// действий. Каждая из них по отдельности незаметна, а вместе они через
/// год-другой съедают диск, и разбираться приходится авралом.
///
/// Здесь сроки хранения собраны в одном месте, задаются администратором
/// из портала и применяются одинаково — и фоновой уборкой, и кнопкой
/// «убрать сейчас».
///
/// ЧТО ЗДЕСЬ НЕ УБИРАЕТСЯ И ПОЧЕМУ
///
///   * Файлы в папках. Они и есть содержимое портала; их срок задаётся
///     по каждой папке отдельно (RetentionDays) — общий срок на всё
///     хранилище означал бы, что договоры пятилетней давности исчезают
///     заодно с черновиками.
///   * Закреплённые объявления. Их закрепили именно потому, что они
///     должны висеть постоянно, — срок хранения к ним не применяется.
///
/// БЕЗОПАСНОСТЬ ДЕЙСТВИЯ
///
/// Ноль в любом сроке означает «не убирать вовсе», и это значение
/// по умолчанию. Портал ничего не удаляет, пока администратор явно
/// не назначит срок.
/// </summary>
public sealed class PortalCleanup
{
    /// <summary>Сколько дней держать переписку. 0 — вечно.</summary>
    public const string MessageDaysKey = "cleanup.messageDays";

    /// <summary>Сколько дней держать объявления (кроме закреплённых). 0 — вечно.</summary>
    public const string AnnouncementDaysKey = "cleanup.announcementDays";

    /// <summary>Сколько дней держать корзину. 0 — брать значение из файла настроек.</summary>
    public const string TrashDaysKey = "cleanup.trashDays";

    /// <summary>Сколько дней держать журнал действий. 0 — брать значение из файла настроек.</summary>
    public const string AuditDaysKey = "cleanup.auditDays";

    private readonly PortalDbContext _db;
    private readonly PortalSettings _settings;
    private readonly AnnouncementStorage _announcements;
    private readonly MessageStorage _messages;
    private readonly TimeProvider _time;
    private readonly ILogger<PortalCleanup> _logger;

    public PortalCleanup(
        PortalDbContext db,
        PortalSettings settings,
        AnnouncementStorage announcements,
        MessageStorage messages,
        TimeProvider time,
        ILogger<PortalCleanup> logger)
    {
        _db = db;
        _settings = settings;
        _announcements = announcements;
        _messages = messages;
        _time = time;
        _logger = logger;
    }

    /// <summary>Сроки хранения, как их задал администратор.</summary>
    public async Task<(int Messages, int Announcements, int Trash, int Audit)> DaysAsync(
        CancellationToken cancellationToken = default) =>
        (
            await _settings.IntAsync(MessageDaysKey, 0, cancellationToken),
            await _settings.IntAsync(AnnouncementDaysKey, 0, cancellationToken),
            await _settings.IntAsync(TrashDaysKey, 0, cancellationToken),
            await _settings.IntAsync(AuditDaysKey, 0, cancellationToken)
        );

    /// <summary>
    /// Что накопилось и что уйдёт при уборке. Только считает, ничего
    /// не трогает: администратор должен видеть цену действия ДО нажатия.
    /// </summary>
    public async Task<IReadOnlyList<CleanupLine>> PreviewAsync(CancellationToken cancellationToken = default)
    {
        var (messageDays, announcementDays, trashDays, auditDays) = await DaysAsync(cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        var lines = new List<CleanupLine>();

        // ---- Корзина ----
        var trashBorder = now.AddDays(-Math.Max(0, trashDays));

        var trashTotal = await _db.Files.CountAsync(f => f.DeletedAt != null, cancellationToken);
        var trashOld = trashDays <= 0
            ? 0
            : await _db.Files.CountAsync(f => f.DeletedAt != null && f.DeletedAt < trashBorder, cancellationToken);
        var trashBytes = trashDays <= 0
            ? 0
            : await _db.Files
                .Where(f => f.DeletedAt != null && f.DeletedAt < trashBorder)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

        lines.Add(new CleanupLine("Корзина", trashTotal, trashOld, trashBytes, trashDays));

        // ---- Переписка ----
        var messageBorder = now.AddDays(-Math.Max(0, messageDays));

        var messagesTotal = await _db.Messages.CountAsync(cancellationToken);
        var messagesOld = messageDays <= 0
            ? 0
            : await _db.Messages.CountAsync(m => m.CreatedAt < messageBorder, cancellationToken);
        var messageBytes = messageDays <= 0
            ? 0
            : await _db.MessageFiles
                .Where(f => f.PurgedAt == null && f.Message != null && f.Message.CreatedAt < messageBorder)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

        lines.Add(new CleanupLine("Сообщения в переписках", messagesTotal, messagesOld, messageBytes, messageDays));

        // ---- Объявления ----
        var announcementBorder = now.AddDays(-Math.Max(0, announcementDays));

        var announcementsTotal = await _db.Announcements.CountAsync(cancellationToken);
        var announcementsOld = announcementDays <= 0
            ? 0
            : await _db.Announcements.CountAsync(
                a => !a.IsPinned && a.CreatedAt < announcementBorder, cancellationToken);
        var announcementBytes = announcementDays <= 0
            ? 0
            : await _db.AnnouncementFiles
                .Where(f => f.Announcement != null
                            && !f.Announcement.IsPinned
                            && f.Announcement.CreatedAt < announcementBorder)
                .SumAsync(f => (long?)f.SizeBytes, cancellationToken) ?? 0;

        lines.Add(new CleanupLine(
            "Объявления (кроме закреплённых)", announcementsTotal, announcementsOld, announcementBytes, announcementDays));

        // ---- Журнал действий ----
        var auditBorder = now.AddDays(-Math.Max(0, auditDays));

        var auditTotal = await _db.AuditEntries.CountAsync(cancellationToken);
        var auditOld = auditDays <= 0
            ? 0
            : await _db.AuditEntries.CountAsync(a => a.At < auditBorder, cancellationToken);

        lines.Add(new CleanupLine("Записи журнала действий", auditTotal, auditOld, 0, auditDays));

        return lines;
    }

    /// <summary>
    /// Убирает всё, чему вышел срок. Возвращает короткий отчёт для человека.
    ///
    /// Порядок внутри каждого раздела один и тот же: сначала файлы с диска,
    /// потом записи из базы. В обратном порядке прерванная уборка оставила бы
    /// на диске файлы, на которые больше ничто не ссылается, — найти их потом
    /// уже нечем.
    /// </summary>
    public async Task<string> RunAsync(string byUserName, CancellationToken cancellationToken = default)
    {
        var (messageDays, announcementDays, _, _) = await DaysAsync(cancellationToken);

        var now = _time.GetUtcNow().UtcDateTime;
        var done = new List<string>();

        if (messageDays > 0)
        {
            var removed = await PurgeMessagesAsync(now.AddDays(-messageDays), cancellationToken);

            if (removed > 0)
            {
                done.Add($"сообщений: {removed}");
            }
        }

        if (announcementDays > 0)
        {
            var removed = await PurgeAnnouncementsAsync(now.AddDays(-announcementDays), cancellationToken);

            if (removed > 0)
            {
                done.Add($"объявлений: {removed}");
            }
        }

        if (done.Count > 0)
        {
            _db.AuditEntries.Add(AuditLog.SystemEntry(
                now, AuditAction.RetentionCleanup, "уборка портала",
                $"запустил {byUserName}; убрано — {string.Join(", ", done)}"));

            await _db.SaveChangesAsync(cancellationToken);
        }

        return done.Count == 0 ? "Убирать нечего: ничего не вышло за срок хранения." : "Убрано: " + string.Join(", ", done) + ".";
    }

    /// <summary>Сообщения старше срока — вместе с вложениями и опустевшими беседами.</summary>
    private async Task<int> PurgeMessagesAsync(DateTime border, CancellationToken cancellationToken)
    {
        var expired = await _db.Messages.AsTracking()
            .Include(m => m.Files)
            .Where(m => m.CreatedAt < border)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var file in expired.SelectMany(m => m.Files).Where(f => f.PurgedAt is null))
        {
            _messages.Delete(file.Message!.ConversationId, file.StorageName);
        }

        var conversations = expired.Select(m => m.ConversationId).Distinct().ToList();

        _db.Messages.RemoveRange(expired);

        await _db.SaveChangesAsync(cancellationToken);

        // Беседа, из которой убрали все сообщения, остаётся пустой строкой
        // в списке и сбивает с толку: человек открывает её и видит «сообщений
        // пока нет», хотя переписка была. Такие беседы убираем следом.
        foreach (var id in conversations)
        {
            var left = await _db.Messages.CountAsync(m => m.ConversationId == id, cancellationToken);

            if (left > 0)
            {
                continue;
            }

            var participants = await _db.Participants.AsTracking()
                .Where(p => p.ConversationId == id)
                .ToListAsync(cancellationToken);

            _db.Participants.RemoveRange(participants);

            var conversation = await _db.Conversations.AsTracking()
                .FirstOrDefaultAsync(c => c.Id == id, cancellationToken);

            if (conversation is not null)
            {
                _db.Conversations.Remove(conversation);
            }

            _messages.DeleteFolderIfEmpty(id);
        }

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Уборка портала: убрано сообщений — {Count}.", expired.Count);

        return expired.Count;
    }

    /// <summary>Объявления старше срока. Закреплённые не трогаем никогда.</summary>
    private async Task<int> PurgeAnnouncementsAsync(DateTime border, CancellationToken cancellationToken)
    {
        var expired = await _db.Announcements.AsTracking()
            .Include(a => a.Files)
            .Where(a => !a.IsPinned && a.CreatedAt < border)
            .ToListAsync(cancellationToken);

        if (expired.Count == 0)
        {
            return 0;
        }

        foreach (var announcement in expired)
        {
            // Вложения объявления лежат в своей папке на диске — убираем её
            // целиком вместе с объявлением.
            _announcements.DeleteAll(announcement.Id);
        }

        _db.Announcements.RemoveRange(expired);

        await _db.SaveChangesAsync(cancellationToken);

        _logger.LogInformation("Уборка портала: убрано объявлений — {Count}.", expired.Count);

        return expired.Count;
    }
}
