namespace HomekoWorld.Hardware;

public enum MouseButton { Left, Right, Middle }

public interface IKeyDeviceTransport : IDisposable
{
    bool IsConnected { get; }
    event EventHandler<string>? LineReceived;
    event EventHandler<string>? Disconnected;
    event EventHandler<long>?   LatencyMs;   // round-trip ping latency in milliseconds

    Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default);
    Task SendLineAsync(string line, CancellationToken ct = default);
    // Sends pre-formatted multi-line payload as a single TCP write (one flush → one packet)
    Task SendRawAsync(string payload, CancellationToken ct = default);
    void Disconnect();

    // ── Faz 17: Mouse path ────────────────────────────────────────────────────
    /// <summary>
    /// Fareyi verilen ekran piksel koordinatına taşır.
    /// x,y: gerçek ekran pikseli (GetSystemMetrics ile normalleştirme transport içinde yapılır).
    /// </summary>
    Task MoveAbsAsync(int x, int y, CancellationToken ct = default)
        => Task.CompletedTask; // default: no-op (kernel transport stub)

    /// <summary>Belirtilen mouse butonuna tek tıklama gönderir.</summary>
    Task ClickAsync(MouseButton btn = MouseButton.Left, CancellationToken ct = default)
        => Task.CompletedTask; // default: no-op

    /// <summary>Mouse butonunu basılı tutar (DOWN-only, koordinat yok).</summary>
    Task MouseDownAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>Basılı mouse butonunu bırakır (UP-only, koordinat yok).</summary>
    Task MouseUpAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Mouse'u göreli olarak (dx, dy) piksel taşır — ABSOLUTE flag olmadan.
    /// Kamera drag için kullanılır; buton flag içermez.
    /// </summary>
    Task MoveRelAsync(int dx, int dy, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <summary>
    /// Bir tuşu basılı tutar (KEYDOWN).
    /// Local modda Win32 SendInput scan-code, BLE modda KEYDOWN:key protokolü.
    /// </summary>
    Task KeyDownAsync(string key, CancellationToken ct = default)
        => Task.CompletedTask; // default: no-op

    /// <summary>Basılı tutulan tuşu bırakır (KEYUP).</summary>
    Task KeyUpAsync(string key, CancellationToken ct = default)
        => Task.CompletedTask; // default: no-op
}
