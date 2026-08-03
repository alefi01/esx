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
    Text = 3
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
/// Office-документы (docx, xlsx) браузер показывать не умеет; для них
/// нужен отдельный преобразователь, это обсуждается отдельно.
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
            [".sql"] = (PreviewKind.Text, "text/plain; charset=utf-8")
        };

    /// <summary>Наибольший размер текстового файла, который имеет смысл показывать целиком.</summary>
    public const long MaxTextPreviewBytes = 2 * 1024 * 1024;

    public static PreviewKind KindOf(string fileName) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var entry) ? entry.Kind : PreviewKind.None;

    /// <summary>Тип содержимого для безопасной отдачи «на просмотр». null — показывать нельзя.</summary>
    public static string? ContentTypeFor(string fileName) =>
        Allowed.TryGetValue(Path.GetExtension(fileName), out var entry) ? entry.ContentType : null;

    /// <summary>Человеческое название типа — для окна свойств.</summary>
    public static string Describe(string fileName) => KindOf(fileName) switch
    {
        PreviewKind.Image => "изображение",
        PreviewKind.Pdf => "документ PDF",
        PreviewKind.Text => "текстовый файл",
        _ => "файл"
    };
}
