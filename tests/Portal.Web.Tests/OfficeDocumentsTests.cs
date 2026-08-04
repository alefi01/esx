using System.IO.Compression;
using System.Text;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Разбор документов Office без сторонних программ.
///
/// Документы для проверок собираются прямо здесь: .docx, .xlsx и .pptx —
/// это zip-архивы с XML внутри, поэтому подготовить настоящий файл
/// в несколько строк проще, чем таскать за собой двоичные образцы.
/// Заодно видно, из чего эти форматы состоят.
/// </summary>
public class OfficeDocumentsTests
{
    /// <summary>Собирает zip-архив из пар «путь внутри архива → содержимое».</summary>
    private static MemoryStream Package(params (string Path, string Xml)[] parts)
    {
        var memory = new MemoryStream();

        using (var archive = new ZipArchive(memory, ZipArchiveMode.Create, leaveOpen: true))
        {
            foreach (var (path, xml) in parts)
            {
                using var writer = new StreamWriter(archive.CreateEntry(path).Open(), Encoding.UTF8);
                writer.Write(xml);
            }
        }

        memory.Position = 0;

        return memory;
    }

    private const string WordNs = "http://schemas.openxmlformats.org/wordprocessingml/2006/main";
    private const string SheetNs = "http://schemas.openxmlformats.org/spreadsheetml/2006/main";
    private const string DrawNs = "http://schemas.openxmlformats.org/drawingml/2006/main";
    private const string RelNs = "http://schemas.openxmlformats.org/officeDocument/2006/relationships";
    private const string PkgRelNs = "http://schemas.openxmlformats.org/package/2006/relationships";

    private static MemoryStream Docx(string bodyXml) =>
        Package(("word/document.xml",
            $"""<?xml version="1.0" encoding="UTF-8"?><w:document xmlns:w="{WordNs}"><w:body>{bodyXml}</w:body></w:document>"""));

    private static string Paragraph(string text, string? style = null, bool bold = false) =>
        "<w:p>" +
        (style is null ? "" : $"<w:pPr><w:pStyle w:val=\"{style}\"/></w:pPr>") +
        "<w:r>" + (bold ? "<w:rPr><w:b/></w:rPr>" : "") + $"<w:t>{text}</w:t></w:r></w:p>";

    // ------------------------------------------------------------------
    // Word
    // ------------------------------------------------------------------

    [Fact]
    public void Word_показывает_текст_абзацев()
    {
        using var stream = Docx(Paragraph("Первый абзац") + Paragraph("Второй абзац"));

        var html = OfficeDocuments.ToHtml(stream, "договор.docx");

        Assert.Contains("Первый абзац", html);
        Assert.Contains("Второй абзац", html);
    }

    [Fact]
    public void Word_различает_заголовки_и_обычный_текст()
    {
        using var stream = Docx(
            Paragraph("Раздел первый", "Heading1") +
            Paragraph("Пояснение", "Heading2") +
            Paragraph("Просто текст"));

        var html = OfficeDocuments.ToHtml(stream, "инструкция.docx");

        Assert.Contains("doc__h1", html);
        Assert.Contains("doc__h2", html);
        Assert.Contains("<p>Просто текст</p>", html);
    }

    [Fact]
    public void Word_сохраняет_жирный_шрифт()
    {
        using var stream = Docx(Paragraph("важно", bold: true));

        var html = OfficeDocuments.ToHtml(stream, "записка.docx");

        Assert.Contains("<strong>важно</strong>", html);
    }

    [Fact]
    public void Word_собирает_абзац_из_нескольких_кусков()
    {
        // Word почти всегда режет абзац на части, даже когда человек
        // видит сплошную строку: проверка правки, язык, оформление.
        using var stream = Docx(
            "<w:p><w:r><w:t>Договор </w:t></w:r><w:r><w:t>аренды </w:t></w:r>" +
            "<w:r><w:t>помещения</w:t></w:r></w:p>");

        var html = OfficeDocuments.ToHtml(stream, "договор.docx");

        Assert.Contains("Договор аренды помещения", html);
    }

    [Fact]
    public void Word_показывает_таблицы()
    {
        using var stream = Docx(
            "<w:tbl><w:tr>" +
            "<w:tc><w:p><w:r><w:t>Наименование</w:t></w:r></w:p></w:tc>" +
            "<w:tc><w:p><w:r><w:t>Сумма</w:t></w:r></w:p></w:tc>" +
            "</w:tr><w:tr>" +
            "<w:tc><w:p><w:r><w:t>Аренда</w:t></w:r></w:p></w:tc>" +
            "<w:tc><w:p><w:r><w:t>12 000</w:t></w:r></w:p></w:tc>" +
            "</w:tr></w:tbl>");

        var html = OfficeDocuments.ToHtml(stream, "смета.docx");

        Assert.Contains("doc__table", html);
        Assert.Contains("Наименование", html);
        Assert.Contains("12 000", html);
    }

