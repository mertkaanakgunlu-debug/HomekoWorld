using System.Runtime;
using System.Runtime.InteropServices;
using HomekoWorld.Core;

namespace HomekoWorld.Hardware;

// Executes the same EXEC/KEYDOWN:key:ms/KEYUP:key:ms/ENDEXEC batch protocol
// as the Android TcpServer, but locally via Win32 SendInput (scan codes).
// No external device or network connection required.
public sealed class LocalInputTransport : IKeyDeviceTransport
{
    // ── Win32 P/Invoke ────────────────────────────────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint   dwFlags;
        public uint   time;
        public nint   dwExtraInfo;
    }

    // MOUSEINPUT is included only to give the union its correct size (32 bytes on x64)
    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int    dx, dy;
        public uint   mouseData, dwFlags, time;
        public nint   dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct InputUnion
    {
        [FieldOffset(0)] public KEYBDINPUT ki;
        [FieldOffset(0)] public MOUSEINPUT mi; // sizes union to 32 bytes
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint      Type;  // INPUT_KEYBOARD = 1
        public InputUnion U;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    // Thread-level CPU affinity: pins the macro thread to a chosen core
    [DllImport("kernel32.dll")]
    private static extern nint SetThreadAffinityMask(nint hThread, nint dwThreadAffinityMask);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentThread();

    private const uint INPUT_KEYBOARD     = 1;
    private const uint INPUT_MOUSE        = 0;
    private const uint KEYEVENTF_KEYUP    = 0x0002;
    private const uint KEYEVENTF_SCANCODE = 0x0008;
    private const uint KEYEVENTF_EXTENDED = 0x0001;

    // Mouse flags (MOUSEEVENTF_*)
    private const uint MOUSEEVENTF_MOVE        = 0x0001;
    private const uint MOUSEEVENTF_LEFTDOWN    = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP      = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN   = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP     = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN  = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP    = 0x0040;
    private const uint MOUSEEVENTF_ABSOLUTE    = 0x8000;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);
    private const int SM_CXSCREEN = 0;
    private const int SM_CYSCREEN = 1;

    // GetCursorPos: farenin anlık fiziksel piksel konumunu alır (ClickAsync kullanır)
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(ref POINT lpPoint);

    // ── ThreadStatic INPUT buffer — one array per thread, reused every call ──
    [ThreadStatic] private static INPUT[]? _inputBuf;

    // ── Persistent worker thread ──────────────────────────────────────────────
    // The worker thread is created once in the constructor and stays alive for
    // the app lifetime. This eliminates the ~5-10 ms OS thread-creation overhead
    // that would otherwise shift every combo's first event.

    private sealed class WorkItem
    {
        public required List<BatchParser.BatchEvent> Events;
        public required bool                          IsLoop;
        public required CancellationToken             Token;
    }

    private volatile WorkItem?  _pendingWork;
    private readonly SemaphoreSlim _workReady = new(0, 1);

    // ── State ─────────────────────────────────────────────────────────────────

    private bool _active;
    private CancellationTokenSource? _batchCts;
    private readonly object _batchLock = new();

    public bool IsConnected => _active;
#pragma warning disable CS0067  // interface contract; local transport never disconnects or receives lines
    public event EventHandler<string>? LineReceived;
    public event EventHandler<string>? Disconnected;
