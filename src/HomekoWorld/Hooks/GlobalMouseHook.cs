using System.Diagnostics;
using System.Drawing;
using System.Runtime.InteropServices;

namespace HomekoWorld.Hooks;

public sealed class GlobalMouseHook : IDisposable
{
    private const int WH_MOUSE_LL    = 14;
    private const int WM_LBUTTONDOWN = 0x0201;

    private nint _hook;
    private readonly LowLevelMouseProc _proc;

    // ── Adanmış pompa thread'i (UI thread'inden izolasyon — bkz. HookMessageLoop) ──
    private Thread?      _pumpThread;
    private volatile uint _pumpThreadId;
    private volatile bool _running;
    private readonly ManualResetEventSlim _installed = new(false);

    public event EventHandler<Point>? MouseDown;

    public GlobalMouseHook() => _proc = HookCallback;

    /// <summary>
    /// Hook'u kendi mesaj-pompalı arka plan thread'inde kurar. Callback'ler bu thread'de
    /// çalışır → UI thread yükünden bağımsız (UI donsa bile mouse akıcı kalır).
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _installed.Reset();
        _pumpThread = new Thread(PumpThreadProc)
        {
            IsBackground = true,
            Name         = "HomekoMouseHookPump",
            Priority     = ThreadPriority.AboveNormal, // yoğun yükte aç kalmasın → akıcı mouse
        };
        _pumpThread.Start();
        _installed.Wait(2000);
    }

    private void PumpThreadProc()
    {
        _pumpThreadId = HookMessageLoop.CurrentThreadId();
        nint hook = 0;
        int  err  = 0;
        try
        {
            using var module = Process.GetCurrentProcess().MainModule!;
            hook = SetWindowsHookEx(WH_MOUSE_LL, _proc, GetModuleHandle(module.ModuleName), 0);
            if (hook == 0) err = Marshal.GetLastWin32Error();
            _hook = hook; // callback'in CallNextHookEx'i için paylaşılan alan
        }
        catch { hook = 0; }
        finally { _installed.Set(); }

        if (hook == 0)
        {
            HomekoWorld.Program.Log($"[Hook] Mouse hook kurulamadı (err={err})");
            _running = false;
            return;
        }

        HookMessageLoop.Run();

        // Bu thread'in KENDİ handle'ını kaldır (hızlı Stop/Start'ta yeni handle'ı silmemek için).
        UnhookWindowsHookEx(hook);
        if (_hook == hook) _hook = 0;
    }

    public void Stop()
    {
        if (!_running) return;
        _running = false;
        HookMessageLoop.Quit(_pumpThreadId);
        _pumpThreadId = 0;
        _pumpThread   = null;
    }

    private nint HookCallback(int nCode, nint wParam, ref MSLLHOOKSTRUCT lParam)
    {
        // LLMHF_INJECTED (bit 0): kendi SendInput çıkışlarımızı yoksay.
        // Biz sentetik event gönderdiğimizde bu hook tetiklenir; ama WH_MOUSE_LL
        // varlığının sentetik click'i etkilememesi için injected event'lerde
        // callback işlemini atlayıp sadece CallNextHookEx ile zinciri ilerletiyoruz.
        if (nCode < 0 || (lParam.flags & 0x1u) != 0)
            return CallNextHookEx(_hook, nCode, wParam, ref lParam);

        if (wParam == WM_LBUTTONDOWN)
            MouseDown?.Invoke(this, new Point(lParam.pt.x, lParam.pt.y));

        return CallNextHookEx(_hook, nCode, wParam, ref lParam);
    }

    public void Dispose() => Stop();

    // ---- P/Invoke ----
    private delegate nint LowLevelMouseProc(int nCode, nint wParam, ref MSLLHOOKSTRUCT lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public POINT pt;
        public uint  mouseData, flags, time;
        public nint  dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelMouseProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, ref MSLLHOOKSTRUCT lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);
}
