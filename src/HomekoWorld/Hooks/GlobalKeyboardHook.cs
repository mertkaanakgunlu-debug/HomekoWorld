using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Windows.Input;
using HomekoWorld.Core;

namespace HomekoWorld.Hooks;

public sealed class GlobalKeyboardHook : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WM_KEYDOWN     = 0x0100;
    private const int WM_KEYUP       = 0x0101;
    private const int WM_SYSKEYDOWN  = 0x0104;
    private const int WM_SYSKEYUP    = 0x0105;

    private nint _hook;
    private readonly LowLevelKeyboardProc _proc;

    // ── Adanmış pompa thread'i (UI thread'inden izolasyon — bkz. HookMessageLoop) ──
    private Thread?      _pumpThread;
    private volatile uint _pumpThreadId;
    private volatile bool _running;
    private readonly ManualResetEventSlim _installed = new(false);

    public event EventHandler<HookKeyEventArgs>? KeyDown;
    public event EventHandler<HookKeyEventArgs>? KeyUp;

    public GlobalKeyboardHook()
    {
        _proc = HookCallback;
    }

    /// <summary>
    /// Hook'u kendi mesaj-pompalı arka plan thread'inde kurar. Callback'ler bu thread'de
    /// çalışır → UI thread yükünden bağımsız (kesintisiz kill-switch / akıcı mouse).
    /// </summary>
    public void Start()
    {
        if (_running) return;
        _running = true;
        _installed.Reset();
        _pumpThread = new Thread(PumpThreadProc)
        {
            IsBackground = true,
            Name         = "HomekoKeyboardHookPump",
            Priority     = ThreadPriority.AboveNormal, // yoğun yükte aç kalmasın → kesintisiz kill-switch
        };
        _pumpThread.Start();
        _installed.Wait(2000); // hook kurulana kadar kısa bekle (caller askıda kalmasın)
    }

    private void PumpThreadProc()
    {
        _pumpThreadId = HookMessageLoop.CurrentThreadId();
        nint hook = 0;
        int  err  = 0;
        try
        {
            using var module = Process.GetCurrentProcess().MainModule!;
            hook = SetWindowsHookEx(WH_KEYBOARD_LL, _proc, GetModuleHandle(module.ModuleName), 0);
            if (hook == 0) err = Marshal.GetLastWin32Error();
            _hook = hook; // callback'in CallNextHookEx'i için paylaşılan alan
        }
        catch { hook = 0; }
        finally { _installed.Set(); }

        if (hook == 0)
        {
            HomekoWorld.Program.Log($"[Hook] Keyboard hook kurulamadı (err={err})");
            _running = false;
            return;
        }

        // Mesaj döngüsü: LL hook callback'leri burada dispatch edilir; WM_QUIT ile çıkar.
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

    private nint HookCallback(int nCode, nint wParam, ref KBDLLHOOKSTRUCT lParam)
    {
        if (nCode >= 0)
        {
            var key   = KeyInterop.KeyFromVirtualKey(lParam.vkCode);
            var mods  = GetCurrentModifiers();
            var args  = new HookKeyEventArgs(key, mods);

            if (wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN)
                KeyDown?.Invoke(this, args);
            else if (wParam == WM_KEYUP || wParam == WM_SYSKEYUP)
                KeyUp?.Invoke(this, args);

            if (args.Handled)
                return 1;
        }
        return CallNextHookEx(_hook, nCode, wParam, ref lParam);
    }

    private static KeyboardModifiers GetCurrentModifiers()
    {
        var mods = KeyboardModifiers.None;
        if ((GetAsyncKeyState(0x10) & 0x8000) != 0) mods |= KeyboardModifiers.Shift;
        if ((GetAsyncKeyState(0x11) & 0x8000) != 0) mods |= KeyboardModifiers.Ctrl;
        if ((GetAsyncKeyState(0x12) & 0x8000) != 0) mods |= KeyboardModifiers.Alt;
        return mods;
    }

    public void Dispose() => Stop();

    // ---- P/Invoke ----
    private delegate nint LowLevelKeyboardProc(int nCode, nint wParam, ref KBDLLHOOKSTRUCT lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public int vkCode, scanCode, flags, time;
        public nint dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern nint SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, nint hMod, uint dwThreadId);

    [DllImport("user32.dll")]
    private static extern bool UnhookWindowsHookEx(nint hhk);

    [DllImport("user32.dll")]
    private static extern nint CallNextHookEx(nint hhk, int nCode, nint wParam, ref KBDLLHOOKSTRUCT lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern nint GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern short GetAsyncKeyState(int vKey);
}

public class HookKeyEventArgs : EventArgs
{
    public Key Key { get; }
    public KeyboardModifiers Modifiers { get; }
    public bool Handled { get; set; }

    public HookKeyEventArgs(Key key, KeyboardModifiers modifiers)
    {
        Key = key;
        Modifiers = modifiers;
    }
}
