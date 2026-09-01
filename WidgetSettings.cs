using System.IO;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

public class WidgetSettings
{
    /// <summary>Kept for older versions - <see cref="ManualX"/> is what counts today.</summary>
    public bool AutoPosition { get; set; } = true;

    /// <summary>Kept for older versions - <see cref="ManualX"/> is what counts today.</summary>
    public double X { get; set; } = 150;

    /// <summary>Manual positions per taskbar (physical px), indexed by monitor
    /// (0 = primary). A taskbar with no entry positions automatically. Per-monitor
    /// on purpose: dragging one widget must not move the ones on other screens.</summary>
    public Dictionary<int, double> ManualX { get; set; } = new();

    /// <summary>Distance (physical px) between the RIGHT edge of the widget and
    /// the start of the notification area, per taskbar. This is what keeps the
    /// widget tracking the tray as it grows or shrinks (a new icon, the clock
    /// changing width). ManualX stays as the fallback for when the notification
    /// area cannot be read.</summary>
    public Dictionary<int, double> ManualGap { get; set; } = new();

    /// <summary>Font for title/artist. Empty = the system one (whatever the XAML
    /// inherits) - stored by name so it survives a settings file copied to
    /// another machine without that font installed.</summary>
    public string FontFamily { get; set; } = "";

    /// <summary>Widget scale (0.8 = small, 1.0 = normal, 1.1 = large).</summary>
    public double Scale { get; set; } = 1.0;

    /// <summary>Widget brightness/opacity (1.0 = full; lower values for dimmed
    /// taskbars on OLED monitors).</summary>
    public double Opacity { get; set; } = 1.0;

    /// <summary>Kept for older versions - <see cref="Monitors"/> is what counts today.</summary>
    public int MonitorIndex { get; set; } = 0;

    /// <summary>Taskbars carrying a widget (0 = primary, 1+ = secondary).
    /// One window per entry; empty list = old file, migrated from MonitorIndex.</summary>
    public List<int> Monitors { get; set; } = new();

    /// <summary>With Spotify closed: true shows an "Open Spotify" button; false hides the widget.</summary>
    public bool ShowLauncher { get; set; } = false;

    /// <summary>Interface language: "en", "pt", "ru", "uk", or empty to follow
    /// the Windows one.</summary>
    public string Language { get; set; } = "";

    /// <summary>Extra gap (DIP, pre-scale) between the text column and the
    /// buttons, in fit-to-text mode. No effect at normal width: there the
    /// column stretches and there is no slack to give.</summary>
    public double TextPadding { get; set; } = 0;

    /// <summary>Show the album cover to the left of the title.</summary>
    public bool ShowArt { get; set; } = true;

    /// <summary>Text column width glued to the content: with short titles the
    /// widget shrinks instead of leaving a gap between the text and the buttons.
    /// Since the position is right-anchored, cover+text+buttons end up tucked
    /// against the tray.</summary>
    public bool AutoSizeText { get; set; } = false;

    /// <summary>Текстовая колонка забирает всё свободное место панели: виджет
    /// тянется от левого якоря до кнопки Пуск, вместо того чтобы упираться в
    /// потолок ширины и оставлять пустую полосу. Взаимоисключающе с
    /// <see cref="AutoSizeText"/> - там ширина, наоборот, жмётся к тексту.</summary>
    public bool StretchText { get; set; } = false;

    /// <summary>Track progress bar at the bottom of the widget (click to seek).</summary>
    public bool ShowProgress { get; set; } = true;

    /// <summary>Long titles: true = slide ONCE at the start of the track then sit
    /// still; false (default) = continuous slide. Community request (#14).</summary>
    public bool ScrollTitleOnce { get; set; } = false;

    // Visible buttons (on small screens the least important hide themselves)
    // ShowPlay = false turns the widget into a "now playing" display with no
    // controls (community request - play was the only one that never hid)
    public bool ShowPlay { get; set; } = true;
    public bool ShowPrev { get; set; } = true;
    public bool ShowNext { get; set; } = true;
    public bool ShowLike { get; set; } = true;
    public bool ShowShuffle { get; set; } = true;
    public bool ShowRepeat { get; set; } = true;
    public bool ShowVolume { get; set; } = true;

    private static readonly object SaveLock = new();

    private static string Dir =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "SpotifyTaskbarWidget");

    private static string FilePath => Path.Combine(Dir, "settings.json");

    /// <summary>Single instance shared by every widget window.</summary>
    public static WidgetSettings Shared => _shared ??= Load();
    private static WidgetSettings? _shared;

    /// <summary>Fired after every Save - the other windows re-apply their UI.</summary>
    public static event Action? Changed;

    public static WidgetSettings Load()
    {
        WidgetSettings s;
        try
        {
            s = JsonSerializer.Deserialize<WidgetSettings>(File.ReadAllText(FilePath)) ?? new WidgetSettings();
            // A hand-edited / half-written file can carry null fields -
            // normalize INSIDE the try: broken settings must not block startup
            // (it left an invisible process running forever)
            if (s.Monitors is null) s.Monitors = new List<int>();
            if (s.ManualX is null) s.ManualX = new Dictionary<int, double>();
            if (s.ManualGap is null) s.ManualGap = new Dictionary<int, double>();
        }
        catch
        {
            s = new WidgetSettings();
        }
        if (s.Monitors.Count == 0)
            s.Monitors.Add(Math.Max(0, s.MonitorIndex)); // migrate the old format
        s.Monitors = s.Monitors.Where(i => i >= 0).Distinct().OrderBy(i => i).ToList();
        if (!s.AutoPosition && s.ManualX.Count == 0)
            s.ManualX[s.MonitorIndex] = s.X; // migrate the single manual position
        // Never below 20%: broken settings with Opacity=0 made the widget
        // invisible with no way to click it back
        s.Opacity = Math.Clamp(s.Opacity, 0.2, 1.0);
        s.TextPadding = Math.Clamp(s.TextPadding, 0, 40);
        return s;
    }

    public void Save()
    {
        // Mirrors of the old format, so a downgrade still reads something valid
        MonitorIndex = Monitors.Count > 0 ? Monitors[0] : 0;
        AutoPosition = !ManualX.ContainsKey(MonitorIndex);
        if (ManualX.TryGetValue(MonitorIndex, out double x))
            X = x;
        try
        {
            Directory.CreateDirectory(Dir);
        // Atomic write: save alongside and swap by rename - a crash or a power
        // cut halfway left a truncated JSON and Load silently reset ALL of the
        // user's settings
            string tmp = FilePath + ".tmp";
            lock (SaveLock)
            {
                File.WriteAllText(tmp, JsonSerializer.Serialize(this));
                File.Move(tmp, FilePath, overwrite: true);
            }
        }
        catch { }
        Changed?.Invoke();
    }
}