#pragma warning restore CS0067
    public event EventHandler<long>? LatencyMs;

    public LocalInputTransport()
    {
        var worker = new Thread(WorkerLoop)
        {
            IsBackground = true,
            Priority     = ThreadPriority.Highest,
            Name         = "HomekoLocalInputEngine"
        };
        worker.Start();
    }

    // ── Worker loop — runs for the app lifetime on a pre-warmed thread ────────

    private void WorkerLoop()
    {
        // One-time per-thread setup: affinity + timer resolution is set globally in Program.Main
        int cores = Environment.ProcessorCount;
        if (cores > 1)
            SetThreadAffinityMask(GetCurrentThread(), (nint)(1L << (cores - 1)));

        while (true) // exits only when the process dies (IsBackground = true)
        {
            _workReady.Wait(); // sleep until a batch arrives

            var item = _pendingWork;
            _pendingWork = null;
            if (item is null) continue;

            // Suppress Gen 2 / LOH collections during the hot execution window.
            var prevGcMode = GCSettings.LatencyMode;
            GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            try
            {
                do
                {
                    BatchExecutor.Run(item.Events, InjectKey, item.Token);
                    if (item.IsLoop && !item.Token.IsCancellationRequested)
                        item.Token.WaitHandle.WaitOne(50);
                }
                while (item.IsLoop && !item.Token.IsCancellationRequested);
            }
            finally
            {
                GCSettings.LatencyMode = prevGcMode;
            }
        }
    }

    // ── IKeyDeviceTransport ───────────────────────────────────────────────────

    public Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        _active = true;
        return Task.FromResult(true);
    }

    public Task SendLineAsync(string line, CancellationToken ct = default)
    {
        if (line == HidBridgeProtocol.Ping)
        {
            // Emit 0 ms latency so adaptive ping stays neutral in local mode
            LatencyMs?.Invoke(this, 0L);
        }
        else if (line == HidBridgeProtocol.Cancel)
        {
            CancelBatch();
        }
        return Task.CompletedTask;
    }

    public Task SendRawAsync(string payload, CancellationToken ct = default)
    {
        var events = BatchParser.Parse(payload);
        if (events.Count == 0) return Task.CompletedTask;

        // EXEC_LOOP: mirror Android TcpServer behavior — loop the batch internally until CANCEL
        bool isLoop = payload.TrimStart().StartsWith(HidBridgeProtocol.ExecLoop,
                          StringComparison.OrdinalIgnoreCase);

        CancellationTokenSource batchCts;
        lock (_batchLock)
        {
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            batchCts = _batchCts = new CancellationTokenSource();
        }

        // Post work to the persistent worker thread (zero thread-creation overhead)
        _pendingWork = new WorkItem
        {
            Events  = events,
            IsLoop  = isLoop,
            Token   = batchCts.Token
        };

        // Release the semaphore once — worker picks it up immediately if idle,
        // or after finishing the currently cancelled batch if busy.
        if (_workReady.CurrentCount == 0)
            _workReady.Release();

        return Task.CompletedTask;
    }

    public void Disconnect()
    {
        CancelBatch();
        _active = false;
    }

    public void Dispose() => Disconnect();

    private void CancelBatch()
    {
        lock (_batchLock)
        {
            _batchCts?.Cancel();
            _batchCts?.Dispose();
            _batchCts = null;
        }
    }

    // ── Mouse injection (Faz 17) ──────────────────────────────────────────────

    /// <inheritdoc/>
    public Task MoveAbsAsync(int x, int y, CancellationToken ct = default)
    {
        _inputBuf ??= new INPUT[1];
        int screenW = GetSystemMetrics(SM_CXSCREEN);
        int screenH = GetSystemMetrics(SM_CYSCREEN);
        // MOUSEEVENTF_ABSOLUTE: dx/dy are in the range 0..65535
        int dx = (int)((x * 65535L) / (screenW > 0 ? screenW : 1920));
        int dy = (int)((y * 65535L) / (screenH > 0 ? screenH : 1080));

        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion
            {
                mi = new MOUSEINPUT
                {
                    dx       = dx,
                    dy       = dy,
                    dwFlags  = MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE,
                    time     = 0,
                    dwExtraInfo = 0,
                }
            }
        };
        SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    // ── ATOMIC 3-ADIM CLICK ──────────────────────────────────────────────────
    // JitBit'in "click" komutu (combined DOWN+UP veya MOVE|DOWN) KO tarafından yutuluyordu.
    // Sadece JitBit'in "mouseleftbuttondown" + "mouseleftbuttonup" (ayrı atomic event'ler) çalışıyor.
    // Kural: KO/SDL/ACME stack'i BAYRAKLARI BİRLEŞMİŞ event'leri reddediyor.
    // Çözüm: 3 tamamen bağımsız SendInput çağrısı → SAF MOVE, ardından SAF DOWN, ardından SAF UP.
    //   1) MOVE-only  (ABSOLUTE | MOVE,  dx/dy dolu, button flag YOK)
    //   2) DOWN-only  (sadece downFlag,  dx=0 dy=0)
    //   3) UP-only    (sadece upFlag,    dx=0 dy=0)
    // Thread.Sleep ile DOWN/UP AYNI thread'den gidiyor (await Task.Delay thread değiştirirdi).
    public Task ClickAsync(MouseButton btn = MouseButton.Left, CancellationToken ct = default)
    {
        _inputBuf ??= new INPUT[1];

        (uint downFlag, uint upFlag) = btn switch
        {
            MouseButton.Right  => (MOUSEEVENTF_RIGHTDOWN,  MOUSEEVENTF_RIGHTUP),
            MouseButton.Middle => (MOUSEEVENTF_MIDDLEDOWN, MOUSEEVENTF_MIDDLEUP),
            _                  => (MOUSEEVENTF_LEFTDOWN,   MOUSEEVENTF_LEFTUP),
        };

        // ── Adım 1: Saf MOVE — cursor'u koordinata çivile ─────────────────────
        // KO'nun 3D raycast/hover state'i imlecin gerçekten HAREKET ETTİĞİNİ görmek istiyor.
        var pt = new POINT();
        GetCursorPos(ref pt);
        int sw = GetSystemMetrics(SM_CXSCREEN);
        int sh = GetSystemMetrics(SM_CYSCREEN);
        int dx = (int)((pt.X * 65535L) / (sw > 0 ? sw : 1920));
        int dy = (int)((pt.Y * 65535L) / (sh > 0 ? sh : 1080));

        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion { mi = new MOUSEINPUT
            {
                dx = dx, dy = dy,
                dwFlags = MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_MOVE,
            }}
        };
        uint r1 = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (r1 == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[HomekoClick] MOVE SendInput failed: err={Marshal.GetLastWin32Error()}");

        Thread.Sleep(25); // raycast/hover state güncellenmesi için bekle

        // ── Adım 2: Saf DOWN — sadece button bayrak, koordinat YOK ───────────
        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion { mi = new MOUSEINPUT { dwFlags = downFlag } }
        };
        uint r2 = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (r2 == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[HomekoClick] DOWN SendInput failed: err={Marshal.GetLastWin32Error()}");

        Thread.Sleep(80); // KO tick — DOWN/UP aynı frame'e düşmemeli

        // ── Adım 3: Saf UP — sadece button bayrak ─────────────────────────────
        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion { mi = new MOUSEINPUT { dwFlags = upFlag } }
        };
        uint r3 = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (r3 == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[HomekoClick] UP SendInput failed: err={Marshal.GetLastWin32Error()}");

        return Task.CompletedTask;
    }

    // ── MouseDownAsync / MouseUpAsync / MoveRelAsync (Faz 19 — kamera drag) ───

    /// <inheritdoc/>
    // Tek atomic RIGHTDOWN — bayrak karışımı yok (ACME kuralı).
    public Task MouseDownAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
    {
        _inputBuf ??= new INPUT[1];
        uint downFlag = btn switch
        {
            MouseButton.Right  => MOUSEEVENTF_RIGHTDOWN,
            MouseButton.Middle => MOUSEEVENTF_MIDDLEDOWN,
            _                  => MOUSEEVENTF_LEFTDOWN,
        };
        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion { mi = new MOUSEINPUT { dwFlags = downFlag } }
        };
        uint r = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (r == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[HomekoClick] MouseDown({btn}) SendInput failed: err={Marshal.GetLastWin32Error()}");
        Thread.Sleep(20);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task MouseUpAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
    {
        _inputBuf ??= new INPUT[1];
        uint upFlag = btn switch
        {
            MouseButton.Right  => MOUSEEVENTF_RIGHTUP,
            MouseButton.Middle => MOUSEEVENTF_MIDDLEUP,
            _                  => MOUSEEVENTF_LEFTUP,
        };
        _inputBuf[0] = new INPUT
        {
            Type = INPUT_MOUSE,
            U    = new InputUnion { mi = new MOUSEINPUT { dwFlags = upFlag } }
        };
        uint r = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (r == 0)
            System.Diagnostics.Debug.WriteLine(
                $"[HomekoClick] MouseUp({btn}) SendInput failed: err={Marshal.GetLastWin32Error()}");
        Thread.Sleep(20);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    // Göreli hareket — ABSOLUTE flag YOK, buton flag YOK.
    // Büyük sıçramaları önlemek için 10'ar piksel adımlarla gönderir.
    public Task MoveRelAsync(int dx, int dy, CancellationToken ct = default)
    {
        _inputBuf ??= new INPUT[1];
        const int stepSize = 10;
        int stepsX = dx == 0 ? 0 : (int)Math.Ceiling(Math.Abs(dx) / (double)stepSize);
        int stepsY = dy == 0 ? 0 : (int)Math.Ceiling(Math.Abs(dy) / (double)stepSize);
        int totalSteps = Math.Max(stepsX, stepsY);
        if (totalSteps == 0) return Task.CompletedTask;

        float stepDx = (float)dx / totalSteps;
        float stepDy = (float)dy / totalSteps;
        float accumX = 0, accumY = 0;

        for (int i = 0; i < totalSteps; i++)
        {
            accumX += stepDx;
            accumY += stepDy;
            int sx = (int)Math.Round(accumX);
            int sy = (int)Math.Round(accumY);
            if (sx == 0 && sy == 0) continue;
            accumX -= sx;
            accumY -= sy;

            _inputBuf[0] = new INPUT
            {
                Type = INPUT_MOUSE,
                U    = new InputUnion { mi = new MOUSEINPUT
                {
                    dx      = sx,
                    dy      = sy,
                    dwFlags = MOUSEEVENTF_MOVE, // relative: ABSOLUTE flag yok
                }}
            };
            uint r = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
            if (r == 0)
                System.Diagnostics.Debug.WriteLine(
                    $"[HomekoClick] MoveRel step{i} SendInput failed: err={Marshal.GetLastWin32Error()}");
            Thread.Sleep(10);
        }
        return Task.CompletedTask;
    }

    // ── KeyDownAsync / KeyUpAsync (Faz 17 — FarmEngine tuş gönderimi) ─────────

    /// <inheritdoc/>
    public Task KeyDownAsync(string key, CancellationToken ct = default)
    {
        InjectKey(key, isDown: true);
        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task KeyUpAsync(string key, CancellationToken ct = default)
    {
        InjectKey(key, isDown: false);
        return Task.CompletedTask;
    }

    // ── Key injection ─────────────────────────────────────────────────────────

    private static void InjectKey(string keyName, bool isDown)
    {
        if (!KeyMap.TryGet(keyName, out var scan, out var extended)) return;

        uint flags = KEYEVENTF_SCANCODE;
        if (!isDown)  flags |= KEYEVENTF_KEYUP;
        if (extended) flags |= KEYEVENTF_EXTENDED;

        // Reuse the per-thread array to avoid a heap allocation per key event
        _inputBuf ??= new INPUT[1];
        _inputBuf[0] = new INPUT
        {
            Type = INPUT_KEYBOARD,
            U    = new InputUnion
            {
                ki = new KEYBDINPUT
                {
                    wVk         = 0,
                    wScan       = scan,
                    dwFlags     = flags,
                    time        = 0,
                    dwExtraInfo = 0,
                }
            }
        };
        // SendInput başarısızlığını logla (fare yolunda zaten var; tuş yolunda yoktu). Combo "2 düşmesi"
        // tanısı için: bu satır çıkarsa tuş OS'a gönderilemedi (odak/yük); çıkmazsa tuş gönderildi ama
        // oyun yutmuş olabilir (down+up aynı frame'e düşmesi → hold çok kısa) demektir.
        uint sent = SendInput(1, _inputBuf, Marshal.SizeOf<INPUT>());
        if (sent == 0)
            Program.Log($"[Local] Tuş SendInput BAŞARISIZ: '{keyName}' {(isDown ? "down" : "up")} " +
                        $"err={Marshal.GetLastWin32Error()}");
    }
}
