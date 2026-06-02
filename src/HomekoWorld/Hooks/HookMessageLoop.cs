using System.Runtime.InteropServices;

namespace HomekoWorld.Hooks;

/// <summary>
/// Düşük seviye (WH_*_LL) hook'lar için Win32 mesaj döngüsü yardımcıları.
///
/// KRİTİK: WH_KEYBOARD_LL / WH_MOUSE_LL callback'leri, hook'u KURAN thread'in mesaj
/// kuyruğunda işlenir; o thread mesaj pompalamazsa callback gecikir/düşer. Eskiden hook'lar
/// UI thread'inde kuruluyordu → UI dolunca/donunca callback'ler LowLevelHooksTimeout (~300ms)
/// içinde dönemiyor, Windows tüm sistemin mouse'unu kesintili yapıp tuşları (kill-switch dahil)
/// düşürüyordu. Çözüm: her hook kendi adanmış arka plan thread'inde kurulur ve burada pompalanır
/// → hook gecikmesi UI/render yükünden TAMAMEN izole.
/// </summary>
internal static class HookMessageLoop
{
    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public nint hwnd;
        public uint message;
        public nint wParam;
        public nint lParam;
        public uint time;
        public int  ptX;
        public int  ptY;
    }

    private const uint WM_QUIT = 0x0012;

    [DllImport("user32.dll")]
    private static extern int GetMessage(out MSG lpMsg, nint hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage(ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern nint DispatchMessage(ref MSG lpMsg);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool PostThreadMessage(uint idThread, uint Msg, nint wParam, nint lParam);

    [DllImport("kernel32.dll")]
    private static extern uint GetCurrentThreadId();

    /// <summary>Çalışan pompa thread'inin Win32 thread id'si (Quit için saklanır).</summary>
    public static uint CurrentThreadId() => GetCurrentThreadId();

    /// <summary>
    /// Bu thread'i WM_QUIT gelene kadar pompalar. Hook callback'leri bu çağrı sırasında
    /// dispatch edilir. WM_QUIT (PostThreadMessage ile) gelince temiz çıkar.
    /// </summary>
    public static void Run()
    {
        // GetMessage: >0 normal mesaj, 0 = WM_QUIT, -1 = hata
        while (GetMessage(out var msg, nint.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }
    }

    /// <summary>Verilen pompa thread'ine WM_QUIT göndererek Run() döngüsünü sonlandırır.</summary>
    public static void Quit(uint threadId)
    {
        if (threadId != 0)
            PostThreadMessage(threadId, WM_QUIT, nint.Zero, nint.Zero);
    }
}
