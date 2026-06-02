namespace HomekoWorld.Hardware;

public static class HidBridgeProtocol
{
    public static string Tap(string key, int holdMs = 50) => $"TAP:{key}:{holdMs}";
    public static string Hold(string key)    => $"HOLD:{key}";
    public static string Release(string key) => $"RELEASE:{key}";
    public static string KeyDown(string key) => $"KEYDOWN:{key}";
    public static string KeyUp(string key)   => $"KEYUP:{key}";
    public const string Ping = "PING";
    public const string Pong = "PONG";

    // Batch execution
    public const string ExecBatch = "EXEC";
    public const string ExecLoop  = "EXEC_LOOP";
    public const string ExecEnd   = "ENDEXEC";
    public const string Cancel    = "CANCEL";

    // Batch event lines — KEYDOWN/KEYUP with absolute ms offset from batch start
    public static string BatchKeyDown(string key, int ms) => $"KEYDOWN:{key}:{ms}";
    public static string BatchKeyUp(string key, int ms)   => $"KEYUP:{key}:{ms}";

    // ── Faz 17: Mouse protocol ────────────────────────────────────────────────
    // Android HID bridge bu komutları parse edip BLE HID mouse report olarak gönderir.
    // x,y: 0..32767 (HIDP absolute coordinate space)
    public static string MouseMoveAbs(int x, int y) => $"MOUSE_MOVE_ABS:{x}:{y}";
    public static string MouseClick(MouseButton btn) => $"MOUSE_CLICK:{BtnStr(btn)}";
    public static string MouseDown(MouseButton btn)  => $"MOUSE_DOWN:{BtnStr(btn)}";
    public static string MouseUp(MouseButton btn)    => $"MOUSE_UP:{BtnStr(btn)}";
    public static string MouseWheel(int delta)       => $"MOUSE_WHEEL:{delta}";

    private static string BtnStr(MouseButton b) => b switch
    {
        MouseButton.Left   => "L",
        MouseButton.Right  => "R",
        MouseButton.Middle => "M",
        _                  => "L",
    };
}