    [Fact]
    public void Word_показывает_списки_одним_перечнем()
    {
        using var stream = Docx(
            "<w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Первый пункт</w:t></w:r></w:p>" +
            "<w:p><w:pPr><w:numPr/></w:pPr><w:r><w:t>Второй пункт</w:t></w:r></w:p>");

        var html = OfficeDocuments.ToHtml(stream, "памятка.docx");

        // Именно один список на два пункта, а не два списка по одному.
        Assert.Equal(1, html.Split("<ul").Length - 1);
        Assert.Contains("<li>Первый пункт</li>", html);
    }

    /// <summary>
    /// Главная проверка безопасности: текст документа не должен становиться
    /// разметкой. Иначе достаточно было бы принести файл с кодом внутри —
    /// и он выполнился бы у всех, кто откроет предпросмотр.
    /// </summary>
    [Fact]
    public void Текст_документа_не_превращается_в_разметку()
    {
        using var stream = Docx(Paragraph("&lt;script&gt;alert(1)&lt;/script&gt;"));

        var html = OfficeDocuments.ToHtml(stream, "вредный.docx");

        Assert.DoesNotContain("<script>", html);
        Assert.Contains("&lt;script&gt;", html);
    }

    [Fact]
    public void Word_сохраняет_пробелы_помеченные_как_значимые()
    {
        // Word помечает куски с краевыми пробелами атрибутом xml:space="preserve".
        // Без него разборщик склеил бы слова: «Договораренды».
        using var stream = Docx(
            """<w:p><w:r><w:t xml:space="preserve">Договор </w:t></w:r>""" +
            "<w:r><w:t>аренды</w:t></w:r></w:p>");

        var html = OfficeDocuments.ToHtml(stream, "договор.docx");

        Assert.Contains("Договор аренды", html);
    }

    [Fact]
    public void Word_показывает_текст_ссылок()
    {
        // Ссылка в Word — это обёртка w:hyperlink вокруг обычных кусков текста.
        // Если искать куски только среди прямых потомков абзаца,
        // весь текст ссылки пропадёт.
        using var stream = Docx(
            "<w:p><w:hyperlink><w:r><w:t>перейти к приложению</w:t></w:r></w:hyperlink></w:p>");

        var html = OfficeDocuments.ToHtml(stream, "письмо.docx");

        Assert.Contains("перейти к приложению", html);
    }

    [Fact]
    public void Word_пропускает_служебные_коды_полей()
    {
        // Номера страниц и оглавления Word хранит служебными кодами
        // (w:instrText). Показывать их человеку незачем.
        using var stream = Docx(
            "<w:p><w:r><w:instrText>PAGE \\* MERGEFORMAT</w:instrText></w:r>" +
            "<w:r><w:t>Страница</w:t></w:r></w:p>");

        var html = OfficeDocuments.ToHtml(stream, "отчёт.docx");

        Assert.Contains("Страница", html);
        Assert.DoesNotContain("MERGEFORMAT", html);
    }

    [Fact]
    public void Пустой_документ_не_роняет_разбор()
    {
        using var stream = Docx("");

        var html = OfficeDocuments.ToHtml(stream, "пустой.docx");

        Assert.Contains("doc__note", html);
    }

    // ------------------------------------------------------------------
    // Excel
    // ------------------------------------------------------------------

    private static MemoryStream Xlsx(string sheetXml, string? sharedXml = null, string? stylesXml = null)
    {
        var parts = new List<(string, string)>
        {
            ("xl/workbook.xml",
                $"""<?xml version="1.0"?><workbook xmlns="{SheetNs}" xmlns:r="{RelNs}"><sheets><sheet name="Смета" sheetId="1" r:id="rId1"/></sheets></workbook>"""),
            ("xl/_rels/workbook.xml.rels",
                $"""<?xml version="1.0"?><Relationships xmlns="{PkgRelNs}"><Relationship Id="rId1" Target="worksheets/sheet1.xml"/></Relationships>"""),
            ("xl/worksheets/sheet1.xml",
                $"""<?xml version="1.0"?><worksheet xmlns="{SheetNs}"><sheetData>{sheetXml}</sheetData></worksheet>""")
        };

        if (sharedXml is not null)
        {
            parts.Add(("xl/sharedStrings.xml",
                $"""<?xml version="1.0"?><sst xmlns="{SheetNs}">{sharedXml}</sst>"""));
        }

        if (stylesXml is not null)
        {
            parts.Add(("xl/styles.xml",
                $"""<?xml version="1.0"?><styleSheet xmlns="{SheetNs}">{stylesXml}</styleSheet>"""));
        }

        return Package(parts.ToArray());
    }

