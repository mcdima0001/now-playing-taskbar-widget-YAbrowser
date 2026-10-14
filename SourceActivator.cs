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

        // Тем же приёмом, что и для браузера: "нажатие" Alt шлёт клавишу в чужое
        // окно (у кого-то она фокусирует меню), а привязка потока ввода ничего
        // никуда не отправляет
        bool ok = BringToFront(hwnd);
        if (Interop.IsWindowVisible(hwnd)) return ok;

        return ShowBySecondLaunch(hwnd) || ok;
    }

    /// <summary>
    /// Последняя попытка открыть окно программы, которая держит его скрытым
    /// сама. У AIMP, свёрнутого в трей, не помогает ничто из ShowWindow -
    /// проверено на SW_SHOW, SW_SHOWNORMAL и SW_RESTORE: окно остаётся
    /// скрытым, хотя и становится активным. Со стороны это выглядит так, будто
    /// клик по названию не делает ничего.
    ///
    /// Остаётся попросить саму программу: почти все такие работают в одном
    /// экземпляре, и повторный запуск заставляет уже работающую копию показать
    /// окно, а вторую - сразу завершиться (проверено: процесс остаётся один).
    ///
    /// Делается это ТОЛЬКО когда окно так и осталось скрытым. Обычные
    /// программы сюда не доходят, и второй копии у них не появится.
    /// </summary>
    private static bool ShowBySecondLaunch(IntPtr hwnd)
    {
        try
        {
            Interop.GetWindowThreadProcessId(hwnd, out uint pid);
            using var proc = Process.GetProcessById((int)pid);
            string path = proc.MainModule?.FileName ?? "";
            if (path.Length == 0) return false;
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            return true;
        }
        catch (Exception ex)
        {
            Diag.Once("activate-relaunch",
                "Asking the player to show its own window failed: " + ex.Message);
            return false;
        }
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

    /// <summary>
    /// Главное окно процесса, имя которого встречается в AUMID.
    /// Process.MainWindowHandle тут не годится: у свёрнутых в трей приложений
    /// (Telegram, AyuGram) он равен нулю.
    ///
    /// Отсутствия владельца мало. У AIMP, свёрнутого в трей, таких окон
    /// восемнадцать, и все скрытые: всплывашка QFI, панель информации,
    /// значок в трее, окна IPC и DDE. Признаки, которые их различают:
    ///
    ///     TAIMPQuickFileInfoWindow 'QFI'   480x260  TOOLWINDOW
    ///     TAIMPMainForm 'Aprile - ...'    1110x626  APPWINDOW CAPTION
    ///
    /// Служебные помечены WS_EX_TOOLWINDOW (на панели задач они не появляются
    /// никогда), главное - WS_EX_APPWINDOW. Раньше счёт у них выходил равным,
    /// побеждала первая попавшаяся, и виджет "показывал источник", делая
    /// активным невидимое окошко подсказки - со стороны клик просто не работал.
    /// </summary>
    private static IntPtr FindWindowFor(string appId)
    {
        IntPtr best = IntPtr.Zero;
        int bestScore = 0;
        long bestArea = 0;
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

                int ex = Interop.GetWindowLong(hwnd, Interop.GWL_EXSTYLE);
                if ((ex & Interop.WS_EX_TOOLWINDOW) != 0) return true;

                // Нулевые и однопиксельные - это служебные окна сообщений
                if (!Interop.GetWindowRect(hwnd, out var r)) return true;
                long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
                if (r.Right - r.Left <= 1 || r.Bottom - r.Top <= 1) return true;

                int style = Interop.GetWindowLong(hwnd, Interop.GWL_STYLE);
                int score = 1;
                if ((ex & Interop.WS_EX_APPWINDOW) != 0) score += 8;
                if (Interop.IsWindowVisible(hwnd)) score += 4;
                if (Interop.GetWindowTextLength(hwnd) > 0) score += 2;
                if ((style & Interop.WS_CAPTION) == Interop.WS_CAPTION) score += 1;

                // При равном счёте выигрывает большее окно: у главного оно
                // заметно крупнее служебных
                if (score > bestScore || (score == bestScore && area > bestArea))
                {
                    bestScore = score;
                    bestArea = area;
                    best = hwnd;
                }
            }
            catch { }
            return true;
        }, IntPtr.Zero);

        return best;
    }
}
