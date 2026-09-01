using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Microsoft.Win32;


namespace SpotifyTaskbarWidget;

public partial class MainWindow : Window
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "SpotifyTaskbarWidget";

    // Donation link; empty = the menu item stays hidden
    private const string DonateUrl = "https://ko-fi.com/mechanicwb2";

    // Spotify icons (16x16 paths from the web player)
    private static readonly Geometry PlayGeo = Geometry.Parse("M3 1.713a.7.7 0 0 1 1.05-.607l10.89 6.288a.7.7 0 0 1 0 1.212L4.05 14.894A.7.7 0 0 1 3 14.288V1.713z");
    private static readonly Geometry PauseGeo = Geometry.Parse("M2.7 1a.7.7 0 0 0-.7.7v12.6a.7.7 0 0 0 .7.7h2.6a.7.7 0 0 0 .7-.7V1.7a.7.7 0 0 0-.7-.7H2.7zm8 0a.7.7 0 0 0-.7.7v12.6a.7.7 0 0 0 .7.7h2.6a.7.7 0 0 0 .7-.7V1.7a.7.7 0 0 0-.7-.7h-2.6z");
    private static readonly Geometry AddCircleGeo = Geometry.Parse("M8 1.5a6.5 6.5 0 1 0 0 13 6.5 6.5 0 0 0 0-13zM0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8z M11.75 8a.75.75 0 0 1-.75.75H8.75V11a.75.75 0 0 1-1.5 0V8.75H5a.75.75 0 0 1 0-1.5h2.25V5a.75.75 0 0 1 1.5 0v2.25H11a.75.75 0 0 1 .75.75z");
    private static readonly Geometry CheckCircleGeo = Geometry.Parse("M0 8a8 8 0 1 1 16 0A8 8 0 0 1 0 8zm11.748-1.97a.75.75 0 0 0-1.06-1.06l-4.47 4.44-1.405-1.406a.75.75 0 1 0-1.061 1.06l2.466 2.467 5.53-5.5z");

    // Spotify colours; the neutral ones depend on the bar theme (light/dark)
    private static readonly Brush SpotifyGreen = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
    private Brush Subdued = new SolidColorBrush(Color.FromRgb(0xB3, 0xB3, 0xB3));
    private Brush DimWhite = new SolidColorBrush(Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
    private Brush _progressFillNormal = Brushes.White;
    private bool? _lightTheme;

    private readonly MediaHub _media = new();
    private readonly SpotifyUiaService _uia = new();
    private readonly WidgetSettings _settings = WidgetSettings.Shared;
    private bool? _liked;

    /// <summary>This window's taskbar (0 = primary, 1+ = secondary). Every
    /// monitor selected in the settings has its own instance.</summary>
    public int TrayIndex { get; set; }

    /// <summary>Programmatic close (monitor sync) - do not recreate.</summary>
    internal bool ClosedByApp;

    private bool _closed;
    private Action? _mediaChanged;
    private Action? _mediaTimeline;

    private static readonly List<MainWindow> Instances = new();
    private static bool _updateCheckStarted;
    private static bool _recreatePending;

    // Update available (shared by every window): the silent check stores it
    // here and each window highlights its own menu item
    private static (Version Version, string Url)? _pendingUpdate;

    /// <summary>True if at least one widget window is alive.</summary>
    public static bool HasWindows => Instances.Count > 0;

    /// <summary>Guarantees one window per taskbar selected in the settings:
    /// creates the missing ones, closes the extra ones.</summary>
    public static void SyncToMonitors()
    {
        var wanted = WidgetSettings.Shared.Monitors;
        foreach (var win in Instances.Where(w => !wanted.Contains(w.TrayIndex)).ToList())
        {
            win.ClosedByApp = true;
            win.Close();
        }
        foreach (int idx in wanted)
        {
            if (Instances.Any(w => w.TrayIndex == idx)) continue;
            // A window that throws while constructing (e.g. a XAML/startup
            // failure specific to one machine) must not take the others down
            // nor leave the app alive with no UI - log it and carry on
            try { new MainWindow { TrayIndex = idx }.Show(); }
            catch (Exception ex) { Diag.Log($"Widget window failed to create (tray {idx}): {ex}"); }
        }
        // Each window's taskbar choice may have changed (e.g. the orphan that
        // fell back to the primary has to let it go NOW, not in 2s)
        foreach (var win in Instances)
            win._trayCache = IntPtr.Zero;
    }

    // 200 мс - это и есть частота слежения за якорями панели: сам опрос UIA
    // вызывается изнутри UpdatePosition и чаще этого тика случиться не может.
    // Классических WinEvent для XAML-панели Windows 11 не присылает
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(200) };
    private readonly DispatcherTimer _trackTimer = new() { Interval = TimeSpan.FromSeconds(3) };

    private IntPtr _hwnd;
    private bool _moveMode;
    private bool _dragging;
    private bool _dragMoved;
    private bool _pressed;
    private Point _dragStartScreen;
    private double _dragStartLeft;

    private string _lastTrackKey = "";
    private bool _artDirty = true;
    private bool _refreshing;
    private bool _volLoading;
    private bool _spotifyPresent = true;

    /// <summary>Spotify process alive - only its extras depend on this.</summary>
    private bool _spotifyProc;
    private DateTime _sessionLostAt = DateTime.MinValue;
    private DateTime _trackNullSince = DateTime.MinValue;

    // Progress: last known position + the instant it was read (interpolation)
    private TimeSpan _duration;
    private TimeSpan _basePosition;
    private DateTime _basePositionAt;
    private bool _isPlayingUi;

    // State through accessibility (favorite/shuffle/repeat): expensive to read,
    // so only on track changes, after clicks, or every 5s
    private (bool? Liked, ShuffleMode Shuffle, RepeatMode Repeat) _uiaState =
        (null, ShuffleMode.Unknown, RepeatMode.Unknown);
    private DateTime _lastUiaStateAt = DateTime.MinValue;
    private bool _uiaDirty = true;

    // After an optimistic click, old reads (pre-click) keep arriving for ~2s
    // and contradict the new state - they are ignored during that window
    private DateTime _playToggledAt = DateTime.MinValue;
    private DateTime _likedOptimisticAt = DateTime.MinValue;
    private DateTime _shuffleToggledAt = DateTime.MinValue;
    private DateTime _seekAt = DateTime.MinValue;

    private bool AcceptPlayingState(bool incoming) =>
        incoming == _isPlayingUi || DateTime.UtcNow - _playToggledAt > TimeSpan.FromSeconds(2);

    // Taskbar anchors (physical pixels), refreshed in the background through UI Automation
    // Foreground hook: the delegate has to stay referenced (or the GC takes it)
    // A single foreground hook for the whole process: with N windows, N hooks
    // meant N callbacks and N full pipelines for EVERY focus change in the OS
    private static Interop.WinEventDelegate? _fgProc;
    private static IntPtr _fgHook;

    private static void EnsureForegroundHook()
    {
        if (_fgHook != IntPtr.Zero) return;
        _fgProc = (_, _, _, _, _, _, _) =>
        {
            foreach (var win in Instances)
                win.Dispatcher.BeginInvoke((Action)win.UpdatePosition);
        };
        _fgHook = Interop.SetWinEventHook(
            Interop.EVENT_SYSTEM_FOREGROUND, Interop.EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _fgProc, 0, 0, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private readonly object _anchorLock = new();
    private double? _widgetsRightPx;
    private double? _startLeftPx;
    private double? _taskEndPx;
    private DateTime _lastAnchorQuery = DateTime.MinValue;
    private bool _anchorQueryRunning;
    private IntPtr _anchorsTray;

    private const double MaxTextWidth = 150;
    private const double MinTextWidth = 60;

    /// <summary>Floor of the text column in fit-to-text mode.</summary>
    private const double AutoMinTextWidth = 40;

    public MainWindow()
    {
        InitializeComponent();
        Instances.Add(this);
        Loaded += OnLoaded;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        _hwnd = new WindowInteropHelper(this).Handle;
        // Do not steal focus on click and do not show up in Alt+Tab
        int ex = Interop.GetWindowLong(_hwnd, Interop.GWL_EXSTYLE);
        Interop.SetWindowLong(_hwnd, Interop.GWL_EXSTYLE, ex | Interop.WS_EX_TOOLWINDOW | Interop.WS_EX_NOACTIVATE);
    }

    /// <summary>Applies the strings in the selected language.</summary>
    private void ApplyLanguage()
    {
        MoveMenu.Header = L.MoveWidget;
        MoveMenu.ToolTip = L.MoveWidgetTip;
        ResetPosMenu.Header = L.ResetAutoPos;
        MonitorMenu.Header = L.MonitorMenu;
        SizeMenuItem.Header = L.SizeMenu;
        ArtMenu.Header = L.ShowArt;
        AutoSizeMenu.Header = L.AutoSizeText;
        AutoSizeMenu.ToolTip = L.AutoSizeTextTip;
        TextPadMenuItem.Header = L.TextPadding;
        TextPadMenuItem.ToolTip = L.TextPaddingTip;
        FontMenuItem.Header = L.FontMenu;
        LanguageMenuItem.Header = L.LanguageMenu;
        OpacityMenuItem.Header = L.OpacityMenu;
        SizeSmall.Header = L.SizeSmall;
        SizeNormal.Header = L.SizeNormal;
        SizeLarge.Header = L.SizeLarge;
        ButtonsMenuItem.Header = L.ButtonsMenu;
        BtnPlayMenu.Header = L.BtnPlay;
        BtnLikeMenu.Header = L.BtnLike;
        BtnShuffleMenu.Header = L.BtnShuffle;
        BtnPrevMenu.Header = L.BtnPrev;
        BtnNextMenu.Header = L.BtnNext;
        BtnRepeatMenu.Header = L.BtnRepeat;
        BtnVolumeMenu.Header = L.BtnVolume;
        ProgressMenu.Header = L.ProgressBar;
        ScrollOnceMenu.Header = L.ScrollTitleOnce;
        LauncherMenu.Header = L.ShowLauncher;
        LauncherMenu.ToolTip = L.ShowLauncherTip;
        AutoStartMenu.Header = L.AutoStart;
        OpenSpotifyMenu.Header = L.OpenSpotify;
        UpdateMenu.Header = L.CheckUpdates;
        DonateMenu.Header = L.Donate;
        DonateMenu.Visibility = DonateUrl.Length > 0 ? Visibility.Visible : Visibility.Collapsed;
        ExitMenu.Header = L.Exit;

        PrevButton.ToolTip = L.TipPrev;
        PlayPauseButton.ToolTip = L.TipPlayPause;
        NextButton.ToolTip = L.TipNext;
        VolumeButton.ToolTip = L.TipVolume;
        RepeatButton.ToolTip = L.TipRepeat;
        ShuffleButton.ToolTip = L.TipShuffle;
        LikeButton.ToolTip = L.TipLikeAdd;
        LauncherPanel.ToolTip = L.TipOpenSpotify;
        LauncherText.Text = L.OpenSpotify;
        // Placeholder only - with music playing the artist is on screen and
        // rewriting it here wiped it until the next refresh
        if (!_spotifyPresent)
            ArtistText.Text = L.NothingPlaying;
    }

    private bool _uiReady;

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        _uiReady = true; // InitializeComponent finished: named elements exist
        ApplyLanguage();
        if (!UpdateService.IsConfigured)
            UpdateMenu.Visibility = Visibility.Collapsed; // updates disabled in this build
        if (PackagedApp.IsPackaged)
        {
            UpdateMenu.Visibility = Visibility.Collapsed; // the Store handles updates
            _ = InitStartupTaskStateAsync();
        }
        else
        {
            AutoStartMenu.IsChecked = IsAutoStartEnabled();
        }
        ApplyThemeIfChanged();
        RebuildMonitorMenu();
        ApplySettingsUi();
        // The settings are shared: when another window saves, re-apply them
        WidgetSettings.Changed += OnSettingsChanged;

        // Mouse capture stolen mid-drag (menu, system overlay): without this
        // _dragging stayed stuck and the widget stopped repositioning; the next
        // click still saved a phantom position
        Root.LostMouseCapture += (_, _) =>
        {
            // Only when stolen MID-drag - on a normal release _dragging is
            // already false and saving the position goes through intact
            if (_dragging)
            {
                _dragging = false;
                _dragMoved = false;
            }
        };

        // Re-asserting topmost over an open tooltip pushes it behind the bar
        // (community report) - suspend it while one is up
        AddHandler(ToolTipService.ToolTipOpeningEvent,
            new ToolTipEventHandler((_, _) => _tooltipOpen = true), true);
        AddHandler(ToolTipService.ToolTipClosingEvent,
            new ToolTipEventHandler((_, _) => _tooltipOpen = false), true);

        // The volume popup has to beat the bar (which is topmost too under
        // auto-hide): re-assert it the instant it opens
        VolumePopup.Opened += (_, _) =>
        {
            _wheelAccum = 0;
            if (VolumePopup.Child != null &&
                PresentationSource.FromVisual(VolumePopup.Child) is HwndSource src)
                Interop.EnsureTopmost(src.Handle);

            // Opened by the wheel, the mouse may never ENTER the popup - MouseLeave
            // would never fire and it stayed open forever (suppressing
            // ReassertTopmost). Watchdog: close it when the mouse is neither in
            // the popup nor on the button.
            _volPopupWatchdog?.Stop();
            _volPopupWatchdog = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
            _volPopupWatchdog.Tick += (_, _) =>
            {
                if (!VolumePopup.IsOpen ||
                    (!VolumePopupBorder.IsMouseOver && !VolumeButton.IsMouseOver))
                {
                    _volPopupWatchdog?.Stop();
                    VolumePopup.IsOpen = false;
                }
            };
            _volPopupWatchdog.Start();
        };

        UpdatePosition();
        _positionTimer.Tick += (_, _) =>
        {
            UpdatePosition();
            UpdateProgressUi();
            ApplyThemeIfChanged();
        };
        _positionTimer.Start();

        // Clicking the taskbar puts the bar over the widget; re-assert topmost
        // the instant the active window changes (the timer alone left flicker)
        EnsureForegroundHook();
        Closed += (_, _) =>
        {
            if (_trayLocHook != IntPtr.Zero) Interop.UnhookWinEvent(_trayLocHook);
        };

        // Subscribe BEFORE awaiting initialization: with the SMTC retry, init
        // can take minutes - the widget has to react as soon as it catches
        _mediaChanged = () =>
        {
            _artDirty = true;
            _uiaDirty = true;
            Dispatcher.InvokeAsync(() => _ = RefreshTrackAsync());
        };
        _mediaTimeline = () => Dispatcher.InvokeAsync(RefreshTimeline);
        _media.Changed += _mediaChanged;
        _media.TimelineChanged += _mediaTimeline;

        _trackTimer.Tick += (_, _) => _ = RefreshTrackAsync();
        _trackTimer.Start();

        var mediaInit = _media.InitializeAsync();

        if (UpdateService.IsConfigured && !PackagedApp.IsPackaged && !_updateCheckStarted)
        {
            _updateCheckStarted = true; // with several windows, only one checks
            _ = CheckUpdatesQuietlyAsync();
        }
        RefreshUpdateMenu(); // if a new version is already known, highlight it now

        await mediaInit;
        if (_closed)
            return; // closed during the await (monitor sync / Explorer restart)
        await RefreshTrackAsync();
    }

    /// <summary>UI state mirroring the shared settings (menus, scale) - called
    /// at startup and whenever any window saves.</summary>
    private void ApplySettingsUi()
    {
        LauncherMenu.IsChecked = _settings.ShowLauncher;
        ProgressMenu.IsChecked = _settings.ShowProgress;
        ScrollOnceMenu.IsChecked = _settings.ScrollTitleOnce;
        AutoSizeMenu.IsChecked = _settings.AutoSizeText;
        ArtMenu.IsChecked = _settings.ShowArt;
        BtnPlayMenu.IsChecked = _settings.ShowPlay;
        BtnLikeMenu.IsChecked = _settings.ShowLike;
        BtnShuffleMenu.IsChecked = _settings.ShowShuffle;
        BtnPrevMenu.IsChecked = _settings.ShowPrev;
        BtnNextMenu.IsChecked = _settings.ShowNext;
        BtnRepeatMenu.IsChecked = _settings.ShowRepeat;
        BtnVolumeMenu.IsChecked = _settings.ShowVolume;
        ApplyScale();
        UpdateSizeChecks();
        ApplyLanguage();
        BuildLanguageMenu();
        ApplyArt();
        ApplyFont();
        BuildFontMenu();
        ApplyOpacity();
        UpdateOpacityChecks();
        UpdateTextPadChecks();
    }

    private void OnSettingsChanged()
    {
        ApplySettingsUi();
        // Reposition only AFTER the layout settles: changing size/scale and
        // measuring the window in the same instant used the old dimensions and
        // the widget landed misaligned until the next tick
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)UpdatePosition);
        _ = RefreshTrackAsync();
    }

    // ---------- Positioning on the taskbar ----------

    private IntPtr _trayCache;
    private DateTime _trayCacheAt;

    /// <summary>Short cache of the bar handle: enumerating and ordering every
    /// bar on EVERY position event (dozens/s during a slide) was repeated work
    /// to re-derive a handle that almost never changes.</summary>
    private IntPtr GetTargetTray()
    {
        if (_trayCache != IntPtr.Zero && Interop.IsWindow(_trayCache)
            && (DateTime.UtcNow - _trayCacheAt).TotalSeconds < 2)
            return _trayCache;
        _trayCache = ResolveTargetTray();
        _trayCacheAt = DateTime.UtcNow;
        return _trayCache;
    }

    /// <summary>This window's taskbar (primary or secondary). If the target bar
    /// does not exist (monitor turned off), ONE orphan window - the lowest index
    /// - falls back to the primary bar, as long as no other window lives there;
    /// the rest hide. Guarantees the app never goes fully invisible (with no
    /// widget there is no context menu to recover from).</summary>
    private IntPtr ResolveTargetTray()
    {
        if (TrayIndex > 0)
        {
            var secondaries = Interop.GetSecondaryTrays();
            if (TrayIndex <= secondaries.Count)
                return secondaries[TrayIndex - 1];
            // The fallback exists only so the app is not ENTIRELY invisible: if
            // another window still has a bar (primary or a live secondary), or a
            // lower-index orphan falls back first, this one hides
            bool someoneVisible = Instances.Any(w => w != this &&
                (w.TrayIndex == 0 || w.TrayIndex <= secondaries.Count));
            bool lowerOrphan = Instances.Any(w => w != this &&
                w.TrayIndex > secondaries.Count && w.TrayIndex < TrayIndex);
            if (someoneVisible || lowerOrphan)
                return IntPtr.Zero;
        }
        return Interop.FindWindow("Shell_TrayWnd", null);
    }

    private static bool IsTaskbarLeftAligned()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Explorer\Advanced");
            return key?.GetValue("TaskbarAl") is int v && v == 0;
        }
        catch
        {
            return false;
        }
    }

    private IntPtr _ownerTray;
    private DateTime _anchorsMissingSince = DateTime.MinValue;

    // Taskbar movement hook: when it slides (auto-hide) the events arrive by
    // the millisecond and the widget "rides" the animation
    private IntPtr _trayLocHook;
    private IntPtr _hookedTray;
    private Interop.WinEventDelegate? _trayLocProc;
    private bool _updateQueued;

    /// <summary>A reposition is already queued because of a width change -
    /// stops a cascade of passes on every tick.</summary>
    private bool _relayoutQueued;

    private void EnsureTrayLocationHook(IntPtr tray)
    {
        if (tray == _hookedTray) return;
        if (_trayLocHook != IntPtr.Zero)
        {
            Interop.UnhookWinEvent(_trayLocHook);
            _trayLocHook = IntPtr.Zero;
        }
        _hookedTray = tray;
        uint tid = Interop.GetWindowThreadProcessId(tray, out uint pid);
        _trayLocProc ??= (_, _, hwnd, idObject, _, _, _) =>
        {
            // Сама панель поехала (авто-скрытие): ловим каждый кадр
            if (hwnd == _hookedTray && idObject == 0)
            {
                if (_updateQueued) return;
                _updateQueued = true;
                // High priority: every ms counts to catch the start of the slide
                Dispatcher.BeginInvoke(DispatcherPriority.Send, (Action)UpdatePosition);
                return;
            }

            // Что-то внутри панели сменило размер или место - скорее всего
            // соседний виджет (погода) или кнопка Пуск. Якоря устарели, но
            // такие события сыплются пачками, поэтому с порогом
            // Пока ширина едет, якорь важно перечитывать часто: Windows
            // доводит кнопку Пуск постепенно, и цель уточняется на ходу
            if ((DateTime.UtcNow - _anchorsDirtyAt).TotalMilliseconds < 60) return;
            _anchorsDirtyAt = DateTime.UtcNow;
            _anchorsDirty = true;
            if (_updateQueued) return;
            _updateQueued = true;
            // Background в очереди диспетчера уступает всему подряд - для
            // реакции на движение панели это лишние десятки миллисекунд
            Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)UpdatePosition);
        };
        _trayLocHook = Interop.SetWinEventHook(
            Interop.EVENT_OBJECT_LOCATIONCHANGE, Interop.EVENT_OBJECT_LOCATIONCHANGE,
            IntPtr.Zero, _trayLocProc, pid, tid, Interop.WINEVENT_OUTOFCONTEXT);
    }

    private void UpdatePosition()
    {
        _updateQueued = false;
        if (_closed)
            return; // a late dispatch on a closed window: without this it could
                    // re-register hooks on a dead hwnd (crash in a GC'd callback)
        IntPtr tray = GetTargetTray();
        if (tray == IntPtr.Zero || !Interop.GetWindowRect(tray, out var r))
        {
            // Target bar unavailable (monitor off / Explorer restarting): hide
            // and clear the slide state - without this, reappearing played a
            // spurious rise animation
            CancelRide();
            _barWasHidden = false;
            HideWidget();
            return;
        }

        EnsureTrayLocationHook(tray);

        // Window "owned" by the taskbar: the window manager keeps it ALWAYS above
        // its owner - kills the z-order flicker by construction. If Explorer
        // restarts, the window dies with the bar and OnClosed recreates the widget.
        if (tray != _ownerTray && _hwnd != IntPtr.Zero)
        {
            Interop.SetWindowLongPtr(_hwnd, Interop.GWLP_HWNDPARENT, tray);
            _ownerTray = tray;
            ReassertTopmost();
        }

        // Auto-hide: the widget "rides" the bar - the movement hook calls this on
        // every frame of the animation and Y follows the current rect, so it goes
        // down and up glued to the bar. It only hides once the bar settles off
        // screen; anchors stay intact (X does not change in a vertical slide).
        int trayHeightPx = r.Bottom - r.Top;
        int visiblePx = Interop.GetTaskbarVisiblePx(r, out int monitorBottomPx, out int workAreaBottomPx);

        if (!Interop.GetWindowRect(_hwnd, out var w))
            return;
        int winWidth = w.Right - w.Left;
        int winHeight = w.Bottom - w.Top;

        // Centre on the DRAWN band of the bar, not on its window rect (on 25H2
        // the rect is taller and the widget floated - issue #9). The work area
        // reservation gives the real drawn height for any bar (multi-row
        // included); with auto-hide there is no reservation and the Win11 48 DIP
        // heuristic applies.
        int reservedPx = monitorBottomPx - workAreaBottomPx;
        int barBandPx = reservedPx > 8
            ? Math.Min(trayHeightPx, reservedPx)
            : Math.Min(trayHeightPx, (int)Math.Round(48 * Interop.GetDpiForWindow(tray) / 96.0));
        // Where the widget "would be" with the bar settled off screen (same
        // vertical offset inside it): start of the rise and target of the fall
        int belowEdgeTopPx = monitorBottomPx - 2 + (barBandPx - winHeight) / 2;

        if (_rideAnimating)
        {
            // The animation owns the position; if the bar reverses halfway,
            // we reverse too, starting from where the widget is
            if (_rideDown && visiblePx >= trayHeightPx - 4)
            {
                CancelRide();
                StartRide(w.Left, w.Top, r.Bottom - barBandPx + (barBandPx - winHeight) / 2,
                    down: false, winWidth, winHeight, monitorBottomPx);
            }
            else if (!_rideDown && visiblePx <= 8)
            {
                CancelRide();
                StartRide(w.Left, w.Top, belowEdgeTopPx,
                    down: true, winWidth, winHeight, monitorBottomPx);
            }
            return;
        }

        // Fraction of the hide path the bar has already covered - the widget
        // enters the animation at that point to stay in sync with it
        double hiddenPhase = 1.0 - Math.Clamp((double)visiblePx / trayHeightPx, 0, 1);

        // An interaction in progress "pins" the widget: hiding mid-drag of the
        // slider/menu closed the popup in the user's hands (and a hidden
        // move-mode drag lost mouse capture and left _dragging stuck). Once the
        // interaction ends, the next tick hides normally.
        bool pinned = _dragging || VolumePopup.IsOpen ||
                      (Root.ContextMenu?.IsOpen ?? false);

        if (visiblePx <= 8)
        {
            if (pinned)
                return;
            // Settled off screen. If the widget is still visible, the hide
            // happened in a single jump - animate the fall anyway.
            if (Visibility == Visibility.Visible && Interop.IsAutoHideEnabled())
            {
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
                return;
            }
            _barWasHidden = true;
            HideWidget();
            return;
        }

        if (visiblePx < trayHeightPx - 4)
        {
            // Sliding. Widget visible = the bar started hiding: animate our fall
            // (following its coarse window steps was jerky). Invisible = a reveal
            // is under way: wait for it to settle - the rise animates then.
            if (Visibility == Visibility.Visible && !pinned && Interop.IsAutoHideEnabled())
                StartRide(w.Left, w.Top, belowEdgeTopPx, down: true, winWidth, winHeight, monitorBottomPx, hiddenPhase);
            return;
        }

        if (tray != _anchorsTray)
        {
            // Target bar changed (another monitor): drop the previous anchors
            _anchorsTray = tray;
            _lastAnchorQuery = DateTime.MinValue;
            lock (_anchorLock)
            {
                _widgetsRightPx = null;
                _startLeftPx = null;
                _taskEndPx = null;
            }
        }
        // From here down the bar is settled on screen - anchors are reliable
        RefreshAnchors(tray);
        double? widgetsRightPx, startLeftPx, taskEndPx;
        lock (_anchorLock)
        {
            widgetsRightPx = _widgetsRightPx;
            startLeftPx = _startLeftPx;
            taskEndPx = _taskEndPx;
        }

        // All positioning maths run in PHYSICAL PIXELS of the target bar:
        // converting to DIP used the scale of the window's CURRENT monitor and,
        // moving to a monitor with a different DPI, the sum came out wrong and
        // the widget landed in the middle of the screen.
        double windowScale = Interop.GetDpiForWindow(_hwnd) / 96.0; // px per DIP, on the current monitor

        // Виджет по просьбе смещён на пару пикселей вниз от геометрического
        // центра полосы - визуально так он садится ровнее соседей
        const int VerticalNudgePx = 3;
        int topPx = r.Bottom - barBandPx + (barBandPx - winHeight) / 2 + VerticalNudgePx;

        bool rightAnchored = false;
        int rightAnchorLeftLimitPx = r.Left + 12;
        int leftPx, rightLimitPx;
        if (_settings.ManualX.TryGetValue(TrayIndex, out double manualX))
        {
            // Manual position for THIS bar (per monitor - dragging one widget
            // must not drag the ones on other screens). Stored as a DISTANCE to
            // the tray, not as an absolute X: when it grows (a new icon, a wider
            // clock) or shrinks, the widget follows instead of staying put and
            // ending up covered.
            int? notifyLeftManual = Interop.GetTrayNotifyLeft(tray);
            bool haveGap = _settings.ManualGap.TryGetValue(TrayIndex, out double gap);
            if (!haveGap && notifyLeftManual.HasValue)
            {
                // Migrating an old position (X only): convert it once
                gap = notifyLeftManual.Value - (manualX + winWidth);
                _settings.ManualGap[TrayIndex] = gap;
                haveGap = true;
                // Save OUTSIDE the positioning path: Save fires
                // Changed -> ApplySettingsUi -> UpdatePosition (re-entry)
                Dispatcher.BeginInvoke(DispatcherPriority.Background,
                    (Action)(() => _settings.Save()));
            }
            leftPx = haveGap && notifyLeftManual.HasValue
                ? (int)Math.Round(notifyLeftManual.Value - gap - winWidth)
                : (int)manualX;
            leftPx = Math.Max(r.Left + 4, Math.Min(leftPx, r.Right - winWidth - 4));
            rightLimitPx = r.Right - 4;
        }
        else if (!IsTaskbarLeftAligned())
        {
            // On a centred bar the Start button always exists - a null anchor
            // means the read has not arrived yet or failed.
            if (!startLeftPx.HasValue && Visibility == Visibility.Visible)
            {
            // Already well positioned: STAY PUT until the anchors come back -
            // hiding and reappearing at the left edge (over the weather button)
            // was exactly the jump that was reported
                leftPx = w.Left;
                rightLimitPx = r.Right - 4;
            }
            else if (!startLeftPx.HasValue)
            {
                // Still without a position (startup / first reveal): wait instead
                // of positioning blind; past the limit, fall back to the left
                if (_anchorsMissingSince == DateTime.MinValue)
                    _anchorsMissingSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _anchorsMissingSince < TimeSpan.FromSeconds(4))
                {
                    HideWidget();
                    return;
                }
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : r.Left + 12;
                rightLimitPx = r.Right - 4;
            }
            else
            {
                _anchorsMissingSince = DateTime.MinValue;
                // Centred icons (on any bar/monitor): the free space is on the
                // left - align right after the widgets/weather button; without
                // it, at the left edge. Never invade the Start button.
                leftPx = widgetsRightPx.HasValue ? (int)widgetsRightPx.Value + 8 : r.Left + 12;
                // Зазор до кнопки Пуск. Он крошечный намеренно: видимое
                // расстояние создаёт ещё и собственное поле крайней кнопки
                // виджета (26 DIP против 13 у значка)
                rightLimitPx = (int)startLeftPx.Value - 2;
            }
        }
        else
        {
            // Left-aligned icons: the empty space is on the right - tuck in
            // before the system icons/clock, without ever covering the row of
            // app icons
            rightAnchored = true;
            int? notifyLeftPx = Interop.GetTrayNotifyLeft(tray);
            rightLimitPx = notifyLeftPx ?? (r.Right - 220);
            rightLimitPx -= 8;
            if (taskEndPx.HasValue)
                rightAnchorLeftLimitPx = (int)taskEndPx.Value + 8;
            leftPx = Math.Max(rightAnchorLeftLimitPx, rightLimitPx - winWidth);
        }

        if (_spotifyPresent)
        {
            int availPx = rightAnchored ? rightLimitPx - rightAnchorLeftLimitPx : rightLimitPx - leftPx;
            if (!ApplyResponsiveLayout(availPx / windowScale))
            {
                // On a crowded bar not even the minimum version fits: hide
                // instead of overflowing onto the clock/icons (issue #10)
                _barWasHidden = false;
                HideWidget();
                return;
            }

            // The widget width changes with the title (fit-to-text) and with the
            // buttons that fit, but the layout only settles on the NEXT pass:
            // positioning now would use the PREVIOUS track's width and the
            // buttons ended up shifted until the next tick (visible on every
            // track change). Apply the layout now and, if the width changed,
            // repeat the positioning with the right value - the anchor is the
            // right edge, so a wrong width shifts everything.
            UpdateLayout();
            // Пока идёт анимация ширины, окно меняет размер каждый кадр -
            // перезапускать из-за этого позиционирование не нужно
            if (!_sizeAnimating &&
                Interop.GetWindowRect(_hwnd, out var wAfter) &&
                wAfter.Right - wAfter.Left != winWidth &&
                !_relayoutQueued)
            {
                _relayoutQueued = true;
                Dispatcher.BeginInvoke(DispatcherPriority.Loaded, (Action)(() =>
                {
                    _relayoutQueued = false;
                    UpdatePosition();
                }));
                return;
            }
        }

        // Hide when: the app is fullscreen (irrelevant under auto-hide - maximized
        // windows cover the whole screen and would give false positives; visibility
        // already follows the bar); or Spotify closed with no launcher button.
        bool hide = (!Interop.IsAutoHideEnabled() && Interop.IsForegroundFullscreen(_hwnd, tray))
                    || (!_spotifyPresent && !_settings.ShowLauncher);
        if (hide)
        {
            _barWasHidden = false;
            HideWidget();
            return;
        }

        // Bar reveal: Windows teleports its window to the destination and animates
        // only the visuals - there are no frames to follow. We animate the rise
        // ourselves, emerging from the screen edge in sync with the bar.
        // (Only meaningful with auto-hide - without it, a transient degenerate
        // bar rect triggered a spurious rise.)
        if (_barWasHidden && !_dragging && Interop.IsAutoHideEnabled())
        {
            _barWasHidden = false;
            StartRide(leftPx, belowEdgeTopPx, topPx, down: false, winWidth, winHeight, monitorBottomPx);
            return;
        }
        _barWasHidden = false;

        if (!_dragging && (Math.Abs(w.Left - leftPx) > 1 || Math.Abs(w.Top - topPx) > 1))
            MoveOrSlideTo(w.Left, w.Top, leftPx, topPx);

        // While sliding, clip the part of the widget that already left the screen -
        // without this, the excess showed up crossing a monitor arranged below
        Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - topPx);

        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
        ReassertTopmost();
    }

    private bool _tooltipOpen;

    /// <summary>Hides the widget and ALWAYS closes the volume popup and the
    /// context menu - every hide path goes through here so they cannot diverge
    /// (some paths used to leave orphan popups floating).</summary>
    private void HideWidget()
    {
        StopSlide(); // не доводить сдвиг у скрытого окна
        VolumePopup.IsOpen = false;
        if (Root.ContextMenu is { IsOpen: true } menu)
            menu.IsOpen = false;
        if (Visibility != Visibility.Hidden)
            Visibility = Visibility.Hidden;
    }

    /// <summary>Re-asserts the widget on top, EXCEPT while one of our tooltips/
    /// popups is open - re-asserting over them pushes them behind the bar (which
    /// under auto-hide also lives in the topmost band).</summary>
    private void ReassertTopmost()
    {
        if (!_tooltipOpen && !VolumePopup.IsOpen)
            Interop.EnsureTopmost(_hwnd);
    }

    // ---------- Slide animation (taskbar auto-hide) ----------

    private bool _barWasHidden;
    private bool _rideAnimating;
    private bool _rideDown;
    // ---------- Плавный горизонтальный сдвиг ----------

    // Соседи по панели (погода, Пуск) меняют ширину скачком, и виджет
    // телепортировался на новое место. Короткий разгон с торможением убирает
    // рывок; вертикаль трогать нельзя - ею занимается "поездка" за панелью.
    private volatile bool _anchorsDirty;
    private DateTime _anchorsDirtyAt = DateTime.MinValue;

    private DispatcherTimer? _slideTimer;
    private double _slideFromX;
    private int _slideToX, _slideY;
    private DateTime _slideStartedAt;
    private const double SlideMs = 150;

    private void MoveOrSlideTo(int curLeft, int curTop, int leftPx, int topPx)
    {
        // Прыжок по вертикали (панель переехала, смена монитора) или далёкий
        // сдвиг анимировать незачем - это будет выглядеть как уползание
        if (curTop != topPx || Math.Abs(leftPx - curLeft) > 260)
        {
            StopSlide();
            Interop.MoveWindowTo(_hwnd, leftPx, topPx);
            return;
        }

        _slideToX = leftPx;
        _slideY = topPx;
        if (_slideTimer is { IsEnabled: true })
            return; // цель обновлена на лету, кадры уже идут

        _slideFromX = curLeft;
        _slideStartedAt = DateTime.UtcNow;
        if (_slideTimer == null)
        {
            _slideTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(8),
            };
            _slideTimer.Tick += SlideTick;
        }
        _slideTimer.Start();
    }

    private void SlideTick(object? sender, EventArgs e)
    {
        if (_closed || _hwnd == IntPtr.Zero) { StopSlide(); return; }
        double t = (DateTime.UtcNow - _slideStartedAt).TotalMilliseconds / SlideMs;
        if (t >= 1)
        {
            StopSlide();
            Interop.MoveWindowTo(_hwnd, _slideToX, _slideY);
            return;
        }
        double eased = 1 - Math.Pow(1 - t, 3); // замедление к концу
        int x = (int)Math.Round(_slideFromX + (_slideToX - _slideFromX) * eased);
        Interop.MoveWindowTo(_hwnd, x, _slideY);
    }

    private void StopSlide() => _slideTimer?.Stop();

    // ---------- Плавное изменение ширины ----------

    // Ширина меняется, когда съезжают якоря панели. Анимацию ведёт сам WPF:
    // самодельный DispatcherTimer здесь голодал в очереди диспетчера и вместо
    // 8 мс срабатывал раз в проход позиционирования - ширина ползла ступенями
    // по два пикселя больше секунды. SnapshotAndReplace продолжает движение с
    // текущего значения, поэтому смена цели на лету не даёт рывка
    private bool _sizeAnimating;
    private const double SizeMs = 140;

    private void AnimateTextWidth(double to)
    {
        var anim = new DoubleAnimation(to, TimeSpan.FromMilliseconds(SizeMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.HoldEnd,
        };
        anim.Completed += (_, _) =>
        {
            _sizeAnimating = false;
            UpdateMarquee(); // бегущая строка считается от финальной ширины
        };
        _sizeAnimating = true;
        TextStack.BeginAnimation(FrameworkElement.WidthProperty, anim, HandoffBehavior.SnapshotAndReplace);
    }

    private void SetTextWidthNow(double width)
    {
        // Снять анимацию, иначе она держит значение и присваивание не сработает
        TextStack.BeginAnimation(FrameworkElement.WidthProperty, null);
        _sizeAnimating = false;
        TextStack.Width = width;
        UpdateMarquee();
    }

    private DispatcherTimer? _rideTimer;

    /// <summary>Animates the widget between the settled position and the bottom
    /// of the screen (both ways), with the clip making it emerge/submerge at the
    /// edge. Fluent motion: entrances decelerate, exits accelerate.
    /// startPhase (0..1): fraction of the path the bar has ALREADY covered when
    /// the trigger arrived - its window's first step comes late, and entering the
    /// curve halfway keeps the widget in sync instead of trailing it.</summary>
    private void StartRide(int leftPx, int fromTopPx, int toTopPx, bool down,
        int winWidth, int winHeight, int monitorBottomPx, double startPhase = 0)
    {
        var sw = Stopwatch.StartNew();
        const double DurationMs = 220; // approximates the bar's own animation
        startPhase = Math.Clamp(startPhase, 0, 1);
        // Point on the curve whose easing matches the requested phase
        double t0 = down ? Math.Cbrt(startPhase) : 1 - Math.Cbrt(1 - startPhase);
        double t0Ms = t0 * DurationMs;

        _rideAnimating = true;
        _rideDown = down;
        if (down)
            VolumePopup.IsOpen = false;

        double eased0 = down ? t0 * t0 * t0 : 1 - Math.Pow(1 - t0, 3);
        int startTopPx = (int)Math.Round(fromTopPx + (toTopPx - fromTopPx) * eased0);
        Interop.MoveWindowTo(_hwnd, leftPx, startTopPx);
        Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - startTopPx);
        if (Visibility != Visibility.Visible)
            Visibility = Visibility.Visible;
        ReassertTopmost();

        _rideTimer?.Stop();
        _rideTimer = new DispatcherTimer(DispatcherPriority.Render)
        {
            Interval = TimeSpan.FromMilliseconds(10)
        };
        _rideTimer.Tick += (_, _) =>
        {
            double t = Math.Min(1.0, (t0Ms + sw.ElapsedMilliseconds) / DurationMs);
            double eased = down ? t * t * t : 1 - Math.Pow(1 - t, 3);
            int topPx = (int)Math.Round(fromTopPx + (toTopPx - fromTopPx) * eased);
            Interop.MoveWindowTo(_hwnd, leftPx, topPx);
            Interop.ClipWindowBottom(_hwnd, winWidth, winHeight, monitorBottomPx - topPx);
            if (t >= 1.0)
            {
                _rideTimer?.Stop();
                _rideAnimating = false;
                if (down)
                {
                    _barWasHidden = true;
                    Visibility = Visibility.Hidden;
                }
            }
        };
        _rideTimer.Start();
    }

    private void CancelRide()
    {
        _rideTimer?.Stop();
        _rideAnimating = false;
        // "Поездка" сама ставит X и Y - параллельный горизонтальный сдвиг
        // дрался бы с ней за позицию окна
        StopSlide();
    }

    /// <summary>
    /// Fits the widget into the available space: first shrinks the text down to a
    /// minimum; if it still does not fit, hides the least important buttons
    /// (volume -> shuffle -> favorites -> next -> previous).
    /// Returns false when not even the minimum version fits the given space.
    /// </summary>
    /// <summary>Срезает пустое поле у последней видимой кнопки, чтобы значок
    /// стоял ближе к краю виджета. Набор кнопок меняется от настроек и ширины
    /// панели, поэтому крайняя вычисляется каждый раз.</summary>
    private void TrimEdgeButton(double trim)
    {
        // Порядок совпадает с разметкой; правое поле по умолчанию у части
        // кнопок 2, у остальных 0
        (Button Btn, double Right)[] tail =
        {
            (LikeButton, 2), (ShuffleButton, 2), (PrevButton, 2),
            (PlayPauseButton, 2), (NextButton, 0), (RepeatButton, 0), (VolumeButton, 0),
        };

        Button? last = null;
        foreach (var (btn, _) in tail)
            if (btn.Visibility == Visibility.Visible) last = btn;

        foreach (var (btn, right) in tail)
        {
            var m = btn.Margin;
            double want = ReferenceEquals(btn, last) ? -trim : right;
            if (Math.Abs(m.Right - want) > 0.1)
                btn.Margin = new Thickness(m.Left, m.Top, want, m.Bottom);
        }
    }

    private bool ApplyResponsiveLayout(double availableDip)
    {
        double s = _settings.Scale;
        double avail = availableDip / s; // work in pre-scale units

        const double IconBtn = 28;            // 26 + margins
        // Крайняя кнопка отдаёт часть собственного поля: она 26 DIP при значке
        // 13, и справа оставался лишний воздух. Отрицательное поле срезает
        // пустую часть кнопки, сам значок остаётся целым
        const double EdgeTrim = 4;
        const double PlayBtn = 34;            // 30 + margins
        // Поля Root (8 слева, справа ноль) + отступы текста + обложка.
        // Справа поле урезано: у крайней кнопки есть собственное поле внутри
        // (26 против 13 у значка), и вместе с 8 получалась заметная пустота
        double BasePart = 8 + 15 + (_settings.ShowArt ? 34 : 0) - EdgeTrim;

        // Play stopped being mandatory: someone who only wants the "now playing"
        // display can hide it (community request)
        double used = BasePart + MinTextWidth + (_settings.ShowPlay ? PlayBtn : 0);
        if (avail < used)
            return false;

        bool prev = false, next = false, like = false, shuffle = false, repeat = false, volume = false;
        Take(ref prev, _settings.ShowPrev);
        Take(ref next, _settings.ShowNext);
        // Избранное: у Spotify через его интерфейс, у вкладки - через кнопку
        // сайта, которую нашло расширение. Без второго условия кнопка пропадала
        // всегда, когда десктопный Spotify не запущен
        Take(ref like, _settings.ShowLike && (_spotifyProc || _media.BrowserCanLike));
        Take(ref shuffle, _settings.ShowShuffle);
        Take(ref repeat, _settings.ShowRepeat);
        Take(ref volume, _settings.ShowVolume && _spotifyProc); // internal volume: Spotify only

        // Maximum space the text column can take on this bar
        double room = Math.Min(MaxTextWidth, MinTextWidth + (avail - used));
        double text;
        if (_settings.AutoSizeText)
        {
            // NATURAL width of the text, measured unconstrained (same technique as
            // the marquee: the layout no longer constrains them in this measure).
            // The column shrinks to what title/artist need, instead of stretching
            // and leaving a gap before the buttons.
            TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            ArtistText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double natural = Math.Ceiling(Math.Max(TitleText.DesiredSize.Width, ArtistText.DesiredSize.Width));
            // The minimum stops the widget bouncing on two-letter titles;
            // room always wins, so it never overflows a full bar
            text = Math.Min(room, Math.Max(AutoMinTextWidth, natural + _settings.TextPadding));
        }
        else if (_settings.StretchText)
        {
            // Растянуть до правого предела (кнопка Пуск / область уведомлений):
            // room ограничен MaxTextWidth, и из-за него между виджетом и Пуском
            // оставалась пустая полоса
            text = Math.Max(MinTextWidth, MinTextWidth + (avail - used));
        }
        else
        {
            text = Math.Max(MinTextWidth, room);
        }

        SetVis(PlayPauseButton, _settings.ShowPlay);
        SetVis(PrevButton, prev);
        SetVis(NextButton, next);
        SetVis(LikeButton, like);
        SetVis(ShuffleButton, shuffle);
        SetVis(RepeatButton, repeat);
        SetVis(VolumeButton, volume);
        TrimEdgeButton(EdgeTrim);
        // Ширина меняется, когда съезжают якоря панели: кнопка Пуск на
        // центрированной панели ходит от числа открытых окон. Мгновенная смена
        // читается как рывок, поэтому ведём её анимацией
        double curText = double.IsNaN(TextStack.Width) ? text : TextStack.Width;
        if (Math.Abs(curText - text) > 1)
        {
            // Большой скачок (смена монитора, показ скрытого окна) анимировать
            // незачем - это выглядело бы как уползание
            if (Math.Abs(curText - text) > 220 || Visibility != Visibility.Visible)
                SetTextWidthNow(text);
            else
                AnimateTextWidth(text);
        }
        return true;

        void Take(ref bool flag, bool wanted)
        {
            if (wanted && used + IconBtn <= avail)
            {
                flag = true;
                used += IconBtn;
            }
        }

        static void SetVis(UIElement el, bool show)
        {
            var v = show ? Visibility.Visible : Visibility.Collapsed;
            if (el.Visibility != v) el.Visibility = v;
        }
    }

    /// <summary>
    /// Refreshes the bar anchors (widgets button and Start button) in the
    /// background, at most every 5 seconds - UI Automation queries are not free.
    /// </summary>
    private int _startMissingReads;

    private void RefreshAnchors(IntPtr tray)
    {
        // Дешёвый опрос (погода + Пуск) можно гонять часто - именно он отвечает
        // за то, как быстро виджет реагирует на смену ширины соседей. Полный,
        // с обходом всех кнопок панели, нужен только правой привязке
        bool full = IsTaskbarLeftAligned();
        // В простое опрашивать часто незачем: о переменах сообщает хук панели,
        // и тогда читаем сразу. Постоянный частый опрос UIA стоил ~8% ядра
        double minGapMs = _anchorsDirty ? 60 : (full ? 5000 : 200);
        if ((DateTime.UtcNow - _lastAnchorQuery).TotalMilliseconds < minGapMs)
            return;
        _anchorsDirty = false;
        // Watchdog: if a query hung (UIA against a dying Explorer), the flag
        // must not hold the anchors forever - after 15s start another one
        if (_anchorQueryRunning && (DateTime.UtcNow - _lastAnchorQuery).TotalSeconds < 15)
            return;

        _anchorQueryRunning = true;
        _lastAnchorQuery = DateTime.UtcNow;
        Task.Run(() =>
        {
            bool moved = false;
            try
            {
                var (ok, widgetsRight, startLeft, taskButtonsRight) = TaskbarAnchors.Get(tray, full);
                lock (_anchorLock)
                {
                    // The target bar changed while the query ran: these values
                    // are coordinates of the wrong monitor - throw them away
                    if (tray != _anchorsTray)
                        return;
                    // Failed read -> ALL previous anchors are kept (a transient
                    // UIA failure != the bar layout changed; this was what put
                    // the widget on top of the weather button)
                    if (!ok)
                        return;
                    // Start always exists - an OK read without it is suspicious;
                    // but accept it on the 3rd in a row, otherwise a genuinely
                    // hidden Start (modified shells) froze every anchor
                    if (!startLeft.HasValue && _startLeftPx.HasValue && ++_startMissingReads < 3)
                        return;
                    bool startVanished = !startLeft.HasValue && _startLeftPx.HasValue;
                    _startMissingReads = 0;
                    // If Start vanished (abnormal shell state), the other null
                    // anchors probably just failed TOGETHER - keep the old ones;
                    // with a complete read, null really means disabled
                    double? newWidgets = startVanished ? (widgetsRight ?? _widgetsRightPx) : widgetsRight;
                    moved = newWidgets != _widgetsRightPx || startLeft != _startLeftPx;
                    _widgetsRightPx = newWidgets;
                    _taskEndPx = startVanished || !full ? (taskButtonsRight ?? _taskEndPx) : taskButtonsRight;
                    _startLeftPx = startLeft;
                }
            }
            finally
            {
                _anchorQueryRunning = false;
            }

            // Опрос асинхронный, и без этого новые якоря ждали бы следующего
            // прохода позиционирования - то есть до секунды по таймеру.
            // Именно отсюда бралась задержка перед началом анимации
            if (moved && !_closed)
                Dispatcher.BeginInvoke(DispatcherPriority.Render, (Action)UpdatePosition);
        });
    }

    // ---------- Track refresh ----------

    private DateTime _spotifyProcAt = DateTime.MinValue;

    /// <summary>Перечисление процессов стоит заметно дороже остального тика, а
    /// Spotify не появляется и не исчезает по десять раз в минуту - хватает
    /// проверки раз в 5 секунд. Объекты Process закрываем сразу: иначе на
    /// каждый тик оставались бы висящие хендлы до сборки мусора.</summary>
    private void RefreshSpotifyPresence()
    {
        if (DateTime.UtcNow - _spotifyProcAt < TimeSpan.FromSeconds(5))
            return;
        _spotifyProcAt = DateTime.UtcNow;
        var procs = Process.GetProcessesByName("Spotify");
        _spotifyProc = procs.Length > 0;
        foreach (var p in procs) p.Dispose();
    }

    private async Task RefreshTrackAsync()
    {
        if (_refreshing || _closed) return;
        _refreshing = true;
        try
        {
            // Any player that publishes to SMTC (YouTube in a browser, Apple
            // Music, ...). The Spotify process no longer decides whether the
            // widget exists - it only tells whether its extras make sense.
            RefreshSpotifyPresence();

            // SMTC calls can hang forever on a session in teardown (player
            // closing/reopening) - without a timeout the _refreshing flag stayed
            // stuck and the widget froze until a restart
            TrackInfo? track = null;
            try { track = await _media.GetTrackAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
            catch (TimeoutException) { }

            // On track changes the session/title go empty for a few hundred ms -
            // keep the current state on screen and re-check right away, instead
            // of flashing placeholders or hiding the widget
            bool noData = track == null || string.IsNullOrWhiteSpace(track.Title);
            if (noData && _lastTrackKey.Length > 0)
            {
                if (_trackNullSince == DateTime.MinValue)
                    _trackNullSince = DateTime.UtcNow;
                if (DateTime.UtcNow - _trackNullSince < TimeSpan.FromSeconds(1.2))
                {
                    _ = QuickRecheckAsync();
                    return;
                }
            }
            else if (!noData)
            {
                _trackNullSince = DateTime.MinValue;
            }

            // Losing the session persistently = the player is closing.
            // Hide, with no intermediate states.
            if (noData && _lastTrackKey.Length > 0)
            {
                _sessionLostAt = DateTime.UtcNow;
                _lastTrackKey = "";
            }
            bool closing = noData && DateTime.UtcNow - _sessionLostAt < TimeSpan.FromSeconds(6);
            // With Spotify open the old behaviour stays (shows "nothing playing");
            // without it, the widget only exists while there really is a session -
            // otherwise an empty rectangle sat glued to the bar.
            _spotifyPresent = !noData || (_spotifyProc && !closing);

            var launcherWanted = !_spotifyPresent && _settings.ShowLauncher
                ? Visibility.Visible : Visibility.Collapsed;
            var contentWanted = _spotifyPresent ? Visibility.Visible : Visibility.Collapsed;
            if (LauncherPanel.Visibility != launcherWanted) LauncherPanel.Visibility = launcherWanted;
            if (ContentPanel.Visibility != contentWanted) ContentPanel.Visibility = contentWanted;

            if (!_spotifyPresent)
            {
                _liked = null;
                _duration = TimeSpan.Zero;
                VolumePopup.IsOpen = false;
                UpdateProgressUi();
                UpdatePosition(); // hide/show immediately, without waiting for the timer
                return;
            }

            _duration = track?.Duration ?? TimeSpan.Zero;
            if (AcceptPlayingState(track?.IsPlaying == true))
            {
                // Anchor on Windows' LastUpdatedTime (not on the read instant):
                // re-reading an old snapshot gives the same interpolated value - no jumps
                TimeSpan pos = track?.Position ?? TimeSpan.Zero;
                DateTime posAt = track?.PositionAtUtc ?? DateTime.UtcNow;
                // Snapshot of the previous track (position > duration, or very old
                // on a just-changed track): show from the start until it settles
                bool stale = pos > _duration ||
                             (track != null && track.Title + "|" + track.Artist != _lastTrackKey &&
                              DateTime.UtcNow - posAt > TimeSpan.FromSeconds(5));
                if (stale)
                {
                    pos = TimeSpan.Zero;
                    posAt = DateTime.UtcNow;
                }
                // After a seek, ignore positions captured BEFORE the jump
                if (!(DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && posAt < _seekAt))
                {
                    _basePosition = pos;
                    _basePositionAt = posAt;
                }
                _isPlayingUi = track?.IsPlaying == true;
            }
            UpdateProgressUi();

            if (track == null || string.IsNullOrWhiteSpace(track.Title))
            {
                TitleText.Text = "Spotify";
                ArtistText.Text = L.NothingPlaying;
                SetPlayPauseIcon(false);
                ShuffleIcon.Fill = DimWhite;
                ShuffleDot.Visibility = Visibility.Collapsed;
                ShuffleSmartStar.Visibility = Visibility.Collapsed;
                RepeatIcon.Fill = DimWhite;
                RepeatDot.Visibility = Visibility.Collapsed;
                RepeatOneBadge.Visibility = Visibility.Collapsed;
                LikeIcon.Data = AddCircleGeo;
                LikeIcon.Fill = DimWhite;
                _liked = null;
                SetAlbumArt(null);
                _lastTrackKey = "";
                UpdateMarquee();
                return;
            }

            TitleText.Text = track.Title;
            ArtistText.Text = track.Artist;
            var explicitWanted = _media.Explicit ? Visibility.Visible : Visibility.Collapsed;
            if (ExplicitBadge.Visibility != explicitWanted)
                ExplicitBadge.Visibility = explicitWanted;
            UpdateMarquee();
            SetPlayPauseIcon(_isPlayingUi);

            // Real state (favorites + shuffle + repeat) from Spotify's
            // accessibility tree; SMTC is the safety net.
            string key = track.Title + "|" + track.Artist;
            bool keyChanged = key != _lastTrackKey;
            if (_spotifyProc && (keyChanged || _uiaDirty || DateTime.UtcNow - _lastUiaStateAt > TimeSpan.FromSeconds(5)))
            {
                _uiaDirty = false;
                var state = (Liked: (bool?)null, Shuffle: ShuffleMode.Unknown, Repeat: RepeatMode.Unknown, Fresh: false);
                try { state = await Task.Run(() => _uia.GetState(track.Title)).WaitAsync(TimeSpan.FromSeconds(8)); }
                catch (TimeoutException) { }
                _lastStateFresh = state.Fresh;
                // Group still from the previous track (zombie): do not show the old tick
                _uiaState = (state.Fresh ? state.Liked : null, state.Shuffle, state.Repeat);
                _lastUiaStateAt = DateTime.UtcNow;
            }
            if (_spotifyProc && keyChanged)
                _ = SettleStateAsync(); // re-read until Spotify renders the new track's bar
            var (liked, uiaMode, repeatMode) = _uiaState;
            // В браузерном режиме избранное приходит от расширения (у Яндекса
            // это aria-pressed на кнопке "Нравится"), Spotify тут ни при чём
            if (_media.UsingBrowser)
                liked = _media.BrowserLiked;
            // After adding to favorites, ignore a stale "not liked" - Spotify's
            // button text can take several seconds to update
            // Правило про запаздывающий Spotify: вкладка отвечает мгновенно, и тут
            // оно бы на 8 секунд возвращало зелёную галочку после снятия лайка
            if (!_media.UsingBrowser && liked == false &&
                DateTime.UtcNow - _likedOptimisticAt < TimeSpan.FromSeconds(8))
                liked = true;
            _liked = liked;

            ApplyRepeatVisual(repeatMode);

            LikeIcon.Data = liked == true ? CheckCircleGeo : AddCircleGeo;
            LikeIcon.Fill = liked == true ? SpotifyGreen : (liked == false ? Subdued : DimWhite);
            // Honest: with Spotify minimized we cannot confirm the state (null) -
            // say so instead of letting the "+" look like "not liked"
            LikeButton.ToolTip = liked == true ? L.TipLiked
                               : liked == false ? L.TipLikeAdd
                               : L.TipLikeUnknown;

            ShuffleMode mode = uiaMode;
            // The SMTC safety net lags several seconds behind a click - letting
            // it override a fresh UIA read made the icon flicker On->Off->On;
            // grace window like the one on play/pause
            if (DateTime.UtcNow - _shuffleToggledAt > TimeSpan.FromSeconds(4))
            {
                if (track.IsShuffle == false && mode != ShuffleMode.Unknown)
                    mode = ShuffleMode.Off;
                else if (track.IsShuffle == true && mode is ShuffleMode.Off or ShuffleMode.Unknown)
                    mode = ShuffleMode.On;
            }

            ApplyShuffleVisual(mode);

            if (_settings.ShowArt && (keyChanged || _artDirty))
            {
                _lastTrackKey = key;
                _artDirty = false;
                byte[]? bytes = null;
                try { bytes = await _media.GetThumbnailAsync().WaitAsync(TimeSpan.FromSeconds(5)); }
                catch (TimeoutException) { }
                BitmapImage? art = null;
                if (bytes != null)
                {
                    // Truncated/corrupt thumbnails happen on track transitions -
                    // they must not blow up the whole refresh
                    try { art = ToBitmap(bytes); } catch { }
                }
                SetAlbumArt(art);
            }
        }
        finally
        {
            _refreshing = false;
        }
    }

    /// <summary>Swaps the cover with a smooth crossfade: the new one fades in over
    /// the current one (top layer) and then becomes the base - instead of the dry
    /// swap, it gives the "premium" touch that was asked for. With no cover before:
    /// a simple fade from the placeholder; with no new cover: back to it.</summary>
    private void SetAlbumArt(BitmapImage? art)
    {
        const int FadeMs = 250;
        if (art == null)
        {
            ArtImageTop.BeginAnimation(OpacityProperty, null);
            ArtImageTop.Visibility = Visibility.Collapsed;
            ArtImage.Visibility = Visibility.Collapsed;
            ArtBrush.ImageSource = null;
            ArtBrushTop.ImageSource = null;
            ArtPlaceholder.Visibility = Visibility.Visible;
            return;
        }

        bool hadArt = ArtImage.Visibility == Visibility.Visible && ArtBrush.ImageSource != null;
        if (!hadArt)
        {
            // Coming from the placeholder: appear with a short fade
            ArtBrush.ImageSource = art;
            ArtImage.BeginAnimation(OpacityProperty, null);
            ArtImage.Opacity = 1;
            ArtImage.Visibility = Visibility.Visible;
            ArtPlaceholder.Visibility = Visibility.Collapsed;
            ArtImage.BeginAnimation(OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs)));
            return;
        }

        // There was already a cover: real crossfade - the new one on the top
        // layer fading in over the current one
        ArtImage.BeginAnimation(OpacityProperty, null);
        ArtImage.Opacity = 1;
        ArtBrushTop.ImageSource = art;
        ArtImageTop.Visibility = Visibility.Visible;
        var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(FadeMs));
        fade.Completed += (_, _) =>
        {
            // The top one becomes the base; the top hides for the next swap
            ArtBrush.ImageSource = art;
            ArtImageTop.BeginAnimation(OpacityProperty, null);
            ArtImageTop.Opacity = 0;
            ArtImageTop.Visibility = Visibility.Collapsed;
        };
        ArtImageTop.BeginAnimation(OpacityProperty, fade);
    }

    private static BitmapImage ToBitmap(byte[] bytes)
    {
        var bmp = new BitmapImage();
        using (var ms = new MemoryStream(bytes))
        {
            bmp.BeginInit();
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.StreamSource = ms;
            bmp.EndInit();
        }
        bmp.Freeze();
        return bmp;
    }

    // ---------- Controls ----------

    /// <summary>A geometrically centred play triangle looks shifted to the left
    /// (its visual mass sits left) - 1.5px optical compensation.</summary>
    private void SetPlayPauseIcon(bool playing)
    {
        PlayPauseIcon.Data = playing ? PauseGeo : PlayGeo;
        PlayPauseIcon.Margin = playing ? new Thickness(0) : new Thickness(1.5, 0, 0, 0);
    }

    private void ApplyShuffleVisual(ShuffleMode mode)
    {
        ShuffleIcon.Fill = mode switch
        {
            ShuffleMode.On or ShuffleMode.Smart => SpotifyGreen,
            ShuffleMode.Off => Subdued,
            _ => DimWhite,
        };
        ShuffleDot.Visibility = mode is ShuffleMode.On or ShuffleMode.Smart
            ? Visibility.Visible : Visibility.Collapsed;
        ShuffleSmartStar.Visibility = mode == ShuffleMode.Smart ? Visibility.Visible : Visibility.Collapsed;
        ShuffleButton.ToolTip = mode switch
        {
            ShuffleMode.Smart => L.TipShuffleSmart,
            ShuffleMode.On => L.TipShuffleOn,
            _ => L.TipShuffle,
        };
    }

    private void ApplyRepeatVisual(RepeatMode mode)
    {
        RepeatIcon.Fill = mode is RepeatMode.Context or RepeatMode.Track
            ? SpotifyGreen
            : (mode == RepeatMode.Off ? Subdued : DimWhite);
        RepeatDot.Visibility = mode is RepeatMode.Context or RepeatMode.Track
            ? Visibility.Visible : Visibility.Collapsed;
        RepeatOneBadge.Visibility = mode == RepeatMode.Track
            ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>Light refresh fired by the timeline events (frequent).
    /// Off the UI thread and with a timeout: SMTC can hang on dead sessions.</summary>
    private async void RefreshTimeline()
    {
        (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? tlMaybe = null;
        try { tlMaybe = await Task.Run(() => _media.GetTimeline()).WaitAsync(TimeSpan.FromSeconds(3)); }
        catch (TimeoutException) { }
        if (tlMaybe is not { } tl) return;
        // After a seek, snapshots from BEFORE the jump keep arriving for a few
        // seconds - applying them made the bar go back and jump again
        if (DateTime.UtcNow - _seekAt < TimeSpan.FromSeconds(3) && tl.PositionAtUtc < _seekAt)
            return;
        _duration = tl.Duration;
        if (AcceptPlayingState(tl.IsPlaying) && tl.Position <= tl.Duration)
        {
            _basePosition = tl.Position;
            _basePositionAt = tl.PositionAtUtc;
            _isPlayingUi = tl.IsPlaying;
            SetPlayPauseIcon(tl.IsPlaying);
        }
        UpdateProgressUi();
    }

    private async void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        // Immediate feedback; old reads are ignored for 2s (grace) and after
        // that the real SMTC state takes over again
        _isPlayingUi = !_isPlayingUi;
        _playToggledAt = DateTime.UtcNow;
        SetPlayPauseIcon(_isPlayingUi);
        if (!_isPlayingUi)
            _basePosition += DateTime.UtcNow - _basePositionAt; // freeze the position
        _basePositionAt = DateTime.UtcNow;
        await _media.TogglePlayPauseAsync();
    }
    private async void Next_Click(object sender, RoutedEventArgs e) => await _media.NextAsync();
    private async void Prev_Click(object sender, RoutedEventArgs e) => await _media.PreviousAsync();

    private async void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        // Immediate feedback: cycle locally; the state read corrects it if needed
        var next = _uiaState.Shuffle switch
        {
            ShuffleMode.Off => ShuffleMode.On,
            ShuffleMode.On => ShuffleMode.Smart,
            ShuffleMode.Smart => ShuffleMode.Off,
            _ => ShuffleMode.Unknown,
        };
        if (next != ShuffleMode.Unknown)
        {
            _uiaState = (_uiaState.Liked, next, _uiaState.Repeat);
            ApplyShuffleVisual(next);
        }
        _shuffleToggledAt = DateTime.UtcNow;

        bool ok = await Task.Run(() => _uia.CycleShuffle());
        if (!ok)
            await _media.ToggleShuffleAsync(); // no Spotify window: on/off only
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    private async void Repeat_Click(object sender, RoutedEventArgs e)
    {
        // Immediate feedback: cycle locally; the state read corrects it if needed
        var next = _uiaState.Repeat switch
        {
            RepeatMode.Off => RepeatMode.Context,
            RepeatMode.Context => RepeatMode.Track,
            RepeatMode.Track => RepeatMode.Off,
            _ => RepeatMode.Unknown,
        };
        if (next != RepeatMode.Unknown)
        {
            _uiaState = (_uiaState.Liked, _uiaState.Shuffle, next);
            ApplyRepeatVisual(next);
        }

        bool ok = await Task.Run(() => _uia.CycleRepeat());
        if (!ok)
            await _media.CycleRepeatAsync(); // no Spotify window: try through SMTC
        await Task.Delay(400);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    private async void Like_Click(object sender, RoutedEventArgs e)
    {
        // Вкладка умеет и снимать лайк, поэтому здесь это переключатель, а не
        // одностороннее "добавить", как у Spotify
        if (_media.UsingBrowser)
        {
            bool add = _liked != true;
            _liked = add;
            _likedOptimisticAt = DateTime.UtcNow;
            LikeIcon.Data = add ? CheckCircleGeo : AddCircleGeo;
            LikeIcon.Fill = add ? SpotifyGreen : DimWhite;
            LikeButton.ToolTip = add ? L.TipLiked : L.TipLikeAdd;
            _media.ToggleBrowserLike();
            return;
        }

        if (_liked == true) return; // already in favorites

        // Immediate feedback; if it fails, the next state read corrects it
        _liked = true;
        _likedOptimisticAt = DateTime.UtcNow;
        LikeIcon.Data = CheckCircleGeo;
        LikeIcon.Fill = SpotifyGreen;
        LikeButton.ToolTip = L.TipLiked;

        bool ok = await Task.Run(() => _uia.AddToFavorites());
        if (!ok)
            await Task.Run(() => _uia.AddToFavoritesByClick()); // rare fallback: no verb available

        // Spotify's button text takes seconds to reflect the addition -
        // reconcile later instead of concluding right away that it failed
        _ = ReconcileLikeLaterAsync();
    }

    private async Task ReconcileLikeLaterAsync()
    {
        await Task.Delay(4000);
        _uiaDirty = true;
        await RefreshTrackAsync();
    }

    /// <summary>Quick re-check during the transient gap of track changes.</summary>
    private async Task QuickRecheckAsync()
    {
        await Task.Delay(350);
        await RefreshTrackAsync();
    }

    private int _settleToken;
    private bool _lastStateFresh = true;

    /// <summary>After a track change, re-read every 500ms until Spotify's title
    /// group belongs to the new track (validated against the SMTC title).
    /// A new track change cancels the previous series.</summary>
    private async Task SettleStateAsync()
    {
        int token = ++_settleToken;
        for (int i = 0; i < 12; i++) // max ~6s
        {
            await Task.Delay(500);
            if (token != _settleToken) return;
            _uiaDirty = true;
            await RefreshTrackAsync();
            if (_lastStateFresh) return;
        }
    }

    private async void Volume_Click(object sender, RoutedEventArgs e)
    {
        if (VolumePopup.IsOpen)
        {
            VolumePopup.IsOpen = false;
            return;
        }
        if (_volLoading)
            return; // an open is already under way - another one would overwrite
                    // the user's adjustment with the old volume on completion

        CaptureForeground();
        _volLoading = true;
        try
        {
            // The CoreAudio fallback also off the UI thread - it is an RPC to the
            // audio service and it did block the interface at times
            double? current = await Task.Run(() => _uia.GetVolume() ?? SpotifyVolume.GetVolume());
            if (_closed || Visibility != Visibility.Visible)
                return; // the widget hid during the read: do not open an orphan
                        // popup floating over the bar
            // Assign WITH _volLoading still set: otherwise ValueChanged echoed the
            // read back to Spotify on every open - and a failed read (null -> 100%)
            // blew up the volume just by opening the popup
            if (current is double v)
                VolumeSlider.Value = v * 100;
        }
        finally
        {
            _volLoading = false;
        }
        VolumePopup.IsOpen = true;
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_volLoading) return;
        ApplyVolume(e.NewValue / 100.0);
    }

    private double? _pendingVolume;
    private bool _volApplying;

    /// <summary>
    /// Applies the volume on Spotify's own slider (its UI follows along),
    /// with the Windows mixer as the fallback. Serializes the requests so
    /// dragging the slider does not pile up calls.
    /// </summary>
    private async void ApplyVolume(double fraction)
    {
        _pendingVolume = fraction;
        if (_volApplying) return;
        _volApplying = true;
        try
        {
            while (_pendingVolume is double v)
            {
                _pendingVolume = null;
                await Task.Run(() =>
                {
                    if (!_uia.SetVolume(v))
                        SpotifyVolume.SetVolume((float)v);
                });
            }
        }
        finally
        {
            _volApplying = false;
        }
    }

    private void VolumePopup_MouseLeave(object sender, MouseEventArgs e) => VolumePopup.IsOpen = false;

    private int _wheelAccum;

    /// <summary>Mouse wheel over the volume button/popup: closed it opens (with
    /// the current volume loaded), open it adjusts by 5 - like Spotify itself.</summary>
    private void Volume_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (!VolumePopup.IsOpen)
        {
            Volume_Click(sender, null!); // the _volLoading guard prevents re-entry
            return;
        }
        if (_volLoading) return;
        // Accumulate the delta instead of 5 per EVENT: precision touchpads send
        // dozens of small events per gesture (volume went from 50 to 0 in one
        // touch) and fast wheels pack several notches into a single event.
        // Reversing direction discards the accumulated remainder - otherwise the
        // first notch the other way was "swallowed" cancelling the residue
        if (_wheelAccum != 0 && Math.Sign(_wheelAccum) != Math.Sign(e.Delta))
            _wheelAccum = 0;
        _wheelAccum += e.Delta;
        int steps = _wheelAccum / 120;
        if (steps == 0) return;
        _wheelAccum -= steps * 120;
        VolumeSlider.Value = Math.Clamp(VolumeSlider.Value + 5 * steps, 0, 100);
    }

    // ---------- Theme (light/dark bar) ----------

    private static bool IsSystemLightTheme()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("SystemUsesLightTheme") is int v && v == 1;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>The taskbar follows the SYSTEM theme (not the app one); on a
    /// light bar the text/icons have to darken.</summary>
    private void ApplyThemeIfChanged()
    {
        bool light = IsSystemLightTheme();
        if (_lightTheme == light) return;
        _lightTheme = light;

        Subdued = new SolidColorBrush(light ? Color.FromRgb(0x48, 0x48, 0x48) : Color.FromRgb(0xB3, 0xB3, 0xB3));
        DimWhite = new SolidColorBrush(light ? Color.FromArgb(0x66, 0x00, 0x00, 0x00) : Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
        _progressFillNormal = light ? Brushes.Black : Brushes.White;

        TitleText.Foreground = light ? Brushes.Black : Brushes.White;
        ArtistText.Foreground = Subdued;
        LauncherText.Foreground = Subdued;
        PrevIcon.Fill = Subdued;
        NextIcon.Fill = Subdued;
        VolumeIcon.Fill = Subdued;
        ArtPlaceholder.Foreground = DimWhite;
        ProgressTrack.Background = new SolidColorBrush(light ? Color.FromArgb(0x2E, 0x00, 0x00, 0x00) : Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));
        if (!ProgressTrack.IsMouseOver)
            ProgressFill.Background = _progressFillNormal;

        // Play button: white circle on dark, black on light (like Spotify)
        PlayPauseButton.Background = light ? Brushes.Black : Brushes.White;
        PlayPauseIcon.Fill = light ? Brushes.White : Brushes.Black;

        _marqueeKey = ""; // no effect on the text, but forces a coherent refresh
        _ = RefreshTrackAsync();
    }

    // ---------- Title marquee ----------

    private string _marqueeKey = "";

    /// <summary>Title wider than the text column -> continuous scroll with pauses,
    /// like Spotify; otherwise it stays static.</summary>
    private void UpdateMarquee()
    {
        // Значок ненормативной лексики стоит в той же строке и отъедает место -
        // без этого длинное название считало бы, что помещается
        double badge = ExplicitBadge.Visibility == Visibility.Visible
            ? ExplicitBadge.Width + ExplicitBadge.Margin.Left
            : 0;
        // TextStack.Width на первом проходе ещё NaN: без подстраховки колонка
        // названия получала NaN, а Canvas с такой шириной в колонке Auto
        // схлопывается в ноль - текст пропадал целиком
        double column = double.IsNaN(TextStack.Width) ? MaxTextWidth : TextStack.Width;
        double clipWidth = Math.Max(0, column - badge);
        // DPI goes into the key: the rendered width changes with the monitor
        // scale and the scroll decision went stale when moving screens
        string key = $"{TitleText.Text}|{clipWidth:0}|{badge:0}|{VisualTreeHelper.GetDpi(this).PixelsPerDip:0.##}|{TitleText.FontFamily.Source}";
        if (key == _marqueeKey) return;
        _marqueeKey = key;

        // Measure the width that is REALLY rendered. The window uses
        // TextFormattingMode=Display (pixel-snapped advances) and FormattedText
        // measures in Ideal mode - the difference varies with screen scale and
        // font, and on some machines it crossed the tolerance: titles that fit
        // scrolled anyway (community report).
        // Measuring the TextBlock itself unconstrained gives the exact value of
        // what gets drawn (it sits in a Canvas, the layout no longer constrains it).
        TitleText.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double textWidth = Math.Ceiling(TitleText.DesiredSize.Width);

        TitleShift.BeginAnimation(TranslateTransform.XProperty, null);
        TitleShift.X = 0;

        // Колонка названия ужимается до текста - тогда значок стоит вплотную
        // за ним; шире доступного места не растём, иначе поедут кнопки.
        // Ноль недопустим: это снова спрятало бы текст
        double titleWidth = Math.Min(textWidth, clipWidth);
        if (!(titleWidth > 0)) titleWidth = clipWidth;
        TitleClip.Width = titleWidth;

        double overflow = textWidth - clipWidth;
        if (overflow > 4)
        {
            double scrollSeconds = Math.Max(1.5, overflow / 25.0);
            double end = -(overflow + 12);
            var anim = new DoubleAnimationUsingKeyFrames();
            double t = 2.5; // initial pause (read the start)
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            t += scrollSeconds;
            anim.KeyFrames.Add(new LinearDoubleKeyFrame(end, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            t += 1.5; // pause at the end (read the rest)
            anim.KeyFrames.Add(new DiscreteDoubleKeyFrame(end, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
            if (_settings.ScrollTitleOnce)
            {
                // Once: return to the start and stay static (#14)
                t += 0.6;
                anim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(t))));
                // no RepeatBehavior -> runs once and HoldEnd pins it at X=0
            }
            else
            {
                anim.RepeatBehavior = RepeatBehavior.Forever; // continuous (default)
            }
            anim.Duration = TimeSpan.FromSeconds(t);
            TitleShift.BeginAnimation(TranslateTransform.XProperty, anim);
        }
        // No overflow: text parked at X=0 (the Canvas never truncates the rendering)
    }

    // ---------- Progress bar ----------

    /// <summary>Spotify only publishes the position now and then; between reads
    /// the position is interpolated with the local clock while playing.</summary>
    private void UpdateProgressUi()
    {
        bool show = _settings.ShowProgress && _spotifyPresent && _duration > TimeSpan.Zero;
        var wanted = show ? Visibility.Visible : Visibility.Collapsed;
        if (ProgressTrack.Visibility != wanted)
            ProgressTrack.Visibility = wanted;
        if (!show) return;

        TimeSpan pos = _basePosition;
        if (_isPlayingUi)
            pos += DateTime.UtcNow - _basePositionAt;

        double fraction = Math.Clamp(pos.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);

        if (!_isPlayingUi)
        {
            _progressTimer?.Stop();
            ProgressScale.ScaleX = fraction;
            return;
        }

        ProgressScale.ScaleX = fraction;

        // Ход полосы рисуется отдельным таймером: непрерывная анимация WPF
        // держала бы перерисовку окна на 60 кадрах в секунду, а окно прозрачное
        // и рисуется процессором - это стоило почти четверти ядра
        if (_progressTimer == null)
        {
            _progressTimer = new DispatcherTimer(DispatcherPriority.Render)
            {
                Interval = TimeSpan.FromMilliseconds(ProgressStepMs),
            };
            _progressTimer.Tick += (_, _) => AdvanceProgress();
        }
        if (!_progressTimer.IsEnabled) _progressTimer.Start();
    }

    // Шаг обновления полосы. Дальше уменьшать смысла нет: при движении
    // масштабом сдвиг за такт и так доля пикселя, а каждая перерисовка этого
    // окна стоит дорого - оно прозрачное и рисуется процессором
    private const double ProgressStepMs = 200;
    private DispatcherTimer? _progressTimer;

    /// <summary>Двигает заполнение между опросами источника. Масштаб, а не
    /// ширина: ширина прошла бы через раскладку и округлилась до целых
    /// пикселей, из-за чего полоса шла ступеньками.</summary>
    private void AdvanceProgress()
    {
        if (_closed || _duration <= TimeSpan.Zero || !_isPlayingUi ||
            ProgressTrack.Visibility != Visibility.Visible)
        {
            _progressTimer?.Stop();
            return;
        }
        TimeSpan pos = _basePosition + (DateTime.UtcNow - _basePositionAt);
        ProgressScale.ScaleX = Math.Clamp(pos.TotalMilliseconds / _duration.TotalMilliseconds, 0, 1);
    }


    private async void Progress_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode) return; // in move mode the drag takes priority
        e.Handled = true;      // do not treat it as a click to open Spotify

        if (_duration <= TimeSpan.Zero || ProgressTrack.ActualWidth <= 0) return;
        double fraction = Math.Clamp(e.GetPosition(ProgressTrack).X / ProgressTrack.ActualWidth, 0, 1);
        var target = TimeSpan.FromTicks((long)(_duration.Ticks * fraction));

        // Optimistic update so the bar reacts immediately; the grace window stops
        // pre-jump snapshots from dragging it back over the next few seconds
        _seekAt = DateTime.UtcNow;
        _basePosition = target;
        _basePositionAt = DateTime.UtcNow;
        UpdateProgressUi();

        await _media.SeekAsync(target);
    }

    private void Progress_MouseEnter(object sender, MouseEventArgs e) => ProgressFill.Background = SpotifyGreen;
    private void Progress_MouseLeave(object sender, MouseEventArgs e) => ProgressFill.Background = _progressFillNormal;

    private void Progress_MenuClick(object sender, RoutedEventArgs e)
    {
        _settings.ShowProgress = ProgressMenu.IsChecked;
        _settings.Save();
        UpdateProgressUi();
    }

    private void ScrollOnce_Click(object sender, RoutedEventArgs e)
    {
        _settings.ScrollTitleOnce = ScrollOnceMenu.IsChecked;
        _settings.Save();
        _marqueeKey = ""; // force the animation to be recomputed with the new mode
        UpdateMarquee();
    }

    private void VolumePopup_Closed(object sender, EventArgs e)
    {
        _volPopupWatchdog?.Stop();
        RestoreForeground();
    }

    private DispatcherTimer? _volPopupWatchdog;

    // ---------- Focus preservation ----------
    // The volume popup and the context menu activate WPF windows of their own;
    // when they close, focus can end up orphaned and global shortcuts
    // (PrintScreen, Win+Shift+S) stop responding until another window is clicked.

    private IntPtr _fgBeforeUi;

    private void Root_ContextMenuOpening(object sender, ContextMenuEventArgs e)
    {
        CaptureForeground();
        RebuildMonitorMenu();
        // Global state - another window (or Task Manager) may have changed it
        if (!PackagedApp.IsPackaged)
            AutoStartMenu.IsChecked = IsAutoStartEnabled();
    }

    /// <summary>One item PER EXISTING taskbar, with multiple selection - every
    /// ticked monitor gets its own widget instance (community request).
    /// At least one always stays ticked.</summary>
    private void RebuildMonitorMenu()
    {
        MonitorMenu.Items.Clear();
        int count = Interop.GetSecondaryTrays().Count;
        var monitors = _settings.Monitors;
        for (int i = 0; i <= count; i++)
        {
            int index = i;
            var item = new MenuItem
            {
                Header = i == 0 ? L.MonitorPrimary : L.MonitorN(i + 1),
                IsCheckable = true,
                IsChecked = monitors.Contains(i),
                StaysOpenOnClick = true, // tick several without the menu closing
            };
            item.Click += (s, _) =>
            {
                if (monitors.Contains(index))
                {
                    // At least one monitor THAT EXISTS has to remain - entries
                    // for disconnected monitors do not count, otherwise the
                    // selection could end up all non-existent bars and the whole
                    if (!monitors.Any(m => m != index && m <= count))
                    {
                        ((MenuItem)s).IsChecked = true;
                        return;
                    }
                    monitors.Remove(index);
                }
                else
                {
                    monitors.Add(index);
                    monitors.Sort();
                }
                _settings.Save();
                // If this very window is about to be removed, close the menu first -
                // an orphan StaysOpen menu on a destroyed window hangs around
                if (!monitors.Contains(TrayIndex) && Root.ContextMenu is { IsOpen: true } cm)
                    cm.IsOpen = false;
                SyncToMonitors();
            };
            MonitorMenu.Items.Add(item);
        }
        if (count == 0)
        {
            // Without secondary bars Windows has nowhere to anchor the widget on
            // another screen - explain how to enable it instead of hiding the menu
            // (users thought the feature did not exist, issue #11)
            MonitorMenu.Items.Add(new MenuItem
            {
                Header = L.MonitorHint,
                IsEnabled = false,
            });
        }
    }

    private void ContextMenu_Closed(object sender, RoutedEventArgs e) => RestoreForeground();

    private void CaptureForeground()
    {
        IntPtr fg = Interop.GetForegroundWindow();
        _fgBeforeUi = fg == _hwnd ? IntPtr.Zero : fg;
    }

    private void RestoreForeground()
    {
        if (_fgBeforeUi == IntPtr.Zero) return;
        IntPtr target = _fgBeforeUi;
        _fgBeforeUi = IntPtr.Zero;
        if (Interop.GetForegroundWindow() != target)
            Interop.SetForegroundWindow(target);
    }

    // ---------- Move (only when enabled in the menu) / click to open Spotify ----------

    private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_moveMode)
        {
            _dragging = true;
            _dragMoved = false;
            _dragStartLeft = Left;
            _dragStartScreen = PointToScreen(e.GetPosition(this));
            Root.CaptureMouse();
        }
        else
        {
            _pressed = true;
        }
    }

    private void Root_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;

        Point cur = PointToScreen(e.GetPosition(this));
        double dxDevice = cur.X - _dragStartScreen.X;

        var source = PresentationSource.FromVisual(this);
        if (source?.CompositionTarget == null) return;
        double dx = source.CompositionTarget.TransformFromDevice.Transform(new Vector(dxDevice, 0)).X;

        if (Math.Abs(dx) > 3) _dragMoved = true;
        if (_dragMoved)
            Left = _dragStartLeft + dx;
    }

    private void Root_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (_dragging)
        {
            _dragging = false;
            Root.ReleaseMouseCapture();
            if (_dragMoved)
            {
                // Locked at this position (manual mode) for THIS bar only;
                // physical px, indexed by this window's monitor
                if (Interop.GetWindowRect(_hwnd, out var w))
                {
                    _settings.ManualX[TrayIndex] = w.Left;
                    // ...and the distance to the tray, which is what drives the
                    // position from now on (see ManualGap). With no readable
                    // notification area, forget the old one so a gap measured on
                    // another bar is not applied here.
                    IntPtr trayNow = GetTargetTray();
                    int? notifyLeft = trayNow != IntPtr.Zero
                        ? Interop.GetTrayNotifyLeft(trayNow)
                        : null;
                    if (notifyLeft.HasValue)
                        _settings.ManualGap[TrayIndex] = notifyLeft.Value - w.Right;
                    else
                        _settings.ManualGap.Remove(TrayIndex);
                }
                _settings.Save();
            }
        }
        else if (_pressed)
        {
            _pressed = false;
            // Клик показывает то, откуда сейчас звук: вкладку браузера или
            // окно приложения из системной сессии. Spotify остаётся запасным
            // вариантом - в том числе когда не играет ничего
            if (!_media.FocusCurrentSource())
                SpotifyActions.OpenSpotifyWindow();
        }
    }

    // ---------- Context menu ----------

    private void MoveMode_Click(object sender, RoutedEventArgs e)
    {
        _moveMode = MoveMenu.IsChecked;
        Root.Cursor = _moveMode ? Cursors.SizeAll : Cursors.Hand;
    }

    private void ResetPos_Click(object sender, RoutedEventArgs e)
    {
        // Restores the automatic POSITIONS on every bar; the monitor selection
        // stays as it is (clearing it destroyed the user's choice)
        _settings.AutoPosition = true;
        _settings.ManualX.Clear();
        _settings.ManualGap.Clear();
        _settings.Save();
        MoveMenu.IsChecked = false;
        _moveMode = false;
        Root.Cursor = Cursors.Hand;
        UpdatePosition();
    }

    private void Size_Click(object sender, RoutedEventArgs e)
    {
        var item = (MenuItem)sender;
        _settings.Scale = double.Parse((string)item.Tag, CultureInfo.InvariantCulture);
        // Save propagates to ALL windows through Changed (ApplySettingsUi +
        // repositioning deferred until after layout) - nothing to do here
        _settings.Save();
    }

    private void ApplyScale() => Root.LayoutTransform = new ScaleTransform(_settings.Scale, _settings.Scale);

    /// <summary>Font for title/artist. We keep whatever came from the XAML on
    /// the first apply - that is where "system default" goes back to.</summary>
    private FontFamily? _xamlFont;

    /// <summary>Cover on/off. Collapsing the whole container is enough: the
    /// crossfade layers and the placeholder all live inside it.</summary>
    private void ApplyArt() =>
        ArtPanel.Visibility = _settings.ShowArt ? Visibility.Visible : Visibility.Collapsed;

    private void ApplyFont()
    {
        _xamlFont ??= TitleText.FontFamily;
        string name = _settings.FontFamily;
        FontFamily font = _xamlFont;
        if (!string.IsNullOrWhiteSpace(name))
        {
            // A font uninstalled in the meantime must not break the widget: WPF
            // only throws when rendering, so we validate here
            try { font = new FontFamily(name); }
            catch { font = _xamlFont; }
        }
        TitleText.FontFamily = font;
        ArtistText.FontFamily = font;
        LauncherText.FontFamily = font;
        // The text width changes with the font: re-decide the marquee
        UpdateMarquee();
    }

    /// <summary>Short list of fonts, filtered to the ones that really exist on
    /// this machine - a menu with the system's ~400 was unusable on the bar.</summary>
    private static readonly string[] FontChoices =
    {
        "Segoe UI", "Segoe UI Variable", "Arial", "Calibri", "Cascadia Mono",
        "Consolas", "Georgia", "Tahoma", "Times New Roman", "Verdana",
    };

    private void BuildFontMenu()
    {
        FontMenuItem.Items.Clear();

        var installed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var f in Fonts.SystemFontFamilies)
                installed.Add(f.Source);
        }
        catch { }

        var systemItem = new MenuItem
        {
            Header = L.FontSystem,
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(_settings.FontFamily),
            Tag = "",
        };
        systemItem.Click += Font_Click;
        FontMenuItem.Items.Add(systemItem);
        FontMenuItem.Items.Add(new Separator());

        foreach (string name in FontChoices)
        {
            if (installed.Count > 0 && !installed.Contains(name)) continue;
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(_settings.FontFamily, name, StringComparison.OrdinalIgnoreCase),
                Tag = name,
                // Preview inside the menu itself: choosing blind by name meant
                // closing and reopening the menu on every attempt
                FontFamily = new FontFamily(name),
            };
            item.Click += Font_Click;
            FontMenuItem.Items.Add(item);
        }

        // Free text entry: the curated list does not cover the hundreds of fonts
        // installed. A TextBox INSIDE the menu does not work - the ContextMenu
        // keeps keyboard focus and swallows the keys (you clicked, typed, and
        // nothing appeared) - so this opens a window of its own.
        FontMenuItem.Items.Add(new Separator());

        bool isCustom = !string.IsNullOrWhiteSpace(_settings.FontFamily) &&
                        !FontChoices.Contains(_settings.FontFamily, StringComparer.OrdinalIgnoreCase);
        var customItem = new MenuItem
        {
            Header = isCustom ? L.FontCustomCurrent(_settings.FontFamily) : L.FontCustom,
            IsCheckable = true,
            IsChecked = isCustom,
        };
        customItem.Click += (_, _) =>
        {
            // The tick only changes with the dialog result, not with the click
            customItem.IsChecked = isCustom;
            if (Root.ContextMenu is { } menu) menu.IsOpen = false;
            string? picked = PromptForFontName(_settings.FontFamily, installed);
            if (picked == null) return; // cancelled
            _settings.FontFamily = picked;
            _settings.Save(); // propagates to every window and rebuilds the menu
        };
        FontMenuItem.Items.Add(customItem);
    }

    /// <summary>Small dialog for typing the name of any installed font.
    /// Returns null if the user cancels, "" to go back to the system font.</summary>
    /// do sistema.</summary>
    private string? PromptForFontName(string current, HashSet<string> installed)
    {
        var label = new TextBlock
        {
            Text = L.FontCustomLabel,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        };
        var box = new TextBox { Text = current, FontSize = 13, Padding = new Thickness(4, 3, 4, 3) };
        var hint = new TextBlock
        {
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 6, 0, 0),
            Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99)),
            Text = L.FontCustomClear,
        };
        // Preview: the box renders in the typed font, so the result is visible
        // before confirming
        void Validate()
        {
            string n = box.Text.Trim();
            if (n.Length == 0)
            {
                box.FontFamily = new TextBox().FontFamily;
                hint.Text = L.FontCustomClear;
                hint.Foreground = new SolidColorBrush(Color.FromRgb(0x99, 0x99, 0x99));
                return;
            }
            bool unknown = installed.Count > 0 && !installed.Contains(n);
            try { box.FontFamily = new FontFamily(n); } catch { }
            hint.Text = unknown ? L.FontNotInstalled : L.FontCustomClear;
            hint.Foreground = new SolidColorBrush(unknown
                ? Color.FromRgb(0xC8, 0x7A, 0x10)
                : Color.FromRgb(0x99, 0x99, 0x99));
        }
        box.TextChanged += (_, _) => Validate();

        var ok = new Button { Content = "OK", Width = 84, Height = 26, IsDefault = true, Margin = new Thickness(0, 0, 8, 0) };
        var cancel = new Button { Content = L.Cancel, Width = 84, Height = 26, IsCancel = true };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 14, 0, 0),
        };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);

        var panel = new StackPanel { Margin = new Thickness(14) };
        panel.Children.Add(label);
        panel.Children.Add(box);
        panel.Children.Add(hint);
        panel.Children.Add(buttons);

        var win = new Window
        {
            Title = L.FontCustomTitle,
            Content = panel,
            Width = 340,
            SizeToContent = SizeToContent.Height,
            WindowStyle = WindowStyle.ToolWindow,
            ResizeMode = ResizeMode.NoResize,
            WindowStartupLocation = WindowStartupLocation.CenterScreen,
            ShowInTaskbar = false,
            // The widget is topmost; without this the dialog could open behind it
            Topmost = true,
        };
        ok.Click += (_, _) => win.DialogResult = true;
        win.Loaded += (_, _) => { box.Focus(); box.SelectAll(); };
        Validate();

        return win.ShowDialog() == true ? box.Text.Trim() : null;
    }

    /// <summary>Languages offered. The names stay ALWAYS in their own language -
    /// translating them hides the option from whoever only reads that one.</summary>
    private static readonly (string Code, string Name)[] LanguageChoices =
    {
        ("en", "English"),
        ("pt", "Português"),
        ("ru", "Русский"),
        ("uk", "Українська"),
    };

    private void BuildLanguageMenu()
    {
        LanguageMenuItem.Items.Clear();

        var auto = new MenuItem
        {
            Header = L.LanguageAuto,
            IsCheckable = true,
            IsChecked = string.IsNullOrWhiteSpace(_settings.Language),
            Tag = "",
        };
        auto.Click += Language_Click;
        LanguageMenuItem.Items.Add(auto);
        LanguageMenuItem.Items.Add(new Separator());

        foreach (var (code, name) in LanguageChoices)
        {
            var item = new MenuItem
            {
                Header = name,
                IsCheckable = true,
                IsChecked = string.Equals(_settings.Language, code, StringComparison.OrdinalIgnoreCase),
                Tag = code,
            };
            item.Click += Language_Click;
            LanguageMenuItem.Items.Add(item);
        }
    }

    private void Language_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        _settings.Language = (string)(item.Tag ?? "");
        // Save propagates to every window: each one re-applies the strings
        _settings.Save();
    }

    private void Font_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item) return;
        _settings.FontFamily = (string)(item.Tag ?? "");
        // Save propagates to every window through Changed (ApplySettingsUi)
        _settings.Save();
    }

    private void UpdateSizeChecks()
    {
        SizeSmall.IsChecked = Math.Abs(_settings.Scale - 0.8) < 0.01;
        SizeNormal.IsChecked = Math.Abs(_settings.Scale - 1.0) < 0.01;
        SizeLarge.IsChecked = _settings.Scale > 1.05;
    }

    /// <summary>Widget brightness/opacity - requested by OLED users who dim the
    /// bar: a widget at full brightness clashes and marks the panel.</summary>
    private bool _opacityLoading;

    /// <summary>Brightness slider 20-100% (OLED community request). Previews live
    /// while dragging; saves once at the end, so as not to flood the disk nor the
    /// Changed event with a Save per drag step.</summary>
    private void OpacitySlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        // While the XAML loads, setting Minimum=20 coerces Value from 0 to 20 and
        // fires this event BEFORE Root/OpacityValueText exist - without this guard
        // it was an NRE and the window never built (the widget vanished)
        if (_opacityLoading || !_uiReady) return;
        _settings.Opacity = Math.Clamp(e.NewValue / 100.0, 0.2, 1.0);
        ApplyOpacity();
        OpacityValueText.Text = $"{Math.Round(e.NewValue)}%";
        // Defer the Save until the slider settles (no events for ~400ms)
        _opacitySaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _opacitySaveTimer.Stop();
        _opacitySaveTimer.Tick -= OpacitySaveTick;
        _opacitySaveTimer.Tick += OpacitySaveTick;
        _opacitySaveTimer.Start();
    }

    private DispatcherTimer? _opacitySaveTimer;

    private void OpacitySaveTick(object? sender, EventArgs e)
    {
        _opacitySaveTimer?.Stop();
        _settings.Save(); // propagates to every window through Changed
    }

    private int _opacityWheelAccum;

    /// <summary>Mouse wheel over the brightness slider: 5% per notch, same as
    /// volume. Accumulates the delta (precision touchpads send many small events)
    /// and discards the remainder when the direction reverses.</summary>
    private void Opacity_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_opacityWheelAccum != 0 && Math.Sign(_opacityWheelAccum) != Math.Sign(e.Delta))
            _opacityWheelAccum = 0;
        _opacityWheelAccum += e.Delta;
        int steps = _opacityWheelAccum / 120;
        if (steps == 0) return;
        _opacityWheelAccum -= steps * 120;
        // Touching Value fires ValueChanged, which applies it and schedules the Save
        OpacitySlider.Value = Math.Clamp(OpacitySlider.Value + 5 * steps, 20, 100);
    }

    private void ApplyOpacity() => Root.Opacity = _settings.Opacity;

    private bool _textPadLoading;
    private DispatcherTimer? _textPadSaveTimer;

    /// <summary>Gap between the text and the buttons (fit-to-text mode).
    /// Previews while dragging and saves only when the slider settles, like
    /// brightness - a Save per step flooded the disk and the Changed event.</summary>
    private void TextPadSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_textPadLoading || !_uiReady) return;
        _settings.TextPadding = Math.Clamp(e.NewValue, 0, 40);
        TextPadValueText.Text = $"{Math.Round(e.NewValue)} px";
        // The text column changes width: redo the layout now
        _marqueeKey = "";
        UpdatePosition();
        _textPadSaveTimer ??= new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _textPadSaveTimer.Stop();
        _textPadSaveTimer.Tick -= TextPadSaveTick;
        _textPadSaveTimer.Tick += TextPadSaveTick;
        _textPadSaveTimer.Start();
    }

    private void TextPadSaveTick(object? sender, EventArgs e)
    {
        _textPadSaveTimer?.Stop();
        _settings.Save();
    }

    private int _textPadWheelAccum;

    private void TextPad_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        e.Handled = true;
        if (_textPadWheelAccum != 0 && Math.Sign(_textPadWheelAccum) != Math.Sign(e.Delta))
            _textPadWheelAccum = 0;
        _textPadWheelAccum += e.Delta;
        int steps = _textPadWheelAccum / 120;
        if (steps == 0) return;
        _textPadWheelAccum -= steps * 120;
        TextPadSlider.Value = Math.Clamp(TextPadSlider.Value + 2 * steps, 0, 40);
    }

    private void UpdateTextPadChecks()
    {
        _textPadLoading = true;
        TextPadSlider.Value = Math.Round(_settings.TextPadding);
        TextPadValueText.Text = $"{Math.Round(_settings.TextPadding)} px";
        _textPadLoading = false;
        // Without fit-to-text the gap does nothing - say so instead of offering
        // a slider that moves nothing
        TextPadMenuItem.IsEnabled = _settings.AutoSizeText;
    }

    private void UpdateOpacityChecks()
    {
        // Reflect the stored value on the slider without re-firing the Save
        _opacityLoading = true;
        OpacitySlider.Value = Math.Round(_settings.Opacity * 100);
        OpacityValueText.Text = $"{Math.Round(_settings.Opacity * 100)}%";
        _opacityLoading = false;
    }

    private void Art_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowArt = ArtMenu.IsChecked;
        _settings.Save();
        // Turning it back on: the current track's cover has to be requested again
        _artDirty = true;
        _ = RefreshTrackAsync();
        UpdatePosition();
    }

    private void AutoSize_Click(object sender, RoutedEventArgs e)
    {
        _settings.AutoSizeText = AutoSizeMenu.IsChecked;
        _settings.Save();
        // The column changes width now, without waiting for the next track
        _marqueeKey = "";
        UpdatePosition();
    }

    private void Launcher_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowLauncher = LauncherMenu.IsChecked;
        _settings.Save();
        _ = RefreshTrackAsync();
        UpdatePosition();
    }

    private void Buttons_Click(object sender, RoutedEventArgs e)
    {
        _settings.ShowPlay = BtnPlayMenu.IsChecked;
        _settings.ShowLike = BtnLikeMenu.IsChecked;
        _settings.ShowShuffle = BtnShuffleMenu.IsChecked;
        _settings.ShowPrev = BtnPrevMenu.IsChecked;
        _settings.ShowNext = BtnNextMenu.IsChecked;
        _settings.ShowRepeat = BtnRepeatMenu.IsChecked;
        _settings.ShowVolume = BtnVolumeMenu.IsChecked;
        _settings.Save();
        UpdatePosition();
    }

    private const string StartupTaskId = "SpotifyTaskbarWidgetStartup";

    private async Task InitStartupTaskStateAsync()
    {
        try
        {
            var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
            AutoStartMenu.IsChecked = task.State is Windows.ApplicationModel.StartupTaskState.Enabled
                or Windows.ApplicationModel.StartupTaskState.EnabledByPolicy;
        }
        catch { }
    }

    private async void AutoStart_Click(object sender, RoutedEventArgs e)
    {
        if (PackagedApp.IsPackaged)
        {
            try
            {
                var task = await Windows.ApplicationModel.StartupTask.GetAsync(StartupTaskId);
                if (AutoStartMenu.IsChecked)
                {
                    var state = await task.RequestEnableAsync();
                    AutoStartMenu.IsChecked = state == Windows.ApplicationModel.StartupTaskState.Enabled;
                }
                else
                {
                    task.Disable();
                }
            }
            catch { }
            return;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath);
            if (AutoStartMenu.IsChecked)
                key.SetValue(RunValueName, $"\"{Environment.ProcessPath}\"");
            else
                key.DeleteValue(RunValueName, false);
        }
        catch { }
    }

    private static bool IsAutoStartEnabled()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
            return key?.GetValue(RunValueName) != null;
        }
        catch
        {
            return false;
        }
    }

    private void OpenSpotify_Click(object sender, RoutedEventArgs e) => SpotifyActions.OpenSpotifyWindow();

    private void Donate_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo(DonateUrl) { UseShellExecute = true });
        }
        catch { }
    }

    private async void Update_Click(object sender, RoutedEventArgs e)
    {
        if (!UpdateService.IsConfigured)
        {
            MessageBox.Show(L.UpdateNotConfigured(UpdateService.CurrentVersion), L.AppTitle);
            return;
        }

        UpdateMenu.IsEnabled = false;
        try
        {
            // If the silent check already found a new version, use that result
            // (go straight to the prompt) instead of checking again
            var update = _pendingUpdate ?? await UpdateService.CheckAsync();
            if (update == null)
            {
                MessageBox.Show(L.UpdateLatest(UpdateService.CurrentVersion), L.AppTitle);
                return;
            }

            var answer = MessageBox.Show(
                L.UpdatePrompt(update.Value.Version, UpdateService.CurrentVersion),
                L.AppTitle, MessageBoxButton.YesNo);
            if (answer == MessageBoxResult.Yes)
                await UpdateService.DownloadAndApplyAsync(update.Value.Url);
        }
        catch (Exception ex)
        {
            MessageBox.Show(L.UpdateError(ex.Message), L.AppTitle);
        }
        finally
        {
            UpdateMenu.IsEnabled = true;
        }
    }

    /// <summary>Silent check: at startup and then every 6h (a widget stays open
    /// for days and would never notice otherwise). If there is a new version, it
    /// highlights the menu item in ALL windows.</summary>
    private async Task CheckUpdatesQuietlyAsync()
    {
        await Task.Delay(TimeSpan.FromSeconds(20));
        while (!_closed)
        {
            try
            {
                var update = await UpdateService.CheckAsync();
                if (update != null)
                {
                    _pendingUpdate = update;
                    await Dispatcher.InvokeAsync(RefreshAllUpdateMenus);
                }
            }
            catch { }
            await Task.Delay(TimeSpan.FromHours(6));
        }
    }

    private static void RefreshAllUpdateMenus()
    {
        foreach (var w in Instances)
            w.RefreshUpdateMenu();
    }

    /// <summary>Applies the "new version" highlight to the menu item (green + bold
    /// + a dot) when an update is pending. Called when the window loads and when
    /// the check finds a new version.</summary>
    private void RefreshUpdateMenu()
    {
        if (PackagedApp.IsPackaged) return; // the Store handles updates
        if (_pendingUpdate is { } u)
        {
            UpdateMenu.Header = L.UpdateAvailable(u.Version);
            UpdateMenu.Foreground = new SolidColorBrush(Color.FromRgb(0x1E, 0xD7, 0x60));
            UpdateMenu.FontWeight = FontWeights.Bold;
        }
    }

    private void Exit_Click(object sender, RoutedEventArgs e)
    {
        App.IntentionalExit = true;
        Application.Current.Shutdown();
    }

    protected override void OnClosed(EventArgs e)
    {
        base.OnClosed(e);
        _closed = true; // stops OnLoaded continuing if it is still in the await
        _positionTimer.Stop();
        _progressTimer?.Stop();
        StopSlide();
        _trackTimer.Stop();
        CancelRide(); // the ride tick must not continue on a dead hwnd
        if (_mediaChanged != null) _media.Changed -= _mediaChanged;
        if (_mediaTimeline != null) _media.TimelineChanged -= _mediaTimeline;
        _media.Shutdown(); // releases the WinRT subscriptions that pinned the window
        WidgetSettings.Changed -= OnSettingsChanged;
        Instances.Remove(this);
        // Last window closed (e.g. an Explorer restart): reopen the door to the
        // update check, so the recreated widget checks again
        if (Instances.Count == 0)
            _updateCheckStarted = false;
        if (!App.IntentionalExit && !ClosedByApp && !_recreatePending)
        {
            // Explorer restarted and took the windows with it (they are owned by
            // the bars): a single waiter recreates the whole set when it returns
            _recreatePending = true;
            _ = RecreateAfterTaskbarRestartAsync();
        }
    }

    /// <summary>Since the widget is owned by the taskbar, an Explorer restart
    /// destroys the window(s) - wait for the new bar and recreate the set.</summary>
    private static async Task RecreateAfterTaskbarRestartAsync()
    {
        try
        {
            for (int i = 0; i < 120; i++)
            {
                await Task.Delay(1000);
                if (Interop.FindWindow("Shell_TrayWnd", null) != IntPtr.Zero)
                {
                    await Task.Delay(2000); // let the bar (and the secondaries) settle
                    SyncToMonitors();
                    return;
                }
            }
            App.IntentionalExit = true;
            Application.Current.Shutdown();
        }
        finally
        {
            _recreatePending = false;
        }
    }
}
