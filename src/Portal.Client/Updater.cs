using System.Net.Http.Json;

namespace Portal.Client;

/// <summary>
/// Обновление программы с портала.
///
/// ЗАЧЕМ
///
/// Программа стоит на двух десятках рабочих ПК. Ходить по ним с флешкой
/// при каждой правке — занятие на полдня, и кончается оно тем, что
/// половина машин остаётся со старой версией. Поэтому новая версия
/// лежит на портале, а программа забирает её сама.
///
/// КАК
///
/// Раз в несколько часов спрашивает у портала номер версии. Если он
/// отличается от своего — скачивает файл, подменяет себя и
/// перезапускается. Старый файл остаётся рядом под именем .old
/// и стирается при следующем запуске: Windows не даёт удалить
/// работающую программу, зато даёт её переименовать.
/// </summary>
public static class Updater
{
    private static readonly TimeSpan CheckEvery = TimeSpan.FromHours(3);

    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromMinutes(10) };

    public static async Task RunAsync(MainForm form, Settings settings)
    {
        // Остатки прошлого обновления убираем при запуске.
        TryDeleteOld();

        while (true)
        {
            await Task.Delay(CheckEvery);

            try
            {
                if (await CheckAsync(form, settings))
                {
                    return;
                }
            }
            catch (Exception)
            {
                // Портал недоступен или версия не отдалась — ничего
                // страшного, попробуем через несколько часов. Падать
                // из-за обновления программа не должна.
            }
        }
    }

    private static async Task<bool> CheckAsync(MainForm form, Settings settings)
    {
        if (settings.Server.Length == 0)
        {
            return false;
        }

        var mine = typeof(Updater).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

        var theirs = await Http.GetFromJsonAsync<VersionAnswer>(settings.Server + "/client/version");

        if (theirs?.Version is not { Length: > 0 } version || version == mine)
        {
            return false;
        }

        var data = await Http.GetByteArrayAsync(settings.Server + "/client/portal-widget.exe");

        // Проверяем, что это вообще программа: оборванная закачка или
        // страница с ошибкой вместо файла не должны заменить собой
        // работающий виджет.
        if (data.Length < 1_000_000 || data[0] != (byte)'M' || data[1] != (byte)'Z')
        {
            return false;
        }

        var exe = Environment.ProcessPath;

        if (exe is null)
        {
            return false;
        }

        var fresh = exe + ".new";
        var old = exe + ".old";

        await File.WriteAllBytesAsync(fresh, data);

        TryDeleteOld();

        File.Move(exe, old);

        try
        {
            File.Move(fresh, exe);
        }
        catch (Exception)
        {
            // Не вышло поставить новое — возвращаем старое на место,
            // иначе человек останется вообще без программы.
            File.Move(old, exe);

            return false;
        }

        form.BeginInvoke(() => form.RestartForUpdate(exe));

        return true;
    }

    private static void TryDeleteOld()
    {
        try
        {
            var old = Environment.ProcessPath + ".old";

            if (File.Exists(old))
            {
                File.Delete(old);
            }
        }
        catch (Exception)
        {
            // Ещё держится — сотрётся в следующий раз.
        }
    }

    private sealed record VersionAnswer(string Version);
}
