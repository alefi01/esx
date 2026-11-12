using Portal.Web.Services.Notifications;

namespace Portal.Web.Services.Tasks;

/// <summary>
/// Фоновая работа по задачам: заводит очередные экземпляры повторяющихся
/// задач и рассылает напоминания о приближении срока.
///
/// ПОЧЕМУ РАЗ В МИНУТУ, А НЕ РАЗ В ЧАС
///
/// Уборка хранилища ходит раз в несколько часов, и этого ей хватает:
/// лишние полдня в корзине никому не мешают. Здесь иначе — напоминание
/// «через полчаса срок», пришедшее через час после срока, бессмысленно.
/// Работы при этом почти нет: два коротких запроса, и в обычную минуту
/// оба возвращают пустой список.
///
/// Задача выполняется в том же процессе, что и сайт. Веб-сервер один,
/// поэтому это допустимо; появится второй — две копии начнут заводить
/// экземпляры одновременно, и это придётся разводить.
/// </summary>
public sealed class TaskSchedulerService : BackgroundService
{
    /// <summary>Как часто просыпаемся.</summary>
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(1);

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly NotificationHub _hub;
    private readonly ILogger<TaskSchedulerService> _logger;

    public TaskSchedulerService(
        IServiceScopeFactory scopeFactory,
        NotificationHub hub,
        ILogger<TaskSchedulerService> logger)
    {
        _scopeFactory = scopeFactory;
        _hub = hub;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Пауза после старта: пусть приложение поднимется и применит
        // миграции, а уж потом мы полезем в базу.
        //
        // Отмена здесь — обычное дело: приложение могли остановить
        // в первые же секунды (например, занят порт). Молча выходим,
        // иначе необработанное исключение фоновой службы уронит весь
        // хост и настоящая причина потеряется в чужих стеках.
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(40), stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                // База могла быть недоступна. Это не повод останавливать
                // фоновую работу насовсем: через минуту попробуем снова.
                _logger.LogError(ex, "Не удалось выполнить обход задач.");
            }

            try
            {
                await Task.Delay(Interval, stoppingToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        using var scope = _scopeFactory.CreateScope();

        var tasks = scope.ServiceProvider.GetRequiredService<AssignedTaskService>();

        var created = await tasks.AdvanceRepeatsAsync(cancellationToken);

        if (created > 0)
        {
            _logger.LogInformation("Заведено экземпляров повторяющихся задач: {Count}.", created);
        }

        var reminders = await tasks.TakeDueRemindersAsync(cancellationToken);

        // Будим страницы тех, кому пора напомнить: дальше страница сама
        // спросит, что у неё нового, и покажет это звуком и окошком.
        // Разбирать здесь, кому что показать, незачем — это та же
        // проверка, только дважды.
        var people = reminders
            .Select(instance => instance.AssigneeUserName.Length > 0
                ? instance.AssigneeUserName
                : instance.Task!.AssigneeUserName)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (people.Count > 0)
        {
            _hub.Notify(people);
        }

        if (created > 0)
        {
            // Новые экземпляры — это новые задачи в списке у людей.
            _hub.NotifyAll();
        }
    }
}
