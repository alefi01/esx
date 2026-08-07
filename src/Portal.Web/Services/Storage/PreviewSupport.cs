namespace Portal.Web.Services.Storage;

/// <summary>Чем портал умеет показать файл, не скачивая его.</summary>
public enum PreviewKind
{
    /// <summary>Предпросмотр невозможен — только скачивание.</summary>
    None = 0,

    /// <summary>Картинка: показывается тегом img.</summary>
    Image = 1,

    /// <summary>PDF: показывается встроенным просмотрщиком браузера.</summary>
    Pdf = 2,

    /// <summary>Обычный текст: показывается как есть.</summary>
    Text = 3,

    /// <summary>
    /// Документ Office (docx, xlsx, pptx). Портал разбирает его сам
    /// и показывает содержимое разметкой — см. OfficeDocuments.
    /// </summary>
    Office = 4,

    /// <summary>Видео: проигрывается тегом video средствами браузера.</summary>
    Video = 5,

    /// <summary>Звук: проигрывается тегом audio средствами браузера.</summary>
    Audio = 6,

    /// <summary>
    /// Архив ZIP. Показывается не содержимое файлов, а список того,
    /// что внутри, — этого хватает, чтобы понять, тот ли это архив.
    /// </summary>
    Archive = 7
}

/// <summary>
/// Решает, можно ли показать файл прямо в браузере и с каким типом содержимого
/// его для этого отдавать.
///
/// ПОЧЕМУ СПИСОК, А НЕ «ОТДАДИМ ЧТО ЕСТЬ»
///
/// Предпросмотр — это отдача файла с заголовком «покажи, а не скачивай»
/// (Content-Disposition: inline). Если так отдать что попало, браузер может
/// выполнить содержимое как страницу: загруженный «документ» с разметкой
/// и скриптом внутри выполнится в адресе нашего портала, то есть с правами
/// того, кто его открыл.
///
/// Поэтому здесь белый список: показываем только то, что заведомо безопасно,
/// и тип содержимого берём ИЗ ЭТОГО СПИСКА, а не из того, что записано
/// в базе при загрузке.
///
/// Отдельно про SVG: это картинка, но внутри неё может быть скрипт,
/// поэтому в списке её нет — SVG отдаётся только на скачивание.
///
/// Документы Office (docx, xlsx, pptx) браузер показывать не умеет, поэтому
/// они идут отдельным путём: портал разбирает их сам (OfficeDocuments)
/// и отдаёт уже готовую разметку. Файл целиком браузеру при этом не уходит.
/// </summary>
public static class PreviewSupport
{
    private static readonly Dictionary<string, (PreviewKind Kind, string ContentType)> Allowed =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = (PreviewKind.Image, "image/png"),
            [".jpg"] = (PreviewKind.Image, "image/jpeg"),
            [".jpeg"] = (PreviewKind.Image, "image/jpeg"),
            [".gif"] = (PreviewKind.Image, "image/gif"),
            [".webp"] = (PreviewKind.Image, "image/webp"),
            [".bmp"] = (PreviewKind.Image, "image/bmp"),
            [".ico"] = (PreviewKind.Image, "image/x-icon"),

            [".pdf"] = (PreviewKind.Pdf, "application/pdf"),

            // Текстовые типы отдаём именно как text/plain, даже если по расширению
            // это json или xml: так браузер точно покажет их текстом,
            // а не попытается что-то из них построить.
            [".txt"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".log"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".csv"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".md"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".json"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".xml"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".ini"] = (PreviewKind.Text, "text/plain; charset=utf-8"),
            [".sql"] = (PreviewKind.Text, "text/plain; charset=utf-8"),

            // Документы Office. Тип содержимого здесь не используется:
            // наружу уходит не сам файл, а разобранная из него разметка.
            [".docx"] = (PreviewKind.Office, ""),
            [".docm"] = (PreviewKind.Office, ""),
            [".xlsx"] = (PreviewKind.Office, ""),
            [".xlsm"] = (PreviewKind.Office, ""),
            [".pptx"] = (PreviewKind.Office, ""),
            [".pptm"] = (PreviewKind.Office, ""),

            // Видео и звук. Список короткий намеренно: сюда попали только те
            // форматы, которые Chromium и Edge проигрывают сами, без кодеков.
            // Остальное (avi, mkv, wmv) браузер показать не сможет, и честнее
            // сразу предложить скачать файл, чем открыть чёрный прямоугольник.
            [".mp4"] = (PreviewKind.Video, "video/mp4"),
            [".webm"] = (PreviewKind.Video, "video/webm"),
            [".ogv"] = (PreviewKind.Video, "video/ogg"),

            [".mp3"] = (PreviewKind.Audio, "audio/mpeg"),
            [".wav"] = (PreviewKind.Audio, "audio/wav"),
            [".ogg"] = (PreviewKind.Audio, "audio/ogg"),
            [".m4a"] = (PreviewKind.Audio, "audio/mp4"),
            [".flac"] = (PreviewKind.Audio, "audio/flac"),

