using System.Text.Json;

namespace Portal.Client;

/// <summary>
/// Настройки программы. Лежат в профиле человека, не в папке программы:
/// на один компьютер может входить несколько сотрудников, и виджет
/// у каждого свой.
///
/// Формат — обычный JSON рядом: реестр для такого неудобен (его не видно
/// и не поправишь блокнотом), а ini разбирать вручную незачем.
/// </summary>
public sealed class Settings
{
    /// <summary>Адрес портала, например http://portal:8080.</summary>
    public string Server { get; set; } = "";

    public int X { get; set; }

    public int Y { get; set; }

    public int Width { get; set; } = 360;

    public int Height { get; set; } = 680;

    /// <summary>
    /// «desk» — виджет лежит на рабочем столе, под всеми окнами;
    /// «top» — висит поверх окон.
    ///
    /// Два режима, а не галочка «поверх всех»: на рабочем столе виджет
    /// не мешает работе и виден, когда свернули окна; поверх окон —
    /// для тех, кто смотрит в него постоянно.
    /// </summary>
    public string Mode { get; set; } = "desk";

    /// <summary>Виджет закреплён: его нельзя сдвинуть мышью случайно.</summary>
    public bool Locked { get; set; }

    /// <summary>Непрозрачность, проценты. Меньше 100 — виджет просвечивает.</summary>
    public int Opacity { get; set; } = 100;

    public bool Autostart { get; set; } = true;

    /// <summary>
    /// Ярлык на рабочем столе уже делали.
    ///
    /// Нужна, чтобы не создавать его заново при каждом запуске: человек
    /// мог ярлык удалить намеренно, и возвращать его против воли —
    /// навязчивость.
    /// </summary>
    public bool ShortcutMade { get; set; }

    /// <summary>Виджет уже ставили на экран — значит X и Y осмысленные.</summary>
    public bool Placed { get; set; }

    private static string Folder =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PortalWidget");

    private static string FilePath => Path.Combine(Folder, "widget.json");

    public static Settings Load()
    {
        var settings = new Settings();

        try
        {
            if (File.Exists(FilePath))
            {
                settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath)) ?? new Settings();
            }
        }
        catch (Exception)
        {
            // Испорченный файл настроек не должен мешать программе
            // запуститься: берём значения по умолчанию и живём дальше.
        }

        // Значения из файла проверяем: его мог поправить человек руками,
        // а окно шириной в три точки восстановить мышью уже нельзя.
        settings.Width = Clamp(settings.Width, 300, 600);
        settings.Height = Clamp(settings.Height, 420, 1400);
        settings.Opacity = Clamp(settings.Opacity, 40, 100);

        if (settings.Mode != "top")
        {
            settings.Mode = "desk";
        }

        return settings;
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Folder);

            File.WriteAllText(FilePath, JsonSerializer.Serialize(
                this, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (Exception)
        {
            // Не сохранилось — программа всё равно работает, просто
            // забудет положение окна. Ронять её из-за этого незачем.
        }
    }

    public static int Clamp(int value, int low, int high) =>
        value < low ? low : value > high ? high : value;

    /// <summary>
    /// Приводит адрес к виду http://имя:порт — без хвостовой косой черты
    /// и со схемой, если человек её не написал.
    /// </summary>
    public static string NormalizeServer(string? value)
    {
        var text = (value ?? "").Trim();

        if (text.Length == 0)
        {
            return "";
        }

        if (!text.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
            && !text.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
        {
            text = "http://" + text;
        }

        return text.TrimEnd('/');
    }
}
