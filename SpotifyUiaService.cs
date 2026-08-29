using System.Diagnostics;
using System.Windows.Automation;

namespace SpotifyTaskbarWidget;

public enum ShuffleMode { Unknown, Off, On, Smart }
public enum RepeatMode { Unknown, Off, Context, Track }

/// <summary>
/// Reads and controls the favorites, shuffle, repeat and volume state through
/// the accessibility tree of the Spotify window (Chromium).
///
/// Spotify recreates the buttons in the DOM on every track change, so only the
/// stable CONTAINERS are cached (the player controls group and the title/artist
/// group); the buttons are looked up fresh inside them on each use - small
/// subtrees, milliseconds. The full (expensive) rebuild only happens when the
/// containers themselves die.
///
/// State from names/aria (language independent wherever possible):
/// - favorites: saved <=> the name mentions "playlist";
/// - shuffle: the name describes the next action (Enable/Disable + "smart");
/// - repeat: aria-checked false/true/mixed = off/playlist/track.
/// </summary>
public sealed class SpotifyUiaService
{
    private static readonly string[] SmartTerms =
        { "inteligente", "smart", "intelligent", "slim", "inteligentny", "akıllı" };

    private static readonly string[] DisableTerms =
        { "desativar", "disable", "desactivar", "désactiver", "deaktivieren",
          "disattiva", "uitschakelen", "wyłącz", "stäng av", "slå fra", "kapat" };

    // Favorites: when the track is saved, the button starts mentioning the
    // "playlist" (the "add to playlist" menu). Many languages keep the
    // anglicism (PT/ES/IT/DE/FR) - and "playlista" (PL) contains "playlist" -
    // but the Nordic languages and Dutch translate it. Matched with Contains.
    private static readonly string[] PlaylistTerms =
        { "playlist", "spellista", "spilleliste", "soittolista", "afspeellijst" };

    private static bool IsLikedName(string name) =>
        PlaylistTerms.Any(t => name.Contains(t, StringComparison.OrdinalIgnoreCase));

    /// <summary>Logs ONCE each distinct name of the favorites and shuffle
    /// buttons, but only on non-English Windows - the population hit by #16.
    /// That collects the real names Spotify exposes in those languages so the
    /// term lists can grow from data instead of guesswork. No noise for English
    /// users (the majority, and where detection already works).</summary>
    private static void LogI18nNames(string likeName, string shuffleName)
    {
        if (System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName
            .Equals("en", StringComparison.OrdinalIgnoreCase))
            return;
        if (likeName.Length > 0)
            Diag.Once("i18n-like:" + likeName, "[i18n] like button name: " + likeName);
        if (shuffleName.Length > 0)
            Diag.Once("i18n-shuffle:" + shuffleName, "[i18n] shuffle button name: " + shuffleName);
    }

