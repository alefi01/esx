namespace Portal.Web.Security;

/// <summary>Одна неудачная по части доступа попытка.</summary>
/// <param name="At">Когда, в местном времени.</param>
/// <param name="Path">Куда обращались.</param>
/// <param name="Reason">Почему не пустили — самое главное поле.</param>
/// <param name="CookiePresent">Пришла ли вообще cookie входа.</param>
/// <param name="CookieLength">Её длина: пустая или обрезанная видна сразу.</param>
/// <param name="RemoteIp">С какого адреса — по нему понятен офис.</param>
/// <param name="UserAgent">Какой браузер.</param>
public sealed record AuthFailure(
    DateTime At,
    string Path,
    string Reason,
    bool CookiePresent,
    int CookieLength,
    string RemoteIp,
    string UserAgent);

/// <summary>
/// Последние отказы в доступе — для разбора жалоб вида «не открывается».
///
/// ЗАЧЕМ ЭТО НУЖНО
///
/// Когда браузер показывает «401 Unauthorized», по одному этому числу
/// понять ничего нельзя: то ли cookie не дошла, то ли протухла, то ли
/// её не удалось расшифровать, то ли запрос вообще перехватил IIS.
/// Все четыре случая лечатся по-разному, а выглядят одинаково.
///
/// Поэтому портал запоминает последние отказы вместе с причиной, и их
/// видно на странице «Диагностика». Человек из другого офиса повторяет
/// действие, администратор открывает страницу и сразу видит, что произошло.
///
/// Список живёт в памяти и теряется при перезапуске — этого достаточно:
/// разбираются с такими жалобами по горячим следам. В базу не пишем,
/// чтобы отказ в доступе не зависел от доступности базы.
/// </summary>
public sealed class AuthDiagnostics
{
    /// <summary>Сколько последних случаев помним.</summary>
    private const int Capacity = 50;

    private readonly Lock _lock = new();
    private readonly Queue<AuthFailure> _failures = new();

    private long _total;

    /// <summary>Сколько всего отказов с момента запуска портала.</summary>
    public long Total
    {
        get
        {
            lock (_lock)
            {
                return _total;
            }
        }
    }

    public void Record(AuthFailure failure)
    {
        lock (_lock)
        {
            _total++;
            _failures.Enqueue(failure);

            while (_failures.Count > Capacity)
            {
                _failures.Dequeue();
            }
        }
    }

    /// <summary>Последние отказы, свежие сверху.</summary>
    public IReadOnlyList<AuthFailure> Recent()
    {
        lock (_lock)
        {
            return _failures.Reverse().ToList();
        }
    }
}
