namespace Portal.Web.Pages.Shared;

/// <summary>
/// Постраничник со списком номеров.
///
/// ЗАЧЕМ НОМЕРА, А НЕ ТОЛЬКО «ДАЛЬШЕ»
///
/// Со стрелками «новее / старее» до четвёртой страницы надо нажать
/// три раза, а до двадцатой — девятнадцать, и всё это время человек
/// не знает, сколько ещё осталось. Номера отвечают сразу: страниц
/// столько-то, вы на такой-то, прыгнуть можно куда угодно.
///
/// Номеров показывается не больше девяти: длинный ряд цифр сам
/// становится тем, в чём надо разбираться. Края всегда на месте —
/// первая и последняя страницы нужнее середины, а пропуск обозначен
/// многоточием.
/// </summary>
/// <param name="Page">Текущая страница, считая с единицы.</param>
/// <param name="TotalPages">Сколько всего страниц.</param>
/// <param name="PageKey">
/// Имя параметра в адресе: у журнала это «pageNumber», у переписок «page».
/// Своё у каждой страницы, и подгонять их друг под друга значило бы
/// ломать закладки.
/// </param>
/// <param name="Query">Остальные параметры адреса — фильтры. Без них переход
/// по страницам сбрасывал бы отбор, и человек начинал бы поиск заново.</param>
public sealed record Pager(
    int Page,
    int TotalPages,
    string PageKey,
    IReadOnlyDictionary<string, string?> Query)
{
    /// <summary>Сколько номеров показываем, не считая краёв и многоточий.</summary>
    private const int Window = 9;

    /// <summary>
    /// Номера страниц для показа. Ноль означает многоточие: там страницы
    /// пропущены.
    /// </summary>
    public IReadOnlyList<int> Numbers
    {
        get
        {
            if (TotalPages <= Window)
            {
                return Enumerable.Range(1, TotalPages).ToList();
            }

            var numbers = new List<int> { 1 };

            // Вокруг текущей — по две страницы с каждой стороны: столько
            // и нужно, чтобы шагнуть на соседнюю, не целясь в стрелку.
            var from = Math.Max(2, Page - 2);
            var to = Math.Min(TotalPages - 1, Page + 2);

            if (from > 2)
            {
                numbers.Add(0);
            }

            for (var i = from; i <= to; i++)
            {
                numbers.Add(i);
            }

            if (to < TotalPages - 1)
            {
                numbers.Add(0);
            }

            numbers.Add(TotalPages);

            return numbers;
        }
    }

    /// <summary>Значения для ссылки на страницу: фильтры плюс сам номер.</summary>
    public Dictionary<string, string?> RouteFor(int page)
    {
        var values = new Dictionary<string, string?>(Query)
        {
            [PageKey] = page.ToString()
        };

        return values;
    }
}
