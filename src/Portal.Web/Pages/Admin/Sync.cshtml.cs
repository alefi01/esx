using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;
using Portal.Web.Services.Sync;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Синхронизация между филиалами: что настроено, как прошёл последний обмен
/// и что делать, если он не прошёл.
///
/// Раздел виден только администраторам портала — как и весь /Admin.
///
/// Настройка разделена надвое НАМЕРЕННО:
///   * адреса филиалов и пароль обмена лежат в appsettings.json — это
///     устройство сети, менять его через веб-страницу опаснее, чем удобнее;
///   * промежуток между обменами и пауза лежат здесь — это то, что
///     подкручивают по ходу дела, и лезть ради этого на сервер незачем.
/// </summary>
public class SyncModel : PageModel
{
    private readonly SyncOptions _options;
    private readonly SyncSettings _settings;
    private readonly SyncRunner _runner;
    private readonly PortalDbContext _db;
    private readonly ILogger<SyncModel> _logger;

    public SyncModel(
        IOptions<SyncOptions> options,
        SyncSettings settings,
        SyncRunner runner,
        PortalDbContext db,
        ILogger<SyncModel> logger)
    {
        _options = options.Value;
        _settings = settings;
        _runner = runner;
        _db = db;
        _logger = logger;
    }

    /// <summary>Состояние обмена с одним филиалом — строка таблицы на странице.</summary>
    /// <param name="Configured">Задан ли для него адрес.</param>
    public sealed record PeerRow(
        string Code,
        string Name,
        string BaseUrl,
        bool Configured,
        long LastReceivedId,
        DateTime? LastSuccessAt,
        string? LastError);

    public SyncOptions Options => _options;

    public IReadOnlyList<PeerRow> Peers { get; private set; } = [];

    /// <summary>Сколько записей накопилось в журнале изменений.</summary>
    public int OutboxCount { get; private set; }

    /// <summary>Номер последней записи журнала — его соседи и «догоняют».</summary>
    public long OutboxLastId { get; private set; }

    [BindProperty]
    public int IntervalMinutes { get; set; }

    public bool Paused { get; private set; }

    /// <summary>Что не так с настройкой. Пусто — всё в порядке.</summary>
    public List<string> Problems { get; } = [];

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public string? DatabaseError { get; private set; }

    public Task OnGetAsync(CancellationToken cancellationToken) => LoadAsync(cancellationToken);

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        CheckConfiguration();

