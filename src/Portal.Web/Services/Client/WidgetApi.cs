using Portal.Web.Services.Tasks;

namespace Portal.Web.Services.Client;

/// <summary>
/// Данные для настольного виджета — то, чего нет у колокольчика.
///
/// ЗАЧЕМ ОТДЕЛЬНЫЙ АДРЕС
///
/// Виджет устроен как окно мессенджера: вкладки «Всё», «Чат»,
/// «Объявления», «Задачи». Первые три собираются из обычного
/// /api/notifications — того же, которым живёт колокольчик в портале.
/// А вкладка «Задачи» показывает не «что нового», а что человеку
/// делать: там и задачи без единого нового события, о которых
/// колокольчик молчит и молчать должен.
///
/// Поэтому один дополнительный адрес, а не раздувание общего: иначе
/// каждый опрос колокольчика в браузере таскал бы список задач,
/// который странице портала не нужен — у неё есть своя страница задач.
/// </summary>
public static class WidgetApi
{
    /// <summary>Строка списка задач в виджете.</summary>
    /// <param name="Kind">«head» — от начальника, «own» — своя заметка.</param>
    /// <param name="Id">Номер — чтобы виджет отличал строки друг от друга.</param>
    /// <param name="Title">Что сделать.</param>
    /// <param name="Due">Срок по-человечески: «сегодня», «12 ноября».</param>
    /// <param name="Late">Срок прошёл.</param>
    /// <param name="State">Состояние задачи от начальника. У своих пусто.</param>
    /// <param name="Important">Пометка «Важно».</param>
    /// <param name="Who">От кого задача. У своих пусто.</param>
    /// <param name="Url">Куда вести по щелчку.</param>
    public sealed record TaskRow(
        string Kind, int Id, string Title, string Due, bool Late,
        string State, bool Important, string Who, string Url);

    public static void MapWidgetApi(this WebApplication app)
    {
        app.MapGet("/api/widget/tasks", async (
            AssignedTaskService assigned,
            TaskService tasks,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var user = http.User.Identity?.Name;

            if (string.IsNullOrEmpty(user))
            {
                return Results.Unauthorized();
            }

            // Ответ не кэшируем по той же причине, что и у колокольчика:
            // иначе виджет показывает список, которого на сервере уже нет.
            http.Response.Headers.CacheControl = "no-store, no-cache, must-revalidate";

            var fromHead = await assigned.ForAssigneeAsync(user, closed: false, cancellationToken);
            var own = await tasks.ListAsync(user, "active", cancellationToken);

            // Сначала от начальника, потом свои: за первое спрашивают,
            // второе человек ведёт сам.
            var rows = fromHead
                .Select(card => new TaskRow(
                    "head",
                    card.Instance.Id,
                    card.Task.Title,
                    Portal.Web.Pages.Tasks.IndexModel.DueText(
                        card.Instance.DueAt, card.Task.DueMinutes is not null),
                    card.Overdue,
                    Portal.Web.Pages.Tasks.AssignedTaskText.StateFor(card.Instance.State),
                    card.Task.Important,
                    card.Task.AuthorDisplayName,
                    "/Tasks"))
                .Concat(own
                    .Where(card => !card.Task.IsDone)
                    .Select(card => new TaskRow(
                        "own",
                        card.Task.Id,
                        card.Task.Title,
                        card.Task.DueOn is { } due
                            ? Portal.Web.Pages.Tasks.IndexModel.DueText(due)
                            : "",
                        card.Overdue,
                        "",
                        false,
                        card.Mine ? "" : card.Task.OwnerDisplayName,
                        "/Tasks")))
                .ToList();

            return Results.Ok(rows);
        });
    }
}
