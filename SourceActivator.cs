using System.Diagnostics;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Показывает приложение, из которого сейчас идёт звук. Системная сессия
/// (SMTC) сообщает о нём только строку AUMID вида "Spotify.exe" или
/// "AyuGram.AyuGramDesktop.08bde0d8..." - по ней и ищем окно.
/// </summary>
internal static class SourceActivator
{
    public static bool Activate(string appId)
    {
        if (string.IsNullOrWhiteSpace(appId)) return false;

        // Приложения из Store своих окон могут не показывать в перечислении
        // (другой процесс-хост), зато активируются по AUMID через оболочку
        if (appId.Contains('!'))
        {
            try
            {
                Process.Start(new ProcessStartInfo("explorer.exe", "shell:AppsFolder\\" + appId)
                {
                    UseShellExecute = true,
                });
                return true;
            }
            catch
            {
                return false;
            }
        }

        IntPtr hwnd = FindWindowFor(appId);
        if (hwnd == IntPtr.Zero) return false;

        // Свёрнутое в трей окно скрыто, а не свёрнуто - сначала показать
        if (!Interop.IsWindowVisible(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_SHOW);
        if (Interop.IsIconic(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_RESTORE);

        // "Нажатие" Alt снимает запрет на смену активного окна - тот же приём,
        // что и в SpotifyActions
        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.SetForegroundWindow(hwnd);
        Interop.keybd_event(Interop.VK_MENU, 0, 2 /* KEYEVENTF_KEYUP */, UIntPtr.Zero);
        return true;
    }

    /// <summary>Процессы браузеров. По одному классу окна их не отличить:
    /// Chrome_WidgetWin_1 у всех приложений на Electron тоже (Claude, Discord).</summary>
    private static readonly string[] BrowserProcesses =
        { "browser", "chrome", "msedge", "opera", "brave", "vivaldi" };

    /// <summary>
    /// Поднять окно браузера со звучащей вкладкой. Вкладку расширение уже
    /// сделало активной, её заголовок стал заголовком окна - по нему и ищем
    /// нужное среди нескольких окон. Не нашлось по заголовку - берём верхнее
    /// окно браузера: EnumWindows идёт сверху вниз по Z-порядку.
    /// </summary>
    public static bool BringBrowserToFront(string tabTitle)
    {
        IntPtr byTitle = IntPtr.Zero, topmost = IntPtr.Zero;
        var names = new Dictionary<uint, string>();

        Interop.EnumWindows((hwnd, _) =>
        {
            try
            {
                if (!Interop.IsWindowVisible(hwnd)) return true;
                if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true;
                int len = Interop.GetWindowTextLength(hwnd);
                if (len <= 0) return true;

                Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (!names.TryGetValue(pid, out string? name))
                {
                    try { using var p = Process.GetProcessById((int)pid); name = p.ProcessName; }
                    catch { name = ""; }
                    names[pid] = name;
                }
                if (!BrowserProcesses.Contains(name, StringComparer.OrdinalIgnoreCase)) return true;

                if (topmost == IntPtr.Zero) topmost = hwnd;
                if (tabTitle.Length > 0)
                {
                    var sb = new System.Text.StringBuilder(len + 1);
                    Interop.GetWindowText(hwnd, sb, sb.Capacity);
                    if (sb.ToString().StartsWith(tabTitle, StringComparison.Ordinal))
                    {
                        byTitle = hwnd;
                        return false; // нашли - дальше не перебираем
                    }
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        IntPtr target = byTitle != IntPtr.Zero ? byTitle : topmost;
        return target != IntPtr.Zero && BringToFront(target);
    }

    /// <summary>
    /// Вывести окно на передний план без мигания на панели задач - приём из
    /// Джарвиса (skills/windows). Сначала честный SetForegroundWindow; если
    /// Windows отказала (запрет смены активного окна - от него и мигание),
    /// свой поток ввода на время вызова привязывается к потоку окна, которое
    /// сейчас впереди, и запрет перестаёт действовать. Прав администратора не
    /// требует. В отличие от "нажатия" Alt ничего не шлёт в окна: одиночный
    /// Alt у браузера фокусирует кнопку меню.
    /// </summary>
    public static bool BringToFront(IntPtr hwnd)
    {
        // Разворачиваем только свёрнутое: SW_RESTORE у окна во весь экран
        // вернул бы его к обычному размеру
        if (Interop.IsIconic(hwnd))
            Interop.ShowWindow(hwnd, Interop.SW_RESTORE);

        if (Interop.GetForegroundWindow() == hwnd) return true;
        if (Interop.SetForegroundWindow(hwnd)) return true;

        IntPtr fg = Interop.GetForegroundWindow();
        uint theirs = Interop.GetWindowThreadProcessId(fg, out _);
        uint ours = Interop.GetCurrentThreadId();
        if (theirs == 0 || theirs == ours)
            return Interop.SetForegroundWindow(hwnd);

        Interop.AttachThreadInput(ours, theirs, true);
        try
        {
            Interop.BringWindowToTop(hwnd);
            return Interop.SetForegroundWindow(hwnd);
        }
        finally
        {
            Interop.AttachThreadInput(ours, theirs, false);
        }
    }

    /// <summary>Главное окно процесса, имя которого встречается в AUMID.
    /// Process.MainWindowHandle тут не годится: у свёрнутых в трей приложений
    /// (Telegram, AyuGram) он равен нулю.</summary>
    private static IntPtr FindWindowFor(string appId)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        var names = new Dictionary<uint, string>();

        Interop.EnumWindows((hwnd, _) =>
        {
            try
            {
                // Диалоги и всплывашки имеют владельца - нам нужно главное окно
                if (Interop.GetWindow(hwnd, Interop.GW_OWNER) != IntPtr.Zero) return true;

                Interop.GetWindowThreadProcessId(hwnd, out uint pid);
                if (pid == 0) return true;

                if (!names.TryGetValue(pid, out string? name))
                {
                    try
                    {
                        using var proc = Process.GetProcessById((int)pid);
                        name = proc.ProcessName;
                    }
                    catch
                    {
                        name = "";
                    }
                    names[pid] = name;
                }

                // Короткие имена дали бы ложные совпадания внутри длинного AUMID
                if (name.Length < 3) return true;
                if (appId.IndexOf(name, StringComparison.OrdinalIgnoreCase) < 0) return true;

                // Видимое окно с заголовком - вероятнее главное, чем скрытое
                // служебное того же процесса
                int score = 1;
                if (Interop.IsWindowVisible(hwnd)) score += 2;
                if (Interop.GetWindowTextLength(hwnd) > 0) score += 2;
                if (score > bestScore)
                {
                    bestScore = score;
                    best = hwnd;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return best;
    }
}
