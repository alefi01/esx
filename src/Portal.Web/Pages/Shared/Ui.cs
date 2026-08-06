using System.Text;
using Microsoft.AspNetCore.Html;
using Portal.Web.Services.Storage;

namespace Portal.Web.Pages.Shared;

/// <summary>
/// Кружок с буквами вместо фотографии.
///
/// Фотографий в Active Directory обычно нет, а кружок с инициалами читается
/// куда лучше безликого силуэта — так сделано и в макете.
/// </summary>
public static class Avatars
{
    /// <summary>Одна-две первые буквы имени: «Иванов Иван» — «ИИ».</summary>
    public static string Initials(string? name)
    {
        var parts = (name ?? "").Split([' ', '.', '_', '-'], StringSplitOptions.RemoveEmptyEntries);

        if (parts.Length == 0)
        {
            return "?";
        }

        return parts.Length == 1
            ? parts[0][..1].ToUpperInvariant()
            : (parts[0][..1] + parts[1][..1]).ToUpperInvariant();
    }

    /// <summary>
    /// Номер цвета кружка, от 0 до 7. Считается из самого имени, поэтому
    /// у одного человека цвет всегда один и тот же, а у разных людей разный.
    /// Никакой таблицы цветов вести не нужно, и новый сотрудник получает
    /// свой цвет сам собой.
    ///
    /// Возвращается именно НОМЕР, а не цвет: подставить цвет прямо
    /// в разметку нельзя — встроенные стили запрещены политикой
    /// безопасности страницы. Сами цвета лежат в site.css.
    /// </summary>
    public static int ColorIndex(string? name)
    {
        var sum = 0;

        foreach (var c in name ?? "")
        {
            sum = (sum * 31 + c) & 0x7fffffff;
        }

        return sum % 8;
    }
}

/// <summary>
/// Значки файлов и папок — те же, что в макете, точка в точку.
///
/// Рисуются здесь, на сервере, а не картинками: в макете это делает
/// JavaScript, но у нас список файлов приходит уже готовым с сервера,
/// и городить ради значков отдельный проход кодом страницы незачем.
///
/// Размер значка задают СТИЛИ (.tile-icon svg, .frow .ficon svg), поэтому
/// здесь ширина проставляется только там, где стилей нет, — атрибутом
/// width, а не через style: встроенные стили запрещены политикой
/// безопасности страницы, и браузер их молча отбрасывает.
/// </summary>
public static class FileIcons
{
    /// <summary>Цвета семейств файлов — ровно те, что в макете.</summary>
    private static readonly Dictionary<string, string> KindColor = new()
    {
        ["image"] = "#10a56b",
        ["pdf"] = "#e5484d",
        ["word"] = "#2b6cb0",
        ["excel"] = "#1e8e5a",
        ["ppt"] = "#e0641f",
        ["video"] = "#8b5cf6",
        ["audio"] = "#ec4899",
        ["zip"] = "#b45309",
        ["code"] = "#0ea5e9",
        ["text"] = "#64748b",
        ["other"] = "#64748b"
    };

    /// <summary>Человеческое название семейства — для столбца «Тип».</summary>
    private static readonly Dictionary<string, string> KindName = new()
    {
        ["image"] = "Изображение",
        ["pdf"] = "PDF",
        ["word"] = "Документ",
        ["excel"] = "Таблица",
        ["ppt"] = "Презентация",
        ["video"] = "Видео",
        ["audio"] = "Аудио",
        ["zip"] = "Архив",
        ["code"] = "Код",
        ["text"] = "Текст",
        ["other"] = "Файл"
    };

    public static string Color(string fileName) =>
        KindColor.GetValueOrDefault(FileKinds.Of(fileName), "#64748b");

    public static string Describe(string fileName) =>
        KindName.GetValueOrDefault(FileKinds.Of(fileName), "Файл");

    /// <summary>Папка: жёлтая, с отогнутым язычком — как в макете.</summary>
    public static IHtmlContent Folder(int? width = null)
    {
        var w = width is { } value ? $" width=\"{value}\"" : "";

        return new HtmlString(
            $"<svg viewBox=\"0 0 96 76\"{w}>" +
            "<path d=\"M6 16a8 8 0 0 1 8-8h22l10 10h36a8 8 0 0 1 8 8v4H6z\" fill=\"url(#fldB)\"/>" +
            "<rect x=\"6\" y=\"24\" width=\"84\" height=\"44\" rx=\"10\" fill=\"url(#fldG)\"/>" +
            "<rect x=\"6\" y=\"24\" width=\"84\" height=\"10\" rx=\"5\" fill=\"#fff\" opacity=\".28\"/>" +
            "</svg>");
    }

    /// <summary>
    /// Лист бумаги с загнутым уголком, покрашенный по типу файла,
    /// и расширение подписью на цветной плашке.
    /// </summary>
    public static IHtmlContent File(string fileName, int? width = null)
    {
        var color = Color(fileName);

        var label = Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant();

        if (label.Length > 4)
        {
            label = label[..4];
        }

        if (label.Length == 0)
        {
            label = "FILE";
        }

        var w = width is { } value ? $" width=\"{value}\"" : "";
        var fontSize = label.Length > 3 ? "11" : "13";

        var builder = new StringBuilder();

        builder.Append($"<svg viewBox=\"0 0 64 76\"{w}>")
            .Append($"<path d=\"M8 4h30l18 18v46a4 4 0 0 1-4 4H8a4 4 0 0 1-4-4V8a4 4 0 0 1 4-4z\" fill=\"{color}22\"/>")
            .Append($"<path d=\"M38 4l18 18H42a4 4 0 0 1-4-4z\" fill=\"{color}55\"/>")
            .Append($"<rect x=\"4\" y=\"44\" rx=\"7\" width=\"48\" height=\"24\" fill=\"{color}\"/>")
            .Append("<text x=\"28\" y=\"60.5\" font-size=\"").Append(fontSize)
            .Append("\" font-weight=\"800\" fill=\"#fff\" text-anchor=\"middle\" font-family=\"Inter,Arial,sans-serif\">")
            .Append(System.Net.WebUtility.HtmlEncode(label))
            .Append("</text></svg>");

        return new HtmlString(builder.ToString());
    }
}

/// <summary>
/// Согласование числительных с существительными по-русски.
///
/// «1 объект», «2 объекта», «5 объектов» — правило простое, но если
/// его не соблюсти, на странице получается «1 объектов», и портал
/// выглядит недоделанным.
/// </summary>
public static class Plural
{
    /// <param name="one">форма для 1: объект</param>
    /// <param name="few">форма для 2–4: объекта</param>
    /// <param name="many">форма для 5–20 и всех остальных: объектов</param>
    public static string Of(long count, string one, string few, string many)
    {
        var last = Math.Abs(count) % 100;

        // Числа от 11 до 14 — исключение: «11 объектов», а не «11 объект».
        if (last is >= 11 and <= 14)
        {
            return many;
        }

        return (last % 10) switch
        {
            1 => one,
            2 or 3 or 4 => few,
            _ => many
        };
    }

    /// <summary>То же, но сразу с самим числом: «3 объекта».</summary>
    public static string With(long count, string one, string few, string many) =>
        $"{count} {Of(count, one, few, many)}";
}
