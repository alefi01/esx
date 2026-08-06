using System.IO.Compression;
using System.Net;
using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Portal.Web.Services.Storage;

/// <summary>
/// Показ документов Word, Excel и PowerPoint без единой сторонней программы.
///
/// ПОЧЕМУ ЭТО ВООБЩЕ ВОЗМОЖНО
///
/// Файлы .docx, .xlsx и .pptx — не «двоичные документы», как думают многие.
/// Это обычные zip-архивы, внутри которых лежит несколько файлов XML:
/// текст, таблицы, оформление. Формат открытый (стандарт OOXML).
/// А распаковывать zip и читать XML .NET умеет сам, без чужих библиотек —
/// значит, ничего скачивать и переносить на сервер не нужно.
///
/// Мы НЕ пытаемся повторить Word. Задача другая: дать человеку понять,
/// тот ли это документ, не скачивая его. Поэтому берём текст, заголовки,
/// списки и таблицы, а оформление, картинки, колонтитулы и формулы
/// сознательно опускаем.
///
/// ЧЕГО ЗДЕСЬ НЕТ И НЕ БУДЕТ
///
/// Старые форматы .doc, .xls, .ppt (до Office 2007) — двоичные, закрытые
/// и разобрать их таким способом нельзя. Для них портал честно говорит,
/// что показать не может, и предлагает скачать.
///
/// БЕЗОПАСНОСТЬ
///
/// Весь текст из документа проходит через HtmlEncode. Разметку составляем
/// только мы сами, из документа в неё не попадает ни одного тега и ни одного
/// значения атрибута. Плюс защита от «архивных бомб»: и число файлов,
/// и объём распакованного ограничены.
/// </summary>
public static class OfficeDocuments
{
    /// <summary>Сколько распакованного XML согласны прочитать из одной части архива.</summary>
    private const long MaxPartBytes = 24 * 1024 * 1024;

    /// <summary>Предохранители от слишком больших документов.</summary>
    private const int MaxParagraphs = 4000;
    private const int MaxTableRows = 2000;
    private const int MaxSheetRows = 2000;
    private const int MaxSheetColumns = 64;
    private const int MaxSlides = 200;

    // Пространства имён OOXML. Длинные и некрасивые, но это часть стандарта.
    private static readonly XNamespace W =
        "http://schemas.openxmlformats.org/wordprocessingml/2006/main";

    private static readonly XNamespace S =
        "http://schemas.openxmlformats.org/spreadsheetml/2006/main";

    private static readonly XNamespace A =
        "http://schemas.openxmlformats.org/drawingml/2006/main";

    private static readonly XNamespace R =
        "http://schemas.openxmlformats.org/officeDocument/2006/relationships";

    private static readonly XNamespace PackageRels =
        "http://schemas.openxmlformats.org/package/2006/relationships";

    /// <summary>Умеет ли портал показать такой файл.</summary>
    public static bool IsSupported(string fileName) => Kind(fileName) is not null;

    private static string? Kind(string fileName) => Path.GetExtension(fileName).ToLowerInvariant() switch
    {
        ".docx" or ".docm" => "word",
        ".xlsx" or ".xlsm" => "excel",
        ".pptx" or ".pptm" => "powerpoint",
        _ => null
    };

    /// <summary>Сколько строк списка показывать для архива.</summary>
    private const int MaxArchiveEntries = 500;

    /// <summary>
    /// Кодировка имён внутри архивов, созданных проводником Windows.
    ///
    /// Читается один раз при первом обращении. Кодовые страницы вроде 866
    /// в .NET по умолчанию не подключены — их приносит отдельный пакет,
    /// уже входящий в состав платформы; регистрируем его здесь.
    /// </summary>
    private static readonly Encoding OemEncoding = CreateOemEncoding();