        try
        {
            IntervalMinutes = await _settings.IntervalMinutesAsync(cancellationToken);
            Paused = await _settings.PausedAsync(cancellationToken);

            var states = await _db.SyncPeers.ToListAsync(cancellationToken);

            Peers = _options.Peers
                .Select(peer =>
                {
                    var state = states.FirstOrDefault(s => s.PeerCode == peer.Code);

                    return new PeerRow(
                        peer.Code,
                        string.IsNullOrWhiteSpace(peer.Name) ? peer.Code : peer.Name,
                        peer.BaseUrl,
                        !string.IsNullOrWhiteSpace(peer.BaseUrl),
                        state?.LastReceivedId ?? 0,
                        state?.LastSuccessAt,
                        state?.LastError);
                })
                .ToList();

            OutboxCount = await _db.SyncOutbox.CountAsync(cancellationToken);

            OutboxLastId = await _db.SyncOutbox
                .OrderByDescending(e => e.Id)
                .Select(e => e.Id)
                .FirstOrDefaultAsync(cancellationToken);
        }
        catch (Exception ex)
        {
            // База может быть недоступна — страница всё равно должна
            // открыться и показать хотя бы настройку из файла.
            _logger.LogError(ex, "Не удалось прочитать состояние синхронизации.");

            DatabaseError = ex.Message;
            IntervalMinutes = _options.DefaultIntervalMinutes;
        }
    }

    /// <summary>
    /// Проверки настройки, которые дешевле сделать заранее, чем разбирать
    /// потом по журналу. Каждая — про ошибку, которую легко допустить.
    /// </summary>
    private void CheckConfiguration()
    {
        if (!_options.Enabled)
        {
            Problems.Add(
                "Синхронизация выключена: Sync:Enabled = false в appsettings.json. "
                + "Портал работает сам по себе, обмена с филиалами нет.");

            return;
        }

        if (string.IsNullOrWhiteSpace(_options.BranchCode))
        {
            Problems.Add(
                "Не задан код филиала (Sync:BranchCode). Без него изменения этого сервера "
                + "невозможно отличить от чужих, и обмен не начнётся.");
        }

        if (string.IsNullOrWhiteSpace(_options.Key))
        {
            Problems.Add(
                "Не задан пароль обмена (Sync:Key). Пока он пуст, портал не отдаёт "
                + "изменения соседям и не принимает их: отдавать содержимое портала "
                + "без проверки нельзя.");
        }

        if (_options.Peers.Count == 0)
        {
            Problems.Add("Не перечислены соседние филиалы (Sync:Peers). Спрашивать не у кого.");
        }

        foreach (var peer in _options.Peers)
        {
            if (string.IsNullOrWhiteSpace(peer.BaseUrl))
            {
                Problems.Add($"У филиала «{peer.Code}» не указан адрес (BaseUrl).");
            }

            if (string.Equals(peer.Code, _options.BranchCode, StringComparison.OrdinalIgnoreCase))
            {
                Problems.Add(
                    $"Филиал «{peer.Code}» указан сам на себя: его код совпадает с Sync:BranchCode. "
                    + "Такой сервер будет спрашивать изменения у самого себя.");
            }
        }
    }

    /// <summary>Сохранить промежуток между обменами.</summary>
    public async Task<IActionResult> OnPostIntervalAsync(CancellationToken cancellationToken)
    {
        if (IntervalMinutes is < 1 or > 1440)
        {
            ErrorMessage = "Промежуток должен быть от 1 минуты до 1440 (сутки).";

            return RedirectToPage();
        }

        await _settings.SetIntervalAsync(IntervalMinutes, User.Identity?.Name ?? "", cancellationToken);

        StatusMessage = $"Теперь портал будет спрашивать соседей раз в {IntervalMinutes} мин.";

        return RedirectToPage();
    }

    /// <summary>Приостановить или возобновить обмен.</summary>
    public async Task<IActionResult> OnPostPauseAsync(bool paused, CancellationToken cancellationToken)
    {
        await _settings.SetPausedAsync(paused, User.Identity?.Name ?? "", cancellationToken);

        StatusMessage = paused
            ? "Обмен приостановлен. Изменения продолжают копиться в журнале и разойдутся, когда вы его возобновите."
            : "Обмен возобновлён.";

        return RedirectToPage();
    }

    /// <summary>
    /// Запустить обмен прямо сейчас, не дожидаясь расписания.
    ///
    /// Нужно ровно для одного: проверить, что настройка верна. Без этой
    /// кнопки проверка означала бы ожидание следующего срабатывания.
    /// </summary>
    public async Task<IActionResult> OnPostRunAsync(CancellationToken cancellationToken)
    {
        if (!_options.Enabled)
        {
            ErrorMessage = "Синхронизация выключена в настройках (Sync:Enabled).";

            return RedirectToPage();
        }

        try
        {
            var reports = await _runner.RunAsync(cancellationToken);

            if (reports.Count == 0)
            {
                ErrorMessage = "Соседние филиалы не настроены — спрашивать не у кого.";

                return RedirectToPage();
            }

            var failed = reports.Where(r => !r.Ok).ToList();

            if (failed.Count > 0)
            {
                ErrorMessage = string.Join(" ", failed.Select(r => r.Error));
            }
            else
            {
                var received = reports.Sum(r => r.Received);
                var blobs = reports.Sum(r => r.Blobs);

                StatusMessage = received == 0 && blobs == 0
                    ? "Обмен прошёл: нового у соседей нет."
                    : $"Обмен прошёл: принято изменений — {received}, файлов — {blobs}.";
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Обмен по кнопке завершился ошибкой.");

            ErrorMessage = $"Не удалось: {ex.Message}";
        }

        return RedirectToPage();
    }
}
