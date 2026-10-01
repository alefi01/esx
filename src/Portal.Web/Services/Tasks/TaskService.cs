using System.Security.Claims;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Services.Tasks;

/// <summary>
/// Насколько горит задача. Считается по сроку и используется и в цвете
/// карточки, и в напоминании при входе — чтобы «красное» значило
/// в обоих местах ровно одно и то же.
/// </summary>
public enum TaskUrgency
{
    /// <summary>Срока нет или он далеко — больше пяти дней.</summary>
    Calm = 0,

    /// <summary>Пять дней и меньше.</summary>
    Soon = 1,

    /// <summary>Три дня и меньше.</summary>
    Close = 2,

    /// <summary>Завтра, сегодня или уже просрочено.</summary>
    Now = 3
}

/// <summary>Задача вместе с тем, что нужно знать о ней странице.</summary>
/// <param name="Task">Сама задача.</param>
/// <param name="Mine">Моя ли она — от этого зависит, что с ней можно делать.</param>
/// <param name="Overdue">Срок вышел, а задача не закрыта.</param>
/// <param name="SharedWith">Кому она ещё видна, готовой строкой.</param>
/// <param name="Urgency">Насколько горит — см. TaskUrgency.</param>
public sealed record TaskCard(
    UserTask Task, bool Mine, bool Overdue, string SharedWith, TaskUrgency Urgency);

/// <summary>
/// Задачи: свои и те, что показали коллеги.
///
/// Правило доступа всюду одно: ПРАВИТЬ задачу может только её хозяин,
/// ВИДЕТЬ — он и те, кому он её показал. Поэтому здесь нет ни одного
/// метода, который менял бы чужую задачу, — проверка стоит в каждом,
/// а не в вызывающем коде: страница может забыть, служба не забудет.
/// </summary>
public sealed class TaskService
{
    private readonly PortalDbContext _db;
    private readonly TimeProvider _time;

    public TaskService(PortalDbContext db, TimeProvider time)
    {
        _db = db;
        _time = time;
    }

    private DateTime Now => _time.GetUtcNow().UtcDateTime;

    /// <summary>
    /// Что показывать в списке.
    ///
    /// Три состояния — три отдельных списка, а не фильтр по одному:
    /// «в работе» смотрят каждый день, «выполненные» — изредка,
    /// «архив» — почти никогда. Складывать их в один список и давать
    /// галочку «показать выполненные» значит заставлять снимать её
    /// при каждом заходе.
    /// </summary>
    public async Task<IReadOnlyList<TaskCard>> ListAsync(
        string userName, string view, CancellationToken cancellationToken)
    {
        var shared = _db.TaskShares
            .Where(share => share.UserName.ToLower() == userName.ToLower())
            .Select(share => share.TaskId);

        var query = _db.Tasks
            .Include(t => t.Shares)
            .Where(t => t.OwnerUserName.ToLower() == userName.ToLower() || shared.Contains(t.Id));

        query = view switch
        {
            // В архиве лежит и выполненное, и брошенное — для архива важно
            // только то, что его убрали с глаз.
            "archive" => query.Where(t => t.ArchivedAt != null),
            "done" => query.Where(t => t.ArchivedAt == null && t.CompletedAt != null),
            _ => query.Where(t => t.ArchivedAt == null && t.CompletedAt == null)
        };

        var list = await query.ToListAsync(cancellationToken);

        var today = DateTime.Now.Date;

        return list
            // Сначала просроченное и то, у чего срок ближе; задачи без срока
            // идут после — они никуда не торопятся по определению.
            .OrderBy(t => t.DueOn is null)
            .ThenBy(t => t.DueOn)
            .ThenByDescending(t => t.Id)
            .Select(t => new TaskCard(
                t,
                string.Equals(t.OwnerUserName, userName, StringComparison.OrdinalIgnoreCase),
                t.CompletedAt is null && t.DueOn is { } due && due.Date < today,
                string.Join(", ", t.Shares.Select(share => share.DisplayName)),
                UrgencyOf(t, today)))
            .ToList();
    }

    /// <summary>
    /// Насколько горит задача.
    ///
    /// Выполненная не горит никогда, даже если срок давно прошёл: она
    /// сделана, и красный цвет на ней — ложная тревога.
    /// </summary>
    public static TaskUrgency UrgencyOf(UserTask task, DateTime today)
    {
        if (task.CompletedAt is not null || task.DueOn is not { } due)
        {
            return TaskUrgency.Calm;
        }

        var days = (due.Date - today.Date).Days;

        return days switch
        {
            <= 1 => TaskUrgency.Now,
            <= 3 => TaskUrgency.Close,
            <= 5 => TaskUrgency.Soon,
            _ => TaskUrgency.Calm
        };
    }

    /// <summary>
    /// О чём напомнить при входе: СВОИ несделанные задачи, до срока которых
    /// остался день или меньше.
    ///
    /// Только свои и только самые срочные. Напоминание, которое показывают
    /// на каждый вход, живёт ровно до тех пор, пока его читают: стоит
    /// добавить в него «через пять дней» и чужие задачи — и его начинают
    /// закрывать не глядя, вместе с тем, что горит по-настоящему.
    /// </summary>
    public async Task<IReadOnlyList<UserTask>> UrgentAsync(
        string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return [];
        }

