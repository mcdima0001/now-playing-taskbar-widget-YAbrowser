using System.Net.Http;

namespace SpotifyTaskbarWidget;

/// <summary>
/// Один источник трека для окна: системная сессия (SMTC) плюс браузерное
/// расширение через <see cref="BrowserBridge"/>. Наружу отдаёт ровно тот же
/// набор методов, что и <see cref="MediaService"/>, поэтому окно не знает,
/// откуда пришли данные, и команды сами уходят туда, откуда взят трек.
/// </summary>
public sealed class MediaHub
{
    private readonly MediaService _smtc = new();
    private readonly BrowserBridge _browser = new();
    private readonly HttpClient _http = new() { Timeout = TimeSpan.FromSeconds(5) };

    private volatile bool _useBrowser;
    private string _artUrl = "";
    private byte[]? _artBytes;

    public event Action? Changed;
    public event Action? TimelineChanged;

    /// <summary>Трек сейчас берётся из браузера.</summary>
    public bool UsingBrowser => _useBrowser;

    /// <summary>Перемешивание во вкладке: "off"/"on"/"disabled", либо пусто -
    /// у сайта такой кнопки нет. В Моей волне её не бывает вовсе.</summary>
    public string BrowserShuffle => _useBrowser ? _browser.Current?.Shuffle ?? "" : "";

    /// <summary>Повтор во вкладке: "off"/"context"/"one"/"disabled" либо пусто.</summary>
    public string BrowserRepeat => _useBrowser ? _browser.Current?.Repeat ?? "" : "";

    /// <summary>У вкладки есть чем крутить громкость - свой ползунок либо
    /// сам медиаэлемент.</summary>
    public bool BrowserCanVolume => _useBrowser && _browser.Current is { CanVolume: true };

    /// <summary>Громкость вкладки, 0..1.</summary>
    public double BrowserVolume => _useBrowser ? _browser.Current?.Volume ?? 1 : 1;

    /// <summary>Избранное во вкладке: true/false, либо null - сайт не сообщает
    /// (нет кнопки), и тогда окно показывает нейтральный плюс.</summary>
    public bool? BrowserLiked => _browser.Current is { CanLike: true } b ? b.Liked : null;

    /// <summary>У вкладки есть кнопка избранного - окну можно показывать свою.</summary>
    public bool BrowserCanLike => _useBrowser && _browser.Current is { CanLike: true };

    /// <summary>Приписка к названию - "Remix", "Acoustic". Окно рисует её
    /// серой, как сам Яндекс; в SMTC такого поля нет.</summary>
    public string TitleVersion => _useBrowser ? _browser.Current?.Version ?? "" : "";

    /// <summary>Трек помечен как содержащий ненормативную лексику. Приходит
    /// только из браузера: ни SMTC, ни mediaSession такого поля не имеют.</summary>
    public bool Explicit => _useBrowser && _browser.Current is { Explicit: true };

    public async Task InitializeAsync()
    {
        _smtc.Changed += () => Changed?.Invoke();
        _smtc.TimelineChanged += () => TimelineChanged?.Invoke();
        _browser.Changed += () => Changed?.Invoke();
        _browser.TimelineChanged += () => TimelineChanged?.Invoke();
        _browser.Start();
        await _smtc.InitializeAsync();
    }

    public void Shutdown()
    {
        _smtc.Shutdown();
        _browser.Shutdown();
    }

    /// <summary>Кто главный прямо сейчас. Играющий источник побеждает
    /// стоящий на паузе; при равенстве - системная сессия, потому что
    /// десктопные плееры отдают больше (обложка, shuffle, избранное).</summary>
    private static bool PreferBrowser(TrackInfo? smtc, BrowserState? browser)
    {
        bool smtcOk = smtc != null && !string.IsNullOrWhiteSpace(smtc.Title);
        bool browserOk = browser != null && !string.IsNullOrWhiteSpace(browser.Title);
        if (!browserOk) return false;
        if (!smtcOk) return true;
        if (smtc!.IsPlaying) return false;
        return browser!.Playing;
    }

    private static TrackInfo FromBrowser(BrowserState b) => new(
        b.Title, b.Artist, b.Playing, null,
        TimeSpan.FromSeconds(b.Position), TimeSpan.FromSeconds(b.Duration), b.At);

