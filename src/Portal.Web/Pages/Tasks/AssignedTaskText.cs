using Portal.Web.Data;

namespace Portal.Web.Pages.Tasks;

/// <summary>
/// Подписи состояний задачи — одним местом на все страницы.
///
/// Вынесено специально: одно и то же состояние видят и исполнитель,
/// и тот, кто поставил. Напиши подписи в двух разметках — и однажды
/// на одной странице будет «Сдано на проверку», а на другой «Отправлено»,
/// и люди будут выяснять, в чём разница. Разницы нет.
/// </summary>
public static class AssignedTaskText
{
    public static string StateFor(TaskState state) => state switch
    {
        TaskState.Open => "Открыта",
        TaskState.Submitted => "Сдано на проверку",
        TaskState.Accepted => "Принято",
        TaskState.Returned => "Возвращено",
        TaskState.Done => "Выполнено",
        TaskState.Cancelled => "Снято",
        TaskState.Missed => "Не сдано",
        _ => ""
    };

    /// <summary>
    /// Слово состояния для КОНКРЕТНОГО экземпляра.
    ///
    /// Открытая задача бывает двух видов: только что поставленная
    /// и взятая в работу. Для человека это разные вещи, хотя состояние
    /// у них одно: по первой он ещё ничего не решил, по второй уже
    /// взялся.
    /// </summary>
    public static string StateFor(AssignedTaskInstance instance) =>
        instance is { State: TaskState.Open, TakenAt: null }
            ? "Новая"
            : instance.State == TaskState.Open && instance.TakenAt is not null
                ? "В работе"
                : StateFor(instance.State);

    /// <summary>Как часто повторяется — короткой строкой для карточки.</summary>
    public static string RepeatFor(AssignedTask task) => task.Repeat switch
    {
        TaskRepeat.Daily => "каждый день",
        TaskRepeat.Weekdays => "по дням недели: " + WeekDaysFor(task.WeekDays),
        TaskRepeat.Weekly => "еженедельно: " + WeekDaysFor(task.WeekDays),
        TaskRepeat.Monthly => $"ежемесячно, {task.MonthDay}-го",
        _ => "разовая"
    };

    private static readonly string[] Days = ["пн", "вт", "ср", "чт", "пт", "сб", "вс"];

    public static string WeekDaysFor(int mask)
    {
        var chosen = new List<string>();

        for (var i = 0; i < Days.Length; i++)
        {
            if ((mask & (1 << i)) != 0)
            {
                chosen.Add(Days[i]);
            }
        }

        return chosen.Count == 0 ? "не выбрано" : string.Join(", ", chosen);
    }
}
