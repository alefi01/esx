using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.Options;
using Portal.Web.Configuration;
using Portal.Web.Data;

namespace Portal.Web.Services.Storage;

/// <summary>Что не так с файлом, который пытаются загрузить.</summary>
public sealed record UploadRejection(string FileName, string Reason);

/// <summary>
/// Проверки, которые файл должен пройти до того, как что-то запишется на диск.
///
/// Порядок важен: сначала дешёвые проверки (имя, расширение, размер),
/// и только потом дорогая — свободное место в квоте.
/// </summary>
public sealed class UploadValidator
{
    private readonly StorageOptions _options;
    private readonly HashSet<string> _blocked;

    // Определитель типа содержимого по расширению — штатный, из ASP.NET Core.
    // Своего списка «расширение → тип» заводить не нужно.
    private static readonly FileExtensionContentTypeProvider ContentTypes = new();

    public UploadValidator(IOptions<StorageOptions> options)
    {
        _options = options.Value;

        _blocked = new HashSet<string>(
            _options.BlockedExtensions.Select(e => e.Trim().ToLowerInvariant()),
            StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Проверяет один файл. Возвращает null, если всё в порядке,
    /// либо причину отказа — понятную человеку, а не разработчику.
    /// </summary>
    /// <param name="fileName">Имя файла, как его прислал браузер.</param>
    /// <param name="sizeBytes">Размер файла.</param>
    /// <param name="maxFileSizeBytes">Предел на один файл, действующий для этой папки.</param>
    /// <param name="quotaBytes">Квота папки, если задана.</param>
    /// <param name="usedBytes">Сколько в папке уже занято.</param>
    public UploadRejection? Validate(
        string fileName,
        long sizeBytes,
        long maxFileSizeBytes,
        long? quotaBytes,
        long usedBytes)
    {
        var safeName = SanitizeName(fileName);

        if (string.IsNullOrWhiteSpace(safeName))
        {
            return new UploadRejection(fileName, "у файла пустое имя");
        }

        if (sizeBytes <= 0)
        {
            return new UploadRejection(safeName, "файл пустой");
        }

        var extension = Path.GetExtension(safeName);

        if (!string.IsNullOrEmpty(extension) && _blocked.Contains(extension))
        {
            return new UploadRejection(safeName,
                $"файлы с расширением {extension} загружать запрещено — " +
                "это исполняемый или потенциально опасный тип");
        }

        // Двойное расширение вида «отчёт.pdf.exe» — старый приём: человек видит
        // в списке привычный значок и первое расширение, а запускается последнее.
        // Проверка выше уже поймала бы этот случай, но такие имена стоит
        // отклонять и когда опасным оказалось не последнее расширение.
        var allExtensions = safeName.Split('.').Skip(1).Select(part => "." + part.ToLowerInvariant());

        if (allExtensions.Any(_blocked.Contains))
        {
            return new UploadRejection(safeName,
                "в имени файла есть запрещённое расширение");
        }

        if (sizeBytes > maxFileSizeBytes)
        {
            return new UploadRejection(safeName,
                $"размер {Format(sizeBytes)} больше разрешённых для этой папки {Format(maxFileSizeBytes)}");
        }

        if (quotaBytes is { } quota && usedBytes + sizeBytes > quota)
        {
            var free = Math.Max(0, quota - usedBytes);

            return new UploadRejection(safeName,
                $"в папке не хватает места: свободно {Format(free)}, требуется {Format(sizeBytes)}");
        }

        return null;
    }

    /// <summary>
    /// Приводит имя файла к безопасному виду.
    ///
    /// Браузеры в норме присылают только имя, но Internet Explorer и некоторые
    /// программы шлют полный путь вида C:\Users\ivanov\отчёт.docx.
    /// Плюс имя приходит от клиента и может содержать что угодно, включая
    /// «..\..\» и символы, запрещённые в файловой системе.
    ///
    /// На диск это имя всё равно не попадает (там идентификаторы), но оно
    /// уходит в заголовок ответа при скачивании и показывается на странице,
    /// поэтому чистим.
    /// </summary>
    public static string SanitizeName(string fileName)
    {
        var name = (fileName ?? string.Empty).Trim();

        // Отрезаем всё, что похоже на путь.
        var slash = name.LastIndexOfAny(['/', '\\']);

        if (slash >= 0)
        {
            name = name[(slash + 1)..];
        }

        foreach (var invalid in InvalidNameChars)
        {
            name = name.Replace(invalid, '_');
        }

        // Точки в начале и в конце Windows не любит.
        name = name.Trim(' ', '.');

        return name.Length > 200 ? name[..200] : name;
    }

    /// <summary>
    /// Символы, недопустимые в имени файла.
    ///
    /// Список задан явно, а не взят из Path.GetInvalidFileNameChars():
    /// тот возвращает РАЗНОЕ на разных системах. В Linux запрещены только
    /// «/» и нулевой байт, в Windows — ещё двоеточие, звёздочка, кавычки и
    /// вертикальная черта. Портал работает на Windows, но автотесты идут
    /// и на других системах, и поведение должно совпадать: иначе тест
    /// проходит там, где на сервере всё сломается.
    /// </summary>
    private static readonly char[] InvalidNameChars =
    [
        '\0', '/', '\\', ':', '*', '?', '"', '<', '>', '|',
        '\r', '\n', '\t'
    ];

    /// <summary>Тип содержимого по расширению — для правильного скачивания и будущего предпросмотра.</summary>
    public static string ResolveContentType(string fileName) =>
        ContentTypes.TryGetContentType(fileName, out var contentType)
            ? contentType
            : "application/octet-stream";

    /// <summary>Человекочитаемый размер: «12,3 МБ» вместо «12897485».</summary>
    public static string Format(long bytes)
    {
        string[] units = ["Б", "КБ", "МБ", "ГБ", "ТБ"];

        double value = bytes;
        var unit = 0;

        while (value >= 1024 && unit < units.Length - 1)
        {
            value /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes} {units[unit]}"
            : $"{value:0.#} {units[unit]}";
    }
}
