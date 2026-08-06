using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;

namespace Portal.Web.Services.Sync;

/// <summary>
/// Разговор с соседним филиалом по HTTP.
///
/// Ровно два запроса:
///   * «что у тебя изменилось после номера N» — /api/sync/changes;
///   * «дай кусок такого-то файла» — /api/sync/blob.
///
/// Никакой хитрости здесь нет и не нужно: обычный GET, обычный JSON.
/// Разобрать такой обмен можно браузером, если что-то пойдёт не так.
/// </summary>
public sealed class SyncClient
{
    /// <summary>Заголовок с общим паролем обмена.</summary>
    public const string KeyHeader = "X-Portal-Sync-Key";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly SyncOptions _options;

    public SyncClient(HttpClient http, IOptions<SyncOptions> options)
    {
        _http = http;
        _options = options.Value;

        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_options.TimeoutSeconds, 5, 600));
    }

    /// <summary>Забрать у соседа очередную пачку изменений.</summary>
    public async Task<SyncBatch> ChangesAsync(SyncPeer peer, long after, CancellationToken cancellationToken)
    {
        var url = $"{Root(peer)}/api/sync/changes?after={after}&take={_options.BatchSize}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation(KeyHeader, _options.Key);

        using var response = await _http.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        await EnsureOkAsync(response, peer, cancellationToken);

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);

        return await JsonSerializer.DeserializeAsync<SyncBatch>(stream, Json, cancellationToken)
               ?? new SyncBatch();
    }

    /// <summary>
    /// Забрать кусок файла.
    ///
    /// Кусками, а не целиком: канал между офисами узкий и рвётся,
    /// и оборванная передача должна стоить одного куска, а не всего файла.
    /// Возвращает прочитанное; пустой массив значит «файл кончился».
    /// </summary>
    public async Task<byte[]> BlobAsync(
        SyncPeer peer, string kind, Guid globalId, long offset, int length, CancellationToken cancellationToken)
    {
        var url = $"{Root(peer)}/api/sync/blob?kind={Uri.EscapeDataString(kind)}" +
                  $"&globalId={globalId:D}&offset={offset}&length={length}";

        using var request = new HttpRequestMessage(HttpMethod.Get, url);

        request.Headers.TryAddWithoutValidation(KeyHeader, _options.Key);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/octet-stream"));

        using var response = await _http.SendAsync(request, cancellationToken);

        await EnsureOkAsync(response, peer, cancellationToken);

        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    private static string Root(SyncPeer peer) => peer.BaseUrl.TrimEnd('/');

    /// <summary>
    /// Превращает неудачный ответ в понятную ошибку.
    ///
    /// «401» само по себе администратору ничего не скажет, а «не совпал
    /// пароль обмена» — скажет сразу. Ради этого стоит три лишние строки.
    /// </summary>
    private static async Task EnsureOkAsync(
        HttpResponseMessage response, SyncPeer peer, CancellationToken cancellationToken)
    {
        if (response.IsSuccessStatusCode)
        {
            return;
        }

        var reason = (int)response.StatusCode switch
        {
            401 or 403 =>
                "не совпал пароль обмена (Sync:Key). Он должен быть ОДИНАКОВЫМ во всех филиалах",
            404 =>
                "адрес не найден. Проверьте Sync:Peers:BaseUrl — он должен указывать на корень портала, "
                + "например http://ftp2.domen.pro",
            503 =>
                "синхронизация выключена на той стороне (Sync:Enabled) или там не задан пароль обмена",
            _ => $"ответ {(int)response.StatusCode}"
        };

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        throw new SyncException(
            $"Филиал «{peer.Name}» ({peer.BaseUrl}): {reason}."
            + (string.IsNullOrWhiteSpace(body) ? "" : $" Ответ: {Cut(body)}"));
    }

    private static string Cut(string text) =>
        text.Length <= 200 ? text.Trim() : text[..200].Trim() + "…";
}

/// <summary>Неполадка обмена с филиалом. Текст пишется так, чтобы его можно было показать администратору.</summary>
public sealed class SyncException : Exception
{
    public SyncException(string message) : base(message)
    {
    }
}
