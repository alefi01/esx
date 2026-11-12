using System.Diagnostics;

namespace Portal.Client;

/// <summary>
/// Окно «нужен компонент Microsoft Edge WebView2».
///
/// ЗАЧЕМ ОТДЕЛЬНОЕ ОКНО, А НЕ ОБЫЧНОЕ СООБЩЕНИЕ
///
/// Раньше виджет молча уходил качать стомегабайтный установщик
/// и минуту выглядел зависшим: человек не знал ни что скачивается,
/// ни сколько ждать, ни можно ли отказаться. Обычное окно сообщения
/// своих кнопок не допускает — только «ОК» и «Отмена», — а объяснять
/// происходящее надписью на «ОК» бессмысленно.
///
/// Поэтому здесь своё окно: короткое объяснение, что это за компонент
/// и почему он нужен, явная кнопка «Скачать с портала и установить»
/// и строка состояния, пока идёт работа. Отказаться тоже можно —
/// тогда виджет честно скажет, что без компонента не запустится.
/// </summary>
public sealed class WebView2Prompt : Form
{
    private readonly Label _state;
    private readonly Button _go;
    private readonly Button _no;
    private readonly string _server;

    /// <summary>Компонент установлен — можно продолжать запуск.</summary>
    public bool Installed { get; private set; }

    public WebView2Prompt(string server)
    {
        _server = server;

        Text = "Портал — нужен один компонент Windows";
        FormBorderStyle = FormBorderStyle.FixedDialog;
        StartPosition = FormStartPosition.CenterScreen;
        MaximizeBox = false;
        MinimizeBox = false;
        ClientSize = new Size(520, 280);
        Font = new Font("Segoe UI", 9f);

        var title = new Label
        {
            Text = "Не хватает Microsoft Edge WebView2 Runtime",
            Font = new Font("Segoe UI", 12f, FontStyle.Bold),
            Location = new Point(18, 18),
            Size = new Size(484, 26)
        };

        // Текст написан для того, кто просто хочет работать: что это,
        // почему нужно, что сейчас произойдёт и сколько это займёт.
        var text = new Label
        {
            Text = "Виджет показывает страницы портала этим компонентом Windows. "
                + "На этом компьютере его нет.\n\n"
                + "Что произойдёт, если нажать кнопку ниже:\n"
                + "    1.  Установщик компонента скачается с портала (около 100 МБ).\n"
                + "    2.  Установится сам, без вопросов и без прав администратора.\n"
                + "    3.  Виджет запустится дальше.\n\n"
                + "Это занимает одну-две минуты. Интернет не нужен — файл берётся "
                + "с вашего портала.",
            Location = new Point(18, 52),
            Size = new Size(484, 150)
        };

        _state = new Label
        {
            Location = new Point(18, 206),
            Size = new Size(484, 20),
            ForeColor = SystemColors.GrayText
        };

        _go = new Button
        {
            Text = "Скачать с портала и установить",
            Location = new Point(188, 232),
            Size = new Size(220, 30)
        };

        _no = new Button
        {
            Text = "Отмена",
            Location = new Point(416, 232),
            Size = new Size(86, 30),
            DialogResult = DialogResult.Cancel
        };

        _go.Click += OnGo;

        Controls.AddRange([title, text, _state, _go, _no]);

        CancelButton = _no;
        AcceptButton = _go;
    }

    private async void OnGo(object? sender, EventArgs e)
    {
        _go.Enabled = false;
        _no.Enabled = false;

        try
        {
            Say("Скачиваю установщик компонента с портала…");

            var file = await DownloadAsync();

            Say("Устанавливаю. Это самая долгая часть, окно закроется само…");

            await Task.Run(() =>
            {
                // Без повышения прав компонент ставится для текущего
                // пользователя — администратор не нужен.
                var process = Process.Start(new ProcessStartInfo
                {
                    FileName = file,
                    Arguments = "/silent /install",
                    UseShellExecute = false
                });

                process?.WaitForExit();
            });

            if (!Installer.WebView2Present())
            {
                Log.Write("WebView2: установщик отработал, но компонента так и нет");

                Fail("Установщик отработал, но компонент так и не появился.\n\n"
                    + "Передайте администратору: возможно, установку запрещает политика "
                    + "безопасности. Журнал: " + Log.Path);

                return;
            }

            Log.Write("WebView2: установлен");

            Installed = true;
            DialogResult = DialogResult.OK;

            Close();
        }
        catch (Exception error)
        {
            Log.Fail("установка WebView2", error);

            Fail("Не получилось: " + error.Message + "\n\n"
                + "Установщик компонента можно взять у администратора "
                + "и запустить вручную — он называется "
                + "MicrosoftEdgeWebView2RuntimeInstallerX64.exe.");
        }
    }

    /// <summary>Забирает установщик компонента с портала.</summary>
    private async Task<string> DownloadAsync()
    {
        var folder = Path.Combine(Path.GetTempPath(), "PortalWidget");

        Directory.CreateDirectory(folder);

        var file = Path.Combine(folder, "webview2-runtime.exe");

        using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };

        using var answer = await http.GetAsync(
            _server + "/client/webview2", HttpCompletionOption.ResponseHeadersRead);

        if (!answer.IsSuccessStatusCode)
        {
            throw new InvalidOperationException(
                "на портале установщик компонента не выложен. Передайте администратору: "
                + "файл MicrosoftEdgeWebView2RuntimeInstallerX64.exe нужно положить "
                + "в папку App_Data\\client рядом с порталом");
        }

        await using var target = File.Create(file);

        await answer.Content.CopyToAsync(target);

        return file;
    }

    private void Say(string what)
    {
        _state.Text = what;
        _state.Refresh();
    }

    private void Fail(string what)
    {
        Say("");

        MessageBox.Show(this, what, "Портал", MessageBoxButtons.OK, MessageBoxIcon.Warning);

        _go.Enabled = true;
        _no.Enabled = true;
    }
}