            // Архив. Тип содержимого пуст по той же причине, что у Office:
            // сам файл наружу не уходит, уходит только список того, что внутри.
            [".zip"] = (PreviewKind.Archive, "")
        };

    /// <summary>Наибольший размер текстового файла, который имеет смысл показывать целиком.</summary>
    public const long MaxTextPreviewBytes = 2 * 1024 * 1024;

    public static PreviewKind KindOf(string fileName) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var entry) ? entry.Kind : PreviewKind.None;

    /// <summary>
    /// Тип содержимого для безопасной отдачи «на просмотр».
    /// null — файл целиком отдавать нельзя (в том числе для документов Office
    /// и архивов: они уходят разметкой через отдельные обработчики).
    /// </summary>
    public static string? ContentTypeFor(string fileName) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var entry)
        && entry.Kind is not (PreviewKind.Office or PreviewKind.Archive)
            ? entry.ContentType
            : null;

    /// <summary>
    /// То же значение строкой — в том виде, в каком его понимает код страницы
    /// (см. app.js, openPreview). Пустая строка — показать нельзя.
    ///
    /// Здесь, а не в модели страницы «Файлы»: тем же окном предпросмотра
    /// открываются и вложения объявлений, а держать две таблицы соответствий
    /// значит однажды показать один и тот же файл двумя разными способами.
    /// </summary>
    public static string KindName(string fileName) => KindOf(fileName) switch
    {
        PreviewKind.Image => "image",
        PreviewKind.Pdf => "pdf",
        PreviewKind.Text => "text",
        PreviewKind.Office => "office",
        PreviewKind.Video => "video",
        PreviewKind.Audio => "audio",
        PreviewKind.Archive => "archive",
        _ => ""
    };

    /// <summary>Человеческое название типа — для окна свойств.</summary>
    public static string Describe(string fileName) => KindOf(fileName) switch
    {
        PreviewKind.Image => "изображение",
        PreviewKind.Pdf => "документ PDF",
        PreviewKind.Text => "текстовый файл",
        PreviewKind.Video => "видеозапись",
        PreviewKind.Audio => "звукозапись",
        PreviewKind.Archive => "архив ZIP",
        PreviewKind.Office => Path.GetExtension(fileName).ToLowerInvariant() switch
        {
            ".xlsx" or ".xlsm" => "книга Excel",
            ".pptx" or ".pptm" => "презентация PowerPoint",
            _ => "документ Word"
        },
        _ => "файл"
    };
}

/// <summary>
/// К какому «семейству» относится файл — для цвета значка в списке.
///
/// Это НЕ то же самое, что <see cref="PreviewKind"/>. Тот отвечает на вопрос
/// «умеет ли портал это показать», и список там короткий и строгий,
/// потому что от него зависит безопасность. Здесь же вопрос безобидный:
/// каким цветом нарисовать плитку. Поэтому список длиннее и включает
/// в том числе то, что портал показывать не умеет и не собирается.
/// </summary>
public static class FileKinds
{
    private static readonly Dictionary<string, string> ByExtension =
        new(StringComparer.OrdinalIgnoreCase)
        {
            [".png"] = "image", [".jpg"] = "image", [".jpeg"] = "image",
            [".gif"] = "image", [".webp"] = "image", [".bmp"] = "image",
            [".ico"] = "image", [".svg"] = "image", [".tif"] = "image", [".tiff"] = "image",

            [".pdf"] = "pdf",

            [".doc"] = "word", [".docx"] = "word", [".docm"] = "word",
            [".rtf"] = "word", [".odt"] = "word",

            [".xls"] = "excel", [".xlsx"] = "excel", [".xlsm"] = "excel",
            [".csv"] = "excel", [".ods"] = "excel",

            [".ppt"] = "ppt", [".pptx"] = "ppt", [".pptm"] = "ppt", [".odp"] = "ppt",

            [".mp4"] = "video", [".webm"] = "video", [".ogv"] = "video",
            [".avi"] = "video", [".mkv"] = "video", [".mov"] = "video", [".wmv"] = "video",

            [".mp3"] = "audio", [".wav"] = "audio", [".ogg"] = "audio",
            [".m4a"] = "audio", [".flac"] = "audio", [".wma"] = "audio",

            [".zip"] = "zip", [".rar"] = "zip", [".7z"] = "zip",
            [".tar"] = "zip", [".gz"] = "zip", [".cab"] = "zip",

            [".xml"] = "code", [".json"] = "code", [".sql"] = "code",
            [".html"] = "code", [".htm"] = "code", [".css"] = "code",
            [".ps1"] = "code", [".bat"] = "code", [".cmd"] = "code",
            [".py"] = "code", [".cs"] = "code", [".config"] = "code",

            [".txt"] = "text", [".log"] = "text", [".md"] = "text", [".ini"] = "text"
        };

    /// <summary>Семейство файла: image, pdf, word, excel, ppt, video, audio, zip, code, text, other.</summary>
    public static string Of(string fileName) =>
        ByExtension.GetValueOrDefault(Path.GetExtension(fileName), "other");
}
