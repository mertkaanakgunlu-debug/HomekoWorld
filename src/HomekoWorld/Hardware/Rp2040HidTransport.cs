using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using HidSharp;

namespace HomekoWorld.Hardware;

/// <summary>
/// RP2040 Zero USB-HID köprüsü transport'u. <see cref="IKeyDeviceTransport"/>'u
/// vendor-defined HID kanalı (Usage Page 0xFF00, report ID 3) üstünden uygular.
///
/// - Klavye/fare gerçek HID olarak OS'a gider (cihaz emülasyonu); komutlar yalnız
///   vendor koleksiyonundan akar (klavye/fare koleksiyonları Windows'ta açılamaz).
/// - Combo'lar (<see cref="SendRawAsync"/>) cihaz üstü scheduler'a BATCH olarak
///   gönderilir; tekil farm girdileri anlık opcode'lar.
/// - Fare MUTLAK konumlandırma <see cref="MoveAbsAsync"/>: relative HID + PC tarafı
///   closed-loop homing (GetCursorPos geri beslemesi) → her çözünürlük/monitör/DPI.
///
/// Protokol sözleşmesi: <see cref="Rp2040Protocol"/> (firmware aynası protocol.h).
/// </summary>
public sealed class Rp2040HidTransport : IKeyDeviceTransport
{
    // ── Tanı log dosyası (publish-single/rp2040.log) ─────────────────────
    private static readonly string _logPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "rp2040.log");
    private static void TLog(string msg)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}";
            var fi   = new System.IO.FileInfo(_logPath);
            if (fi.Exists && fi.Length > 256 * 1024)
                System.IO.File.WriteAllText(_logPath, line);   // >256 KB → sıfırla
            else
                System.IO.File.AppendAllText(_logPath, line);
        }
        catch { }
    }

    // ── Cihaz kimliği (PLACEHOLDER) ───────────────────────────────────────
    // Faz 1/9'da kullanıcının gerçek klavye/fare descriptor'ı dökülüp jenerik-
    // zararsız bir kimlikle finalize edilecek. Firmware ile AYNI olmalı.
    // Faz 9: Logitech Unifying Receiver C52B taklidi (firmware usb_descriptors.c ile AYNI).
    // Ayırt edici: vendor koleksiyonu 64B in/out — gerçek C52B'nin HID++ koleksiyonları
    // <64B olduğundan, aynı VID/PID'li gerçek receiver takılı olsa bile keşif bizimkini bulur.
    public const int VendorId  = 0xCafe;   // RP2040 generic test VID
    public const int ProductId = 0x4000;   // HID Bridge

    private readonly object _writeLock = new();
    private HidStream? _stream;

    // ── Stale-CANCEL önleme ────────────────────────────────────────────────
    // ComboEngine.ExecuteAsync.finally her combo sonunda CANCEL yollar (Android için).
    // RP2040'da bu "stale CANCEL", yeni batch'in BATCH_END'inden hemen sonra cihaza
    // ulaşıp batch'i KEY_DOWN/UP arasında keser → oyun tuş takılı sanır → hareketsizlik.
    //
    // Çözüm:
    //   - SendLineAsync("CANCEL") → CANCEL'ı hemen göndermez, 30ms geciktirir.
    //   - SendRawAsync → gecikmeyi iptal eder + batch içine kendi CANCEL'ını prepend eder.
    // Böylece stale CANCEL hiç ulaşmaz; loop combo'lar için (yeni batch gelmez) 30ms'de gerçek CANCEL ateşlenir.
    private CancellationTokenSource? _pendingCancelCts;
    private readonly object _cancelLock = new();
    private int _outLen = Rp2040Protocol.VendorReportSize;

    private volatile bool _connected;
    private bool _userClosing;
    private int  _disconnectedRaised;

    // ── PONG watchdog: stale handle tespiti ───────────────────────────────
    // USB Bus Reset sonra Windows eski HID handle'ını geçersiz kılar.
    // HidSharp Write() hata atmadan "başarılı" döner ama veri firmwear'e ulaşmaz;
    // reader ise TimeoutException loop'unda takılır (IOException değil).
    // Çözüm: 5s içinde PONG gelmezse handle stale demektir → zorla yeniden bağlan.
    private const  long PongWatchdogMs = 2_000;   // 2s = ~2 missed heartbeat — stale handle tespiti
    private long _lastPongTickMs;
    private System.Threading.Timer? _pongWatchdog;

    private Thread? _reader;
    private CancellationTokenSource? _readerCts;
    private System.Threading.Timer? _heartbeat;

    // Anlık (batch dışı) klavye modifier durumu.
    private byte _modMask;

    // Mouse acceleration ("Enhance pointer precision") relative homing'i bozar
    // (test: accel açık 159px sapma, kapalı 0-1px). İlk MoveAbs'te kapatılır,
    // Disconnect'te geri yüklenir. (Local mod absolute SendInput → ballistics
    // baypası olduğu için bu sorunu hiç yaşamadı.)
    private int[]? _savedAccel;

    // PING/PONG latency.
    private int  _pingSeq;
    private ushort _pingNonce;
    private long _pingStartTs;
    private TaskCompletionSource<bool>? _pongTcs;

    public bool IsConnected => _connected;

    public event EventHandler<string>? LineReceived;
    public event EventHandler<string>? Disconnected;
    public event EventHandler<long>?   LatencyMs;

    // ── Bağlantı ──────────────────────────────────────────────────────────
    public async Task<bool> ConnectAsync(string host, int port, CancellationToken ct = default)
    {
        TLog("CONNECT_START");
        Disconnect();              // temiz başlangıç
        _userClosing = false;
        _disconnectedRaised = 0;

        var dev = DeviceList.Local
            .GetHidDevices(VendorId, ProductId)
            .FirstOrDefault(d => SafeOutLen(d) >= Rp2040Protocol.VendorReportSize
                              && SafeInLen(d)  >= Rp2040Protocol.VendorReportSize);
        if (dev is null) { TLog("DEVICE_NOT_FOUND"); return false; }
        TLog($"DEVICE_FOUND outLen={SafeOutLen(dev)} inLen={SafeInLen(dev)}");
        if (!dev.TryOpen(out var stream)) { TLog("STREAM_OPEN_FAILED"); return false; }   // vendor değilse / kilitliyse atla

        _stream = stream;
        _outLen = Math.Max(Rp2040Protocol.VendorReportSize, SafeOutLen(dev));
        try { stream.ReadTimeout = 2000; } catch { /* bazı backend'ler desteklemez */ }

        _readerCts = new CancellationTokenSource();
        var rc = _readerCts.Token;
        _reader = new Thread(() => ReaderLoop(rc)) { IsBackground = true, Name = "Rp2040Reader" };
        _reader.Start();

        // El sıkışma: PING → PONG (≤2 s).
        _pongTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        TLog("PING_SENT (handshake)");
        SendPing();
        var done = await Task.WhenAny(_pongTcs.Task, Task.Delay(2000, ct)).ConfigureAwait(false);
        if (done != _pongTcs.Task)
        {
            TLog("PONG_TIMEOUT → connect failed");
            Disconnect();
            return false;
        }

        TLog("PONG_OK → connected");
        _connected       = true;
        _lastPongTickMs  = Environment.TickCount64;   // watchdog baseline sıfırla
        _heartbeat       = new System.Threading.Timer(_ => { if (_connected) SendPing(); }, null, 1000, 1000);
        _pongWatchdog    = new System.Threading.Timer(_ =>
        {
            if (_connected && Environment.TickCount64 - _lastPongTickMs > PongWatchdogMs)
            {
                TLog("PONG_WATCHDOG: stale handle (5s PONG yok) — yeniden bağlan");
                RaiseDisconnected();
            }
        }, null, PongWatchdogMs, PongWatchdogMs);
        return true;
    }

    public void Disconnect()
    {
        _userClosing = true;
        try { if (_stream != null && _connected) WriteOut(NewOut(Rp2040Protocol.OpReleaseAll)); } catch { }

        _heartbeat?.Dispose();    _heartbeat    = null;
        _pongWatchdog?.Dispose(); _pongWatchdog = null;
        _readerCts?.Cancel();

        var s = _stream; _stream = null;
        try { s?.Close(); } catch { }
        try { s?.Dispose(); } catch { }

        lock (_cancelLock) { _pendingCancelCts?.Cancel(); _pendingCancelCts = null; }
        RestoreAcceleration();
        _connected = false;
        _modMask   = 0;
    }

    public void Dispose() => Disconnect();

    /// <summary>
    /// Cihazı BOOTSEL (RPI-RP2 mass-storage) moduna sokar → app-içi firmware güncelleme.
    /// Komuttan sonra cihaz USB'den düşer, RPI-RP2 sürücüsü olarak yeniden çıkar; .uf2 kopyalanır.
    /// (Firmware'de OP_REBOOT_BOOTSEL → reset_usb_boot.)
    /// </summary>
    public Task RebootToBootloaderAsync()
    {
        WriteOut(NewOut(Rp2040Protocol.OpRebootBootsel));
        return Task.CompletedTask;
    }

    // ── Reader thread (cihaz → PC IN report) ──────────────────────────────
    private void ReaderLoop(CancellationToken ct)
    {
        var s = _stream;
        if (s is null) return;
        var buf = new byte[Math.Max(Rp2040Protocol.VendorReportSize, SafeInLenStream(s))];
        while (!ct.IsCancellationRequested)
        {
            int n;
            try { n = s.Read(buf, 0, buf.Length); }
            catch (TimeoutException)        { continue; }   // veri yok → normal
            catch (ObjectDisposedException) { TLog("READER_DISPOSED"); break; }
            catch (System.IO.IOException e) { TLog($"READER_IOEXCEPTION: {e.Message}"); break; }
            catch (Exception ex) when (ex.Message.Contains("timed out",
                                       StringComparison.OrdinalIgnoreCase)) { continue; }
            catch (Exception ex)            { TLog($"READER_ERROR: {ex.Message}"); break; }
            if (n < 2 || buf[0] != Rp2040Protocol.RidVendor) continue;
            // HandleIn'den gelen exception reader'ı öldürmemeli — transport güvenilirliği.
            try { HandleIn(buf); }
            catch { /* olay işleyici hatası — log yok, reader devam */ }
        }
        if (!_userClosing) RaiseDisconnected();
    }

    private void HandleIn(byte[] buf)
    {
        switch (buf[1])
        {
            case Rp2040Protocol.EvPong:
            {
                ushort nonce = (ushort)(buf[2] | (buf[3] << 8));
                _lastPongTickMs = Environment.TickCount64;   // watchdog: son PONG zamanı
                if (nonce == _pingNonce)
                {
                    long el = (Stopwatch.GetTimestamp() - _pingStartTs) * 1000L / Stopwatch.Frequency;
                    LatencyMs?.Invoke(this, el);
                    if (nonce % 10 == 1) TLog($"PONG_RX nonce={nonce} latency={el}ms");   // her 10'unda bir
                }
                _pongTcs?.TrySetResult(true);
                LineReceived?.Invoke(this, HidBridgeProtocol.Pong);
                break;
            }
            case Rp2040Protocol.EvStatus:
                LineReceived?.Invoke(this, $"STATUS:{buf[2]}:{buf[3]}");
                break;
            case Rp2040Protocol.EvAck:
                LineReceived?.Invoke(this, $"ACK:{buf[2]}");
                break;
        }
    }

    private void RaiseDisconnected()
    {
        if (Interlocked.Exchange(ref _disconnectedRaised, 1) != 0) return;
        TLog("DISCONNECTED → raising event");
        _connected = false;
        Disconnected?.Invoke(this, "rp2040");
    }

    // ── Tekil komutlar (immediate) ─────────────────────────────────────────
    public Task SendLineAsync(string line, CancellationToken ct = default)
    {
        line = line.Trim();
        if (line == HidBridgeProtocol.Ping)        SendPing();
        else if (line == HidBridgeProtocol.Cancel) ScheduleCancel();
        else if (line.StartsWith("KEYDOWN:", StringComparison.OrdinalIgnoreCase)) return KeyDownAsync(line[8..], ct);
        else if (line.StartsWith("KEYUP:",   StringComparison.OrdinalIgnoreCase)) return KeyUpAsync(line[6..], ct);
        return Task.CompletedTask;
    }

    public Task KeyDownAsync(string key, CancellationToken ct = default)
    {
        if (_stream is null) return Task.CompletedTask;
        if (HidUsage.IsModifier(key))
        {
            _modMask |= HidUsage.ModifierBit(key);
            SendMod(_modMask);
        }
        else if (HidUsage.TryResolve(key, out var u))
        {
            var b = NewOut(Rp2040Protocol.OpKeyDown); b[2] = u; WriteOut(b);
        }
        return Task.CompletedTask;
    }

    public Task KeyUpAsync(string key, CancellationToken ct = default)
    {
        if (_stream is null) return Task.CompletedTask;
        if (HidUsage.IsModifier(key))
        {
            _modMask &= (byte)~HidUsage.ModifierBit(key);
            SendMod(_modMask);
        }
        else if (HidUsage.TryResolve(key, out var u))
        {
            var b = NewOut(Rp2040Protocol.OpKeyUp); b[2] = u; WriteOut(b);
        }
        return Task.CompletedTask;
    }

    // ── Fare ────────────────────────────────────────────────────────────────

    /// <summary>14.tur (Faz 3.3): true = tek büyük relative-delta + doğrulama (yeni, varsayılan);
    /// false = yalnız eski 120px-adım/2ms servo döngüsü (geri-alma — FarmSettings.AtomicMouseMove'dan
    /// TransportRouter üzerinden set edilir, farm Start()'ta).</summary>
    public bool AtomicMouseMove { get; set; } = true;

    /// <summary>
    /// Fareyi ekran pikseline taşır — relative HID + closed-loop homing.
    /// Gerçek imleci (GetCursorPos) hedefe servolar → her çözünürlük/DPI/monitör,
    /// pointer-acceleration'a dayanıklı (geri besleme yakınsar).
    /// </summary>
    public async Task MoveAbsAsync(int x, int y, CancellationToken ct = default)
    {
        // _connected yerine _stream null kontrolü: _connected yanlış-false olursa
        // mouse sessizce çalışmayı bırakır ama klavye (_stream üstünden) çalışmaya devam eder.
        // _stream gerçek bağlantı durumunu yansıtır (Disconnect() null'a atar).
        if (_stream is null) return;
        if (_savedAccel is null) DisableAcceleration();   // ilk harekette accel'i kapat
        const int tol = 2, maxStep = 120, maxIters = 40;

        // 14.tur (Faz 3.3): protokol ZATEN tek raporda i16 delta taşıyor ve firmware >127'yi kendi
        // 1ms HID raporlarına bölüyor (bkz SendMouseRel yorumu) — eski 120px/2ms PC-tarafı servo
        // döngüsü firmware'in zaten yaptığı işi tekrarlıyordu (dış denetim tespiti). Kapalı-döngü
        // son-doğrulama KORUNUR (DPI/pointer-acceleration/müşteri-PC dayanıklılığı için tek atımlık
        // "gönder ve umut et" YETERSİZ) — yalnız normal yolda gereksiz ara-adımlar elenir.
        if (AtomicMouseMove && await TryAtomicMoveAsync(x, y, tol, ct).ConfigureAwait(false))
            return;

        for (int i = 0; i < maxIters && !ct.IsCancellationRequested; i++)
        {
            if (!GetCursorPos(out var p)) break;
            int dx = x - p.X, dy = y - p.Y;
            if (Math.Abs(dx) <= tol && Math.Abs(dy) <= tol) return;
            SendMouseRel(Math.Clamp(dx, -maxStep, maxStep), Math.Clamp(dy, -maxStep, maxStep));
            try { await Task.Delay(2, ct).ConfigureAwait(false); }   // imleç güncellensin
            catch (OperationCanceledException) { return; }
        }
    }

    /// <summary>Tek tam-delta raporu + tahmini bekleme + doğrulama, en fazla 2 düzeltme adımıyla.
    /// Döner: true = hedefe ulaşıldı (tol içinde) VEYA iptal edildi (çağıran zaten dönecek);
    /// false = 2 düzeltmeden sonra hâlâ tol dışında → çağıran eski servo döngüsüne düşer (güvenlik ağı).</summary>
    private async Task<bool> TryAtomicMoveAsync(int x, int y, int tol, CancellationToken ct)
    {
        if (!GetCursorPos(out var p0)) return false;
        int dx = x - p0.X, dy = y - p0.Y;
        if (Math.Abs(dx) <= tol && Math.Abs(dy) <= tol) return true;   // zaten hedefte

        for (int correction = 0; correction <= 2; correction++)
        {
            SendMouseRel(dx, dy);   // firmware >127'yi 1ms HID raporlarına parçalar
            int maxAxis = Math.Max(Math.Abs(dx), Math.Abs(dy));
            int reports = (int)Math.Ceiling(maxAxis / 127.0);
            int waitMs  = Math.Max(3, (int)Math.Ceiling(reports * 1.5) + 2); // rapor-başı ~1.5ms + gönderim payı

            try { await Task.Delay(waitMs, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { return true; }   // iptal — çağıran zaten dönecek

            if (!GetCursorPos(out var p)) return false;
            dx = x - p.X; dy = y - p.Y;
            if (Math.Abs(dx) <= tol && Math.Abs(dy) <= tol) return true;
        }
        return false;
    }

    public Task MoveRelAsync(int dx, int dy, CancellationToken ct = default)
    {
        SendMouseRel(dx, dy);   // firmware >127'yi parçalar
        return Task.CompletedTask;
    }

    public async Task ClickAsync(MouseButton btn = MouseButton.Left, CancellationToken ct = default)
    {
        byte bit = Rp2040Protocol.ButtonBit(btn);
        SendMouseBtn(bit, 1);
        try { await Task.Delay(20, ct).ConfigureAwait(false); } catch (OperationCanceledException) { }
        SendMouseBtn(bit, 0);
    }

    public Task MouseDownAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
    {
        SendMouseBtn(Rp2040Protocol.ButtonBit(btn), 1);
        return Task.CompletedTask;
    }

    public Task MouseUpAsync(MouseButton btn = MouseButton.Right, CancellationToken ct = default)
    {
        SendMouseBtn(Rp2040Protocol.ButtonBit(btn), 0);
        return Task.CompletedTask;
    }

    // ── Combo batch (cihaz üstü scheduler) ────────────────────────────────
    /// <summary>
    /// EXEC…ENDEXEC text batch'ini cihaza gönderir.
    /// KRİTİK: tüm raporlar (BEGIN + EVT'ler + END) önce listeye toplanır, sonra tek
    /// _writeLock içinde gönderilir. Böylece başka bir thread'in CANCEL raporu
    /// BATCH_BEGIN ile BATCH_END arasına giremez; girince firmware batch_cancel()
    /// çağırır ve BATCH_END geldiğinde batch_count==0 → kombo sessizce kaybolur.
    /// </summary>
    public Task SendRawAsync(string payload, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(payload)) return Task.CompletedTask;

        var firstLine = payload.Split('\n', 2)[0].Trim();
        bool isLoop = firstLine == HidBridgeProtocol.ExecLoop;

        var events = BatchParser.Parse(payload);   // sıralı (ms) KEYDOWN/KEYUP

        // ─── Stale CANCEL'ı iptal et: yeni batch başlarken eski finally CANCEL'ı bastır ──
        lock (_cancelLock)
        {
            _pendingCancelCts?.Cancel();
            _pendingCancelCts = null;
        }

        // ─── Adım 1: tüm raporları listeye topla ────────────────────────────
        var pending = new List<byte[]>(events.Count / Rp2040Protocol.VendorMaxEvtsPerReport + 4);

        // CANCEL prepend: cihazda önceden çalan batch varsa atomik olarak durdur.
        pending.Add(NewOut(Rp2040Protocol.OpCancel));

        // BEGIN
        var begin = NewOut(Rp2040Protocol.OpBatchBegin);
        begin[2] = isLoop ? Rp2040Protocol.BatchFlagLoop : (byte)0;
        pending.Add(begin);

        byte runningMask = 0;
        var report = NewOut(Rp2040Protocol.OpBatchEvt);
        int count = 0, idx = 3;

        void Flush()
        {
            if (count == 0) return;
            report[2] = (byte)count;
            pending.Add(report);                   // WriteOut yerine listeye ekle
            report = NewOut(Rp2040Protocol.OpBatchEvt);
            count = 0; idx = 3;
        }

        void Emit(int ms, byte type, byte code)
        {
            if (count == Rp2040Protocol.VendorMaxEvtsPerReport) Flush();
            ushort t = (ushort)Math.Clamp(ms, 0, ushort.MaxValue);
            report[idx++] = (byte)(t & 0xFF);
            report[idx++] = (byte)((t >> 8) & 0xFF);
            report[idx++] = type;
            report[idx++] = code;
            count++;
        }

        foreach (var e in events)
        {
            if (HidUsage.IsModifier(e.Key))
            {
                byte bit = HidUsage.ModifierBit(e.Key);
                runningMask = e.IsDown ? (byte)(runningMask | bit) : (byte)(runningMask & ~bit);
                Emit(e.Ms, Rp2040Protocol.EvtMod, runningMask);
            }
            else if (HidUsage.TryResolve(e.Key, out var u))
            {
                Emit(e.Ms, e.IsDown ? Rp2040Protocol.EvtKeyDown : Rp2040Protocol.EvtKeyUp, u);
            }
        }
        Flush();

        // END
        pending.Add(NewOut(Rp2040Protocol.OpBatchEnd));

        // ─── Adım 2: hepsini tek lock altında atomik gönder ─────────────────
        WriteAll(pending);
        return Task.CompletedTask;
    }

    /// <summary>
    /// CANCEL'ı 30ms geciktirerek gönderir. Bu süre içinde SendRawAsync çağrılırsa
    /// CANCEL iptal edilir (stale finally CANCEL → yeni batch'i kesmez).
    /// Loop combo'larda yeni batch gelmediği için 30ms'de gerçek CANCEL ateşlenir.
    /// </summary>
    private void ScheduleCancel()
    {
        lock (_cancelLock)
        {
            _pendingCancelCts?.Cancel();
            var cts = new CancellationTokenSource();
            _pendingCancelCts = cts;
            _ = Task.Delay(30).ContinueWith(_ =>
            {
                if (!cts.IsCancellationRequested)
                    WriteOut(NewOut(Rp2040Protocol.OpCancel));
            }, TaskContinuationOptions.ExecuteSynchronously);
        }
    }

    /// <summary>
    /// Birden fazla raporu tek _writeLock içinde gönderir — araya başka write giremez.
    /// </summary>
    private void WriteAll(List<byte[]> reports)
    {
        var s = _stream;
        if (s is null) return;
        lock (_writeLock)
        {
            foreach (var r in reports)
            {
                try { s.Write(r, 0, r.Length); }
                catch { if (!_userClosing) RaiseDisconnected(); return; }
            }
        }
    }

    // ── Düşük seviye yardımcılar ──────────────────────────────────────────
    private byte[] NewOut(byte opcode)
    {
        var b = new byte[_outLen];
        b[0] = Rp2040Protocol.RidVendor;
        b[1] = opcode;
        return b;
    }

    private void WriteOut(byte[] b)
    {
        var s = _stream;
        if (s is null) return;
        lock (_writeLock)
        {
            try { s.Write(b, 0, b.Length); }
            catch { if (!_userClosing) RaiseDisconnected(); }
        }
    }

    private void SendMod(byte mask)
    {
        var b = NewOut(Rp2040Protocol.OpMod); b[2] = mask; WriteOut(b);
    }

    private void SendMouseRel(int dx, int dy)
    {
        var b = NewOut(Rp2040Protocol.OpMouseRel);
        short sdx = (short)Math.Clamp(dx, short.MinValue, short.MaxValue);
        short sdy = (short)Math.Clamp(dy, short.MinValue, short.MaxValue);
        b[2] = (byte)(sdx & 0xFF); b[3] = (byte)((sdx >> 8) & 0xFF);
        b[4] = (byte)(sdy & 0xFF); b[5] = (byte)((sdy >> 8) & 0xFF);
        WriteOut(b);
    }

    private void SendMouseBtn(byte mask, byte state)
    {
        var b = NewOut(Rp2040Protocol.OpMouseBtn); b[2] = mask; b[3] = state; WriteOut(b);
    }

    private void SendPing()
    {
        ushort nonce = unchecked((ushort)Interlocked.Increment(ref _pingSeq));
        _pingNonce   = nonce;
        _pingStartTs = Stopwatch.GetTimestamp();
        var b = NewOut(Rp2040Protocol.OpPing);
        b[2] = (byte)(nonce & 0xFF); b[3] = (byte)((nonce >> 8) & 0xFF);
        WriteOut(b);
    }

    private static int SafeOutLen(HidDevice d) { try { return d.GetMaxOutputReportLength(); } catch { return 0; } }
    private static int SafeInLen (HidDevice d) { try { return d.GetMaxInputReportLength();  } catch { return 0; } }
    private static int SafeInLenStream(HidStream s) { try { return s.Device.GetMaxInputReportLength(); } catch { return Rp2040Protocol.VendorReportSize; } }

    // ── Win32: imleç konumu (homing geri beslemesi) ───────────────────────
    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetCursorPos(out POINT lpPoint);

    // ── Mouse acceleration aç/kapa (homing kararlılığı için) ──────────────
    private const uint SPI_GETMOUSE = 0x0003, SPI_SETMOUSE = 0x0004, SPIF_SENDCHANGE = 0x02;

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SystemParametersInfo(uint uiAction, uint uiParam, [In, Out] int[] pvParam, uint fWinIni);

    private void DisableAcceleration()
    {
        try
        {
            var cur = new int[3];
            if (!SystemParametersInfo(SPI_GETMOUSE, 0, cur, 0)) return;
            _savedAccel = cur;
            if (cur[2] != 0)   // accel açık → kapat (threshold/threshold/enable = 0,0,0)
                SystemParametersInfo(SPI_SETMOUSE, 0, new[] { 0, 0, 0 }, SPIF_SENDCHANGE);
        }
        catch { }
    }

    private void RestoreAcceleration()
    {
        try
        {
            if (_savedAccel is not null)
            {
                SystemParametersInfo(SPI_SETMOUSE, 0, _savedAccel, SPIF_SENDCHANGE);
                _savedAccel = null;
            }
        }
        catch { }
    }
}
