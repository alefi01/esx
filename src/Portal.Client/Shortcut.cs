namespace Portal.Client;

/// <summary>
/// Ярлыки: на рабочем столе и в автозагрузке.
///
/// ПОЧЕМУ ЯРЛЫКИ, А НЕ УСТАНОВКА И НЕ РЕЕСТР
///
/// Раньше программа копировала себя в профиль пользователя и прописывала
/// себя в раздел реестра автозапуска. Для антивируса это узнаваемая
/// связка: файл сам себя размножает и закрепляется в системе — ровно так
/// ведёт себя то, от чего антивирус и защищает. Касперский этот файл
/// удалял, и правильно делал: отличить по поведению было нельзя.
///
/// Теперь программа работает оттуда, куда её положили, а «закрепление»
/// сводится к двум ярлыкам. Ярлык — это обычный файл, его видно
/// в проводнике, его можно удалить мышкой, и он никого не настораживает.
///
/// Ярлык создаётся через WScript.Shell — тот же способ, которым
/// пользуются обычные установщики и скрипты администраторов. Сторонние
/// библиотеки для этого не нужны.
/// </summary>
public static class Shortcut
{
    /// <summary>Ярлык на рабочем столе. Создаётся один раз, при первом запуске.</summary>
    public static void EnsureDesktop()
    {
        var desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);

        Make(Path.Combine(desktop, "Портал.lnk"));
    }

    /// <summary>
    /// Запуск вместе с Windows — ярлыком в папке «Автозагрузка».
    ///
    /// Не реестр: строка в разделе Run не видна человеку и выглядит
    /// для антивируса как закрепление в системе. Папку автозагрузки
    /// человек может открыть и посмотреть, что в ней лежит.
    /// </summary>
    public static void ApplyAutostart(bool on)
    {
        var startup = Environment.GetFolderPath(Environment.SpecialFolder.Startup);
        var path = Path.Combine(startup, "Портал.lnk");

        try
        {
            if (on)
            {
                Make(path);
            }
            else if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception error)
        {
            Log.Fail("автозапуск", error);
        }
    }

    /// <summary>Есть ли ярлык в автозагрузке — по нему и показывается галочка в меню.</summary>
    public static bool AutostartOn()
    {
        try
        {
            return File.Exists(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Startup), "Портал.lnk"));
        }
        catch (Exception)
        {
            return false;
        }
    }

    /// <summary>
    /// Создаёт ярлык на саму программу. Уже существующий переписывается:
    /// файл могли перенести, и ярлык должен вести туда, где он теперь.
    /// </summary>
    private static void Make(string path)
    {
        var target = Environment.ProcessPath;

        if (target is null)
        {
            return;
        }

        try
        {
            var shellType = Type.GetTypeFromProgID("WScript.Shell");

            if (shellType is null)
            {
                Log.Write("Ярлык: WScript.Shell недоступен");

                return;
            }

            dynamic shell = Activator.CreateInstance(shellType)!;
            dynamic link = shell.CreateShortcut(path);

            link.TargetPath = target;
            link.WorkingDirectory = Path.GetDirectoryName(target) ?? "";
            link.Description = "Портал — виджет рабочего стола";
            link.IconLocation = target + ",0";

            link.Save();

            Log.Write("Ярлык создан: " + path);
        }
        catch (Exception error)
        {
            // Без ярлыка программа работает: он удобство, а не условие.
            Log.Fail("ярлык " + path, error);
        }
    }
}
