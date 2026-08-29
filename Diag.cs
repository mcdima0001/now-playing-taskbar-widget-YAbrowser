using System.IO;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Minimal diagnostics for silent failures: writes ONCE per cause into
/// errors.log (the same file the global handler uses), so affected users can
/// paste the contents into a report. In English - that is what travels on Reddit.
/// </summary>
internal static class Diag
{
    private static readonly HashSet<string> Seen = new();
    private static string? _lastWrite;
    private static DateTime _lastWriteAt;

    public static void Once(string key, string message)
    {
        lock (Seen)
        {
            if (!Seen.Add(key)) return;
        }
        Log(message);
    }

    /// <summary>Shared writing to errors.log, with a size ceiling (an exception
    /// in a loop filled the disk) and dedup of consecutive repeats.</summary>
    public static void Log(string message)
    {
        try
        {
            lock (Seen)
            {
                // An error inside a timer repeats several times a second - do
                // not write the same message again within 30s
                if (message == _lastWrite && DateTime.UtcNow - _lastWriteAt < TimeSpan.FromSeconds(30))
                    return;
                _lastWrite = message;
                _lastWriteAt = DateTime.UtcNow;

                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    "SpotifyTaskbarWidget");
                Directory.CreateDirectory(dir);
                string file = Path.Combine(dir, "errors.log");
                // Ceiling: start over past 1 MB (the value is in the recent
                // entries; old history does not help in a report)
                if (File.Exists(file) && new FileInfo(file).Length > 1_000_000)
                    File.Delete(file);
                File.AppendAllText(file, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
            }
        }
        catch { }
    }
}
