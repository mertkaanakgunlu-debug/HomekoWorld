namespace HomekoWorld.Hardware;

public enum TransportMode { Wifi, Usb, Local, Rp2040 }

// Routes IKeyDeviceTransport calls to the appropriate transport implementation.
// ComboEngine holds a single IKeyDeviceTransport reference and never needs to
// know the mode changed.
public sealed class TransportRouter : IKeyDeviceTransport
{
    private readonly HidBridgeClient     _net;
    private readonly LocalInputTransport _local;
    private readonly Rp2040HidTransport  _rp2040;

    // Volatile so background threads (ComboEngine tasks) always see the latest value
    private volatile IKeyDeviceTransport _active;

    public TransportMode Mode { get; private set; } = TransportMode.Wifi;

    public bool IsConnected => _active.IsConnected;
    public event EventHandler<string>? LineReceived;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<long>?   LatencyMs;

    public TransportRouter(HidBridgeClient net, LocalInputTransport local, Rp2040HidTransport rp2040)
    {
        _net    = net;
        _local  = local;
        _rp2040 = rp2040;
        _active = net;

        // Wire inner events once; only re-fire if that inner is currently active.
        _net.LineReceived   += (s, e) => { if (_active == _net)   LineReceived?.Invoke(this, e); };
        _net.Disconnected   += (s, e) => { if (_active == _net)   Disconnected?.Invoke(this, e); };
        _net.LatencyMs      += (s, e) => { if (_active == _net)   LatencyMs?.Invoke(this, e);    };

        _local.LineReceived += (s, e) => { if (_active == _local) LineReceived?.Invoke(this, e); };
        _local.Disconnected += (s, e) => { if (_active == _local) Disconnected?.Invoke(this, e); };
        _local.LatencyMs    += (s, e) => { if (_active == _local) LatencyMs?.Invoke(this, e);    };

        _rp2040.LineReceived += (s, e) => { if (_active == _rp2040) LineReceived?.Invoke(this, e); };
        _rp2040.Disconnected += (s, e) => { if (_active == _rp2040) Disconnected?.Invoke(this, e); };
        _rp2040.LatencyMs    += (s, e) => { if (_active == _rp2040) LatencyMs?.Invoke(this, e);    };
    }

    // Switches to local SendInput mode
    public void SwitchToLocal()
    {
        _net.Disconnect();
        _rp2040.Disconnect();
        Mode    = TransportMode.Local;
        _active = _local;
    }

    // Switches back to network (WiFi or USB — same underlying HidBridgeClient)
    public void SwitchToNet(TransportMode netMode = TransportMode.Wifi)
    {
        _local.Disconnect();
        _rp2040.Disconnect();
        Mode    = netMode;
        _active = _net;
    }

    // Faz 30: RP2040 Zero USB-HID köprüsü (gerçek klavye+fare donanımı)
    public void SwitchToRp2040()
    {
        _net.Disconnect();
        _local.Disconnect();
        Mode    = TransportMode.Rp2040;
        _active = _rp2040;
    }

    // Faz 9: RP2040'ı BOOTSEL'e sokar (app-içi firmware güncelleme).
    public Task RebootRp2040ToBootloaderAsync() => _rp2040.RebootToBootloaderAsync();

    // 14.tur (Faz 3.3): FarmSettings.AtomicMouseMove'u RP2040 transport'una uygular (aktif mod
    // Wifi/Local olsa da zararsız — yalnız Rp2040HidTransport.MoveAbsAsync bu bayrağı okur).
    public bool Rp2040AtomicMouseMove { set => _rp2040.AtomicMouseMove = value; }

    // ── IKeyDeviceTransport forwarding ────────────────────────────────────────

    public Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default)
        => _active.ConnectAsync(host, port, ct);

    public Task SendLineAsync(string line, CancellationToken ct = default)
        => _active.SendLineAsync(line, ct);

    public Task SendRawAsync(string payload, CancellationToken ct = default)
        => _active.SendRawAsync(payload, ct);

    public void Disconnect()
        => _active.Disconnect();

    // ── Faz 17: Mouse forwarding ──────────────────────────────────────────────

    /// <summary>
    /// Fareyi verilen ekran piksel koordinatına taşır.
    /// Local modda Win32 SendInput, BLE modda Android MOUSE_MOVE_ABS protokolü.
    /// </summary>
    public Task MoveAbsAsync(int x, int y, CancellationToken ct = default)
        => _active.MoveAbsAsync(x, y, ct);

    /// <summary>Belirtilen mouse butonuna tek tıklama gönderir.</summary>
    public Task ClickAsync(MouseButton btn = MouseButton.Left, CancellationToken ct = default)
        => _active.ClickAsync(btn, ct);

    /// <inheritdoc/>
    public Task KeyDownAsync(string key, CancellationToken ct = default)
        => _active.KeyDownAsync(key, ct);

    /// <inheritdoc/>
    public Task KeyUpAsync(string key, CancellationToken ct = default)
        => _active.KeyUpAsync(key, ct);

    /// <inheritdoc/>
    public Task MouseDownAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
        => _active.MouseDownAsync(btn, ct);

    /// <inheritdoc/>
    public Task MouseUpAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
        => _active.MouseUpAsync(btn, ct);

    /// <inheritdoc/>
    public Task MoveRelAsync(int dx, int dy, CancellationToken ct = default)
        => _active.MoveRelAsync(dx, dy, ct);

    public void Dispose()
    {
        _net.Dispose();
        _local.Dispose();
        _rp2040.Dispose();
    }
}
