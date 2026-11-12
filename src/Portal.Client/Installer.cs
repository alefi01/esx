using System.Diagnostics;
using System.Text;
using Microsoft.Win32;

namespace Portal.Client;

/// <summary>
/// Установка виджета на рабочий ПК.
///
/// ПОЧЕМУ УСТАНОВЩИК И ПРОГРАММА — ОДИН ФАЙЛ
///
/// Отдельный установщик на .NET весил бы столько же, сколько сама
/// программа: рантайм пришлось бы вложить в оба. Поэтому файл один.
/// Запущенный откуда угодно (из «Загрузок», с флешки), он понимает,
/// что ещё не установлен, ставит себя в профиль человека и запускается
/// уже оттуда. Запущенный из места установки — просто работает.
///
/// ПОЧЕМУ РАНТАЙМ WEBVIEW2 НЕ ВЛОЖЕН В ФАЙЛ
///
/// Он весит сотни мегабайт, а нужен в редком случае: на Windows 11 он
/// часть системы, на подавляющем большинстве Windows 10 уже стоит.
/// Вкладывать его в каждую загрузку значит гонять эти сотни мегабайт
/// по сети два десятка раз ради нескольких машин. Поэтому файл забирает
/// его с ПОРТАЛА и только если рантайма на машине нет.
///
/// Прав администратора установка не требует: без повышения прав рантайм
/// ставится «для пользователя», и программа живёт в профиле.
/// </summary>
public static class Installer
{
    /// <summary>Куда ставится программа.</summary>
    public static string InstallFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortalWidget");

    public static string InstalledExe => Path.Combine(InstallFolder, "portal-widget.exe");

