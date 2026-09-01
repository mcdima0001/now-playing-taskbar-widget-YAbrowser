using System.Runtime.InteropServices;
using System.Text;

namespace SpotifyTaskbarWidget;

internal static class Interop
{
    public const int GWL_EXSTYLE = -20;
    public const int WS_EX_TOOLWINDOW = 0x00000080;
    public const int WS_EX_NOACTIVATE = 0x08000000;

    public const int SW_RESTORE = 9;
    public const int SW_MINIMIZE = 6;

    [DllImport("user32.dll")]
    public static extern bool IsIconic(IntPtr hWnd);

    private static readonly IntPtr HWND_TOPMOST = new(-1);
    private const uint SWP_NOMOVE = 0x0002;
    private const uint SWP_NOSIZE = 0x0001;
    private const uint SWP_NOACTIVATE = 0x0010;

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    public struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    public static extern IntPtr FindWindow(string lpClassName, string? lpWindowName);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr FindWindowEx(IntPtr hWndParent, IntPtr hWndChildAfter, string lpszClass, string? lpszWindow);

    /// <summary>Taskbars on secondary monitors (Win11: Shell_SecondaryTrayWnd),
    /// ordered by monitor POSITION (left->right, top->bottom). The raw enumeration
    /// comes in z-order, which changes constantly - without a stable ordering,
    /// "Monitor 2" and "Monitor 3" swapped identities and the widget jumped between them.</summary>
    public static List<IntPtr> GetSecondaryTrays()
    {
        var list = new List<(IntPtr Handle, int Left, int Top)>();
        IntPtr h = IntPtr.Zero;
        while ((h = FindWindowEx(IntPtr.Zero, h, "Shell_SecondaryTrayWnd", null)) != IntPtr.Zero)
        {
            GetWindowRect(h, out RECT r);
            list.Add((h, r.Left, r.Top));
        }
        return list.OrderBy(t => t.Left).ThenBy(t => t.Top).Select(t => t.Handle).ToList();
    }

    /// <summary>Left edge (physical px) of the system icon area (clock, network...).</summary>
    public static int? GetTrayNotifyLeft(IntPtr tray)
    {
        IntPtr notify = FindWindowEx(tray, IntPtr.Zero, "TrayNotifyWnd", null);
        if (notify == IntPtr.Zero || !GetWindowRect(notify, out RECT r))
            return null;
        return r.Left;
    }

