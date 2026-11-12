using System.Text;

namespace Portal.Client;

/// <summary>
/// Запись о том, что происходило с программой.
///
/// ЗАЧЕМ
///
/// Программа работает на чужом компьютере, в сети без интернета,
/// и когда она не запускается, увидеть причину нечем: окна нет,
/// консоли нет, отладчика нет. Единственный способ узнать, что
/// случилось, — чтобы она сама записала это в файл, который человек
/// может прислать.
///
/// Журнал короткий и простой: строка на событие, без уровней
/// и настроек. Разбираться в нём будет администратор, а не программист,
/// и выглядеть он должен как записка, а не как дамп.
///
/// Файл не растёт бесконечно: при запуске старый журнал больше
/// четверти мегабайта просто стирается. Накопить что-то ценное
/// он всё равно не успевает — интересно только последнее.
/// </summary>
public static class Log
{
    private static readonly Lock Gate = new();

    public static string Path => System.IO.Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortalWidget", "widget.log");

    public static void Write(string message)
    {
        try
        {
            var folder = System.IO.Path.GetDirectoryName(Path)!;

            Directory.CreateDirectory(folder);

            lock (Gate)
            {
                if (File.Exists(Path) && new FileInfo(Path).Length > 262_144)
                {
                    File.Delete(Path);
                }

                File.AppendAllText(
                    Path,
                    DateTime.Now.ToString("dd.MM HH:mm:ss") + "  " + message + Environment.NewLine,
                    Encoding.UTF8);
            }
        }
        catch (Exception)
        {
            // Не записалось — значит не записалось. Падать из-за журнала
            // программа не должна: он здесь, чтобы помогать, а не мешать.
        }
    }

    /// <summary>Записать поломку целиком — со всеми вложенными причинами.</summary>
    public static void Fail(string where, Exception error)
    {
        var text = new StringBuilder();

        text.Append("ОШИБКА ").Append(where).Append(": ");

        for (var e = error; e is not null; e = e.InnerException)
        {
            text.Append(e.GetType().Name).Append(" — ").Append(e.Message);

            if (e.InnerException is not null)
            {
                text.Append(" ⇐ ");
            }
        }

        text.AppendLine();
        text.Append(error.StackTrace);

        Write(text.ToString());
    }
}
