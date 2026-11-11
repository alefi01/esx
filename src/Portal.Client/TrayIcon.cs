using Microsoft.Win32;

namespace Portal.Client;

/// <summary>
/// Значок у часов: число непрочитанного, подсказка, меню и всплывающие
/// уведомления Windows.
///
/// ЗАЧЕМ ОН ВООБЩЕ
///
/// Это главное, ради чего программа и нужна. Браузер не может ни показать
/// окошко Windows без защищённого соединения, ни нарисовать число на своей
/// вкладке так, чтобы его было видно при свёрнутом окне. Программа может
/// и то и другое — и именно этим закрывается вопрос «работаю в другой
/// программе и ничего не замечаю».
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly NotifyIcon _icon = new();
    private readonly MainForm _form;

    private Icon? _drawn;
    private int _count = -1;
    private string _tone = "";

    public TrayIcon(MainForm form)
    {
        _form = form;

        _icon.Text = "Портал";
        _icon.Visible = true;

        Update(0, "calm", "Портал");

        _icon.DoubleClick += (_, _) => _form.ShowWidget();
        _icon.BalloonTipClicked += (_, _) => _form.ShowWidget();

        _icon.ContextMenuStrip = BuildMenu();
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();

        menu.Items.Add("Показать виджет", null, (_, _) => _form.ShowWidget());
        menu.Items.Add("Открыть портал", null, (_, _) => _form.OpenFull());
        menu.Items.Add(new ToolStripSeparator());

        var settings = _form.Current;

        var onTop = new ToolStripMenuItem("Поверх всех окон")
        {
            Checked = settings.Mode == "top",
            CheckOnClick = true
        };

        onTop.Click += (_, _) =>
        {
            settings.Mode = onTop.Checked ? "top" : "desk";
            settings.Save();
            _form.ShowWidget();
        };

        menu.Items.Add(onTop);

        var locked = new ToolStripMenuItem("Закрепить на месте")
        {
            Checked = settings.Locked,
            CheckOnClick = true
        };

        locked.Click += (_, _) =>
        {
            settings.Locked = locked.Checked;
            settings.Save();
        };

        menu.Items.Add(locked);

        var autostart = new ToolStripMenuItem("Запускать вместе с Windows")
        {
            Checked = settings.Autostart,
            CheckOnClick = true
        };

        autostart.Click += (_, _) =>
        {
            settings.Autostart = autostart.Checked;
            settings.Save();
            ApplyAutostart(settings.Autostart);
        };

        menu.Items.Add(autostart);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Сменить адрес портала…", null, (_, _) => _form.ChangeServer());
        menu.Items.Add("Выход", null, (_, _) => _form.QuitForReal());

        return menu;
    }

    /// <summary>
    /// Запуск вместе с Windows — строкой в разделе реестра для текущего
    /// пользователя. Не служба и не планировщик: программа работает
    /// в сеансе человека и показывает ЕГО задачи.
    /// </summary>
    public static void ApplyAutostart(bool on)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run", writable: true);

            if (key is null)
            {
                return;
            }

            if (on)
            {
                key.SetValue("PortalWidget", "\"" + Environment.ProcessPath + "\"");
            }
            else
            {
                key.DeleteValue("PortalWidget", throwOnMissingValue: false);
            }
        }
        catch (Exception)
        {
            // Политика могла запретить запись — не повод ронять программу.
        }
    }

    /// <summary>
    /// Число непрочитанного на значке и в подсказке.
    ///
    /// Значок рисуется кодом, а не берётся из набора картинок: числа
    /// от 1 до 99 картинками не нарисуешь, а ради числа всё и затевалось.
    /// </summary>
    public void Update(int count, string tone, string tip)
    {
        if (count == _count && tone == _tone)
        {
            _icon.Text = Shorten(tip);

            return;
        }

        _count = count;
        _tone = tone;

        var old = _drawn;

        _drawn = Draw(count, tone);
        _icon.Icon = _drawn;
        _icon.Text = Shorten(tip);

        old?.Dispose();
    }

    /// <summary>Подсказка у значка не может быть длиннее 63 знаков — обрежем сами.</summary>
    private static string Shorten(string text) =>
        text.Length <= 63 ? text : text[..62] + "…";

    private static Icon Draw(int count, string tone)
    {
        var background = tone switch
        {
            "alert" => Color.FromArgb(0xE5, 0x48, 0x4D),
            "warn" => Color.FromArgb(0xF2, 0xAE, 0x3C),
            "offline" => Color.FromArgb(0x8A, 0x8A, 0x8A),
            _ => Color.FromArgb(0x00, 0x67, 0xC0)
        };

        using var bitmap = new Bitmap(32, 32);
        using (var g = Graphics.FromImage(bitmap))
        {
            g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            g.TextRenderingHint = System.Drawing.Text.TextRenderingHint.AntiAliasGridFit;

            using var brush = new SolidBrush(background);
            g.FillEllipse(brush, 1, 1, 30, 30);

            if (count > 0)
            {
                var text = count > 99 ? "99" : count.ToString();
                var size = count > 9 ? 15f : 18f;

                using var font = new Font("Segoe UI", size, FontStyle.Bold, GraphicsUnit.Pixel);
                using var white = new SolidBrush(Color.White);
                using var format = new StringFormat
                {
                    Alignment = StringAlignment.Center,
                    LineAlignment = StringAlignment.Center
                };

                g.DrawString(text, font, white, new RectangleF(0, 0, 32, 32), format);
            }
            else
            {
                // Пусто — рисуем точку: значок должен отличаться от «есть
                // непрочитанное», но оставаться узнаваемым.
                using var white = new SolidBrush(Color.White);
                g.FillEllipse(white, 13, 13, 6, 6);
            }
        }

        return Icon.FromHandle(bitmap.GetHicon());
    }

    /// <summary>Всплывающее уведомление Windows — то самое, которого не может браузер.</summary>
    public void Balloon(string title, string body)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = body;
        _icon.BalloonTipIcon = ToolTipIcon.Info;
        _icon.ShowBalloonTip(8000);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
        _drawn?.Dispose();
    }
}
