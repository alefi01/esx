using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Game;

/// <summary>Строка рейтинга для показа на странице игры.</summary>
/// <param name="Place">Место, считая с единицы.</param>
/// <param name="DisplayName">Как подписан игрок.</param>
/// <param name="Points">Очков за всё время.</param>
/// <param name="Best">Лучшая партия.</param>
/// <param name="Games">Сколько партий сыграно.</param>
/// <param name="Mine">Это строка того, кто смотрит, — она подсвечивается.</param>
public sealed record RatingRow(
    int Place, string DisplayName, long Points, int Best, int Games, bool Mine);

/// <summary>
/// Рейтинг игры «Разбор завала»: сложение очков и чтение таблицы.
///
/// ПОЧЕМУ ОТДЕЛЬНАЯ СЛУЖБА, А НЕ ПАРА ЗАПРОСОВ В ТОЧКАХ ДОСТУПА
///
/// Правил здесь немного, но они неочевидны — сколько очков считать
/// допустимым, что делать с первой партией, как подписывать того, кто
/// в каталоге уже не значится. Писать их прямо в обработчике адреса
/// значило бы повторить их во втором обработчике и однажды разойтись.
/// </summary>
public sealed class GameRating
{
    /// <summary>
    /// Больше этого за одну партию не принимаем.
    ///
    /// Не «защита от жульничества» — её здесь нет и быть не может: очки
    /// считает браузер, и подделать их несложно. Это защита от чисел,
    /// которые ломают таблицу: вписанный от руки миллиард навсегда
    /// занял бы первое место и сделал рейтинг бессмысленным. Живой счёт
    /// в этой игре — десятки, редко сотня.
    /// </summary>
    public const int MaxPointsPerGame = 5000;

    /// <summary>Сколько строк показывать в таблице.</summary>
    public const int TopSize = 20;

    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public GameRating(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    /// <summary>
    /// Прибавляет очки сыгранной партии и возвращает новую сумму.
    ///
    /// Партия с нулём очков тоже считается: «сыграно партий» — часть
    /// рейтинга, и прятать неудачные попытки незачем.
    /// </summary>
    public async Task<long> AddAsync(
        string userName, string displayName, int points, CancellationToken cancellationToken)
    {
        var earned = Math.Clamp(points, 0, MaxPointsPerGame);

        var row = await _db.GameScores.AsTracking()
            .FirstOrDefaultAsync(score => score.UserName.ToLower() == userName.ToLower(), cancellationToken);

        if (row is null)
        {
            row = new GameScore { UserName = userName };
            _db.GameScores.Add(row);
        }

        row.Points += earned;
        row.Games += 1;
        row.Best = Math.Max(row.Best, earned);
        row.UpdatedAt = _time.GetUtcNow().UtcDateTime;

        // Имя обновляем при каждой партии: человек мог сменить фамилию,
        // и в таблице должна стоять нынешняя.
        if (!string.IsNullOrWhiteSpace(displayName))
        {
            row.DisplayName = displayName;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return row.Points;
    }

    /// <summary>
    /// Таблица лучших. Тот, кто смотрит, получает свою строку отмеченной —
    /// и, если в первые двадцать он не попал, дополнительной строкой ниже
    /// (см. MineAsync).
    /// </summary>
    public async Task<IReadOnlyList<RatingRow>> TopAsync(
        string userName, CancellationToken cancellationToken)
    {
        var rows = await _db.GameScores
            .OrderByDescending(score => score.Points)
            .ThenBy(score => score.UpdatedAt)
            .Take(TopSize)
            .Select(score => new
            {
                score.UserName,
                score.DisplayName,
                score.Points,
                score.Best,
                score.Games
            })
            .ToListAsync(cancellationToken);

        return rows
            .Select((score, index) => new RatingRow(
                index + 1,
                string.IsNullOrWhiteSpace(score.DisplayName) ? score.UserName : score.DisplayName,
                score.Points,
                score.Best,
                score.Games,
                string.Equals(score.UserName, userName, StringComparison.OrdinalIgnoreCase)))
            .ToList();
    }

    /// <summary>
    /// Строка того, кто смотрит, вместе с его местом в общем списке.
    /// null — ни одной партии ещё не сыграно.
    ///
    /// Место считается по числу тех, у кого очков больше, а не выборкой
    /// всей таблицы в память: игроков может оказаться сколько угодно,
    /// а ответ нужен один.
    /// </summary>
    public async Task<RatingRow?> MineAsync(string userName, CancellationToken cancellationToken)
    {
        var row = await _db.GameScores
            .Where(score => score.UserName.ToLower() == userName.ToLower())
            .Select(score => new { score.DisplayName, score.Points, score.Best, score.Games })
            .FirstOrDefaultAsync(cancellationToken);

        if (row is null)
        {
            return null;
        }

        var ahead = await _db.GameScores
            .CountAsync(score => score.Points > row.Points, cancellationToken);

        return new RatingRow(
            ahead + 1,
            string.IsNullOrWhiteSpace(row.DisplayName) ? userName : row.DisplayName,
            row.Points,
            row.Best,
            row.Games,
            true);
    }

    /// <summary>
    /// Сколько человек в рейтинге и сколько всего очков. Нужно настройкам
    /// портала: кнопка «обнулить» должна говорить, что именно она обнулит.
    /// </summary>
    public async Task<(int Players, long Points)> TotalsAsync(CancellationToken cancellationToken)
    {
        var players = await _db.GameScores.CountAsync(cancellationToken);

        if (players == 0)
        {
            return (0, 0);
        }

        var points = await _db.GameScores.SumAsync(score => score.Points, cancellationToken);

        return (players, points);
    }

    /// <summary>
    /// Обнуляет рейтинг целиком — кнопкой в настройках портала.
    ///
    /// Строки УДАЛЯЮТСЯ, а не обнуляются по очкам: иначе в таблице
    /// остались бы двадцать нулей, и «рейтинга нет» выглядело бы
    /// как «все сыграли по нулю». Возвращает, сколько строк убрано.
    /// </summary>
    public async Task<int> ResetAsync(CancellationToken cancellationToken) =>
        await _db.GameScores.ExecuteDeleteAsync(cancellationToken);
}