    private static Encoding CreateOemEncoding()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            return Encoding.GetEncoding(866);
        }
        catch (Exception)
        {
            // Кодовой страницы в системе нет — читаем как UTF-8, как раньше.
            return Encoding.UTF8;
        }
    }

    /// <summary>
    /// Список того, что лежит внутри архива ZIP.
    ///
    /// Сами файлы НЕ распаковываются: читается только оглавление архива,
    /// которое хранится в нём отдельно. Поэтому даже для архива на несколько
    /// гигабайт эта операция мгновенная и ничего не тратит.
    ///
    /// Задача скромная и намеренно такая: понять, тот ли это архив,
    /// не скачивая его целиком по узкому каналу между офисами.
    /// </summary>
    public static string ArchiveToHtml(Stream stream)
    {
        // ЯВНАЯ кодировка имён — иначе русские имена внутри архива
        // превращаются в «╨Ф╨╛╨│╨╛╨▓╨╛╤А».
        //
        // В формате ZIP имя файла лежит просто набором байтов, и то, какими
        // буквами его читать, указывается флагом. Проводник Windows и старые
        // архиваторы этот флаг НЕ ставят и пишут имена в кодировке MS-DOS
        // (866). .NET по умолчанию читает такие имена как UTF-8 и получает
        // мусор. Указываем 866 явно: у архивов с флагом UTF-8 .NET всё равно
        // возьмёт UTF-8 — флаг сильнее этой настройки, — так что правильно
        // прочитаются и те, и другие.
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true, OemEncoding);

        // Папки внутри архива представлены записями с пустым именем файла
        // и нулевым размером — в списке они не нужны.
        var entries = archive.Entries
            .Where(e => !string.IsNullOrEmpty(e.Name))
            .OrderBy(e => e.FullName, StringComparer.CurrentCultureIgnoreCase)
            .ToList();

        if (entries.Count == 0)
        {
            return Empty("Архив пуст.");
        }

        var builder = new StringBuilder("<div class=\"doc\"><div class=\"zip-list\">");

        foreach (var entry in entries.Take(MaxArchiveEntries))
        {
            builder.Append("<div class=\"zip-row\"><span class=\"zip-name\">")
                .Append(WebUtility.HtmlEncode(entry.FullName))
                .Append("</span><span class=\"zip-size\">")
                .Append(WebUtility.HtmlEncode(UploadValidator.Format(entry.Length)))
                .Append("</span></div>");
        }

        builder.Append("</div>");

        // Считаем по ВСЕМ записям, а не только по показанным: цифра внизу
        // должна описывать архив, а не длину списка на экране.
        builder.Append("<p class=\"doc__note\">Файлов: ")
            .Append(entries.Count)
            .Append(", в распакованном виде ")
            .Append(WebUtility.HtmlEncode(UploadValidator.Format(entries.Sum(e => e.Length))));

        if (entries.Count > MaxArchiveEntries)
        {
            builder.Append(". Показаны первые ").Append(MaxArchiveEntries);
        }

        builder.Append(".</p></div>");

        return builder.ToString();
    }

    /// <summary>
    /// Разбирает документ и возвращает КУСОК разметки для показа в панели.
    /// Не целую страницу — только содержимое.
    /// </summary>
    public static string ToHtml(Stream stream, string fileName)
    {
        var kind = Kind(fileName)
                   ?? throw new NotSupportedException($"Формат файла «{fileName}» не поддерживается.");

        using var archive = new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: true);

        return kind switch
        {
            "word" => WordToHtml(archive),
            "excel" => ExcelToHtml(archive),
            _ => PowerPointToHtml(archive)
        };
    }

    // ==================================================================
    // Word
    // ==================================================================

    private static string WordToHtml(ZipArchive archive)
    {
        var document = ReadXml(archive, "word/document.xml")
                       ?? throw new InvalidDataException("В архиве нет word/document.xml.");

        var body = document.Root?.Element(W + "body");

        if (body is null)
        {
            return Empty("Документ пуст.");
        }

        var html = new StringBuilder("<div class=\"doc\">");
        var paragraphs = 0;
        var listOpen = false;

        // Отдельный признак «что-то вывели»: считать одни абзацы нельзя.
        // Документ может состоять из одной таблицы — и тогда счётчик абзацев
        // остаётся нулевым, а содержимое есть. Раньше такой документ
        // показывался как пустой.
        var hasContent = false;

        foreach (var node in body.Elements())
        {
            if (paragraphs >= MaxParagraphs)
            {
                html.Append(Cut());
                break;
            }

            if (node.Name == W + "p")
            {
                paragraphs++;
                hasContent |= AppendWordParagraph(html, node, ref listOpen);
                continue;
            }

            if (node.Name == W + "tbl")
            {
                if (listOpen)
                {
                    html.Append("</ul>");
                    listOpen = false;
                }

                hasContent |= AppendWordTable(html, node);
            }
        }

        if (listOpen)
        {
            html.Append("</ul>");
        }

        html.Append("</div>");

        return hasContent ? html.ToString() : Empty("В документе нет текста.");
    }

    /// <summary>Выводит абзац. Возвращает false, если выводить было нечего.</summary>
    private static bool AppendWordParagraph(StringBuilder html, XElement paragraph, ref bool listOpen)
    {
        var properties = paragraph.Element(W + "pPr");
        var style = properties?.Element(W + "pStyle")?.Attribute(W + "val")?.Value ?? "";
        var isListItem = properties?.Element(W + "numPr") is not null;

        var text = WordRuns(paragraph);

        // Пустые абзацы пропускаем: в документах их бывает много,
        // и в панели они превращаются в дыры.
        if (string.IsNullOrWhiteSpace(StripTags(text)))
        {
            return false;
        }

        if (isListItem)
        {
            if (!listOpen)
            {
                html.Append("<ul class=\"doc__list\">");
                listOpen = true;
            }

            html.Append("<li>").Append(text).Append("</li>");
            return true;
        }

        if (listOpen)
        {
            html.Append("</ul>");
            listOpen = false;
        }

        // Уровень заголовка берём из имени стиля: Heading1, Heading2, «Заголовок 1»…
        var level = HeadingLevel(style);

        if (level > 0)
        {
            html.Append("<div class=\"doc__h doc__h").Append(level).Append("\">")
                .Append(text).Append("</div>");
            return true;
        }

        html.Append("<p>").Append(text).Append("</p>");

        return true;
    }

    /// <summary>Номер уровня заголовка или 0, если это обычный абзац.</summary>
    private static int HeadingLevel(string style)
    {
        if (style.StartsWith("Heading", StringComparison.OrdinalIgnoreCase)
            || style.StartsWith("Заголовок", StringComparison.OrdinalIgnoreCase))
        {
            var digits = new string(style.Where(char.IsDigit).ToArray());

            return int.TryParse(digits, out var level) && level is >= 1 and <= 6 ? level : 1;
        }

        // Title — заголовок документа, по смыслу первый уровень.
        return style.Equals("Title", StringComparison.OrdinalIgnoreCase) ? 1 : 0;
    }

    /// <summary>
    /// Собирает текст абзаца из «пробегов» (runs) с учётом жирного и курсива.
    /// Пробег — это кусок текста с одинаковым оформлением; Word разбивает
    /// абзац на них даже там, где человек видит сплошную строку.
    /// </summary>
    private static string WordRuns(XElement paragraph)
    {
        var html = new StringBuilder();

        foreach (var run in paragraph.Descendants(W + "r"))
        {
            var properties = run.Element(W + "rPr");
            var bold = properties?.Element(W + "b") is not null;
            var italic = properties?.Element(W + "i") is not null;

            var text = new StringBuilder();

            foreach (var node in run.Elements())
            {
                if (node.Name == W + "t")
                {
                    text.Append(node.Value);
                }
                else if (node.Name == W + "br")
                {
                    text.Append('\n');
                }
                else if (node.Name == W + "tab")
                {
                    text.Append('\t');
                }
            }

            if (text.Length == 0)
            {
                continue;
            }

            var encoded = Encode(text.ToString());

            if (bold)
            {
                encoded = "<strong>" + encoded + "</strong>";
            }

            if (italic)
            {
                encoded = "<em>" + encoded + "</em>";
            }

            html.Append(encoded);
        }

        return html.ToString();
    }

    /// <summary>Выводит таблицу. Возвращает false, если строк в ней не оказалось.</summary>
    private static bool AppendWordTable(StringBuilder html, XElement table)
    {
        html.Append("<table class=\"doc__table\"><tbody>");

        var rows = 0;

        foreach (var row in table.Elements(W + "tr"))
        {
            if (++rows > MaxTableRows)
            {
                break;
            }

            html.Append("<tr>");

            foreach (var cell in row.Elements(W + "tc"))
            {
                html.Append("<td>");

                foreach (var paragraph in cell.Elements(W + "p"))
                {
                    html.Append(WordRuns(paragraph)).Append(' ');
                }

                html.Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>");

        return rows > 0;
    }

    // ==================================================================
    // Excel
    // ==================================================================

    private static string ExcelToHtml(ZipArchive archive)
    {
        var workbook = ReadXml(archive, "xl/workbook.xml")
                       ?? throw new InvalidDataException("В архиве нет xl/workbook.xml.");

        var shared = ReadSharedStrings(archive);
        var dateStyles = ReadDateStyles(archive);
        var targets = ReadRelationships(archive, "xl/_rels/workbook.xml.rels");

        var html = new StringBuilder();
        var sheets = 0;

        foreach (var sheet in workbook.Root?.Element(S + "sheets")?.Elements(S + "sheet") ?? [])
        {
            var name = sheet.Attribute("name")?.Value ?? $"Лист {sheets + 1}";
            var id = sheet.Attribute(R + "id")?.Value;

            if (id is null || !targets.TryGetValue(id, out var target))
            {
                continue;
            }

            var path = target.StartsWith('/') ? target.TrimStart('/') : "xl/" + target;
            var worksheet = ReadXml(archive, path);

            if (worksheet is null)
            {
                continue;
            }

            sheets++;

            html.Append("<div class=\"doc__sheet\">")
                .Append("<div class=\"doc__sheetname\">").Append(Encode(name)).Append("</div>");

            AppendSheet(html, worksheet, shared, dateStyles);

            html.Append("</div>");
        }

        return sheets == 0
            ? Empty("В книге нет листов с данными.")
            : "<div class=\"doc\">" + html + "</div>";
    }

    private static void AppendSheet(
        StringBuilder html, XDocument worksheet, List<string> shared, HashSet<int> dateStyles)
    {
        var rows = worksheet.Root?.Element(S + "sheetData")?.Elements(S + "row").ToList() ?? [];

        if (rows.Count == 0)
        {
            html.Append("<p class=\"doc__note\">Лист пуст.</p>");
            return;
        }

        html.Append("<table class=\"doc__table\"><tbody>");

        var printed = 0;

        foreach (var row in rows)
        {
            if (++printed > MaxSheetRows)
            {
                html.Append("<tr><td>").Append(Cut()).Append("</td></tr>");
                break;
            }

            html.Append("<tr>");

            var columns = 0;

            foreach (var cell in row.Elements(S + "c"))
            {
                if (++columns > MaxSheetColumns)
                {
                    break;
                }

                html.Append("<td>").Append(Encode(CellText(cell, shared, dateStyles))).Append("</td>");
            }

            html.Append("</tr>");
        }

        html.Append("</tbody></table>");
    }

    /// <summary>
    /// Значение ячейки как текст.
    ///
    /// Excel хранит числа и даты одинаково — числом. Дата отличается только
    /// форматом отображения, который лежит отдельно, в стилях. Поэтому,
    /// чтобы не показывать «45678» вместо «03.02.2026», приходится
    /// заглядывать в стиль ячейки.
    /// </summary>
    private static string CellText(XElement cell, List<string> shared, HashSet<int> dateStyles)
    {
        var type = cell.Attribute("t")?.Value;

        // Строка из общего списка: Excel не повторяет одинаковый текст,
        // а складывает его в отдельный файл и ссылается номером.
        if (type == "s")
        {
            return int.TryParse(cell.Element(S + "v")?.Value, out var index)
                   && index >= 0 && index < shared.Count
                ? shared[index]
                : "";
        }

        if (type == "inlineStr")
        {
            return string.Concat(cell.Element(S + "is")?.Descendants(S + "t").Select(t => t.Value) ?? []);
        }

        // Формула: показываем посчитанное значение, а не саму формулу —
        // человек смотрит на документ, а не отлаживает его.
        var value = cell.Element(S + "v")?.Value;

        if (string.IsNullOrEmpty(value))
        {
            return "";
        }

        if (type == "b")
        {
            return value == "1" ? "ИСТИНА" : "ЛОЖЬ";
        }

        var styleIndex = int.TryParse(cell.Attribute("s")?.Value, out var style) ? style : -1;

        if (dateStyles.Contains(styleIndex)
            && double.TryParse(value, System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var serial))
        {
            var date = FromExcelSerial(serial);

            if (date is { } moment)
            {
                return moment.TimeOfDay == TimeSpan.Zero
                    ? moment.ToString("dd.MM.yyyy")
                    : moment.ToString("dd.MM.yyyy HH:mm");
            }
        }

        return value;
    }

    /// <summary>
    /// Дата из «числа Excel»: счёт идёт от 1 января 1900 года.
    ///
    /// Тонкость: в Excel есть несуществующее 29 февраля 1900 года — ошибка,
    /// унаследованная от Lotus 1-2-3 и сохранённая ради совместимости.
    /// Поэтому для дат после 28 февраля 1900 года вычитается лишний день.
    /// </summary>
    private static DateTime? FromExcelSerial(double serial)
    {
        if (serial is < 1 or > 2_958_465)   // 31.12.9999
        {
            return null;
        }

        var whole = Math.Floor(serial);
        var days = whole > 59 ? whole - 1 : whole;

        return new DateTime(1899, 12, 31).AddDays(days).AddSeconds((serial - whole) * 86400);
    }

    private static List<string> ReadSharedStrings(ZipArchive archive)
    {
        var document = ReadXml(archive, "xl/sharedStrings.xml");

        if (document?.Root is null)
        {
            return [];
        }

        return document.Root.Elements(S + "si")
            .Select(item => string.Concat(item.Descendants(S + "t").Select(t => t.Value)))
            .ToList();
    }

    /// <summary>
    /// Номера стилей, которыми в этой книге показывают даты.
    ///
    /// Часть форматов встроенные и известны по номеру (14–22, 45–47),
    /// остальные заданы строкой вроде «dd.mm.yyyy» — такие узнаём
    /// по наличию в записи формата букв года, дня или часа.
    /// Букву «m» не проверяем: в записи формата она означает и месяц,
    /// и минуты, и встречается в обычных числовых форматах.
    /// </summary>
    private static HashSet<int> ReadDateStyles(ZipArchive archive)
    {
        var result = new HashSet<int>();
        var document = ReadXml(archive, "xl/styles.xml");

        if (document?.Root is null)
        {
            return result;
        }

        var builtIn = new HashSet<int> { 14, 15, 16, 17, 18, 19, 20, 21, 22, 45, 46, 47 };
        var custom = new HashSet<int>();

        foreach (var format in document.Root.Element(S + "numFmts")?.Elements(S + "numFmt") ?? [])
        {
            var id = int.TryParse(format.Attribute("numFmtId")?.Value, out var number) ? number : -1;
            var code = format.Attribute("formatCode")?.Value ?? "";

            // Отсекаем то, что в кавычках: там буквы — просто текст.
            var cleaned = string.Concat(code.Split('"').Where((_, i) => i % 2 == 0));

            if (id >= 0 && cleaned.Any(c => c is 'y' or 'Y' or 'd' or 'D' or 'h' or 'H'))
            {
                custom.Add(id);
            }
        }

        var index = 0;

        foreach (var record in document.Root.Element(S + "cellXfs")?.Elements(S + "xf") ?? [])
        {
            var id = int.TryParse(record.Attribute("numFmtId")?.Value, out var number) ? number : 0;

            if (builtIn.Contains(id) || custom.Contains(id))
            {
                result.Add(index);
            }

            index++;
        }

        return result;
    }

    // ==================================================================
    // PowerPoint
    // ==================================================================

    private static string PowerPointToHtml(ZipArchive archive)
    {
        // Слайды лежат как ppt/slides/slide1.xml, slide2.xml…
        // Сортируем по числу в имени, иначе десятый слайд встанет после первого.
        var slides = archive.Entries
            .Where(e => e.FullName.StartsWith("ppt/slides/slide", StringComparison.OrdinalIgnoreCase)
                        && e.FullName.EndsWith(".xml", StringComparison.OrdinalIgnoreCase))
            .Select(e => new { Entry = e, Number = SlideNumber(e.FullName) })
            .Where(x => x.Number > 0)
            .OrderBy(x => x.Number)
            .Take(MaxSlides)
            .ToList();

        if (slides.Count == 0)
        {
            return Empty("В презентации нет слайдов.");
        }

        var html = new StringBuilder("<div class=\"doc\">");

        foreach (var slide in slides)
        {
            var document = ReadXml(archive, slide.Entry.FullName);

            if (document is null)
            {
                continue;
            }

            html.Append("<div class=\"doc__slide\">")
                .Append("<div class=\"doc__slidenum\">Слайд ").Append(slide.Number).Append("</div>");

            var any = false;

            // Каждый a:p — абзац в надписи на слайде.
            foreach (var paragraph in document.Descendants(A + "p"))
            {
                var text = string.Concat(paragraph.Descendants(A + "t").Select(t => t.Value));

                if (string.IsNullOrWhiteSpace(text))
                {
                    continue;
                }

                any = true;
                html.Append("<p>").Append(Encode(text)).Append("</p>");
            }

            if (!any)
            {
                html.Append("<p class=\"doc__note\">На слайде нет текста ")
                    .Append("(возможно, только картинки — их предпросмотр не показывает).</p>");
            }

            html.Append("</div>");
        }

        html.Append("</div>");

        return html.ToString();
    }

    private static int SlideNumber(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var digits = new string(name.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

        return int.TryParse(digits, out var number) ? number : 0;
    }

    // ==================================================================
    // Общее
    // ==================================================================

    private static Dictionary<string, string> ReadRelationships(ZipArchive archive, string path)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var document = ReadXml(archive, path);

        foreach (var relation in document?.Root?.Elements(PackageRels + "Relationship") ?? [])
        {
            var id = relation.Attribute("Id")?.Value;
            var target = relation.Attribute("Target")?.Value;

            if (id is not null && target is not null)
            {
                result[id] = target;
            }
        }

        return result;
    }

    /// <summary>
    /// Читает часть архива как XML.
    ///
    /// Обработка DTD отключена намеренно. Через неё делается известное
    /// нападение: документ объявляет сущность, ссылающуюся на файл сервера
    /// или на саму себя тысячу раз, — и разбор либо выдаёт наружу чужой файл,
    /// либо съедает всю память. Нам DTD в этих форматах не нужна вовсе.
    /// </summary>
    private static XDocument? ReadXml(ZipArchive archive, string path)
    {
        var entry = archive.GetEntry(path);

        if (entry is null)
        {
            return null;
        }

        // Заявленный размер тоже проверяем: он врёт у «архивных бомб»,
        // но отсекает честные слишком большие файлы сразу, без чтения.
        if (entry.Length > MaxPartBytes)
        {
            throw new InvalidDataException($"Часть документа «{path}» слишком велика для предпросмотра.");
        }

        using var raw = entry.Open();
        using var limited = new LimitedStream(raw, MaxPartBytes);

        var settings = new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Prohibit,
            XmlResolver = null,
            IgnoreWhitespace = false
        };

        using var reader = XmlReader.Create(limited, settings);

        return XDocument.Load(reader);
    }

    private static string Encode(string text) => WebUtility.HtmlEncode(text).Replace("\n", "<br />");

    private static string StripTags(string html)
    {
        var result = new StringBuilder();
        var inside = false;

        foreach (var c in html)
        {
            if (c == '<')
            {
                inside = true;
            }
            else if (c == '>')
            {
                inside = false;
            }
            else if (!inside)
            {
                result.Append(c);
            }
        }

        return result.ToString();
    }

    private static string Empty(string message) =>
        "<div class=\"doc\"><p class=\"doc__note\">" + WebUtility.HtmlEncode(message) + "</p></div>";

    private static string Cut() =>
        "<p class=\"doc__note\">Документ показан не целиком — он слишком большой. " +
        "Скачайте файл, чтобы увидеть его полностью.</p>";

    /// <summary>
    /// Обёртка над потоком, которая не даёт прочитать больше разрешённого.
    ///
    /// Нужна против «архивных бомб»: маленький файл, который при распаковке
    /// превращается в гигабайты. Заявленному в архиве размеру верить нельзя,
    /// поэтому считаем прочитанное сами.
    /// </summary>
    private sealed class LimitedStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _limit;
        private long _read;

        public LimitedStream(Stream inner, long limit)
        {
            _inner = inner;
            _limit = limit;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => _read;
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = _inner.Read(buffer, offset, count);

            _read += read;

            if (_read > _limit)
            {
                throw new InvalidDataException(
                    "Документ при распаковке оказался слишком большим для предпросмотра.");
            }

            return read;
        }

        public override void Flush() => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                _inner.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
