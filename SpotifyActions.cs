using System.Diagnostics;

namespace SpotifyTaskbarWidget;

internal static class SpotifyActions
{
    /// <summary>
    /// Adds/removes the current track from favorites by sending Spotify's
    /// official shortcut (Alt+Shift+B) to its window. The Windows media API
    /// does not expose "save to favorites", so Spotify has to be focused
    /// briefly. Run this on a background thread (it uses Sleep).
    /// </summary>
    public static void LikeCurrentTrack()
    {
        var proc = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc == null) return;

        IntPtr spotify = proc.MainWindowHandle;
        IntPtr previous = Interop.GetForegroundWindow();

        // While minimized the window may not process the shortcut - restore it briefly
        bool wasMinimized = Interop.IsIconic(spotify);
        if (wasMinimized)
        {
            Interop.ShowWindow(spotify, Interop.SW_RESTORE);
            Thread.Sleep(250);
        }

        // A "tap" on Alt lifts the SetForegroundWindow restriction
        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.SetForegroundWindow(spotify);
        Thread.Sleep(150);

        Interop.keybd_event(Interop.VK_MENU, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, 0, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_B, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_SHIFT, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Interop.keybd_event(Interop.VK_MENU, 0, Interop.KEYEVENTF_KEYUP, UIntPtr.Zero);
        Thread.Sleep(200);

        if (wasMinimized)
            Interop.ShowWindow(spotify, Interop.SW_MINIMIZE);
        if (previous != IntPtr.Zero)
            Interop.SetForegroundWindow(previous);
    }

    public static void OpenSpotifyWindow()
    {
        var proc = Process.GetProcessesByName("Spotify").FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
        if (proc != null)
        {
            Interop.ShowWindow(proc.MainWindowHandle, Interop.SW_RESTORE);
            Interop.SetForegroundWindow(proc.MainWindowHandle);
        }
        else
        {
            try
            {
                Process.Start(new ProcessStartInfo("spotify:") { UseShellExecute = true });
            }
            catch { }
        }
    }
}
