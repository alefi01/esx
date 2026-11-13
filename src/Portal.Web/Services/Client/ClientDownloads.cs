namespace Portal.Web.Services.Client;

/// <summary>
/// Раздача настольного виджета и того, что нужно для его установки.
///
/// ЗАЧЕМ ЭТО НА ПОРТАЛЕ
///
/// Программа стоит на рабочих ПК, а обновлять её ходьбой по кабинетам
/// нельзя. Поэтому единственное место, где лежит свежий файл, — портал:
/// отсюда человек его скачивает, отсюда же программа берёт обновления
/// и недостающий компонент Microsoft Edge WebView2 Runtime.
///
/// ЧТО КЛАДЁТ АДМИНИСТРАТОР
///
/// В папку App_Data\client рядом с порталом:
///   portal-widget.exe  — собранный виджет (обязательно);
///   version.txt        — номер версии в этом файле, например 1.0.1
///                        (без него обновление не предлагается);
///   MicrosoftEdgeWebView2RuntimeInstallerX64.exe — установщик
///                        компонента, на случай машин, где его нет.
///
/// Рантайм НЕ вложен в виджет: он весит сотни мегабайт, а нужен редко
/// (в Windows 11 он часть системы, на Windows 10 обычно уже стоит).
/// Поэтому установщик виджета забирает его отсюда и только если на
/// машине его действительно нет.
///
/// БЕЗ ВХОДА
///
/// Эти адреса открыты: программа скачивает рантайм и себя ДО того, как
/// человек где-либо вошёл, cookie портала у неё в этот момент нет.
/// Отдаётся при этом только то, что администратор сам сюда положил:
/// свой же виджет и установщик компонента Microsoft. Сеть заказчика
/// изолирована, снаружи этих адресов не видно.
/// </summary>
public static class ClientDownloads
{
    public const string WidgetFileName = "ftp-widget.exe";

    /// <summary>Папка, куда администратор кладёт файлы для рабочих ПК.</summary>
    public static string FolderOf(IWebHostEnvironment environment) =>
        Path.Combine(environment.ContentRootPath, "App_Data", "client");

    public static string WidgetPathOf(IWebHostEnvironment environment) =>
        Path.Combine(FolderOf(environment), WidgetFileName);

    /// <summary>Выложен ли виджет — для страницы настроек.</summary>
    public static bool WidgetReady(IWebHostEnvironment environment) =>
        File.Exists(WidgetPathOf(environment));

    /// <summary>Выложен ли установщик компонента WebView2.</summary>
    public static bool RuntimeReady(IWebHostEnvironment environment) =>
        RuntimePath(environment) is not null;

    /// <summary>Версия выложенного виджета или пустая строка.</summary>
    public static string VersionOf(IWebHostEnvironment environment)
    {
        var file = Path.Combine(FolderOf(environment), "version.txt");

        try
        {
            if (!File.Exists(file))
            {
                return "";
            }

            // Берём первую непустую строку: в такой файл легко попадает
            // перевод строки или пробел, и из-за него программа
            // обновлялась бы по кругу.
            var text = File.ReadAllLines(file)
                .Select(line => line.Trim())
                .FirstOrDefault(line => line.Length > 0) ?? "";

            return text.Length > 32 ? "" : text;
        }
        catch (IOException)
        {
            return "";
        }
    }

    /// <summary>
    /// Установщик рантайма. Имя файла у Microsoft менялось, поэтому
    /// принимаем и привычное, и любое похожее — администратору не нужно
    /// переименовывать скачанное.
    /// </summary>
    private static string? RuntimePath(IWebHostEnvironment environment)
    {
        var folder = FolderOf(environment);

        if (!Directory.Exists(folder))
        {
            return null;
        }

        var exact = Path.Combine(folder, "MicrosoftEdgeWebView2RuntimeInstallerX64.exe");

        if (File.Exists(exact))
        {
            return exact;
        }

        return Directory.EnumerateFiles(folder, "*.exe")
            .Where(path => Path.GetFileName(path).Contains("WebView2", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    public static void MapClientDownloads(this WebApplication app)
    {
        // ---- Номер выложенной версии ----
        //
        // Программа спрашивает его раз в несколько часов и качает файл
        // только при расхождении: гонять по сети десятки мегабайт
        // впустую незачем.
        app.MapGet("/client/version", (IWebHostEnvironment environment) =>
            Results.Json(new { Version = VersionOf(environment) })).AllowAnonymous();

        // ---- Файл для обновления ----
        app.MapGet("/client/" + WidgetFileName, (IWebHostEnvironment environment) =>
        {
            var path = WidgetPathOf(environment);

            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            return Results.File(path, "application/octet-stream", WidgetFileName, enableRangeProcessing: true);
        }).AllowAnonymous();

        // ---- Прежний адрес «установщика» ----
        //
        // Раньше здесь отдавался тот же виджет, но с дописанным в конец
        // адресом ЭТОГО портала: человек скачал и запустил — программа
        // уже знала, куда подключаться.
        //
        // Так больше нельзя. Дописанный хвост делал КАЖДУЮ скачанную
        // копию уникальной: у файла всякий раз другой размер и другая
        // контрольная сумма, и ни в один список известных и безопасных
        // программ он не попадал. Для антивируса это признак
        // самоизменяющейся программы, и Касперский такие файлы удаляет.
        //
        // Адрес портала теперь спрашивается в самом окне при первом
        // запуске — один раз, одним полем. Этот адрес оставлен, чтобы
        // старые ссылки и закладки не ломались: он просто отдаёт тот же
        // файл, что и /client/portal-widget.exe.
        app.MapGet("/client/setup.exe", (IWebHostEnvironment environment) =>
        {
            var path = WidgetPathOf(environment);

            return File.Exists(path)
                ? Results.File(path, "application/octet-stream", WidgetFileName, enableRangeProcessing: true)
                : Results.NotFound();
        }).AllowAnonymous();

        // ---- Недостающий компонент ----
        //
        // Отдаём то, что положил администратор. Если не положил — говорим
        // это словами: установщик на машине человека покажет понятное
        // сообщение, а не «ошибка 404».
        app.MapGet("/client/webview2", (IWebHostEnvironment environment) =>
        {
            var path = RuntimePath(environment);

            if (path is null)
            {
                return Results.Text(
                    "Установщик Microsoft Edge WebView2 Runtime не выложен на портале. "
                    + "Положите файл MicrosoftEdgeWebView2RuntimeInstallerX64.exe в папку App_Data\\client.",
                    "text/plain; charset=utf-8",
                    statusCode: StatusCodes.Status404NotFound);
            }

            return Results.File(
                path,
                "application/octet-stream",
                Path.GetFileName(path),
                enableRangeProcessing: true);
        }).AllowAnonymous();
    }
}
