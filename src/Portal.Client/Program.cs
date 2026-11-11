namespace Portal.Client;

/// <summary>
/// Точка входа виджета портала.
///
/// Файл один и для установки, и для работы: запущенный из «Загрузок»
/// он сначала ставит себя в профиль (см. <see cref="Installer"/>),
/// запущенный из места установки — работает.
///
/// Одна копия на человека: вторая находит первую, просит её показаться
/// и закрывается. Иначе два значка у часов и два окна, спорящих,
/// кто из них наверху.
/// </summary>
internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Адрес портала: из командной строки, из хвоста собственного
        // файла (его дописывает портал при скачивании) или из настроек.
        var fromArgs = "";

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase))
            {
                fromArgs = Settings.NormalizeServer(args[i + 1]);
            }
        }

        // Установка — ДО захвата признака единственной копии: установленная
        // копия запускается отсюда же и должна этот признак получить.
        if (Installer.NeedsInstall())
        {
            var address = fromArgs.Length > 0 ? fromArgs : Installer.ReadAddressFromTail();

            if (address.Length == 0)
            {
                address = Settings.Load().Server;
            }

            Installer.Run(address);

            return;
        }

        using var single = new Mutex(initiallyOwned: true, @"Local\PortalWidget", out var first);

        if (!first)
        {
            // Просим уже работающую копию показаться. Сообщение
            // широковещательное: искать окно по имени ненадёжно,
            // а это сообщение зарегистрировано под своим названием
            // и дойдёт только до нашей программы.
            Native.PostMessage(
                Native.HwndBroadcast,
                Native.RegisterWindowMessage("PortalWidget.Show"),
                IntPtr.Zero,
                IntPtr.Zero);

            return;
        }

        var settings = Settings.Load();

        if (fromArgs.Length > 0)
        {
            settings.Server = fromArgs;
            settings.Save();
        }

        TrayIcon.ApplyAutostart(settings.Autostart);

        Application.Run(new MainForm(settings));
    }
}
