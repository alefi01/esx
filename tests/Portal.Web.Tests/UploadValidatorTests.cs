using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Services.Storage;

namespace Portal.Web.Tests;

/// <summary>
/// Проверки загружаемых файлов: расширение, размер, квота, чистка имени.
/// </summary>
public class UploadValidatorTests
{
    private const long Mb = 1024 * 1024;

    private static UploadValidator Create() =>
        new(Options.Create(new StorageOptions()));

    [Theory]
    [InlineData("вирус.exe")]
    [InlineData("Вирус.EXE")]           // регистр значения не имеет
    [InlineData("скрипт.ps1")]
    [InlineData("макрос.js")]
    [InlineData("ярлык.lnk")]
    public void Опасные_расширения_не_принимаются(string fileName)
    {
        var rejection = Create().Validate(fileName, 1024, 50 * Mb, null, 0);

        Assert.NotNull(rejection);
        Assert.Contains("запрещ", rejection.Reason);
    }

    /// <summary>
    /// Ноль в качестве предела означает «без ограничения».
    ///
    /// Проверка нужна именно как проверка: пока такого правила не было,
    /// ноль означал бы «не пропускать вообще ничего», и папка со снятым
    /// пределом молча перестала бы принимать файлы.
    /// </summary>
    [Fact]
    public void Ноль_как_предел_означает_отсутствие_предела()
    {
        var rejection = Create().Validate("огромный.zip", 8L * 1024 * Mb, 0, null, 0);

        Assert.Null(rejection);
    }

    [Fact]
    public void Снятый_предел_не_отменяет_квоту_папки()
    {
        // Предела на файл нет, но общий объём папки всё равно ограничен.
        var rejection = Create().Validate("большой.zip", 900 * Mb, 0, 500 * Mb, 0);

        Assert.NotNull(rejection);
        Assert.Contains("не хватает места", rejection.Reason);
    }

    [Fact]
    public void Снятый_предел_не_отменяет_запрет_расширений()
    {
        var rejection = Create().Validate("вирус.exe", 1024, 0, null, 0);

        Assert.NotNull(rejection);
    }

    [Fact]
    public void Двойное_расширение_не_обманывает_проверку()
    {
        // Классический приём: человек видит «отчёт.pdf», а запускается .exe.
        var rejection = Create().Validate("отчёт.exe.pdf", 1024, 50 * Mb, null, 0);

        Assert.NotNull(rejection);
    }

    [Theory]
    [InlineData("договор.pdf")]
    [InlineData("смета.xlsx")]
    [InlineData("фото.jpg")]
    [InlineData("файл без расширения")]
    public void Обычные_файлы_проходят(string fileName)
    {
        Assert.Null(Create().Validate(fileName, 1024, 50 * Mb, null, 0));
    }

    [Fact]
    public void Пустой_файл_не_принимается()
    {
        var rejection = Create().Validate("пусто.txt", 0, 50 * Mb, null, 0);

        Assert.NotNull(rejection);
        Assert.Contains("пустой", rejection.Reason);
    }

    [Fact]
    public void Файл_больше_предела_папки_не_принимается()
    {
        var rejection = Create().Validate("большой.pdf", 60 * Mb, 50 * Mb, null, 0);

        Assert.NotNull(rejection);
        Assert.Contains("больше разрешённых", rejection.Reason);
    }

    [Fact]
    public void Файл_ровно_по_пределу_принимается()
    {
        Assert.Null(Create().Validate("ровно.pdf", 50 * Mb, 50 * Mb, null, 0));
    }

    [Fact]
    public void Превышение_квоты_папки_не_принимается()
    {
        // Квота 100 МБ, занято 95, грузим 10.
        var rejection = Create().Validate("файл.pdf", 10 * Mb, 50 * Mb, 100 * Mb, 95 * Mb);

        Assert.NotNull(rejection);
        Assert.Contains("не хватает места", rejection.Reason);
    }

    [Fact]
    public void В_пределах_квоты_файл_принимается()
    {
        Assert.Null(Create().Validate("файл.pdf", 4 * Mb, 50 * Mb, 100 * Mb, 95 * Mb));
    }

    [Theory]
    // Некоторые браузеры и программы присылают полный путь вместо имени.
    [InlineData(@"C:\Users\ivanov\Документы\отчёт.docx", "отчёт.docx")]
    [InlineData("/home/user/отчёт.docx", "отчёт.docx")]
    // Попытка выйти за пределы папки в имени файла.
    [InlineData(@"..\..\windows\system32\config", "config")]
    [InlineData("обычное имя.txt", "обычное имя.txt")]
    public void Имя_файла_очищается_от_пути(string input, string expected)
    {
        Assert.Equal(expected, UploadValidator.SanitizeName(input));
    }

    [Fact]
    public void Запрещённые_символы_в_имени_заменяются()
    {
        var result = UploadValidator.SanitizeName("от:чёт<>|?.txt");

        Assert.DoesNotContain(':', result);
        Assert.DoesNotContain('<', result);
        Assert.DoesNotContain('|', result);
    }

    [Theory]
    [InlineData(0, "0 Б")]
    [InlineData(512, "512 Б")]
    [InlineData(1024, "1 КБ")]
    [InlineData(1536, "1,5 КБ")]
    [InlineData(52428800, "50 МБ")]
    public void Размер_показывается_по_человечески(long bytes, string expected)
    {
        // Разделитель дробной части зависит от языка системы,
        // поэтому сравниваем без привязки к нему.
        Assert.Equal(
            expected.Replace(',', '.'),
            UploadValidator.Format(bytes).Replace(',', '.'));
    }

    [Theory]
    [InlineData("документ.pdf", "application/pdf")]
    [InlineData("картинка.png", "image/png")]
    [InlineData("неизвестно.qwerty", "application/octet-stream")]
    public void Тип_содержимого_определяется_по_расширению(string fileName, string expected)
    {
        Assert.Equal(expected, UploadValidator.ResolveContentType(fileName));
    }
}