    /// <summary>Pixels of the taskbar currently on screen (to tell whether it is
    /// settled, hidden, or mid reveal/hide animation).
    /// Also returns the bottom of the "home" monitor and the bottom of the work
    /// area - the difference between the two is the REAL drawn height of the bar
    /// (the appbar reservation), valid for any bar height; with auto-hide there is
    /// no reservation and the caller falls back to the 48 DIP heuristic.</summary>
    public static int GetTaskbarVisiblePx(RECT trayRect, out int monitorBottomPx, out int workAreaBottomPx)
    {
        IntPtr mon = GetTrayMonitor(trayRect);
        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(mon, ref mi))
        {
            monitorBottomPx = trayRect.Bottom;
            workAreaBottomPx = trayRect.Bottom;
            return trayRect.Bottom - trayRect.Top; // no info: assume settled
        }
        monitorBottomPx = mi.rcMonitor.Bottom;
        workAreaBottomPx = mi.rcWork.Bottom;
        return Math.Min(trayRect.Bottom, mi.rcMonitor.Bottom) - Math.Max(trayRect.Top, mi.rcMonitor.Top);
    }

    [DllImport("user32.dll")]
    public static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    // Перебор окон нужен, чтобы найти приложение-источник звука по его
    // идентификатору из системной сессии (SMTC отдаёт только строку AUMID)
    public delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll")]
    public static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    public static extern IntPtr GetWindow(IntPtr hWnd, uint uCmd);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    public static extern int GetWindowTextLength(IntPtr hWnd);

    /// <summary>Владелец окна: у диалогов и всплывашек он есть, у главного
    /// окна приложения - нет.</summary>
    public const uint GW_OWNER = 4;

    public const int SW_SHOW = 5;

    public const uint EVENT_OBJECT_LOCATIONCHANGE = 0x800B;

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateRectRgn(int left, int top, int right, int bottom);

    [DllImport("user32.dll")]
    private static extern int SetWindowRgn(IntPtr hWnd, IntPtr hRgn, bool bRedraw);

    // Clipping state PER WINDOW: there is one widget per monitor, and a single
    // flag made one window "consume" the other's clip removal - the widget
    // stayed truncated/invisible forever on the other monitor
    private static readonly HashSet<IntPtr> _clippedWindows = new();

    /// <summary>Clips the window to what fits above the bottom of the screen while
    /// the taskbar slides (auto-hide). Without clipping, the part that already
    /// "left" kept drawing - visible on a monitor arranged below.
    /// The system owns the region after SetWindowRgn (do not free it).</summary>
    public static void ClipWindowBottom(IntPtr hwnd, int widthPx, int heightPx, int visibleHeightPx)
    {
        if (visibleHeightPx >= heightPx)
        {
            if (_clippedWindows.Remove(hwnd))
                SetWindowRgn(hwnd, IntPtr.Zero, true);
            return;
        }
        SetWindowRgn(hwnd, CreateRectRgn(0, 0, widthPx, Math.Max(0, visibleHeightPx)), true);
        _clippedWindows.Add(hwnd);
    }

    [DllImport("user32.dll")]
    public static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("user32.dll")]
    public static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    public static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

    [DllImport("user32.dll")]
    public static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    public static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    public static extern void mouse_event(uint dwFlags, int dx, int dy, uint dwData, UIntPtr dwExtraInfo);

    public const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    public const uint MOUSEEVENTF_LEFTUP = 0x0004;

    [StructLayout(LayoutKind.Sequential)]
    public struct POINT
    {
        public int X, Y;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct APPBARDATA
    {
        public uint cbSize;
        public IntPtr hWnd;
        public uint uCallbackMessage;
        public uint uEdge;
        public RECT rc;
        public int lParam;
    }

    [DllImport("shell32.dll")]
    private static extern UIntPtr SHAppBarMessage(uint dwMessage, ref APPBARDATA pData);

    /// <summary>True if taskbar auto-hide is on. In that mode maximized windows
    /// cover the whole screen and "look" fullscreen - the fullscreen test stops
    /// being reliable (and is redundant: the widget's visibility already follows
    /// the bar's).</summary>
    public static bool IsAutoHideEnabled()
    {
        var data = new APPBARDATA { cbSize = (uint)Marshal.SizeOf<APPBARDATA>() };
        return ((ulong)SHAppBarMessage(4 /*ABM_GETSTATE*/, ref data) & 1 /*ABS_AUTOHIDE*/) != 0;
    }

    public const byte VK_SHIFT = 0x10;
    public const byte VK_MENU = 0x12;
    public const byte VK_B = 0x42;
    public const uint KEYEVENTF_KEYUP = 0x0002;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromPoint(POINT pt, uint dwFlags);

    /// <summary>The taskbar's "home" monitor. With auto-hide the bar's rect slides
    /// OFF screen - if a monitor is arranged below, MonitorFromWindow returns that
    /// neighbour and the visibility maths run against the wrong screen. The point
    /// just above the bar does not suffer from that.</summary>
    private static IntPtr GetTrayMonitor(RECT trayRect)
    {
        var pt = new POINT { X = (trayRect.Left + trayRect.Right) / 2, Y = trayRect.Top - 10 };
        return MonitorFromPoint(pt, MONITOR_DEFAULTTONEAREST);
    }

    [DllImport("user32.dll")]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll")]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int x, int y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll")]
    public static extern int GetWindowLong(IntPtr hWnd, int nIndex);

    [DllImport("user32.dll")]
    public static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    public static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

    public const int GWLP_HWNDPARENT = -8;

    [DllImport("user32.dll")]
    public static extern bool SetForegroundWindow(IntPtr hWnd);

    /// <summary>Разрешает другому процессу вывести своё окно вперёд. Без этого
    /// Windows не даёт браузеру подняться по команде извне - окно только мигает
    /// на панели задач. ASFW_ANY снимает ограничение для любого процесса.</summary>
    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool AllowSetForegroundWindow(int dwProcessId);

    public const int ASFW_ANY = -1;

    [DllImport("user32.dll")]
    public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    public static void EnsureTopmost(IntPtr hwnd) =>
        SetWindowPos(hwnd, HWND_TOPMOST, 0, 0, 0, 0, SWP_NOMOVE | SWP_NOSIZE | SWP_NOACTIVATE);

    private const uint SWP_NOZORDER = 0x0004;

    /// <summary>Moves the window to PHYSICAL coordinates (px). Positioning in raw px
    /// avoids the wrong DIP maths between monitors with different scaling.</summary>
    public static void MoveWindowTo(IntPtr hwnd, int xPx, int yPx) =>
        SetWindowPos(hwnd, IntPtr.Zero, xPx, yPx, 0, 0, SWP_NOSIZE | SWP_NOZORDER | SWP_NOACTIVATE);

    [DllImport("user32.dll")]
    public static extern uint GetDpiForWindow(IntPtr hWnd);

    // System event hook: react the instant the active window changes
    // (clicking the taskbar brings the bar above the widget until we re-assert)
    public delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll")]
    public static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate pfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    public static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    public const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    public const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    /// <summary>
    /// True if the foreground window is fullscreen on the same monitor as the
    /// taskbar (games, videos) - in that case the widget should hide.
    /// </summary>
    public static bool IsForegroundFullscreen(IntPtr self, IntPtr tray)
    {
        IntPtr fg = GetForegroundWindow();
        if (fg == IntPtr.Zero || fg == self || fg == tray)
            return false;

        var sb = new StringBuilder(256);
        GetClassName(fg, sb, sb.Capacity);
        string cls = sb.ToString();
        if (cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd"
            or "XamlExplorerHostIslandWindow")
            return false;

        IntPtr monFg = MonitorFromWindow(fg, MONITOR_DEFAULTTONEAREST);
        IntPtr monTray = GetWindowRect(tray, out RECT tr)
            ? GetTrayMonitor(tr)
            : MonitorFromWindow(tray, MONITOR_DEFAULTTONEAREST);
        if (monFg != monTray)
            return false;

        if (!GetWindowRect(fg, out RECT wr))
            return false;

        var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (!GetMonitorInfo(monFg, ref mi))
            return false;

        bool coversMonitor = wr.Left <= mi.rcMonitor.Left && wr.Top <= mi.rcMonitor.Top
            && wr.Right >= mi.rcMonitor.Right && wr.Bottom >= mi.rcMonitor.Bottom;
        if (!coversMonitor)
            return false;

        // Covers the whole screen. These classes serve both harmless shell overlays
        // (Start menu, emoji/dictation, screen snip) and fullscreen UWP games
        // (Store Forza, issue #5) - tell them apart by the process.
        // The process lookup is paid ONLY here: windows that do not cover the
        // screen never reach this point (it used to run for any focused UWP app).
        if (cls is "Windows.UI.Core.CoreWindow" or "ApplicationFrameWindow")
        {
            GetWindowThreadProcessId(fg, out uint pid);
            try
            {
                using var p = System.Diagnostics.Process.GetProcessById((int)pid);
                if (p.ProcessName is "explorer" or "StartMenuExperienceHost" or "SearchHost"
                    or "ShellExperienceHost" or "ShellHost" or "SearchApp" or "SearchUI"
                    or "Cortana" or "LockApp" or "TextInputHost" or "ScreenClippingHost")
                    return false;
            }
            catch
            {
                return false; // process already died: treat as shell (harmless)
            }
        }

        return true;
    }

    // ---------- Глобальная горячая клавиша ----------

    public const int WM_HOTKEY = 0x0312;
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_NOREPEAT = 0x4000;
    /// <summary>Клавиша "." - код физический, поэтому раскладка не важна:
    /// на русской это та же клавиша с "ю".</summary>
    public const uint VK_OEM_PERIOD = 0xBE;

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    public static extern bool UnregisterHotKey(IntPtr hWnd, int id);
}
