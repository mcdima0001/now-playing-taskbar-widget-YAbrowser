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
///
/// Every call into WinRT goes through <see cref="WinRtThread"/> and nowhere
/// else - see the comment there for what happened when they did not.
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
                _manager = await WinRtThread.Run(() => WinRtThread.Await(
                    GlobalSystemMediaTransportControlsSessionManager.RequestAsync(), 10000));
                if (_manager != null) break;
                throw new TimeoutException("RequestAsync did not complete in 10s.");
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

        await WinRtThread.Run(() =>
        {
            // The callbacks arrive on RPC threads, so they only queue the work -
            // reading sessions from there is what used to scatter WinRT objects
            // across transient apartments
            _manager!.SessionsChanged += (_, _) => WinRtThread.Post(PickSession);
            // Без этого смена "текущей" сессии проходила мимо: список сессий
            // не менялся (вкладка/плеер уже были открыты), SessionsChanged молчал,
            // и виджет оставался привязан к прежней сессии
            _manager.CurrentSessionChanged += (_, _) => WinRtThread.Post(PickSession);
            PickSession();
        });
    }

    /// <summary>Unsubscribes everything - without this a closed window stayed
    /// pinned in memory by the WinRT events and kept processing sessions.</summary>
    public void Shutdown()
    {
        // Short wait: this runs while the app is closing, and a session that
        // hangs must not hold the exit back
        try { WinRtThread.Run(() => Unsubscribe(Swap(null))).Wait(1500); } catch { }
    }

    /// <summary>Puts a new session in place and hands back the old one. Only
    /// ever called on the WinRT thread, and that is what serializes the swaps:
    /// two bursts of session events used to interleave and either duplicate
    /// subscriptions or leave handlers pinned to a dead session.</summary>
    private GlobalSystemMediaTransportControlsSession? Swap(GlobalSystemMediaTransportControlsSession? next)
    {
        var old = _session;
        _session = next;
        return old;
    }

    private void Unsubscribe(GlobalSystemMediaTransportControlsSession? session)
    {
        if (session == null) return;
        try
        {
            session.MediaPropertiesChanged -= OnMediaPropertiesChanged;
            session.PlaybackInfoChanged -= OnPlaybackInfoChanged;
            session.TimelinePropertiesChanged -= OnTimelineChanged;
        }
        catch { }
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
                // Реально играющая сессия важнее "текущей" по версии Windows:
                // текущей вполне может остаться давно поставленный на паузу плеер,
                // и тогда виджет показывал его вместо того, что звучит
                chosen = sessions.FirstOrDefault(s =>
                {
                    try
                    {
                        return s.GetPlaybackInfo()?.PlaybackStatus ==
                               GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                    }
                    catch { return false; }
                });
                if (chosen == null)
                {
                    // Ничего не играет - берём выбор самой Windows (та же сессия,
                    // что за плашкой громкости), список как последний резерв
                    try { chosen = _manager.GetCurrentSession(); } catch { }
                    chosen ??= sessions.FirstOrDefault();
                }
            }
        }
        catch (Exception ex)
        {
            Diag.Once("get-sessions", "Reading media sessions failed: " + ex.Message);
        }

        if (ReferenceEquals(_session, chosen))
        {
            if (chosen == null) Changed?.Invoke();
            return;
        }

        Unsubscribe(Swap(chosen));
        // Читаем один раз, здесь: AUMID нужен потоку интерфейса (клик по
        // названию ищет окно источника), а ему до WinRT дотрагиваться нельзя
        _sourceAppId = ReadSourceAppId(chosen);
        if (chosen != null)
        {
            chosen.MediaPropertiesChanged += OnMediaPropertiesChanged;
            chosen.PlaybackInfoChanged += OnPlaybackInfoChanged;
            chosen.TimelinePropertiesChanged += OnTimelineChanged;
        }

        Changed?.Invoke();
    }

    private static string? ReadSourceAppId(GlobalSystemMediaTransportControlsSession? session)
    {
        try { return session?.SourceAppUserModelId; }
        catch { return null; }
    }

    private void OnMediaPropertiesChanged(GlobalSystemMediaTransportControlsSession sender, MediaPropertiesChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnPlaybackInfoChanged(GlobalSystemMediaTransportControlsSession sender, PlaybackInfoChangedEventArgs args) =>
        Changed?.Invoke();

    private void OnTimelineChanged(GlobalSystemMediaTransportControlsSession sender, TimelinePropertiesChangedEventArgs args) =>
        TimelineChanged?.Invoke();

    private volatile string? _sourceAppId;

    /// <summary>Идентификатор приложения текущей сессии (AUMID). По нему
    /// находится окно источника, чтобы показать его по клику. Значение снято
    /// при выборе сессии: меняться ему незачем, а читать по требованию
    /// значило бы звать WinRT из потока интерфейса.</summary>
    public string? SourceAppId => _sourceAppId;

    /// <summary>Quick timeline read (no track properties and no cover art).
    /// Synchronous by contract - the window calls it from its own Task.Run -
    /// but the WinRT part still happens on the WinRT thread.</summary>
    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        var s = _session;
        if (s == null) return null;
        var job = WinRtThread.Run(() => ReadTimeline(s));
        try { return job.Wait(3000) ? job.Result : null; }
        catch { return null; }
    }

    private static (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)?
        ReadTimeline(GlobalSystemMediaTransportControlsSession s)
    {
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
        return await WinRtThread.Run(() =>
        {
            try
            {
                var props = WinRtThread.Await(s.TryGetMediaPropertiesAsync());
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
        });
    }

    public async Task<byte[]?> GetThumbnailAsync()
    {
        var s = _session;
        if (s == null) return null;
        return await WinRtThread.Run(() =>
        {
            try
            {
                var props = WinRtThread.Await(s.TryGetMediaPropertiesAsync());
                if (props?.Thumbnail == null) return null;

                using var stream = WinRtThread.Await(props.Thumbnail.OpenReadAsync(), 5000);
                if (stream == null || stream.Size == 0) return null;

                var bytes = new byte[stream.Size];
                using var reader = new DataReader(stream.GetInputStreamAt(0));
                WinRtThread.AwaitAny(reader.LoadAsync((uint)stream.Size).AsTask(), 5000);
                reader.ReadBytes(bytes);
                return bytes;
            }
            catch
            {
                return null;
            }
        });
    }

    /// <summary>A command for the current session, on the WinRT thread.
    /// Failures are swallowed by design: a player that has just closed is the
    /// normal case here, not an error worth reporting.</summary>
    private Task Command(Action<GlobalSystemMediaTransportControlsSession> action)
    {
        var s = _session;
        if (s == null) return Task.CompletedTask;
        return WinRtThread.Run(() => { try { action(s); } catch { } });
    }

    public Task TogglePlayPauseAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TryTogglePlayPauseAsync().AsTask()));

    public Task NextAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TrySkipNextAsync().AsTask()));

    public Task PreviousAsync() =>
        Command(s => WinRtThread.AwaitAny(s.TrySkipPreviousAsync().AsTask()));

    public Task SeekAsync(TimeSpan position) =>
        Command(s => WinRtThread.AwaitAny(s.TryChangePlaybackPositionAsync(position.Ticks).AsTask()));

    public Task CycleRepeatAsync() => Command(s =>
    {
        var current = s.GetPlaybackInfo()?.AutoRepeatMode ?? Windows.Media.MediaPlaybackAutoRepeatMode.None;
        var next = current switch
        {
            Windows.Media.MediaPlaybackAutoRepeatMode.None => Windows.Media.MediaPlaybackAutoRepeatMode.List,
            Windows.Media.MediaPlaybackAutoRepeatMode.List => Windows.Media.MediaPlaybackAutoRepeatMode.Track,
            _ => Windows.Media.MediaPlaybackAutoRepeatMode.None,
        };
        WinRtThread.AwaitAny(s.TryChangeAutoRepeatModeAsync(next).AsTask());
    });

    public Task ToggleShuffleAsync() => Command(s =>
    {
        bool current = s.GetPlaybackInfo()?.IsShuffleActive ?? false;
        WinRtThread.AwaitAny(s.TryChangeShuffleActiveAsync(!current).AsTask());
    });
}
