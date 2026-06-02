using System.Diagnostics;
using System.Net.Sockets;
using System.IO;

namespace HomekoWorld.Hardware;

public sealed class HidBridgeClient : IKeyDeviceTransport
{
    private TcpClient? _tcp;
    private StreamWriter? _writer;
    private StreamReader? _reader;
    private CancellationTokenSource? _readCts;
    private Timer? _pingTimer;
    private readonly SemaphoreSlim _sendLock = new(1, 1);

    // Timestamp (Stopwatch ticks) of the most recent outgoing PING — used to compute latency
    private long _pingTicks;

    public bool IsConnected => _tcp?.Connected == true;

    public event EventHandler<string>? LineReceived;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<long>?   LatencyMs;

    public async Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        Disconnect();

        _tcp = new TcpClient { NoDelay = true }; // disable Nagle — CANCEL arrives immediately
        try
        {
            await _tcp.ConnectAsync(host, port, ct);
        }
        catch
        {
            Disconnect();
            return false;
        }

        var stream = _tcp.GetStream();
        _writer = new StreamWriter(stream) { AutoFlush = true };
        _reader = new StreamReader(stream);

        // Health check FIRST — ReadLoop henüz başlamadı, yarış yok
        await SendLineAsync(HidBridgeProtocol.Ping, ct);
        using var pongCts = new CancellationTokenSource(TimeSpan.FromMilliseconds(3000));
        try
        {
            var pong = await _reader.ReadLineAsync(pongCts.Token);
            if (pong != HidBridgeProtocol.Pong) { Disconnect(); return false; }
        }
        catch
        {
            Disconnect();
            return false;
        }

        // PONG alındı, şimdi ReadLoop ve keepalive başlat
        _readCts = new CancellationTokenSource();
        _ = Task.Run(() => ReadLoopAsync(_readCts.Token));
        _pingTimer = new Timer(_ => _ = Task.Run(KeepAliveAsync),
            null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));
        return true;
    }

    public async Task SendLineAsync(string line, CancellationToken ct = default)
    {
        if (_writer is null || !IsConnected) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await _writer.WriteLineAsync(line.AsMemory(), ct);
        }
        catch { Disconnect(); }
        finally { _sendLock.Release(); }
    }

    // Writes the entire payload in one operation → one TCP flush → one packet.
    // Use this for batch execution blocks so all lines arrive atomically.
    public async Task SendRawAsync(string payload, CancellationToken ct = default)
    {
        if (_writer is null || !IsConnected) return;
        await _sendLock.WaitAsync(ct);
        try
        {
            await _writer.WriteAsync(payload.AsMemory(), ct);
            await _writer.FlushAsync(ct);
        }
        catch { Disconnect(); }
        finally { _sendLock.Release(); }
    }

    // ── Faz 17: Mouse (BLE HID mouse, Android tarafı Faz 17.1'de eklenir) ─────

    /// <summary>
    /// Android'e MOUSE_MOVE_ABS:x:y komutu gönderir.
    /// Android tarafı bu komutu BLE HID absolute mouse report'a çevirir.
    /// x,y: HIDP absolute space (0..32767).
    /// </summary>
    public async Task MoveAbsAsync(int x, int y, CancellationToken ct = default)
    {
        // Ekran pikselini HIDP absolute koordinatına dönüştür
        // (Android tarafı da dönüştürebilir; şimdilik PC'de yapıyoruz)
        await SendLineAsync(HidBridgeProtocol.MouseMoveAbs(
            x * 32767 / Math.Max(1, System.Windows.SystemParameters.PrimaryScreenWidth  > 0 ? (int)System.Windows.SystemParameters.PrimaryScreenWidth  : 1920),
            y * 32767 / Math.Max(1, System.Windows.SystemParameters.PrimaryScreenHeight > 0 ? (int)System.Windows.SystemParameters.PrimaryScreenHeight : 1080)),
            ct);
    }

    /// <summary>Android'e MOUSE_CLICK:btn komutu gönderir.</summary>
    public Task ClickAsync(MouseButton btn = MouseButton.Left, CancellationToken ct = default)
        => SendLineAsync(HidBridgeProtocol.MouseClick(btn), ct);

    /// <summary>Android'e KEYDOWN:key komutu gönderir.</summary>
    public Task KeyDownAsync(string key, CancellationToken ct = default)
        => SendLineAsync(HidBridgeProtocol.KeyDown(key), ct);

    /// <summary>Android'e KEYUP:key komutu gönderir.</summary>
    public Task KeyUpAsync(string key, CancellationToken ct = default)
        => SendLineAsync(HidBridgeProtocol.KeyUp(key), ct);

    public void Disconnect()
    {
        _pingTimer?.Dispose();
        _pingTimer = null;

        _readCts?.Cancel();
        _readCts?.Dispose();
        _readCts = null;

        _writer?.Dispose();
        _reader?.Dispose();
        _tcp?.Close();
        _tcp?.Dispose();

        _writer = null;
        _reader = null;
        _tcp    = null;
    }

    private async Task KeepAliveAsync()
    {
        if (!IsConnected) return;
        try
        {
            _pingTicks = Stopwatch.GetTimestamp();
            await SendLineAsync(HidBridgeProtocol.Ping);
        }
        catch { }
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested && _reader is not null)
            {
                var line = await _reader.ReadLineAsync(ct);
                if (line is null) break;

                if (line == HidBridgeProtocol.Pong && _pingTicks != 0)
                {
                    var ticks = Stopwatch.GetTimestamp() - _pingTicks;
                    var ms    = ticks * 1000 / Stopwatch.Frequency;
                    LatencyMs?.Invoke(this, ms);
                    continue; // PONG is internal protocol — don't forward to LineReceived
                }

                LineReceived?.Invoke(this, line);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            var reason = "Connection lost";
            Disconnected?.Invoke(this, reason);
            Disconnect();
        }
    }

    public void Dispose() => Disconnect();
}