    /// <summary>
    /// Программа запущена НЕ из места установки — значит её принесли
    /// и запустили, чтобы установить.
    /// </summary>
    public static bool NeedsInstall()
    {
        var current = Environment.ProcessPath;

        if (current is null)
        {
            return false;
        }

        return !string.Equals(
            Path.GetFullPath(current),
            Path.GetFullPath(InstalledExe),
            StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Ставит программу и запускает её из места установки.
    /// Возвращает false, если человек отказался или не получилось.
    /// </summary>
    public static bool Run(string server)
    {
        Log.Write("Установка: начало. Адрес портала: "
            + (server.Length > 0 ? server : "неизвестен"));

        var current = Environment.ProcessPath;

        if (current is null)
        {
            Log.Write("Установка: не удалось узнать путь к себе");

            return false;
        }

        var answer = MessageBox.Show(
            "Установить виджет портала на этот компьютер?\n\n"
            + "Программа встанет в вашу учётную запись и будет запускаться вместе с Windows. "
            + "Права администратора не нужны.",
            "Портал", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);

        if (answer != DialogResult.OK)
        {
            Log.Write("Установка: человек отказался");

            return false;
        }

        // 1. Компонент, на котором работает окно портала.
        if (!WebView2Present())
        {
            Log.Write("Установка: WebView2 не найден, спрашиваем человека");

            if (!InstallWebView2(server))
            {
                return false;
            }
        }

        // 2. Сама программа.
        try
        {
            Directory.CreateDirectory(InstallFolder);

            // Старый файл может быть занят работающей копией — тогда
            // отодвигаем его, как при обновлении.
            if (File.Exists(InstalledExe))
            {
                var old = InstalledExe + ".old";

                File.Delete(old);
                File.Move(InstalledExe, old);
            }

            File.Copy(current, InstalledExe, overwrite: true);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось скопировать программу:\n\n" + ex.Message,
                "Портал", MessageBoxButtons.OK, MessageBoxIcon.Error);

            return false;
        }

        // 3. Адрес портала запоминаем до первого запуска, чтобы человеку
        //    не пришлось вводить его руками.
        if (server.Length > 0)
        {
            var settings = Settings.Load();

            settings.Server = server;
            settings.Save();
        }

        // 4. Запускаем установленную копию и уходим.
        Process.Start(new ProcessStartInfo { FileName = InstalledExe, UseShellExecute = true });

        return true;
    }

    // ==================================================================
    // WebView2
    // ==================================================================

    /// <summary>
    /// Есть ли на машине рантайм WebView2.
    ///
    /// Проверяется так, как предлагает сама Microsoft: значение pv
    /// в разделе обновлений Edge. Оно бывает в машинной ветке реестра
    /// и в пользовательской — рантайм можно поставить и так и так.
    /// </summary>
    public static bool WebView2Present()
    {
        const string guid = "{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}";

        string?[] places =
        [
            ReadVersion(Registry.LocalMachine, $@"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{guid}"),
            ReadVersion(Registry.LocalMachine, $@"SOFTWARE\Microsoft\EdgeUpdate\Clients\{guid}"),
            ReadVersion(Registry.CurrentUser, $@"Software\Microsoft\EdgeUpdate\Clients\{guid}")
        ];

        return places.Any(version => !string.IsNullOrEmpty(version) && version != "0.0.0.0");
    }

    private static string? ReadVersion(RegistryKey root, string path)
    {
        try
        {
            using var key = root.OpenSubKey(path);

            return key?.GetValue("pv") as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Забирает установщик рантайма с портала и ставит его тихо.
    ///
    /// Файл лежит НА ПОРТАЛЕ, а не внутри этой программы: см. пояснение
    /// наверху. Если администратор его туда не положил, честно говорим,
    /// что нужно сделать, — молча падать нельзя.
    /// </summary>
    private static bool InstallWebView2(string server)
    {
        if (server.Length == 0)
        {
            Log.Write("Установка: адрес портала неизвестен, качать компонент неоткуда");

            MessageBox.Show(
                "На этом компьютере нет компонента Microsoft Edge WebView2 Runtime, "
                + "а адрес портала неизвестен — скачать его неоткуда.\n\n"
                + "Скачайте установщик виджета со страницы портала: он знает адрес "
                + "и заберёт недостающее сам.",
                "Портал", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            return false;
        }

        // Окно с объяснением и кнопкой, а не тихое скачивание: раньше
        // программа молча уходила за стомегабайтным файлом и минуту
        // выглядела зависшей.
        using var ask = new WebView2Prompt(server);

        ask.ShowDialog();

        if (!ask.Installed)
        {
            Log.Write("Установка: без WebView2 продолжать нельзя");
        }

        return ask.Installed;
    }

    // ==================================================================
    // Адрес портала внутри файла
    // ==================================================================

    /// <summary>
    /// Метка, которой портал дописывает свой адрес в конец файла,
    /// когда отдаёт его на скачивание.
    /// </summary>
    public const string AddressMarker = "\n[PORTAL-ADDRESS]";

    /// <summary>
    /// Читает адрес портала из хвоста собственного файла.
    ///
    /// ЗАЧЕМ ТАК
    ///
    /// Человек скачивает программу со страницы портала и запускает.
    /// Спрашивать у него адрес сервера в этот момент — значит заставить
    /// идти выяснять то, что порталу и так известно. Поэтому портал
    /// дописывает адрес в конец файла при скачивании.
    ///
    /// Для Windows такой хвост невидим: программа запускается по тому,
    /// что написано в её заголовке, а лишние байты в конце она не читает.
    /// </summary>
    public static string ReadAddressFromTail()
    {
        try
        {
            var path = Environment.ProcessPath;

            if (path is null)
            {
                return "";
            }

            using var stream = File.OpenRead(path);

            // Хвост заведомо короткий — читаем последний килобайт.
            var size = (int)Math.Min(1024, stream.Length);

            stream.Seek(-size, SeekOrigin.End);

            var buffer = new byte[size];

            stream.ReadExactly(buffer, 0, size);

            var tail = Encoding.UTF8.GetString(buffer);
            var at = tail.LastIndexOf(AddressMarker, StringComparison.Ordinal);

            if (at < 0)
            {
                return "";
            }

            return Settings.NormalizeServer(tail[(at + AddressMarker.Length)..]);
        }
        catch (Exception)
        {
            return "";
        }
    }
}
