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
        // ---------- Журнал и перехват поломок ----------
        //
        // Программа работает на чужом компьютере, где нет ни консоли,
        // ни отладчика. Если она не запускается, единственный способ
        // понять почему — чтобы она сама об этом написала.
        //
        // Перехватываем ВСЁ: и ошибки в окне, и ошибки в фоновых
        // задачах. Без этого необработанная ошибка закрывает программу
        // молча, и человек видит ровно ничего: щёлкнул — ничего
        // не произошло.
        Log.Write("Запуск. Файл: " + (Environment.ProcessPath ?? "неизвестно")
            + "; версия: " + (typeof(Program).Assembly.GetName().Version?.ToString() ?? "?")
            + "; Windows: " + Environment.OSVersion.Version);

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            if (e.ExceptionObject is Exception error)
            {
                Log.Fail("вне окна", error);
                Complain(error);
            }
        };

        Application.ThreadException += (_, e) =>
        {
            Log.Fail("в окне", e.Exception);
            Complain(e.Exception);
        };

        try
        {
            Run(args);
        }
        catch (Exception error)
        {
            Log.Fail("при запуске", error);
            Complain(error);
        }
    }

    /// <summary>
    /// Сказать человеку, что случилось, и куда смотреть.
    ///
    /// Окно с текстом ошибки, а не молчание: «щёлкнул — ничего
    /// не произошло» не оставляет человеку ни одного следующего шага.
    /// </summary>
    private static void Complain(Exception error)
    {
        try
        {
            MessageBox.Show(
                "Виджет портала не смог запуститься.\n\n"
                + error.GetType().Name + ": " + error.Message
                + "\n\nПодробности записаны в файл:\n" + Log.Path
                + "\n\nПокажите этот файл администратору.",
                "Портал", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        catch (Exception)
        {
            // Даже окно показать не вышло — остаётся только журнал.
        }
    }

    private static void Run(string[] args)
    {
        ApplicationConfiguration.Initialize();

        // Адрес портала: из командной строки или из настроек. Если
        // не задан нигде, окно само спросит его при первом открытии.
        var fromArgs = "";

        for (var i = 0; i < args.Length - 1; i++)
        {
            if (string.Equals(args[i], "-server", StringComparison.OrdinalIgnoreCase))
            {
                fromArgs = Settings.NormalizeServer(args[i + 1]);
            }
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

        // Ярлык на рабочем столе — один раз, при первом запуске.
        //
        // Программа больше не копирует себя в профиль и не пишет
        // в реестр автозапуска: для антивируса это узнаваемая связка
        // (файл сам себя размножает и закрепляется в системе), и
        // Касперский такой файл удалял — отличить по поведению было
        // нельзя. Теперь она работает оттуда, куда её положили,
        // а закрепление сводится к обычным ярлыкам.
        if (!settings.ShortcutMade)
        {
            Shortcut.EnsureDesktop();

            settings.ShortcutMade = true;
            settings.Save();
        }

        Shortcut.ApplyAutostart(settings.Autostart);

        // Без этого компонента окно портала не покажется вовсе,
        // поэтому проверяем до открытия окна и предлагаем доставить.
        if (!Installer.WebView2Present())
        {
            Log.Write("WebView2 не найден");

            using var ask = new WebView2Prompt(settings.Server);

            ask.ShowDialog();

            if (!ask.Installed)
            {
                return;
            }
        }

        Log.Write("Окно открывается. Адрес портала: "
            + (settings.Server.Length == 0 ? "не задан" : settings.Server));

        Application.Run(new MainForm(settings));

        Log.Write("Программа закрыта.");
    }
}
