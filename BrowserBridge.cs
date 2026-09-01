using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SpotifyTaskbarWidget;

/// <summary>Снимок того, что играет во вкладке браузера.</summary>
public sealed record BrowserState(
    bool Playing, string Title, string Artist, string Art,
    double Position, double Duration,
    bool CanSeek, bool CanNext, bool CanPrev,
    bool CanLike, bool? Liked, bool Explicit, DateTime At);

/// <summary>
/// Приёмник данных от расширения Now Playing Bridge. Нужен для браузеров,
/// которые не публикуют трек в SMTC - Яндекс.Браузер играет через свой
/// YandexMediaPlayer мимо чемиумовского SystemMediaControlsNotifier, поэтому
/// в системной сессии его не видно вообще.
///
/// Минимальный WebSocket-сервер поверх TcpListener: HttpListener для того же
/// потребовал бы netsh urlacl или прав администратора, а виджет работает от
/// обычного пользователя. Слушает только петлю.
/// </summary>
public sealed class BrowserBridge
{
    public const int Port = 45219;

    private static readonly byte[] Guid =
        Encoding.ASCII.GetBytes("258EAFA5-E914-47DA-95CA-C5AB0DC85B11");

    private TcpListener? _listener;
    private CancellationTokenSource? _cts;
    private readonly object _gate = new();
    private BrowserState? _state;
    private NetworkStream? _client;

    /// <summary>Трек/состояние сменились.</summary>
    public event Action? Changed;
    /// <summary>Пришла только новая позиция - обработчик должен быть лёгким.</summary>
    public event Action? TimelineChanged;

    /// <summary>Последнее состояние; null, если браузер молчит дольше 10 секунд
    /// (расширение шлёт пульс раз в 3 секунды).</summary>
    public BrowserState? Current
    {
        get
        {
            lock (_gate)
            {
                if (_state == null) return null;
                return DateTime.UtcNow - _state.At > TimeSpan.FromSeconds(10) ? null : _state;
            }
        }
    }

    public void Start()
    {
        if (_listener != null) return;
        _cts = new CancellationTokenSource();
        try
        {
            _listener = new TcpListener(IPAddress.Loopback, Port);
            _listener.Start();
        }
        catch (Exception ex)
        {
            // Порт занят другой копией виджета - это не смертельно, просто
            // браузерный источник в этом окне работать не будет
            Diag.Once("bridge-listen", $"Browser bridge could not listen on 127.0.0.1:{Port}: {ex.Message}");
            _listener = null;
            return;
        }
        _ = Task.Run(() => AcceptLoopAsync(_cts.Token));
    }

