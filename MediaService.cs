using Windows.Media.Control;
using Windows.Storage.Streams;

namespace SpotifyTaskbarWidget;

public sealed record TrackInfo(string Title, string Artist, bool IsPlaying, bool? IsShuffle,
    TimeSpan Position, TimeSpan Duration, DateTime PositionAtUtc);

/// <summary>
/// Reads the current track through the Windows media API (SMTC).
/// Spotify desktop publishes the playing track there - no login and no
/// Spotify API needed. Prefers the Spotify session; without one, follows
/// the active media session.
/// </summary>
public sealed class MediaService
{
    private GlobalSystemMediaTransportControlsSessionManager? _manager;
    private GlobalSystemMediaTransportControlsSession? _session;

    /// <summary>Fired (on a background thread) when the track or the state changes.</summary>
    public event Action? Changed;

    /// <summary>Fired only when position/duration change - happens every few
    /// seconds, so the handler has to stay light.</summary>
    public event Action? TimelineChanged;

    public async Task InitializeAsync()
    {
        // When starting with Windows, WinRT may not be ready yet - a transient
        // failure here left the widget with no track for the WHOLE session.
        // Retry with backoff (4s->64s, ~2 min) before giving up for good.
        for (int attempt = 0; ; attempt++)
        {
            try
            {
                _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                break;
            }
            catch (Exception ex)
            {
                if (attempt >= 5)
                {
                    // No media session API (N editions without the Media Feature
                    // Pack, broken WinRT) - leave a trace for the report
                    Diag.Once("smtc-init", "Media session API unavailable (this is why nothing shows as playing): " + ex.Message);
                    return;
                }
                await Task.Delay(TimeSpan.FromSeconds(4 << attempt));
            }
        }
        _manager.SessionsChanged += (_, _) => PickSession();
        PickSession();
    }

    private readonly object _pickLock = new();

    /// <summary>Unsubscribes everything - without this a closed window stayed
    /// pinned in memory by the WinRT events and kept processing sessions.</summary>
    public void Shutdown()
    {
        lock (_pickLock)
        {
            var old = _session;
            _session = null;
            if (old != null)
            {
                try
                {
                    old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    old.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                catch { }
            }
        }
    }

    private void PickSession()
    {
        if (_manager == null) return;

        // Any player that publishes to SMTC. Spotify wins (it is the only one
        // with extras through UI Automation); without it, follow the session
        // Windows considers current and, as a last resort, one that is playing.
        GlobalSystemMediaTransportControlsSession? chosen = null;
        try
        {
            var sessions = _manager.GetSessions();
            chosen = sessions.FirstOrDefault(s =>
                (s.SourceAppUserModelId ?? "").Contains("spotify", StringComparison.OrdinalIgnoreCase));
            if (chosen == null)
            {
                // GetCurrentSession() is the pick Windows itself makes (the same
                // one behind the volume flyout) - the scan is only a safety net
                try { chosen = _manager.GetCurrentSession(); } catch { }
                chosen ??= sessions.FirstOrDefault(s =>
                {
                    try
                    {
                        return s.GetPlaybackInfo()?.PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { return false; }
                }) ?? sessions.FirstOrDefault();
            }
        }
        catch (Exception ex)
        {
            Diag.Once("get-sessions", "Reading media sessions failed: " + ex.Message);
        }

        // SessionsChanged arrives on WinRT threads in bursts (player starting/
        // closing): without the lock, two interleaved swaps duplicated
        // subscriptions or left handlers pinned to a dead session
        lock (_pickLock)
        {
            var old = _session;
            if (ReferenceEquals(old, chosen))
            {
                if (chosen == null) Changed?.Invoke();
                return;
            }
            if (old != null)
            {
                try
                {
                    old.MediaPropertiesChanged -= OnMediaPropertiesChanged;
                    old.PlaybackInfoChanged -= OnPlaybackInfoChanged;
                    old.TimelinePropertiesChanged -= OnTimelineChanged;
                }
                catch { }
            }

            _session = chosen;
            if (chosen != null)
            {
                chosen.MediaPropertiesChanged += OnMediaPropertiesChanged;
                chosen.PlaybackInfoChanged += OnPlaybackInfoChanged;
                chosen.TimelinePropertiesChanged += OnTimelineChanged;
            }
        }

        Changed?.Invoke();
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        TimelineChanged?.Invoke();

    /// <summary>Quick timeline read (no track properties and no cover art).</summary>
    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var tl = s.GetTimelineProperties();
            var pi = s.GetPlaybackInfo();
            return (tl?.Position ?? TimeSpan.Zero,
                    tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero,
                    pi?.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing,
                    tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow);
        }
        catch
        {
            return null;
        }
    }

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            var pi = s.GetPlaybackInfo();
            bool playing = pi?.PlaybackStatus ==
                           GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;

            var tl = s.GetTimelineProperties();
            TimeSpan position = tl?.Position ?? TimeSpan.Zero;
            TimeSpan duration = tl != null ? tl.EndTime - tl.StartTime : TimeSpan.Zero;
            DateTime positionAt = tl?.LastUpdatedTime.UtcDateTime ?? DateTime.UtcNow;

            return new TrackInfo(props?.Title ?? "", props?.Artist ?? "", playing, pi?.IsShuffleActive,
                position, duration, positionAt);
        }
        catch (Exception ex)
        {
            Diag.Once("get-track", "Reading track from the Spotify session failed: " + ex.Message);
            return null;
        }
    }

    public async Task<byte[]?> GetThumbnailAsync()
    {
        var s = _session;
        if (s == null) return null;
        try
        {
            var props = await s.TryGetMediaPropertiesAsync();
            if (props?.Thumbnail == null) return null;

            using var stream = await props.Thumbnail.OpenReadAsync();
            if (stream.Size == 0) return null;

            var bytes = new byte[stream.Size];
            using var reader = new DataReader(stream.GetInputStreamAt(0));
            await reader.LoadAsync((uint)stream.Size);
            reader.ReadBytes(bytes);
            return bytes;
        }
        catch
        {
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryTogglePlayPauseAsync(); } catch { }
    }

    public async Task NextAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipNextAsync(); } catch { }
    }

    public async Task PreviousAsync()
    {
        var s = _session;
        if (s == null) return;
        try { await s.TrySkipPreviousAsync(); } catch { }
    }

    public async Task SeekAsync(TimeSpan position)
    {
        var s = _session;
        if (s == null) return;
        try { await s.TryChangePlaybackPositionAsync(position.Ticks); } catch { }
    }

    public async Task CycleRepeatAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            var current = s.GetPlaybackInfo()?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
            var next = current switch
            {
                Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
                Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
                _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
            };
            await s.TryChangeAutoRepeatModeAsync(next);
        }
        catch { }
    }

    public async Task ToggleShuffleAsync()
    {
        var s = _session;
        if (s == null) return;
        try
        {
            bool current = s.GetPlaybackInfo()?.IsShuffleActive ?? false;
            await s.TryChangeShuffleActiveAsync(!current);
        }
        catch { }
    }
}
