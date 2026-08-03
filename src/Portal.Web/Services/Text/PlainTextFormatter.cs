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

            builder.Append(_encoder.Encode(text[start..i].ToString()));
            builder.Append("<br />");
            start = i + 1;
        }

        if (start < text.Length)
        {
            builder.Append(_encoder.Encode(text[start..].ToString()));
        }
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