    public void Shutdown()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        _listener = null;
        lock (_gate) { _client = null; _state = null; }
    }

    private async Task AcceptLoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            TcpClient? tcp = null;
            try
            {
                tcp = await _listener!.AcceptTcpClientAsync(ct);
                _ = Task.Run(() => ServeAsync(tcp, ct), ct);
            }
            catch (OperationCanceledException) { return; }
            catch (Exception)
            {
                tcp?.Dispose();
                if (ct.IsCancellationRequested) return;
                await Task.Delay(500, CancellationToken.None);
            }
        }
    }

    private async Task ServeAsync(TcpClient tcp, CancellationToken ct)
    {
        using var _ = tcp;
        NetworkStream? stream = null;
        try
        {
            tcp.NoDelay = true;
            stream = tcp.GetStream();
            if (!await HandshakeAsync(stream, ct)) return;

            lock (_gate) _client = stream; // последний подключившийся и есть текущий

            var acc = new MemoryStream();
            while (!ct.IsCancellationRequested)
            {
                var frame = await ReadFrameAsync(stream, ct);
                if (frame == null) break;
                var (opcode, fin, payload) = frame.Value;

                if (opcode == 0x8) break;                       // close
                if (opcode == 0x9)                              // ping -> pong
                {
                    await SendFrameAsync(stream, 0xA, payload, ct);
                    continue;
                }
                if (opcode == 0xA) continue;                    // pong

                acc.Write(payload, 0, payload.Length);
                if (!fin) continue;
                var text = Encoding.UTF8.GetString(acc.ToArray());
                acc.SetLength(0);
                Apply(text);
            }
        }
        catch (Exception) { /* вкладка/браузер закрылись - обычное дело */ }
        finally
        {
            // Пока этот клиент отваливался, мог подключиться новый - его
            // состояние затирать нельзя
            bool wasCurrent;
            lock (_gate)
            {
                wasCurrent = stream != null && ReferenceEquals(_client, stream);
                if (wasCurrent) { _client = null; _state = null; }
            }
            if (wasCurrent) Changed?.Invoke();
        }
    }

    private async Task<bool> HandshakeAsync(NetworkStream stream, CancellationToken ct)
    {
        var head = new MemoryStream();
        var buf = new byte[1];
        int zeros = 0;
        // Заголовок целиком: читаем до \r\n\r\n, но не больше 8 КБ
        while (head.Length < 8192)
        {
            int n = await stream.ReadAsync(buf, ct);
            if (n <= 0) return false;
            head.WriteByte(buf[0]);
            zeros = buf[0] switch
            {
                (byte)'\r' => zeros is 0 or 2 ? zeros + 1 : 1,
                (byte)'\n' => zeros is 1 or 3 ? zeros + 1 : 0,
                _ => 0,
            };
            if (zeros == 4) break;
        }

        string request = Encoding.ASCII.GetString(head.ToArray());
        string? key = null, origin = null;
        foreach (var line in request.Split("\r\n"))
        {
            int c = line.IndexOf(':');
            if (c <= 0) continue;
            string name = line[..c].Trim();
            string value = line[(c + 1)..].Trim();
            if (name.Equals("Sec-WebSocket-Key", StringComparison.OrdinalIgnoreCase)) key = value;
            else if (name.Equals("Origin", StringComparison.OrdinalIgnoreCase)) origin = value;
        }

        // Пускаем только расширение: любая страница в браузере тоже может
        // постучаться на localhost, но Origin у неё будет http(s)://...
        bool ok = key != null && origin != null &&
                  origin.StartsWith("chrome-extension://", StringComparison.OrdinalIgnoreCase);
        if (!ok)
        {
            var deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nConnection: close\r\n\r\n");
            await stream.WriteAsync(deny, ct);
            return false;
        }

        string accept = Convert.ToBase64String(
            SHA1.HashData(Encoding.ASCII.GetBytes(key!).Concat(Guid).ToArray()));
        var response = Encoding.ASCII.GetBytes(
            "HTTP/1.1 101 Switching Protocols\r\n" +
            "Upgrade: websocket\r\n" +
            "Connection: Upgrade\r\n" +
            $"Sec-WebSocket-Accept: {accept}\r\n\r\n");
        await stream.WriteAsync(response, ct);
        return true;
    }

    private static async Task<(int Opcode, bool Fin, byte[] Payload)?> ReadFrameAsync(
        NetworkStream stream, CancellationToken ct)
    {
        var head = new byte[2];
        if (!await ReadExactAsync(stream, head, 2, ct)) return null;
        bool fin = (head[0] & 0x80) != 0;
        int opcode = head[0] & 0x0F;
        bool masked = (head[1] & 0x80) != 0;
        long len = head[1] & 0x7F;

        if (len == 126)
        {
            var ext = new byte[2];
            if (!await ReadExactAsync(stream, ext, 2, ct)) return null;
            len = (ext[0] << 8) | ext[1];
        }
        else if (len == 127)
        {
            var ext = new byte[8];
            if (!await ReadExactAsync(stream, ext, 8, ct)) return null;
            len = 0;
            for (int i = 0; i < 8; i++) len = (len << 8) | ext[i];
        }
        // Расширение шлёт короткие JSON-ы; всё крупнее - мусор или не наш клиент
        if (len < 0 || len > 1_000_000) return null;

        var mask = new byte[4];
        if (masked && !await ReadExactAsync(stream, mask, 4, ct)) return null;

        var payload = new byte[len];
        if (len > 0 && !await ReadExactAsync(stream, payload, (int)len, ct)) return null;
        if (masked)
            for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i & 3];

        return (opcode, fin, payload);
    }

    private static async Task<bool> ReadExactAsync(NetworkStream stream, byte[] buffer, int count, CancellationToken ct)
    {
        int read = 0;
        while (read < count)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(read, count - read), ct);
            if (n <= 0) return false;
            read += n;
        }
        return true;
    }

    private static async Task SendFrameAsync(NetworkStream stream, int opcode, byte[] payload, CancellationToken ct)
    {
        var header = new MemoryStream();
        header.WriteByte((byte)(0x80 | opcode));
        if (payload.Length < 126)
        {
            header.WriteByte((byte)payload.Length); // сервер не маскирует
        }
        else
        {
            header.WriteByte(126);
            header.WriteByte((byte)(payload.Length >> 8));
            header.WriteByte((byte)(payload.Length & 0xFF));
        }
        var head = header.ToArray();
        await stream.WriteAsync(head, ct);
        if (payload.Length > 0) await stream.WriteAsync(payload, ct);
        await stream.FlushAsync(ct);
    }

    // Отладка моста: включается файлом bridge-debug.txt рядом с exe, пишет
    // сырые сообщения расширения в bridge.log. Проверка одна, при старте -
    // в горячем пути остаётся только сравнение bool
    private static readonly bool DebugLog =
        File.Exists(Path.Combine(AppContext.BaseDirectory, "bridge-debug.txt"));

    private static void Trace(string line)
    {
        if (!DebugLog) return;
        try
        {
            var dir = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "SpotifyTaskbarWidget");
            Directory.CreateDirectory(dir);
            string file = Path.Combine(dir, "bridge.log");
            if (File.Exists(file) && new FileInfo(file).Length > 500_000) File.Delete(file);
            File.AppendAllText(file, $"[{DateTime.Now:HH:mm:ss}] {line}\n");
        }
        catch { }
    }

    private void Apply(string json)
    {
        Trace(json.Length > 8000 ? json[..8000] : json);

        BrowserState? next;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            string type = root.TryGetProperty("type", out var t) ? t.GetString() ?? "" : "";
            if (type == "idle")
            {
                next = null;
            }
            else if (type == "state")
            {
                next = new BrowserState(
                    Playing: Bool(root, "playing"),
                    Title: Str(root, "title"),
                    Artist: Str(root, "artist"),
                    Art: Str(root, "art"),
                    Position: Num(root, "position"),
                    Duration: Num(root, "duration"),
                    CanSeek: Bool(root, "canSeek"),
                    CanNext: Bool(root, "canNext"),
                    CanPrev: Bool(root, "canPrev"),
                    CanLike: Bool(root, "canLike"),
                    Liked: BoolOrNull(root, "liked"),
                    Explicit: Bool(root, "explicit"),
                    At: DateTime.UtcNow);
            }
            else return;
        }
        catch (Exception ex)
        {
            Diag.Once("bridge-json", "Browser bridge got malformed JSON: " + ex.Message);
            return;
        }

        BrowserState? prev;
        lock (_gate)
        {
            prev = _state;
            _state = next;
        }

        // Сменился трек/состояние - полное обновление; иначе только позиция,
        // и дёргать тяжёлый путь с обложкой и UIA незачем
        bool heavy = prev == null || next == null ||
                     prev.Title != next.Title || prev.Artist != next.Artist ||
                     prev.Art != next.Art || prev.Playing != next.Playing ||
                     prev.CanNext != next.CanNext || prev.CanPrev != next.CanPrev ||
                     prev.Liked != next.Liked || prev.CanLike != next.CanLike ||
                     prev.Explicit != next.Explicit;
        Trace($"-> playing={next?.Playing} title='{next?.Title}' dur={next?.Duration:F0} heavy={heavy}");
        if (heavy) Changed?.Invoke();
        else TimelineChanged?.Invoke();
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : 0;

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    /// <summary>Три состояния: да, нет и "сайт не говорит" (кнопки нет).</summary>
    private static bool? BoolOrNull(JsonElement e, string name) =>
        !e.TryGetProperty(name, out var v) ? null
        : v.ValueKind == JsonValueKind.True ? true
        : v.ValueKind == JsonValueKind.False ? false
        : null;

    /// <summary>Шлёт команду расширению; молча уходит в никуда, если браузер
    /// не подключён.</summary>
    public void Send(string cmd, double? pos = null)
    {
        NetworkStream? stream;
        lock (_gate) stream = _client;
        if (stream == null) return;

        string json = pos.HasValue
            ? $"{{\"cmd\":\"{cmd}\",\"pos\":{pos.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}"
            : $"{{\"cmd\":\"{cmd}\"}}";
        _ = Task.Run(async () =>
        {
            try { await SendFrameAsync(stream, 0x1, Encoding.UTF8.GetBytes(json), CancellationToken.None); }
            catch { lock (_gate) { if (ReferenceEquals(_client, stream)) _client = null; } }
        });
    }
}
