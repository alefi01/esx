using System.Security.Cryptography;
using System.Text;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Announcements;
using Portal.Web.Services.Messaging;
using Portal.Web.Services.Storage;

namespace Portal.Web.Services.Sync;

/// <summary>
/// То, что этот портал отдаёт СОСЕДНИМ филиалам.
///
/// ДОСТУП
///
/// Эти адреса открыты без входа пользователя — заходит не человек,
/// а другой сервер, и учётной записи Active Directory у него нет.
/// Вместо входа проверяется общий пароль обмена в заголовке
/// X-Portal-Sync-Key. Пароль пустой — адреса не работают вовсе:
/// отдавать содержимое портала кому угодно, кто дотянулся до порта,
/// нельзя ни при каких настройках.
///
/// Сравнение пароля делается с постоянным временем. Обычное сравнение
/// строк заканчивается на первом несовпавшем символе, и по времени
/// ответа пароль можно подбирать посимвольно. На локальной сети это
/// малореально, но правило простое, а цена — три строки.
/// </summary>
public static class SyncEndpoints
{
    /// <summary>Наибольший кусок файла за один запрос — защита от запроса «дай 2 ГБ разом».</summary>
    private const int MaxChunk = 8 * 1024 * 1024;

    public static void MapSyncEndpoints(this WebApplication app)
    {
        // ------------------------------------------------------------------
        // «Кто ты и жив ли» — для проверки настройки.
        // ------------------------------------------------------------------
        app.MapGet("/api/sync/ping", (HttpContext http, IOptions<SyncOptions> options) =>
            Authorize(http, options.Value) ?? Results.Ok(new
            {
                branch = options.Value.BranchCode,
                name = options.Value.BranchName,
                at = DateTime.UtcNow
            })).AllowAnonymous();

        // ------------------------------------------------------------------
        // «Что у тебя изменилось после номера N».
        // ------------------------------------------------------------------
        app.MapGet("/api/sync/changes", async (
            HttpContext http,
            IOptions<SyncOptions> options,
            SyncFeed feed,
            long? after,
            int? take,
            CancellationToken cancellationToken) =>
        {
            var denied = Authorize(http, options.Value);

            if (denied is not null)
            {
                return denied;
            }

            return Results.Ok(await feed.BuildAsync(Math.Max(0, after ?? 0), take, cancellationToken));
        }).AllowAnonymous();

        // ------------------------------------------------------------------
        // «Дай кусок такого-то файла».
        //
        // Кусками, а не целиком: канал между офисами узкий, и оборванная
        // передача должна стоить одного куска, а не всего файла.
        // ------------------------------------------------------------------
        app.MapGet("/api/sync/blob", async (
            HttpContext http,
            IOptions<SyncOptions> options,
            PortalDbContext db,
            FileStorage files,
            MessageStorage messageFiles,
            AnnouncementStorage announcementFiles,
            string kind,
            Guid globalId,
            long? offset,
            int? length,
            CancellationToken cancellationToken) =>
        {
            var denied = Authorize(http, options.Value);

            if (denied is not null)
            {
                return denied;
            }

            var from = Math.Max(0, offset ?? 0);
            var size = Math.Clamp(length ?? 256 * 1024, 1, MaxChunk);

            var source = await OpenAsync(
                kind, globalId, db, files, messageFiles, announcementFiles, cancellationToken);

            if (source is null)
            {
                return Results.NotFound();
            }

            await using var stream = source;

            if (from >= stream.Length)
            {
                // Файл кончился. Пустой ответ — условный знак «всё»,
                // и обрабатывается он на стороне забирающего.
                return Results.Bytes([], "application/octet-stream");
            }

            stream.Seek(from, SeekOrigin.Begin);

            var remaining = (int)Math.Min(size, stream.Length - from);
            var buffer = new byte[remaining];

            await stream.ReadExactlyAsync(buffer, 0, remaining, cancellationToken);

            return Results.Bytes(buffer, "application/octet-stream");
        }).AllowAnonymous();
    }

    /// <summary>
    /// Проверка пароля обмена. Возвращает null, если всё хорошо,
    /// или готовый отказ, если нет.
    /// </summary>
    private static IResult? Authorize(HttpContext http, SyncOptions options)
    {
        // «Служба недоступна», а не «запрещено»: с той стороны это разные
        // случаи, и сосед скажет администратору разное.
        if (!options.Enabled || string.IsNullOrWhiteSpace(options.Key))
        {
            return Results.StatusCode(StatusCodes.Status503ServiceUnavailable);
        }

        var presented = http.Request.Headers[SyncClient.KeyHeader].ToString();

        return Same(presented, SyncClient.Fingerprint(options.Key)) ? null : Results.Unauthorized();
    }

    /// <summary>
    /// Сравнение с постоянным временем.
    ///
    /// Сравниваются отпечатки паролей (см. SyncClient.Fingerprint), а не
    /// сами пароли: длина у них всегда одна и та же, и по времени ответа
    /// нельзя судить даже о том, насколько пароль длинный.
    /// </summary>
    private static bool Same(string presented, string expected) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(expected)));

    /// <summary>Открывает содержимое вложения нужного вида. null — такого файла у нас нет.</summary>
    private static async Task<Stream?> OpenAsync(
        string kind,
        Guid globalId,
        PortalDbContext db,
        FileStorage files,
        MessageStorage messageFiles,
        AnnouncementStorage announcementFiles,
        CancellationToken cancellationToken)
    {
        switch (kind)
        {
            case SyncKinds.File:
            {
                var file = await db.Files
                    .FirstOrDefaultAsync(f => f.GlobalId == globalId, cancellationToken);

                return file is null || string.IsNullOrEmpty(file.StorageName)
                       || !files.Exists(file.FolderId, file.StorageName)
                    ? null
                    : files.OpenRead(file.FolderId, file.StorageName);
            }

            case SyncKinds.Message:
            {
                var file = await db.MessageFiles
                    .Include(f => f.Message)
                    .FirstOrDefaultAsync(f => f.GlobalId == globalId, cancellationToken);

                if (file?.Message is null || string.IsNullOrEmpty(file.StorageName)
                    || !messageFiles.Exists(file.Message.ConversationId, file.StorageName))
                {
                    return null;
                }

                return messageFiles.OpenRead(file.Message.ConversationId, file.StorageName);
            }

            case SyncKinds.Announcement:
            {
                var file = await db.AnnouncementFiles
                    .FirstOrDefaultAsync(f => f.GlobalId == globalId, cancellationToken);

                return file is null || string.IsNullOrEmpty(file.StorageName)
                       || !announcementFiles.Exists(file.AnnouncementId, file.StorageName)
                    ? null
                    : announcementFiles.OpenRead(file.AnnouncementId, file.StorageName);
            }

            default:
                return null;
        }
    }
}