        // Границу считаем на сервере и сравниваем с датой: DueOn — колонка
        // date, и сравнение с ней идёт по дате, без часовых поясов.
        var edge = DateTime.Now.Date.AddDays(1);

        var list = await _db.Tasks
            .Where(t => t.OwnerUserName.ToLower() == userName.ToLower()
                        && t.CompletedAt == null
                        && t.ArchivedAt == null
                        && t.DueOn != null
                        && t.DueOn <= edge)
            .OrderBy(t => t.DueOn)
            .Take(5)
            .ToListAsync(cancellationToken);

        return list;
    }

    /// <summary>Сколько задач в работе — число рядом с пунктом меню.</summary>
    public async Task<int> ActiveCountAsync(string userName, CancellationToken cancellationToken)
    {
        if (string.IsNullOrEmpty(userName))
        {
            return 0;
        }

        return await _db.Tasks.CountAsync(
            t => t.OwnerUserName.ToLower() == userName.ToLower()
                 && t.ArchivedAt == null
                 && t.CompletedAt == null,
            cancellationToken);
    }

    public async Task<UserTask> CreateAsync(
        ClaimsPrincipal user, string title, string? notes, DateTime? dueOn,
        CancellationToken cancellationToken)
    {
        var task = new UserTask
        {
            OwnerUserName = user.Identity?.Name ?? "",
            OwnerDisplayName = user.FindFirstValue(ClaimTypes.GivenName) ?? user.Identity?.Name ?? "",
            Title = title.Trim(),
            Notes = string.IsNullOrWhiteSpace(notes) ? null : notes.Trim(),
            DueOn = dueOn?.Date,
            CreatedAt = Now
        };

        _db.Tasks.Add(task);

        await _db.SaveChangesAsync(cancellationToken);

        return task;
    }

    /// <summary>Задача для правки — только своя. Чужая не находится вовсе.</summary>
    public Task<UserTask?> OwnAsync(int id, string userName, CancellationToken cancellationToken) =>
        _db.Tasks.AsTracking()
            .Include(t => t.Shares)
            .FirstOrDefaultAsync(
                t => t.Id == id && t.OwnerUserName.ToLower() == userName.ToLower(),
                cancellationToken);

    /// <summary>Отметить выполненной или снять отметку.</summary>
    public async Task<bool> ToggleDoneAsync(int id, string userName, CancellationToken cancellationToken)
    {
        var task = await OwnAsync(id, userName, cancellationToken);

        if (task is null)
        {
            return false;
        }

        task.CompletedAt = task.CompletedAt is null ? Now : null;

        // Снятая отметка возвращает задачу из архива: иначе она исчезла бы
        // из всех списков разом — и из выполненных, и из работы.
        if (task.CompletedAt is null)
        {
            task.ArchivedAt = null;
        }

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>Убрать в архив или достать обратно.</summary>
    public async Task<bool> ToggleArchivedAsync(int id, string userName, CancellationToken cancellationToken)
    {
        var task = await OwnAsync(id, userName, cancellationToken);

        if (task is null)
        {
            return false;
        }

        task.ArchivedAt = task.ArchivedAt is null ? Now : null;

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    public async Task<bool> DeleteAsync(int id, string userName, CancellationToken cancellationToken)
    {
        var task = await OwnAsync(id, userName, cancellationToken);

        if (task is null)
        {
            return false;
        }

        _db.Tasks.Remove(task);

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }

    /// <summary>
    /// Показать задачу коллеге.
    ///
    /// Себе показать нельзя — задача и так своя; добавление себя в список
    /// выглядело бы как работающее действие и ничего не меняло.
    /// </summary>
    public async Task<string?> ShareAsync(
        int id, string userName, string withUserName, string withDisplayName,
        CancellationToken cancellationToken)
    {
        var task = await OwnAsync(id, userName, cancellationToken);

        if (task is null)
        {
            return "Задача не найдена.";
        }

        var login = (withUserName ?? "").Trim();

        if (login.Length == 0)
        {
            return "Выберите, кому показать задачу.";
        }

        if (string.Equals(login, userName, StringComparison.OrdinalIgnoreCase))
        {
            return "Эта задача и так ваша.";
        }

        if (task.Shares.Any(share => string.Equals(share.UserName, login, StringComparison.OrdinalIgnoreCase)))
        {
            return null;
        }

        task.Shares.Add(new UserTaskShare
        {
            UserName = login,
            DisplayName = string.IsNullOrWhiteSpace(withDisplayName) ? login : withDisplayName.Trim()
        });

        await _db.SaveChangesAsync(cancellationToken);

        return null;
    }

    public async Task<bool> UnshareAsync(
        int id, string userName, string withUserName, CancellationToken cancellationToken)
    {
        var task = await OwnAsync(id, userName, cancellationToken);

        var share = task?.Shares.FirstOrDefault(
            s => string.Equals(s.UserName, withUserName, StringComparison.OrdinalIgnoreCase));

        if (share is null)
        {
            return false;
        }

        _db.TaskShares.Remove(share);

        await _db.SaveChangesAsync(cancellationToken);

        return true;
    }
}
