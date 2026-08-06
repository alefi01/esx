using Portal.Web.Pages.Shared;

namespace Portal.Web.Tests;

/// <summary>
/// Согласование числительных.
///
/// Мелочь, но заметная: «1 объектов» на странице читается как недоделка.
/// Правило с исключением на 11–14 легко забыть, поэтому оно закреплено здесь.
/// </summary>
public class PluralTests
{
    [Theory]
    [InlineData(0, "объектов")]
    [InlineData(1, "объект")]
    [InlineData(2, "объекта")]
    [InlineData(4, "объекта")]
    [InlineData(5, "объектов")]
    [InlineData(10, "объектов")]
    // Исключение: 11–14 всегда «объектов», хотя оканчиваются на 1, 2, 3, 4.
    [InlineData(11, "объектов")]
    [InlineData(12, "объектов")]
    [InlineData(14, "объектов")]
    [InlineData(21, "объект")]
    [InlineData(22, "объекта")]
    [InlineData(25, "объектов")]
    [InlineData(101, "объект")]
    [InlineData(111, "объектов")]
    [InlineData(1002, "объекта")]
    public void Форма_слова_выбирается_по_числу(long count, string expected) =>
        Assert.Equal(expected, Plural.Of(count, "объект", "объекта", "объектов"));

    [Fact]
    public void Число_и_слово_вместе()
    {
        Assert.Equal("1 файл", Plural.With(1, "файл", "файла", "файлов"));
        Assert.Equal("3 файла", Plural.With(3, "файл", "файла", "файлов"));
        Assert.Equal("13 файлов", Plural.With(13, "файл", "файла", "файлов"));
    }
}