    [Fact]
    public void Excel_показывает_имя_листа_и_ячейки()
    {
        using var stream = Xlsx(
            """<row><c r="A1" t="s"><v>0</v></c><c r="B1"><v>12000</v></c></row>""",
            "<si><t>Аренда</t></si>");

        var html = OfficeDocuments.ToHtml(stream, "смета.xlsx");

        Assert.Contains("Смета", html);      // имя листа
        Assert.Contains("Аренда", html);     // строка из общего списка
        Assert.Contains("12000", html);
    }

    [Fact]
    public void Excel_показывает_даты_датами_а_не_числами()
    {
        // 45678 — это 21.01.2025 в счёте Excel. Без разбора стилей
        // человек увидел бы в столбце «Дата» голое число.
        using var stream = Xlsx(
            """<row><c r="A1" s="1"><v>45678</v></c></row>""",
            stylesXml: """<cellXfs><xf numFmtId="0"/><xf numFmtId="14"/></cellXfs>""");

        var html = OfficeDocuments.ToHtml(stream, "график.xlsx");

        Assert.Contains("21.01.2025", html);
    }

    [Fact]
    public void Excel_понимает_свой_формат_даты()
    {
        using var stream = Xlsx(
            """<row><c r="A1" s="1"><v>45678</v></c></row>""",
            stylesXml:
            """<numFmts><numFmt numFmtId="165" formatCode="dd.mm.yyyy"/></numFmts>""" +
            """<cellXfs><xf numFmtId="0"/><xf numFmtId="165"/></cellXfs>""");

        var html = OfficeDocuments.ToHtml(stream, "график.xlsx");

        Assert.Contains("21.01.2025", html);
    }

    [Fact]
    public void Excel_не_принимает_обычное_число_за_дату()
    {
        using var stream = Xlsx(
            """<row><c r="A1" s="0"><v>45678</v></c></row>""",
            stylesXml: """<cellXfs><xf numFmtId="0"/></cellXfs>""");

        var html = OfficeDocuments.ToHtml(stream, "числа.xlsx");

        Assert.Contains("45678", html);
        Assert.DoesNotContain("2025", html);
    }

    [Fact]
    public void Excel_показывает_результат_формулы()
    {
        using var stream = Xlsx("""<row><c r="A1"><f>СУММ(B1:B9)</f><v>500</v></c></row>""");

        var html = OfficeDocuments.ToHtml(stream, "итоги.xlsx");

        Assert.Contains("500", html);
        Assert.DoesNotContain("СУММ", html);
    }

    // ------------------------------------------------------------------
    // PowerPoint
    // ------------------------------------------------------------------

    [Fact]
    public void PowerPoint_показывает_слайды_по_порядку()
    {
        static string Slide(string text) =>
            $"""<?xml version="1.0"?><sld xmlns:a="{DrawNs}"><a:p><a:r><a:t>{text}</a:t></a:r></a:p></sld>""";

        using var stream = Package(
            ("ppt/slides/slide1.xml", Slide("Первый")),
            ("ppt/slides/slide2.xml", Slide("Второй")),
            ("ppt/slides/slide10.xml", Slide("Десятый")));

        var html = OfficeDocuments.ToHtml(stream, "доклад.pptx");

        // Десятый слайд должен идти последним, а не сразу после первого:
        // имена файлов сортируются как текст, и «slide10» встаёт между
        // «slide1» и «slide2», если не разобрать номер.
        var first = html.IndexOf("Первый", StringComparison.Ordinal);
        var second = html.IndexOf("Второй", StringComparison.Ordinal);
        var tenth = html.IndexOf("Десятый", StringComparison.Ordinal);

        Assert.True(first < second && second < tenth);
        Assert.Contains("Слайд 10", html);
    }

    // ------------------------------------------------------------------
    // Общее
    // ------------------------------------------------------------------

    [Theory]
    [InlineData("отчёт.docx", true)]
    [InlineData("смета.xlsx", true)]
    [InlineData("доклад.pptx", true)]
    [InlineData("старый.doc", false)]    // двоичный формат до Office 2007
    [InlineData("старый.xls", false)]
    [InlineData("картинка.png", false)]
    public void Поддерживаются_только_новые_форматы(string name, bool supported)
    {
        Assert.Equal(supported, OfficeDocuments.IsSupported(name));
    }

    [Fact]
    public void Документ_Office_не_отдаётся_файлом_целиком()
    {
        // Наружу уходит разобранная разметка, а не сам файл, — значит
        // прямой отдачи для этих расширений быть не должно.
        Assert.Equal(PreviewKind.Office, PreviewSupport.KindOf("отчёт.docx"));
        Assert.Null(PreviewSupport.ContentTypeFor("отчёт.docx"));
    }

    [Fact]
    public void Испорченный_архив_даёт_понятную_ошибку_а_не_падение()
    {
        using var stream = new MemoryStream("это вообще не архив"u8.ToArray());

        Assert.ThrowsAny<Exception>(() => OfficeDocuments.ToHtml(stream, "битый.docx"));
    }
}
