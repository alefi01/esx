using System.Text;
using System.Text.Encodings.Web;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Html;

namespace Portal.Web.Services.Text;

/// <summary>
/// Превращает обычный текст объявления в безопасный HTML.
///
/// ПОЧЕМУ ПРОСТОЙ ТЕКСТ, А НЕ HTML-РЕДАКТОР
///
/// Соблазн поставить визуальный редактор с жирным шрифтом и картинками
/// велик, но у него есть цена. Всё, что пользователь наберёт в таком
/// редакторе, — это HTML, и его придётся отдавать браузеру как HTML.
/// А значит, кто-то может вставить туда &lt;script&gt; и выполнить свой код
/// в браузере у всех, кто откроет ленту, — от их имени и с их правами.
/// Защита от этого — очистка разметки по «белому списку», отдельная
/// библиотека, которую надо переносить и обновлять.
///
/// Здесь выбран другой путь: хранится обычный текст, а HTML целиком
/// формируем мы сами. Пользовательский ввод при этом ВСЕГДА экранируется,
/// то есть &lt;script&gt; отображается как безобидная строка, а не выполняется.
///
/// Что при этом всё-таки поддерживается:
///   * переносы строк и пустые строки между абзацами
///   * ссылки http:// и https:// — распознаются автоматически и становятся кликабельными
///   * выделение: **жирным** и ==жёлтым, как маркером==
///
/// Выделение сделано ЗНАКАМИ В ТЕКСТЕ, а не разметкой в базе, и по той же
/// причине, по которой здесь нет визуального редактора: в базе по-прежнему
/// лежит обычный текст, который невозможно выполнить. Знаки расставляются
/// правым меню в поле ввода, но их можно набрать и руками — и в письме,
/// и в переписке они читаются как выделение сами по себе.
///
/// Класс не статический, потому что ему нужен HtmlEncoder из контейнера,
/// а не встроенный HtmlEncoder.Default: в Program.cs кодировщик настроен так,
/// чтобы кириллица оставалась кириллицей, а не превращалась в &amp;#x412;.
/// В разметке используется через @@inject (см. Announcements/Index.cshtml).
/// </summary>
public sealed class PlainTextFormatter
{
    private readonly HtmlEncoder _encoder;

    public PlainTextFormatter(HtmlEncoder encoder) => _encoder = encoder;

    /// <summary>
    /// Поиск ссылок в исходном тексте. Схемы намеренно ограничены http и https:
    /// javascript: и data: в ссылке — это способ выполнить чужой код по нажатию.
    /// Вариант с "www." без схемы распознаём тоже: люди часто пишут именно так.
    /// </summary>
    private static readonly Regex UrlRegex = new(
        @"\b(?:https?://|www\.)[^\s<>""']+",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Знаки, которые почти всегда относятся к предложению, а не к адресу:
    /// «зайдите на http://example.com.» — точка в конце не часть ссылки.
    /// </summary>
    private const string TrailingPunctuation = ".,;:!?)»\"'";

    /// <summary>
    /// Выделения. Ищутся УЖЕ В ЭКРАНИРОВАННОМ тексте: экранирование
    /// не трогает звёздочки и знаки равенства, зато к этому моменту всё
    /// опасное обезврежено, и вставить теги можно спокойно.
    ///
    /// Внутри выделения запрещены переносы строк: незакрытая пара знаков
    /// иначе «съедала» бы полобъявления, превращая его в жирную простыню.
    /// </summary>
    private static readonly Regex BoldRegex = new(
        @"\*\*([^\n*]+?)\*\*", RegexOptions.Compiled);

    private static readonly Regex MarkRegex = new(
        @"==([^\n=]+?)==", RegexOptions.Compiled);

    /// <summary>
    /// То же выделение (жирным и жёлтым), но БЕЗ превращения адресов
    /// в ссылки и без переносов строк.
    ///
    /// Нужно для карточек на главной: там вся карточка сама по себе ссылка,
    /// а ссылка внутри ссылки — недопустимая разметка, браузер молча рвёт
    /// внешнюю и карточка разваливается. Выделение при этом терялось:
    /// объявление, набранное с пометками, выглядело на главной как текст
    /// со звёздочками.
    /// </summary>
    public IHtmlContent ToMarkedText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HtmlString.Empty;
        }

        var normalized = text.Replace("\r\n", " ").Replace('\r', ' ').Replace('\n', ' ');

        var builder = new StringBuilder(normalized.Length + 32);

        AppendLine(builder, normalized);

        return new HtmlString(builder.ToString());
    }

    public IHtmlContent ToHtml(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return HtmlString.Empty;
        }

        // Приводим переносы к одному виду: из Windows приходит \r\n, из иных мест \n.
        var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

        var builder = new StringBuilder(normalized.Length + 64);
        var position = 0;

        foreach (Match match in UrlRegex.Matches(normalized))
        {
            // Текст между предыдущей ссылкой и этой — экранируем как есть.
            AppendEncodedText(builder, normalized.AsSpan(position, match.Index - position));

            var url = TrimTrailingPunctuation(match.Value);

            AppendLink(builder, url);

            position = match.Index + url.Length;
        }

        // Хвост после последней ссылки.
        AppendEncodedText(builder, normalized.AsSpan(position));

        return new HtmlString(builder.ToString());
    }

    /// <summary>
    /// Экранирует текст и превращает переносы строк в теги &lt;br /&gt;.
    /// Именно здесь пользовательский ввод обезвреживается: символы
    /// &lt; &gt; &amp; " превращаются в безопасные последовательности.
    /// </summary>
    private void AppendEncodedText(StringBuilder builder, ReadOnlySpan<char> text)
    {
        var start = 0;

        for (var i = 0; i < text.Length; i++)
        {
            if (text[i] != '\n')
            {
                continue;
            }

            AppendLine(builder, text[start..i]);
            builder.Append("<br />");
            start = i + 1;
        }

        if (start < text.Length)
        {
            AppendLine(builder, text[start..]);
        }
    }

    /// <summary>Одна строка: сперва экранирование, потом выделения.</summary>
    private void AppendLine(StringBuilder builder, ReadOnlySpan<char> line)
    {
        var encoded = _encoder.Encode(line.ToString());

        encoded = BoldRegex.Replace(encoded, "<strong>$1</strong>");
        encoded = MarkRegex.Replace(encoded, "<mark>$1</mark>");

        builder.Append(encoded);
    }

    private void AppendLink(StringBuilder builder, string url)
    {
        // Адрес для атрибута href: если схема не указана, подставляем https.
        var href = url.StartsWith("www.", StringComparison.OrdinalIgnoreCase)
            ? "https://" + url
            : url;

        // Кодируется и адрес, и подпись ссылки. Адрес — потому что он попадает
        // внутрь атрибута, где кавычка закрыла бы атрибут и позволила бы
        // дописать к тегу что-нибудь своё.
        builder.Append("<a href=\"");
        builder.Append(_encoder.Encode(href));

        // rel="noopener noreferrer" — чтобы открытая страница не получила доступ
        // к окну портала и не увидела, откуда пришёл переход.
        builder.Append("\" rel=\"noopener noreferrer\" target=\"_blank\">");
        builder.Append(_encoder.Encode(url));
        builder.Append("</a>");
    }

    private static string TrimTrailingPunctuation(string url)
    {
        var end = url.Length;

        while (end > 0 && TrailingPunctuation.Contains(url[end - 1]))
        {
            end--;
        }

        // Если ссылка сократилась до схемы, значит это был не адрес — вернём как есть.
        return end == 0 ? url : url[..end];
    }
}
