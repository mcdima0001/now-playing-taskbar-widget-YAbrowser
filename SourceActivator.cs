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
