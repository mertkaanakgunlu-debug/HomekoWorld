using System.Windows.Input;

namespace HomekoWorld.Core;

public static class KeyCode
{
    private static readonly Dictionary<Key, string> _map = new()
    {
        // F-keys
        [Key.F1] = "F1",  [Key.F2] = "F2",  [Key.F3] = "F3",  [Key.F4] = "F4",
        [Key.F5] = "F5",  [Key.F6] = "F6",  [Key.F7] = "F7",  [Key.F8] = "F8",
        [Key.F9] = "F9",  [Key.F10] = "F10",[Key.F11] = "F11",[Key.F12] = "F12",

        // Numbers row
        [Key.D0] = "0", [Key.D1] = "1", [Key.D2] = "2", [Key.D3] = "3", [Key.D4] = "4",
        [Key.D5] = "5", [Key.D6] = "6", [Key.D7] = "7", [Key.D8] = "8", [Key.D9] = "9",

        // Letters
        [Key.A] = "A", [Key.B] = "B", [Key.C] = "C", [Key.D] = "D", [Key.E] = "E",
        [Key.F] = "F", [Key.G] = "G", [Key.H] = "H", [Key.I] = "I", [Key.J] = "J",
        [Key.K] = "K", [Key.L] = "L", [Key.M] = "M", [Key.N] = "N", [Key.O] = "O",
        [Key.P] = "P", [Key.Q] = "Q", [Key.R] = "R", [Key.S] = "S", [Key.T] = "T",
        [Key.U] = "U", [Key.V] = "V", [Key.W] = "W", [Key.X] = "X", [Key.Y] = "Y",
        [Key.Z] = "Z",

        // Special
        [Key.Space]  = "Space",
        [Key.Return] = "Enter",
        [Key.Tab]    = "Tab",
        [Key.Escape] = "Escape",
        [Key.Back]   = "Backspace",
        [Key.Delete] = "Delete",
        [Key.Insert] = "Insert",
        [Key.Home]   = "Home",
        [Key.End]    = "End",
        [Key.Prior]  = "PageUp",
        [Key.Next]   = "PageDown",
        [Key.Left]   = "Left",
        [Key.Right]  = "Right",
        [Key.Up]     = "Up",
        [Key.Down]   = "Down",
        [Key.CapsLock] = "CapsLock",

        // Numpad
        [Key.NumPad0] = "Num0", [Key.NumPad1] = "Num1", [Key.NumPad2] = "Num2",
        [Key.NumPad3] = "Num3", [Key.NumPad4] = "Num4", [Key.NumPad5] = "Num5",
        [Key.NumPad6] = "Num6", [Key.NumPad7] = "Num7", [Key.NumPad8] = "Num8",
        [Key.NumPad9] = "Num9",

        // Modifier keys — tek başına trigger olarak atanabilir
        [Key.LeftShift]  = "Shift", [Key.RightShift] = "Shift",
        [Key.LeftAlt]    = "Alt",   [Key.RightAlt]   = "Alt",
        [Key.LeftCtrl]   = "Ctrl",  [Key.RightCtrl]  = "Ctrl",
    };

    private static readonly HashSet<Key> _modifierKeys =
    [
        Key.LeftShift, Key.RightShift,
        Key.LeftCtrl,  Key.RightCtrl,
        Key.LeftAlt,   Key.RightAlt,
        Key.LWin,      Key.RWin,
    ];

    public static bool IsModifier(Key key) => _modifierKeys.Contains(key);

    public static string? ToName(Key key) => _map.TryGetValue(key, out var name) ? name : null;

    public static string[] GetModifiers(KeyboardModifiers mods)
    {
        var list = new List<string>(3);
        if ((mods & KeyboardModifiers.Shift) != 0) list.Add("Shift");
        if ((mods & KeyboardModifiers.Ctrl)  != 0) list.Add("Ctrl");
        if ((mods & KeyboardModifiers.Alt)   != 0) list.Add("Alt");
        return [.. list];
    }
}

[Flags]
public enum KeyboardModifiers
{
    None  = 0,
    Shift = 1,
    Ctrl  = 2,
    Alt   = 4,
}
