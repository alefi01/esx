using System.Diagnostics;
using System.Text.Json;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace Portal.Client;

/// <summary>
/// Окно программы. Одно на все три облика:
///
///   * виджет — узкое окно на рабочем столе, всегда на виду;
///   * окно ответа — побольше, по центру, для одной задачи;
///   * полное окно — портал целиком.
///
/// ПОЧЕМУ ОДНО ОКНО, А НЕ ТРИ
///
/// Страница в нём живёт одна и та же, с одним входом и одним открытым
/// соединением уведомлений. Три окна означали бы три копии WebView2,
/// то есть втрое больше памяти на каждом рабочем ПК, и три места,
/// где человек может оказаться «не вошедшим».
/// </summary>
public sealed class MainForm : Form
{
    private const int HotKeyId = 1;

    private readonly Settings _settings;
    private readonly WebView2 _web = new();
    private readonly TrayIcon _tray;
    private readonly uint _showMessage = Native.RegisterWindowMessage("PortalWidget.Show");

    /// <summary>Какой облик сейчас: widget, answer, full.</summary>
    private string _shape = "widget";

    /// <summary>Окно «в лоб»: поверх всего, закрыть нельзя, пока не ответят.</summary>
    private bool _alert;

    private bool _closingForReal;

    public MainForm(Settings settings)
    {
        _settings = settings;

        FormBorderStyle = FormBorderStyle.None;
        ShowInTaskbar = false;
        StartPosition = FormStartPosition.Manual;
        Text = "Портал";
        MinimumSize = new Size(300, 300);
        BackColor = Color.White;

        _web.Dock = DockStyle.Fill;
        Controls.Add(_web);

        _tray = new TrayIcon(this);

        Load += async (_, _) => await StartAsync();
        FormClosing += OnFormClosing;
    }

    // ==================================================================
    // Запуск
    // ==================================================================

    private async Task StartAsync()
    {
        ApplyShape();

        // Звук уведомления должен играть без щелчка по странице: браузеры
        // это запрещают, и правильно делают — но здесь окно и есть портал,
        // а не случайная страница из интернета.
        var options = new CoreWebView2EnvironmentOptions
        {
            AdditionalBrowserArguments = "--autoplay-policy=no-user-gesture-required"
        };

        var dataFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PortalWidget", "WebView2");

        CoreWebView2Environment environment;

        try
        {
            environment = await CoreWebView2Environment.CreateAsync(null, dataFolder, options);
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Не удалось запустить окно портала.\n\n" + ex.Message +
                "\n\nВозможно, на этом компьютере нет компонента Microsoft Edge WebView2 Runtime. " +
                "Запустите установщик виджета ещё раз — он поставит недостающее.",
                "Портал", MessageBoxButtons.OK, MessageBoxIcon.Warning);

            Environment.Exit(1);

            return;
        }

        await _web.EnsureCoreWebView2Async(environment);

        var core = _web.CoreWebView2;

        // Меню по правой кнопке и клавиши браузера ни к чему: это окно
        // программы, а не вкладка. «Назад» при этом остаётся — он нужен.
        core.Settings.AreDefaultContextMenusEnabled = false;
        core.Settings.AreDevToolsEnabled = false;
        core.Settings.IsStatusBarEnabled = false;

        core.WebMessageReceived += OnWebMessage;
        core.NewWindowRequested += OnNewWindow;

        // ---------- Перетаскивание файлов снаружи ----------
        //
        // Файл из «Проводника» можно бросить прямо в окно: страница
        // переписки это умеет, и в программе должно работать так же,
        // как в браузере. Свойство выставляем явно — значение
        // по умолчанию у разных версий WebView2 разное, и молча
        // зависеть от него нельзя.
        _web.AllowExternalDrop = true;

        // ---------- Скачивание ----------
        //
        // Щелчок по файлу в переписке должен ОТКРЫВАТЬ файл, а не
        // оставлять его где-то в «Загрузках». Поэтому полосу скачивания
        // прячем, файл кладём в свою папку и открываем сами —
        // см. OnDownload.
        core.DownloadStarting += OnDownload;