    private static readonly Condition ButtonCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Button);
    private static readonly Condition CheckBoxCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.CheckBox);
    private static readonly Condition GroupCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Group);
    private static readonly Condition HyperlinkCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Hyperlink);
    private static readonly Condition SliderCond =
        new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Slider);

    // Only the CONTROLS group is cached (stable across tracks). The title group
    // is recreated on every track - Chromium keeps the old node readable
    // ("zombie") for seconds, so it has to be derived fresh on every read and
    // validated against the current title (the group name contains it).
    private readonly object _lock = new();
    private AutomationElement? _controlsGroup;   // shuffle/previous/play/next + repeat checkbox

    private RangeValuePattern? _volumePattern;
    private double _volMin;
    private double _volMax = 1;

    // ---------- State ----------

    /// <summary>Reads the state. <paramref name="expectedTitle"/> (from SMTC, which
    /// updates instantly) proves the title group already belongs to the current
    /// track; Fresh=false means a possibly stale read (the caller retries).</summary>
    public (bool? Liked, ShuffleMode Shuffle, RepeatMode Repeat, bool Fresh) GetState(string? expectedTitle = null)
    {
        // TryEnter with a timeout instead of lock: a UIA call hung on a dying
        // Spotify held the lock FOREVER (the caller's WaitAsync abandons the
        // wait but does not release the lock) and every button died with it
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return (null, ShuffleMode.Unknown, RepeatMode.Unknown, false);
        try
        {
            try
            {
                EnsureGroups();

                bool fresh = true;
                bool? liked = null;
                var trackInfo = FindTrackInfoGroup();
                if (expectedTitle is { Length: > 0 } && trackInfo != null)
                {
                    string groupName = trackInfo.Current.Name ?? "";
                    fresh = groupName.Contains(expectedTitle, StringComparison.OrdinalIgnoreCase);
                }
                var like = FindLikeButton(trackInfo);
                string likeName = like?.Current.Name ?? "";
                if (like != null)
                    liked = IsLikedName(likeName);

                var shuffleMode = ShuffleMode.Unknown;
                var shuffle = FindShuffleButton();
                string shuffleName = shuffle?.Current.Name ?? "";
                if (shuffle != null)
                {
                    string n = shuffleName.ToLowerInvariant();
                    bool smart = SmartTerms.Any(n.Contains);
                    // Contains (not StartsWith): languages with the verb at the
                    // end ("... deaktivieren", "... kapat") do not start with the term
                    bool disable = DisableTerms.Any(n.Contains);
                    shuffleMode = smart ? (disable ? ShuffleMode.Smart : ShuffleMode.On) : ShuffleMode.Off;
                }

                // Collect the real button names in the affected languages (#16),
                // to grow the term lists from data instead of guessing
                LogI18nNames(likeName, shuffleName);

                var repeatMode = RepeatMode.Unknown;
                var repeat = FindRepeatCheckbox();
                if (repeat != null)
                {
                    var toggle = (TogglePattern)repeat.GetCurrentPattern(TogglePattern.Pattern);
                    repeatMode = toggle.Current.ToggleState switch
                    {
                        ToggleState.Off => RepeatMode.Off,
                        ToggleState.On => RepeatMode.Context,
                        ToggleState.Indeterminate => RepeatMode.Track,
                        _ => RepeatMode.Unknown,
                    };
                }

                return (liked, shuffleMode, repeatMode, fresh);
            }
            catch
            {
                Invalidate();
                return (null, ShuffleMode.Unknown, RepeatMode.Unknown, false);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Actions ----------

    /// <summary>Adds to favorites and CONFIRMS it was saved (the button name
    /// starts mentioning "playlist"). Without confirmation it returns false so the
    /// caller can fall back to the keyboard shortcut. Does not invoke when already
    /// saved (in that state Spotify's button opens a menu).</summary>
    public bool AddToFavorites() => DoWithRetry(() =>
    {
        var like = FindLikeButton(FindTrackInfoGroup());
        if (like == null) return (bool?)null; // stale container -> retry after rebuild

        string name = like.Current.Name ?? "";
        if (IsLikedName(name))
            return true; // already in favorites

        // Spotify's current + button has aria-haspopup, so Chromium only exposes
        // ExpandCollapse - and Expand() fires the click (kDoDefault), which on an
        // unsaved track ADDS to favorites (verified live).
        // Toggle/Invoke stay for older/future Spotify versions.
        if (like.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? expand))
            ((ExpandCollapsePattern)expand).Expand();
        else if (like.TryGetCurrentPattern(TogglePattern.Pattern, out object? toggle))
            ((TogglePattern)toggle).Toggle();
        else if (like.TryGetCurrentPattern(InvokePattern.Pattern, out object? invoke))
            ((InvokePattern)invoke).Invoke();
        else
            return false; // no usable pattern -> real click as the fallback

        // Do not wait for confirmation here: Spotify takes several seconds to
        // update the button text (even with the action already applied), and
        // waiting held the lock. The caller reconciles the state later.
        return true;
    });

    /// <summary>Fallback for when the accessibility patterns fail: restores the
    /// Spotify window briefly and performs a real mouse click on the favorites
    /// button, confirming the result afterwards.</summary>
    public bool AddToFavoritesByClick()
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            try
            {
                EnsureGroups();
                var like = FindLikeButton(FindTrackInfoGroup());
                if (like == null) return false;
                if (IsLikedName(like.Current.Name ?? ""))
                    return true; // already in favorites

                var proc = Process.GetProcessesByName("Spotify")
                    .FirstOrDefault(p => p.MainWindowHandle != IntPtr.Zero);
                if (proc == null) return false;

                IntPtr wnd = proc.MainWindowHandle;
                IntPtr prevFg = Interop.GetForegroundWindow();
                bool wasMinimized = Interop.IsIconic(wnd);
                Interop.GetCursorPos(out var prevCursor);
                try
                {
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_RESTORE);
                    Interop.SetForegroundWindow(wnd);
                    Thread.Sleep(450);

                    like = FindLikeButton(FindTrackInfoGroup()); // fresh rectangles with the window visible
                    if (like == null) return false;
                    var r = like.Current.BoundingRectangle;
                    if (r.IsEmpty) return false;

                    Interop.SetCursorPos((int)(r.Left + r.Width / 2), (int)(r.Top + r.Height / 2));
                    Thread.Sleep(60);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    Interop.mouse_event(Interop.MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                    Thread.Sleep(350);

                    string after = FindLikeButton(FindTrackInfoGroup())?.Current.Name ?? "";
                    return IsLikedName(after);
                }
                finally
                {
                    Interop.SetCursorPos(prevCursor.X, prevCursor.Y);
                    if (wasMinimized)
                        Interop.ShowWindow(wnd, Interop.SW_MINIMIZE);
                    if (prevFg != IntPtr.Zero)
                        Interop.SetForegroundWindow(prevFg);
                }
            }
            catch
            {
                Invalidate();
                return false;
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>One click on Spotify's button: off -> shuffle -> smart -> off.</summary>
    public bool CycleShuffle() => DoWithRetry(() =>
    {
        var shuffle = FindShuffleButton();
        if (shuffle == null) return (bool?)null;
        ((InvokePattern)shuffle.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
        return true;
    });

    /// <summary>One click on Spotify's button: off -> playlist -> track -> off.</summary>
    public bool CycleRepeat() => DoWithRetry(() =>
    {
        var repeat = FindRepeatCheckbox();
        if (repeat == null) return (bool?)null;
        ((TogglePattern)repeat.GetCurrentPattern(TogglePattern.Pattern)).Toggle();
        return true;
    });

    /// <summary>Runs the action with the containers guaranteed; if the elements
    /// are stale, rebuilds once and tries again. Puts the window back in the
    /// foreground (Chromium clicks can steal it).</summary>
    private bool DoWithRetry(Func<bool?> action)
    {
        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                for (int attempt = 0; attempt < 2; attempt++)
                {
                    try
                    {
                        EnsureGroups();
                        bool? result = action();
                        if (result is bool ok) return ok;
                    }
                    catch { }
                    Invalidate();
                }
                return false;
            }
            finally
            {
                RestoreForeground(fg);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Volume ----------

    /// <summary>Current volume of Spotify's own slider, 0..1.</summary>
    public double? GetVolume()
    {
        // Local snapshot of the trio (pattern+min+max): a concurrent rebuild could
        // pair the new pattern with old limits and produce wrong numbers
        var pattern = _volumePattern;
        double min = _volMin, max = _volMax;
        if (pattern != null && max > min)
        {
            try
            {
                return (pattern.Current.Value - min) / (max - min);
            }
            catch { _volumePattern = null; }
        }

        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return null;
        try
        {
            EnsureGroups();
            if (_volumePattern == null)
            {
                // Spotify can recreate ONLY the slider (output device change)
                // with the rest of the controls alive - without this, the lazy
                // rebuild never ran and volume stayed dead forever
                Invalidate();
                EnsureGroups();
            }
            pattern = _volumePattern;
            if (pattern == null || _volMax <= _volMin) return null;
            return (pattern.Current.Value - _volMin) / (_volMax - _volMin);
        }
        catch
        {
            Invalidate();
            return null;
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    /// <summary>Sets the volume on Spotify's own slider (its UI updates), 0..1.
    /// Fast path outside the lock: called repeatedly while dragging the slider.</summary>
    public bool SetVolume(double fraction)
    {
        var pattern = _volumePattern;
        double min = _volMin, max = _volMax;
        if (pattern != null && max > min)
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                pattern.SetValue(min + Math.Clamp(fraction, 0, 1) * (max - min));
                if (fg != IntPtr.Zero && Interop.GetForegroundWindow() != fg)
                    Interop.SetForegroundWindow(fg);
                return true;
            }
            catch
            {
                _volumePattern = null; // rebuild below
            }
        }

        if (!Monitor.TryEnter(_lock, TimeSpan.FromSeconds(3)))
            return false;
        try
        {
            IntPtr fg = Interop.GetForegroundWindow();
            try
            {
                EnsureGroups();
                if (_volumePattern == null)
                {
                    Invalidate(); // slider recreated on its own - force a full rebuild
                    EnsureGroups();
                }
                pattern = _volumePattern;
                if (pattern == null || _volMax <= _volMin) return false;
                pattern.SetValue(_volMin + Math.Clamp(fraction, 0, 1) * (_volMax - _volMin));
                return true;
            }
            catch
            {
                Invalidate();
                return false;
            }
            finally
            {
                if (fg != IntPtr.Zero && Interop.GetForegroundWindow() != fg)
                    Interop.SetForegroundWindow(fg);
            }
        }
        finally
        {
            Monitor.Exit(_lock);
        }
    }

    // ---------- Locating the elements ----------

    // Selection by DOM ORDER (FindAll returns in document order): immune to the
    // stale/empty rectangles of minimized windows. In the title group the
    // favorites button is the last one (cover -> links -> favorites); in the
    // controls, shuffle is the first (shuffle -> previous -> play -> next).

    /// <summary>Title/artist group, derived FRESH on every read from the controls
    /// group (Spotify recreates it on every track).</summary>
    private AutomationElement? FindTrackInfoGroup()
    {
        var controls = _controlsGroup;
        if (controls == null) return null;
        var bar = TreeWalker.ControlViewWalker.GetParent(controls);
        if (bar == null) return null;
        var siblings = bar.FindAll(TreeScope.Children, GroupCond);
        foreach (AutomationElement sibling in siblings)
        {
            if (sibling.FindFirst(TreeScope.Descendants, HyperlinkCond) == null) continue;
            if (sibling.FindFirst(TreeScope.Descendants, ButtonCond) == null) continue;
            return sibling;
        }
        return null;
    }

    private static AutomationElement? FindLikeButton(AutomationElement? trackInfo)
    {
        if (trackInfo == null) return null;
        var buttons = trackInfo.FindAll(TreeScope.Descendants, ButtonCond);
        return buttons.Count > 0 ? buttons[buttons.Count - 1] : null;
    }

    private AutomationElement? FindShuffleButton()
    {
        var group = _controlsGroup;
        if (group == null) return null;
        var buttons = group.FindAll(TreeScope.Children, ButtonCond);
        return buttons.Count > 0 ? buttons[0] : null;
    }

    private AutomationElement? FindRepeatCheckbox() =>
        _controlsGroup?.FindFirst(TreeScope.Children, CheckBoxCond);

    private void Invalidate()
    {
        _controlsGroup = null;
        _volumePattern = null;
    }

    private void EnsureGroups()
    {
        if (_controlsGroup != null)
        {
            try
            {
                _ = _controlsGroup.Current.ControlType;
                return;
            }
            catch (ElementNotAvailableException)
            {
                Invalidate();
            }
        }

        foreach (var proc in Process.GetProcessesByName("Spotify"))
        {
            if (proc.MainWindowHandle == IntPtr.Zero) continue;
            try
            {
                var root = AutomationElement.FromHandle(proc.MainWindowHandle);
                if (FindInWindow(root)) return;
            }
            catch { }
        }
    }

    /// <summary>Full rebuild. Starts from the CHECKBOXES (rare in the tree)
    /// instead of walking every group: the repeat checkbox identifies the
    /// controls group (4 buttons + 1 checkbox) and the rest of the bar follows.</summary>
    private bool FindInWindow(AutomationElement root)
    {
        var checkboxes = root.FindAll(TreeScope.Descendants, CheckBoxCond);
        foreach (AutomationElement checkbox in checkboxes)
        {
            AutomationElement? controls;
            try { controls = TreeWalker.ControlViewWalker.GetParent(checkbox); }
            catch { continue; }
            if (controls == null) continue;

            var buttons = controls.FindAll(TreeScope.Children, ButtonCond);
            if (buttons.Count != 4) continue;

            var bar = TreeWalker.ControlViewWalker.GetParent(controls);
            if (bar == null) continue;

            // Title/artist group: sibling with hyperlinks and at least one button
            AutomationElement? trackInfo = null;
            var siblings = bar.FindAll(TreeScope.Children, GroupCond);
            foreach (AutomationElement sibling in siblings)
            {
                if (sibling.FindFirst(TreeScope.Descendants, HyperlinkCond) == null) continue;
                if (sibling.FindFirst(TreeScope.Descendants, ButtonCond) == null) continue;
                trackInfo = sibling;
                break;
            }
            if (trackInfo == null) continue;

            // Volume: the rightmost slider on the bar (pre-warmed for the fast path)
            try
            {
                var sliders = bar.FindAll(TreeScope.Descendants, SliderCond);
                var volume = sliders.Cast<AutomationElement>().OrderByDescending(SafeLeft).FirstOrDefault();
                if (volume != null)
                {
                    var rv = (RangeValuePattern)volume.GetCurrentPattern(RangeValuePattern.Pattern);
                    _volMin = rv.Current.Minimum;
                    _volMax = rv.Current.Maximum;
                    _volumePattern = rv;
                }
            }
            catch { _volumePattern = null; }

            _controlsGroup = controls;
            return true;
        }

        return false;
    }

    private static void RestoreForeground(IntPtr before)
    {
        if (before == IntPtr.Zero) return;
        Thread.Sleep(80); // Chromium's focus stealing is asynchronous
        if (Interop.GetForegroundWindow() != before)
            Interop.SetForegroundWindow(before);
    }

    private static double SafeLeft(AutomationElement el)
    {
        try
        {
            var r = el.Current.BoundingRectangle;
            return r.IsEmpty ? double.MaxValue : r.Left;
        }
        catch
        {
            return double.MaxValue;
        }
    }
}
