using System.Text.Encodings.Web;
using System.Text.Unicode;
using Portal.Web.Services.Text;

namespace Portal.Web.Tests;

/// <summary>
/// Проверка превращения текста объявления в HTML.
///
/// Это место в портале самое опасное: сюда попадает текст, который написал
/// пользователь, и результат отдаётся браузеру как разметка. Ошибка здесь —
/// это XSS: чужой скрипт выполняется в браузере у всех, кто открыл ленту.
/// Поэтому проверок больше, чем кажется нужным.
/// </summary>
public class PlainTextFormatterTests
{
    /// <summary>
    /// Кодировщик берём такой же, как настроен в приложении: кириллица выводится
    /// как есть, экранируются только опасные для разметки символы.
    /// Иначе тесты проверяли бы не то поведение, которое будет на сервере.
    /// </summary>
    private static readonly PlainTextFormatter Formatter = new(
        HtmlEncoder.Create(new TextEncoderSettings(UnicodeRanges.BasicLatin, UnicodeRanges.Cyrillic)));

    private static string Render(string? text)
    {
        using var writer = new StringWriter();

        Formatter.ToHtml(text).WriteTo(writer, HtmlEncoder.Default);

        return writer.ToString();
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Пустой_текст_даёт_пустой_результат(string? text)
    {
        Assert.Equal("", Render(text));
    }

    [Fact]
    public void Обычный_текст_остаётся_текстом()
    {
        Assert.Equal("Завтра отключат воду", Render("Завтра отключат воду"));
    }

    [Fact]
    public void Переносы_строк_превращаются_в_разметку()
    {
        Assert.Equal("первая<br />вторая", Render("первая\nвторая"));
        Assert.Equal("первая<br />вторая", Render("первая\r\nвторая"));
    }

    [Theory]
    [InlineData("<script>alert(1)</script>", "&lt;script&gt;alert(1)&lt;/script&gt;")]
    [InlineData("<b>жирный</b>", "&lt;b&gt;жирный&lt;/b&gt;")]
    [InlineData("<img src=x onerror=alert(1)>", "&lt;img src=x onerror=alert(1)&gt;")]
    [InlineData("a & b", "a &amp; b")]
    public void Разметка_из_текста_экранируется(string input, string expected)
    {
        Assert.Equal(expected, Render(input));
    }

    [Fact]
    public void Ссылка_становится_кликабельной()
    {
        var html = Render("Подробности на https://example.com/page");

        Assert.Contains("<a href=\"https://example.com/page\"", html);
        Assert.Contains("rel=\"noopener noreferrer\"", html);
        Assert.Contains(">https://example.com/page</a>", html);
    }

    [Fact]
    public void Адрес_без_схемы_получает_https()
    {
        var html = Render("см. www.example.com");

        Assert.Contains("<a href=\"https://www.example.com\"", html);
        // А подпись остаётся такой, как её написал человек.
        Assert.Contains(">www.example.com</a>", html);
    }

    [Fact]
    public void Точка_в_конце_предложения_не_попадает_в_ссылку()
    {
        var html = Render("Зайдите на https://example.com.");

        Assert.Contains("href=\"https://example.com\"", html);
        Assert.EndsWith("</a>.", html);
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("data:text/html,<script>alert(1)</script>")]
    [InlineData("vbscript:msgbox(1)")]
    [InlineData("file:///C:/Windows/System32")]
    public void Опасные_схемы_ссылками_не_становятся(string input)
    {
        var html = Render(input);

        // Никакого тега <a> тут быть не должно — только экранированный текст.
        Assert.DoesNotContain("<a href", html);
    }

    [Fact]
    public void Кавычка_в_адресе_не_ломает_атрибут()
    {
        // Если бы адрес подставлялся без кодирования, кавычка закрыла бы
        // атрибут href и позволила дописать к тегу свои атрибуты.
        var html = Render("https://example.com/\"onmouseover=\"alert(1)");

        Assert.DoesNotContain("onmouseover=\"alert", html);
    }

    [Fact]
    public void Текст_вокруг_нескольких_ссылок_сохраняется_целиком()
    {
        var html = Render("до https://a.example середина https://b.example после");

        Assert.StartsWith("до <a href=\"https://a.example\"", html);
        Assert.Contains("</a> середина <a href=\"https://b.example\"", html);
        Assert.EndsWith("</a> после", html);
    }

    [Fact]
    public void Разметка_внутри_ссылки_экранируется()
    {
        var html = Render("https://example.com/<script>");

        Assert.DoesNotContain("<script>", html);
    }
}
