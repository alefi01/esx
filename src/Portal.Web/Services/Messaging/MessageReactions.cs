namespace Portal.Web.Services.Messaging;

/// <summary>Один вид реакции: код, что показывать и как назвать в подсказке.</summary>
/// <param name="Kind">Код, который хранится в базе и приходит от браузера.</param>
/// <param name="Glyph">Знак, которым реакция показывается. У надписей пусто.</param>
/// <param name="Caption">
/// Надпись — для реакций без знака («Так точно»). У смайлов пусто.
/// </param>
/// <param name="Title">Название для подсказки при наведении.</param>
public sealed record ReactionKind(string Kind, string Glyph, string Caption, string Title);

/// <summary>
/// Закрытый набор реакций портала.
///
/// ПОЧЕМУ НАБОР, А НЕ ЛЮБОЙ СМАЙЛ
///
/// Выбор из тысячи знаков означает, что под сообщением копится мозаика,
/// в которой уже не видно, согласились с ним или нет. Девяти хватает:
/// пять общечеловеческих и четыре служебных ответа, которыми здесь
/// и отвечают на поручение.
///
/// Набор перечислен ОДНИМ списком и проверяется на сервере: иначе
/// в базу попадал бы любой текст, присланный браузером, а в переписке —
/// подписи на все случаи жизни.
///
/// Порядок списка — это порядок кнопок в окошке выбора и порядок
/// значков под сообщением. Сначала короткие знаки, потом надписи:
/// надпись шире, и вперемешку они бы прыгали.
/// </summary>
public static class MessageReactions
{
    public static readonly IReadOnlyList<ReactionKind> All =
    [
        new("smile", "\U0001F642", "", "Улыбка"),
        new("sad", "\U0001F641", "", "Грустно"),
        new("laugh", "\U0001F606", "", "Смешно"),
        new("serious", "\U0001F610", "", "Серьёзно"),
        new("plus", "➕", "", "Плюс"),
        new("yes-sir", "", "Так точно", "Так точно"),
        new("aye", "", "Есть", "Есть"),
        new("no-sir", "", "Никак нет", "Никак нет"),
        new("serve", "", "Служу России", "Служу России")
    ];

    /// <summary>Есть ли такой вид. Всё, что пришло от браузера, проходит здесь.</summary>
    public static bool IsKnown(string? kind) =>
        !string.IsNullOrEmpty(kind) && All.Any(r => r.Kind == kind);

    public static ReactionKind? Find(string? kind) =>
        All.FirstOrDefault(r => r.Kind == kind);

    /// <summary>
    /// Порядок вида в наборе — по нему реакции под сообщением выстраиваются
    /// всегда одинаково, а не в случайном порядке их появления.
    /// </summary>
    public static int Order(string kind)
    {
        for (var i = 0; i < All.Count; i++)
        {
            if (All[i].Kind == kind)
            {
                return i;
            }
        }

        return All.Count;
    }
}