        // Мостик, через который страница портала управляет окном.
        // Вызовы уходят сообщением, а не вызовом объекта: так страница
        // остаётся обычной страницей и работает в браузере без изменений —
        // там просто нет window.chrome.webview, и портал это видит.
        await core.AddScriptToExecuteOnDocumentCreatedAsync(BridgeScript);

        Navigate();

        _ = Updater.RunAsync(this, _settings);
    }

    /// <summary>
    /// Что подставляется странице. Небольшой и заведомо безобидный: все
    /// решения принимает программа, страница только просит.
    /// </summary>
    private const string BridgeScript = """
        window.portalWidget = {
            version: '1.0.0',
            send: function (cmd, data) {
                try { window.chrome.webview.postMessage(JSON.stringify({ cmd: cmd, data: data || null })); }
                catch (e) { /* программа не слушает — значит открыли в браузере */ }
            },
            status: function (count, tone, tip) { this.send('status', { count: count, tone: tone, tip: tip }); },
            notify: function (title, body) { this.send('notify', { title: title, body: body }); },
            alert: function (on) { this.send('alert', { on: !!on }); },
            shape: function (name) { this.send('shape', { name: name }); },
            drag: function () { this.send('drag'); },
            hide: function () { this.send('hide'); },
            quit: function () { this.send('quit'); }
        };
        """;

    private void Navigate()
    {
        if (_settings.Server.Length == 0)
        {
            ShowServerPage();

            return;
        }

        // Виджет открывает свою лёгкую страницу, а не главную портала:
        // в окно шириной в треть экрана каркас портала не помещается,
        // да и грузить его там незачем.
        _web.CoreWebView2.Navigate(_settings.Server + "/Widget");
    }

    /// <summary>
    /// Страница «куда подключаться». Показывается, пока адрес не задан
    /// или портал не отвечает: пустое белое окно в этом случае выглядит
    /// как сломанная программа.
    /// </summary>
    private void ShowServerPage(string? problem = null)
    {
        var note = problem is null
            ? ""
            : $"<p class=\"err\">{System.Net.WebUtility.HtmlEncode(problem)}</p>";

        _web.CoreWebView2.NavigateToString($$"""
            <!doctype html><html lang="ru"><head><meta charset="utf-8">
            <style>
              body{font:14px/1.5 "Segoe UI",system-ui,sans-serif;margin:0;padding:22px;
                   background:#fff;color:#17151f}
              h1{font-size:17px;margin:0 0 10px}
              p{color:#58546c;margin:0 0 14px}
              .err{color:#b42318;font-weight:600}
              input{width:100%;box-sizing:border-box;min-height:38px;padding:0 10px;
                    border:1px solid #dfdcea;border-radius:8px;font:inherit}
              button{margin-top:12px;min-height:38px;padding:0 16px;border:0;border-radius:8px;
                     background:#0067c0;color:#fff;font:inherit;font-weight:600;cursor:pointer}
            </style></head><body>
              <h1>Подключение к порталу</h1>
              {{note}}
              <p>Адрес портала подскажет администратор. Обычно это имя сервера и порт.</p>
              <input id="a" placeholder="http://portal:8080" value="{{System.Net.WebUtility.HtmlEncode(_settings.Server)}}">
              <button id="go">Подключиться</button>
              <script>
                document.getElementById('go').onclick = function () {
                  window.portalWidget.send('server', { url: document.getElementById('a').value });
                };
                document.getElementById('a').addEventListener('keydown', function (e) {
                  if (e.key === 'Enter') { document.getElementById('go').click(); }
                });
              </script>
            </body></html>
            """);
    }

    // ==================================================================
    // Сообщения со страницы
    // ==================================================================

    private void OnWebMessage(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        string raw;

        try
        {
            raw = e.TryGetWebMessageAsString();
        }
        catch (Exception)
        {
            return;
        }

        JsonElement root;

        try
        {
            root = JsonDocument.Parse(raw).RootElement;
        }
        catch (JsonException)
        {
            return;
        }

        var cmd = root.TryGetProperty("cmd", out var c) ? c.GetString() : null;
        var data = root.TryGetProperty("data", out var d) ? d : default;

        switch (cmd)
        {
            case "status":
                _tray.Update(
                    Number(data, "count"),
                    Str(data, "tone") ?? "calm",
                    Str(data, "tip") ?? "Портал");
                break;

            case "notify":
                _tray.Balloon(Str(data, "title") ?? "Портал", Str(data, "body") ?? "");
                break;

            case "alert":
                SetAlert(Flag(data, "on"));
                break;

            case "shape":
                SetShape(Str(data, "name") ?? "widget");
                break;

            case "drag":
                if (!_settings.Locked && _shape == "widget")
                {
                    Native.ReleaseCapture();
                    Native.SendMessage(Handle, Native.WmNcLButtonDown, Native.HtCaption, IntPtr.Zero);
                }

                break;

            case "hide":
                HideWidget();
                break;

            case "quit":
                QuitForReal();
                break;

            case "server":
                SetServer(Str(data, "url") ?? "");
                break;
        }
    }

    private static string? Str(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int Number(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.TryGetInt32(out var number)
            ? number
            : 0;

    private static bool Flag(JsonElement data, string name) =>
        data.ValueKind == JsonValueKind.Object && data.TryGetProperty(name, out var value)
        && value.ValueKind == JsonValueKind.True;

    /// <summary>
    /// Ссылки «в новом окне» открываем в самом окне программы.
    ///
    /// Иначе щелчок по вложению открывал бы пустое окно WebView2 без
    /// каркаса — человек получил бы белый прямоугольник без объяснений.
    /// </summary>
    private void OnNewWindow(object? sender, CoreWebView2NewWindowRequestedEventArgs e)
    {
        e.Handled = true;

        SetShape("full");

        _web.CoreWebView2.Navigate(e.Uri);
    }

    // ==================================================================
    // Скачанные файлы
    // ==================================================================

    /// <summary>Куда складываются файлы, открытые из переписки.</summary>
    private static string DownloadsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "PortalWidget", "Файлы");

    /// <summary>
    /// Расширения, которые открываются сразу.
    ///
    /// ПОЧЕМУ СПИСОК, А НЕ «ОТКРЫВАТЬ ВСЁ»
    ///
    /// Открыть документ по щелчку — удобно. Открыть по щелчку .exe,
    /// .bat или .ps1, пришедший в переписке, — это запустить чужую
    /// программу на рабочем ПК одним нажатием. Поэтому всё, чего нет
    /// в списке, не открывается: вместо этого показывается папка
    /// с файлом, и человек решает сам.
    /// </summary>
    private static readonly HashSet<string> OpenRightAway = new(StringComparer.OrdinalIgnoreCase)
    {
        ".pdf", ".doc", ".docx", ".rtf", ".odt", ".txt", ".csv",
        ".xls", ".xlsx", ".ods", ".ppt", ".pptx", ".odp",
        ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".webp", ".svg",
        ".mp3", ".wav", ".mp4", ".avi", ".mkv", ".zip", ".7z", ".rar"
    };

    private void OnDownload(object? sender, CoreWebView2DownloadStartingEventArgs e)
    {
        // Полоса скачивания внизу окна — часть браузера, а здесь окно
        // программы: человек нажал на файл и ждёт файл, а не полосу.
        e.Handled = true;

        var name = SafeName(Path.GetFileName(e.ResultFilePath));
        var target = Path.Combine(DownloadsFolder, name);

        try
        {
            Directory.CreateDirectory(DownloadsFolder);
        }
        catch (Exception)
        {
            // Папку не создать — пусть сохраняется туда, куда сам решил.
            target = e.ResultFilePath;
        }

        e.ResultFilePath = target;

        var operation = e.DownloadOperation;

        operation.StateChanged += (_, _) =>
        {
            if (operation.State != CoreWebView2DownloadState.Completed)
            {
                if (operation.State == CoreWebView2DownloadState.Interrupted)
                {
                    BeginInvoke(() => _tray.Balloon("Портал", "Файл скачать не удалось: " + name));
                }

                return;
            }

            BeginInvoke(() => Open(operation.ResultFilePath));
        };
    }

    /// <summary>Открыть скачанное — или показать папку, если открывать опасно.</summary>
    private void Open(string path)
    {
        var extension = Path.GetExtension(path);

        try
        {
            if (OpenRightAway.Contains(extension))
            {
                Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });

                return;
            }

            // Не из списка: показываем в «Проводнике» выделенным.
            // Так человек видит, что именно пришло, и запускает сам,
            // если действительно этого хочет.
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                Arguments = "/select,\"" + path + "\"",
                UseShellExecute = true
            });

            _tray.Balloon("Портал", "Файл сохранён: " + Path.GetFileName(path));
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Файл скачан, но открыть его не получилось:\n\n" + ex.Message +
                "\n\nОн лежит здесь:\n" + path,
                "Портал", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    /// <summary>Имя файла без того, чем можно увести сохранение в другую папку.</summary>
    private static string SafeName(string name)
    {
        foreach (var bad in Path.GetInvalidFileNameChars())
        {
            name = name.Replace(bad, '_');
        }

        return string.IsNullOrWhiteSpace(name) ? "файл" : name;
    }

    private void SetServer(string url)
    {
        var address = Settings.NormalizeServer(url);

        if (address.Length == 0)
        {
            ShowServerPage("Введите адрес портала.");

            return;
        }

        _settings.Server = address;
        _settings.Save();

        Navigate();
    }

    // ==================================================================
    // Облик окна
    // ==================================================================

    private void SetShape(string shape)
    {
        _shape = shape is "answer" or "full" ? shape : "widget";

        ApplyShape();
    }

    private void ApplyShape()
    {
        var area = Screen.FromHandle(Handle).WorkingArea;

        switch (_shape)
        {
            case "full":
                TopMost = false;
                Opacity = 1;
                Size = new Size(
                    Settings.Clamp(1320, 600, area.Width - 40),
                    Settings.Clamp(880, 480, area.Height - 40));
                Location = new Point(
                    area.X + (area.Width - Width) / 2,
                    area.Y + (area.Height - Height) / 2);
                Show();
                Activate();
                break;

            case "answer":
                TopMost = _alert;
                Opacity = 1;
                Size = new Size(
                    Settings.Clamp(720, 360, area.Width - 20),
                    Settings.Clamp(820, 400, area.Height - 20));
                Location = new Point(
                    area.X + (area.Width - Width) / 2,
                    area.Y + (area.Height - Height) / 2);
                Show();
                Activate();
                break;

            default:
                TopMost = _settings.Mode == "top";
                Opacity = _settings.Opacity / 100.0;
                Size = new Size(_settings.Width, Settings.Clamp(_settings.Height, 420, area.Height));

                if (!_settings.Placed)
                {
                    _settings.X = area.Right - Width - 24;
                    _settings.Y = area.Y + 24;
                    _settings.Placed = true;
                }

                _settings.X = Settings.Clamp(_settings.X, area.X, area.Right - Width);
                _settings.Y = Settings.Clamp(_settings.Y, area.Y, area.Bottom - Height);

                Location = new Point(_settings.X, _settings.Y);
                Show();

                // «На рабочем столе» — значит под всеми окнами. Делается
                // только так: у WinForms нет свойства «быть снизу».
                if (_settings.Mode != "top")
                {
                    Native.SetWindowPos(Handle, Native.HwndBottom, 0, 0, 0, 0,
                        Native.SwpNoMove | Native.SwpNoSize | Native.SwpNoActivate);
                }

                break;
        }
    }

    /// <summary>
    /// Окно «в лоб»: поверх всех программ, в фокусе, мигает в панели задач.
    /// Включается страницей для задач с пометкой «Важно».
    /// </summary>
    private void SetAlert(bool on)
    {
        _alert = on;

        if (on)
        {
            _shape = "answer";

            ApplyShape();

            TopMost = true;
            Show();
            Activate();
        }
        else
        {
            TopMost = _settings.Mode == "top";
        }
    }

    public void ToggleWidget()
    {
        if (Visible && _shape == "widget" && Opacity > 0)
        {
            HideWidget();

            return;
        }

        ShowWidget();
    }

    public void ShowWidget()
    {
        _shape = "widget";

        ApplyShape();

        // Поднимаем над остальными окнами на время работы с ним: иначе
        // «показать виджет» на рабочем столе означает «ничего не произошло».
        Native.SetWindowPos(Handle, Native.HwndTop, 0, 0, 0, 0,
            Native.SwpNoMove | Native.SwpNoSize | Native.SwpShowWindow);

        Activate();
    }

    /// <summary>
    /// Убрать окно к часам.
    ///
    /// Окно именно ПРЯЧЕТСЯ, а не усыпляется: страница внутри продолжает
    /// работать, держит соединение с порталом и сообщает о новом окошками
    /// Windows. Если когда-нибудь появится соблазн сэкономить здесь
    /// память (TrySuspend, MemoryUsageTargetLevel), помните: вместе
    /// с памятью пропадут и уведомления из трея — ради которых
    /// программа и существует.
    /// </summary>
    public void HideWidget()
    {
        if (_alert)
        {
            return;
        }

        Hide();
    }

    public void OpenFull()
    {
        SetShape("full");

        if (_settings.Server.Length > 0)
        {
            _web.CoreWebView2?.Navigate(_settings.Server + "/");
        }
    }

    public void ChangeServer() => ShowServerPage();

    public Settings Current => _settings;

    // ==================================================================
    // Окно и система
    // ==================================================================

    protected override void OnHandleCreated(EventArgs e)
    {
        base.OnHandleCreated(e);

        // Ctrl+Alt+S показывает и прячет виджет. Горячая клавиша нужна
        // именно системная: программа работает, когда человек смотрит
        // в другое окно, — иначе бы и виджета не требовалось.
        Native.RegisterHotKey(Handle, HotKeyId,
            Native.ModControl | Native.ModAlt | Native.ModNoRepeat, (uint)Keys.S);
    }

    protected override void WndProc(ref Message m)
    {
        if (m.Msg == Native.WmHotKey && m.WParam.ToInt32() == HotKeyId)
        {
            ToggleWidget();

            return;
        }

        // Вторая копия программы просит показать первую и закрывается.
        if (m.Msg == (int)_showMessage)
        {
            ShowWidget();

            return;
        }

        base.WndProc(ref m);
    }

    protected override void OnResizeEnd(EventArgs e)
    {
        base.OnResizeEnd(e);

        if (_shape != "widget")
        {
            return;
        }

        _settings.X = Location.X;
        _settings.Y = Location.Y;
        _settings.Height = Height;
        _settings.Save();
    }

    private void OnFormClosing(object? sender, FormClosingEventArgs e)
    {
        // Крестик прячет окно, а не закрывает программу: иначе человек
        // закрывает виджет и перестаёт получать уведомления, сам того
        // не желая. Выход — пунктом меню у часов.
        if (!_closingForReal && e.CloseReason == CloseReason.UserClosing)
        {
            e.Cancel = true;

            if (_shape == "widget")
            {
                HideWidget();
            }
            else
            {
                SetShape("widget");
            }
        }
    }

    public void QuitForReal()
    {
        _closingForReal = true;

        Native.UnregisterHotKey(Handle, HotKeyId);

        _settings.Save();
        _tray.Dispose();

        Application.Exit();
    }

    /// <summary>Перезапускает программу — после обновления.</summary>
    public void RestartForUpdate(string exePath)
    {
        _closingForReal = true;

        _tray.Dispose();

        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = exePath,
            UseShellExecute = true
        });

        Application.Exit();
    }
}
