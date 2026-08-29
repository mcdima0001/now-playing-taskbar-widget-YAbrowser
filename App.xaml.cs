using System.IO;
using System.Windows;

namespace SpotifyTaskbarWidget;

public partial class App : Application
{
    private static Mutex? _mutex;

    /// <summary>True only when the user quit on purpose (or an update);
    /// false when the window dies with Explorer and must be recreated.</summary>
    public static bool IntentionalExit;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "SpotifyTaskbarWidget_SingleInstance", out bool isNew);
        if (!isNew)
        {
            IntentionalExit = true;
            Shutdown();
            return;
        }

        // The window can be destroyed by an Explorer restart (it is owned by
        // the taskbar) and recreated - the app only exits when the user says so
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += (_, args) =>
        {
            // Shared writing with Diag: dedup of repeats (a timer stuck in an
            // error fires several times a second) and a size ceiling
            Diag.Log(args.Exception.ToString());
            args.Handled = true;
        };

        base.OnStartup(e);

        // One widget window per taskbar selected in the settings
        SpotifyTaskbarWidget.MainWindow.SyncToMonitors();

        // If NO window could be created (startup crash on a particular
        // machine), exit cleanly instead of leaving a zombie process -
        // no UI, no tray icon - holding the single-instance mutex and
        // blocking further attempts to open it (the "flashes then does not
        // load" complaint). The log keeps the exception for diagnosis.
        if (!SpotifyTaskbarWidget.MainWindow.HasWindows)
        {
            Diag.Log("No widget window could be created at startup — exiting so the single-instance mutex is released.");
            IntentionalExit = true;
            Shutdown();
        }
    }
}
