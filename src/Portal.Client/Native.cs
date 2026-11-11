using System.Runtime.InteropServices;

namespace Portal.Client;

/// <summary>
/// Вызовы Windows, которых нет в WinForms.
///
/// Нужны ради двух вещей: положить окно НА рабочий стол (под все
/// остальные окна) и повесить горячую клавишу, работающую, даже когда
/// программа не в фокусе. Ни того ни другого обычными средствами
/// не сделать.
/// </summary>
internal static class Native
{
    // ---------- Порядок окон ----------

    public static readonly IntPtr HwndTop = new(0);
    public static readonly IntPtr HwndBottom = new(1);
    public static readonly IntPtr HwndTopMost = new(-1);
    public static readonly IntPtr HwndNoTopMost = new(-2);

    public const uint SwpNoSize = 0x0001;
    public const uint SwpNoMove = 0x0002;
    public const uint SwpNoActivate = 0x0010;
    public const uint SwpShowWindow = 0x0040;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool SetWindowPos(
        IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint flags);

    // ---------- Горячая клавиша ----------

    public const int WmHotKey = 0x0312;
    public const uint ModAlt = 0x0001;
    public const uint ModControl = 0x0002;
    public const uint ModNoRepeat = 0x4000;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint modifiers, uint virtualKey);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // ---------- Перетаскивание за любое место ----------
    //
    // У виджета нет заголовка окна: он весь — страница портала.
    // Чтобы его можно было двигать мышью, окну говорят «нажали
    // по заголовку», когда человек тянет за свободное место.

    public const int WmNcLButtonDown = 0x00A1;
    public const int HtCaption = 2;

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool ReleaseCapture();

    [DllImport("user32.dll", EntryPoint = "SendMessageW", CharSet = CharSet.Unicode)]
    public static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

    // ---------- Одна копия программы ----------

    [DllImport("user32.dll", EntryPoint = "RegisterWindowMessageW", CharSet = CharSet.Unicode)]
    public static extern uint RegisterWindowMessage(string name);

    [DllImport("user32.dll", EntryPoint = "PostMessageW", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    public static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    public const int HwndBroadcastValue = 0xffff;

    public static readonly IntPtr HwndBroadcast = new(HwndBroadcastValue);
}
