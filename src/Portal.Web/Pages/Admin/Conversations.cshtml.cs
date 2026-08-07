using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.EntityFrameworkCore;
using Portal.Web.Data;

namespace Portal.Web.Pages.Admin;

/// <summary>
/// Все переписки портала — списком. Только для администраторов: доступ
/// ко всей папке /Admin закрыт политикой (см. Program.cs).
///
/// ЗАЧЕМ ЭТА СТРАНИЦА
///
/// Право читать любую переписку у администратора было и раньше
/// (см. ConversationService.CanRead), но воспользоваться им можно было,
/// только зная номер беседы в адресе. То есть право есть, а способа
/// им воспользоваться нет — обычная ситуация, когда проверка написана
/// раньше, чем интерфейс к ней.
///
/// ЧТО ЗДЕСЬ ПОКАЗЫВАЕТСЯ, А ЧТО НЕТ
///
/// Список бесед, участники, число сообщений и время последнего. САМИ
/// СООБЩЕНИЯ ЗДЕСЬ НЕ ПОКАЗЫВАЮТСЯ и в поиске не участвуют: страница
/// отвечает на вопрос «кто с кем переписывается», а чтобы прочитать
/// переписку, нужно осознанно её открыть — и это действие попадает
/// в журнал (см. Messages/Index).
/// </summary>
public class ConversationsModel : PageModel
{
    /// <summary>Сколько бесед на странице.</summary>
    private const int PageSize = 40;

    private readonly PortalDbContext _db;

    public ConversationsModel(PortalDbContext db)
    {
        _db = db;
    }

    /// <summary>Одна строка списка.</summary>
    /// <param name="Id">Номер беседы — по нему открывается переписка.</param>
    /// <param name="Title">Название группы либо перечисление собеседников.</param>
    /// <param name="IsGroup">Группа или переписка двоих.</param>
    /// <param name="Participants">Кто участвует, готовой строкой.</param>
    /// <param name="Messages">Сколько всего сообщений, включая удалённые.</param>
    /// <param name="Deleted">Сколько из них удалено.</param>
    /// <param name="LastMessageAt">Когда писали в последний раз.</param>
    public sealed record Row(
        int Id,
        string Title,
        bool IsGroup,
        string Participants,
        int Messages,
        int Deleted,
        DateTime LastMessageAt);

    public IReadOnlyList<Row> Rows { get; private set; } = [];

    public int TotalPages { get; private set; } = 1;

    public int TotalCount { get; private set; }

    /// <summary>Фильтр по участнику: логин или имя, часть строки.</summary>
    [BindProperty(SupportsGet = true, Name = "who")]
    public string? Who { get; set; }

    /// <summary>Фильтр по виду: «group», «pair», пусто — все.</summary>
    [BindProperty(SupportsGet = true, Name = "kind")]
    public string? Kind { get; set; }

    [BindProperty(SupportsGet = true, Name = "page")]
    public int PageNumber { get; set; } = 1;

    public string? DatabaseError { get; private set; }

    public async Task OnGetAsync(CancellationToken cancellationToken)
    {
        try
        {
            await LoadAsync(cancellationToken);
        }
        catch (Exception)
        {
            // Та же логика, что на остальных страницах администратора:
            // недоступная база не должна показывать страницу ошибки,
            // из которой не видно, что именно случилось.
            DatabaseError = "Не удалось получить список переписок: база данных не отвечает.";
        }
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        var query = _db.Conversations.AsQueryable();

        if (Kind == "group")
        {
            query = query.Where(c => c.IsGroup);
        }
        else if (Kind == "pair")
        {
            query = query.Where(c => !c.IsGroup);
        }

        if (!string.IsNullOrWhiteSpace(Who))
        {
            var needle = Who.Trim().ToLower();

            // Ищем и по логину, и по имени: администратор помнит человека
            // то так, то так, а в списке участников хранится и то и другое.
            query = query.Where(c => c.Participants.Any(p =>
                p.UserName.ToLower().Contains(needle) || p.DisplayName.ToLower().Contains(needle)));
        }

        TotalCount = await query.CountAsync(cancellationToken);
        TotalPages = Math.Max(1, (int)Math.Ceiling(TotalCount / (double)PageSize));
        PageNumber = Math.Clamp(PageNumber, 1, TotalPages);

        var page = await query
            .OrderByDescending(c => c.LastMessageAt)
            .Skip((PageNumber - 1) * PageSize)
            .Take(PageSize)
            .Select(c => new
            {
                c.Id,
                c.IsGroup,
                c.Title,
                c.LastMessageAt,
                Participants = c.Participants
                    .OrderByDescending(p => p.IsOwner)
                    .Select(p => new { p.DisplayName, p.UserName })
                    .ToList(),

                // Считает база, а не мы в памяти: тянуть сюда все сообщения
                // ради двух чисел — это выгрести на страницу всю переписку.
                Messages = c.Messages.Count,
                Deleted = c.Messages.Count(m => m.DeletedAt != null)
            })
            .ToListAsync(cancellationToken);

        Rows = page.Select(c => new Row(
            c.Id,
            c.IsGroup && !string.IsNullOrWhiteSpace(c.Title)
                ? c.Title
                : string.Join(" — ", c.Participants.Select(p => Name(p.DisplayName, p.UserName))),
            c.IsGroup,
            string.Join(", ", c.Participants.Select(p => Name(p.DisplayName, p.UserName) + " (" + p.UserName + ")")),
            c.Messages,
            c.Deleted,
            c.LastMessageAt)).ToList();
    }

    /// <summary>
    /// Имя участника. Имя копируется в беседу при добавлении, но у старых
    /// записей его может не быть — тогда показываем логин, а не пустоту.
    /// </summary>
    private static string Name(string displayName, string userName) =>
        string.IsNullOrWhiteSpace(displayName) ? userName : displayName;
}
