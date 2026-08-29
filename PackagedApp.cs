namespace SpotifyTaskbarWidget;

/// <summary>
/// Detects whether the app runs packaged (MSIX/Microsoft Store) or loose
/// (exe/Inno). On the Store, updates are handled by the Store itself (the
/// auto-updater hides) and start-with-Windows uses the package StartupTask
/// instead of the registry (which is virtualized under MSIX and would have
/// no real effect).
internal static class PackagedApp
{
    public static bool IsPackaged { get; } = Detect();

    private static bool Detect()
    {
        try
        {
            return Windows.ApplicationModel.Package.Current != null;
        }
        catch
        {
            return false; // outside a package, Package.Current throws
        }
    }
}