    public async Task<TrackInfo?> GetTrackAsync()
    {
        var smtc = await _smtc.GetTrackAsync();
        var browser = _browser.Current;
        _useBrowser = PreferBrowser(smtc, browser);
        return _useBrowser ? FromBrowser(browser!) : smtc;
    }

    public (TimeSpan Position, TimeSpan Duration, bool IsPlaying, DateTime PositionAtUtc)? GetTimeline()
    {
        if (_useBrowser)
        {
            var b = _browser.Current;
            if (b != null)
                return (TimeSpan.FromSeconds(b.Position), TimeSpan.FromSeconds(b.Duration), b.Playing, b.At);
        }
        return _smtc.GetTimeline();
    }

    /// <summary>Обложка: у SMTC она приходит потоком, у браузера - ссылкой,
    /// которую качаем сами и кешируем (одна и та же ссылка на весь трек).</summary>
    public async Task<byte[]?> GetThumbnailAsync()
    {
        if (!_useBrowser) return await _smtc.GetThumbnailAsync();

        string url = _browser.Current?.Art ?? "";
        if (url.Length == 0) return null;
        if (url == _artUrl) return _artBytes;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return null;

        try
        {
            var bytes = await _http.GetByteArrayAsync(uri);
            // Обложки - это десятки килобайт; всё крупнее качать незачем
            if (bytes.Length > 8 * 1024 * 1024) return null;
            _artUrl = url;
            _artBytes = bytes;
            return bytes;
        }
        catch (Exception ex)
        {
            Diag.Once("art-download", "Downloading cover art from the browser failed: " + ex.Message);
            _artUrl = url;
            _artBytes = null;
            return null;
        }
    }

    public async Task TogglePlayPauseAsync()
    {
        if (_useBrowser) _browser.Send("playpause");
        else await _smtc.TogglePlayPauseAsync();
    }

    public async Task NextAsync()
    {
        if (_useBrowser) _browser.Send("next");
        else await _smtc.NextAsync();
    }

    public async Task PreviousAsync()
    {
        if (_useBrowser) _browser.Send("prev");
        else await _smtc.PreviousAsync();
    }

    public async Task SeekAsync(TimeSpan position)
    {
        if (_useBrowser) _browser.Send("seek", position.TotalSeconds);
        else await _smtc.SeekAsync(position);
    }

    /// <summary>Переключает избранное во вкладке (у Яндекса это та же кнопка
    /// "Нравится", что и в плеере) - в SMTC такого действия нет вообще.</summary>
    public void ToggleBrowserLike()
    {
        if (_useBrowser) _browser.Send("like");
    }

    /// <summary>Показать то, откуда идёт звук: вкладку браузера через
    /// расширение либо окно приложения из системной сессии. Возвращает false,
    /// если источник не опознан - тогда окно решает, что делать дальше.</summary>
    public bool FocusCurrentSource()
    {
        if (_useBrowser)
        {
            // Снимаем запрет на смену активного окна, иначе Windows разрешит
            // браузеру только подсветиться на панели задач
            try { Interop.AllowSetForegroundWindow(Interop.ASFW_ANY); } catch { }
            _browser.Send("focus");
            return true;
        }

        string appId = _smtc.SourceAppId ?? "";
        try { return SourceActivator.Activate(appId); }
        catch { return false; }
    }

    /// <summary>Повтор: во вкладке это клик по кнопке сайта, и сайт сам ведёт
    /// цикл выкл -> список -> трек. Виджет только просит следующий шаг.</summary>
    public async Task CycleRepeatAsync()
    {
        if (_useBrowser) _browser.Send("repeat");
        else await _smtc.CycleRepeatAsync();
    }

    public async Task ToggleShuffleAsync()
    {
        if (_useBrowser) _browser.Send("shuffle");
        else await _smtc.ToggleShuffleAsync();
    }

    /// <summary>Громкость вкладки, 0..1. У Яндекса это его собственный
    /// ползунок, у обычного сайта - громкость медиаэлемента; системный
    /// микшер тут ни при чём, браузер в нём один на все вкладки.</summary>
    public void SetBrowserVolume(double fraction)
    {
        if (_useBrowser) _browser.Send("volume", Math.Clamp(fraction, 0, 1), "value");
    }
}
