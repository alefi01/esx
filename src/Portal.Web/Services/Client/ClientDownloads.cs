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
    public const string WidgetFileName = "portal-widget.exe";

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

        // ---- Файл для установки ----
        //
        // Тот же виджет, но с дописанным в конец адресом ЭТОГО портала:
        // человек скачал и запустил — программа уже знает, куда
        // подключаться, и спрашивать адрес сервера не нужно.
        // Для Windows такой хвост невидим, запуску он не мешает.
        app.MapGet("/client/setup.exe", async (
            IWebHostEnvironment environment,
            HttpContext http,
            CancellationToken cancellationToken) =>
        {
            var path = WidgetPathOf(environment);

            if (!File.Exists(path))
            {
                return Results.NotFound();
            }

            var address = http.Request.Scheme + "://" + http.Request.Host.Value;
            var tail = "\n[PORTAL-ADDRESS]" + address;

            http.Response.ContentType = "application/octet-stream";
            http.Response.Headers.ContentDisposition = "attachment; filename=\"portal-widget-setup.exe\"";

            // Длину сообщаем заранее, чтобы в браузере была видна
            // полоса загрузки, а не «неизвестно сколько осталось».
            http.Response.ContentLength = new FileInfo(path).Length + tail.Length;

            await using (var file = File.OpenRead(path))
            {
                await file.CopyToAsync(http.Response.Body, cancellationToken);
            }

            await http.Response.WriteAsync(tail, cancellationToken);

            return Results.Empty;
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
