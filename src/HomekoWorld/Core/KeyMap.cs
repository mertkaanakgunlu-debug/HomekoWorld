using System.Runtime.InteropServices;

namespace HomekoWorld.Core;

// Maps the key name strings used in the batch protocol (KEYDOWN:W:50) to Windows
// scan codes for SendInput injection. Scan codes are resolved once at startup
// via MapVirtualKey so locale differences are handled automatically.
public static class KeyMap
{
    [DllImport("user32.dll")]
    private static extern uint MapVirtualKey(uint uCode, uint uMapType);

    private const uint MAPVK_VK_TO_VSC = 0;

    // VK code + whether the key requires KEYEVENTF_EXTENDEDKEY
    private static readonly Dictionary<string, (ushort vk, bool extended)> _vkTable
        = new(StringComparer.OrdinalIgnoreCase)
    {
        // Letters
        ["A"] = (0x41, false), ["B"] = (0x42, false), ["C"] = (0x43, false),
        ["D"] = (0x44, false), ["E"] = (0x45, false), ["F"] = (0x46, false),
        ["G"] = (0x47, false), ["H"] = (0x48, false), ["I"] = (0x49, false),
        ["J"] = (0x4A, false), ["K"] = (0x4B, false), ["L"] = (0x4C, false),
        ["M"] = (0x4D, false), ["N"] = (0x4E, false), ["O"] = (0x4F, false),
        ["P"] = (0x50, false), ["Q"] = (0x51, false), ["R"] = (0x52, false),
        ["S"] = (0x53, false), ["T"] = (0x54, false), ["U"] = (0x55, false),
        ["V"] = (0x56, false), ["W"] = (0x57, false), ["X"] = (0x58, false),
        ["Y"] = (0x59, false), ["Z"] = (0x5A, false),

        // Number row
        ["0"] = (0x30, false), ["1"] = (0x31, false), ["2"] = (0x32, false),
        ["3"] = (0x33, false), ["4"] = (0x34, false), ["5"] = (0x35, false),
        ["6"] = (0x36, false), ["7"] = (0x37, false), ["8"] = (0x38, false),
        ["9"] = (0x39, false),

        // Function keys
        ["F1"]  = (0x70, false), ["F2"]  = (0x71, false), ["F3"]  = (0x72, false),
        ["F4"]  = (0x73, false), ["F5"]  = (0x74, false), ["F6"]  = (0x75, false),
        ["F7"]  = (0x76, false), ["F8"]  = (0x77, false), ["F9"]  = (0x78, false),
        ["F10"] = (0x79, false), ["F11"] = (0x7A, false), ["F12"] = (0x7B, false),

        // Common specials
        ["Space"]     = (0x20, false),
        ["Enter"]     = (0x0D, false),
        ["Tab"]       = (0x09, false),
        ["Escape"]    = (0x1B, false),
        ["Backspace"] = (0x08, false),
        ["CapsLock"]  = (0x14, false),

        // Navigation — these use the extended-key flag on PC keyboards
        ["Delete"]   = (0x2E, true),
        ["Insert"]   = (0x2D, true),
        ["Home"]     = (0x24, true),
        ["End"]      = (0x23, true),
        ["PageUp"]   = (0x21, true),
        ["PageDown"] = (0x22, true),
        ["Left"]     = (0x25, true),
        ["Right"]    = (0x27, true),
        ["Up"]       = (0x26, true),
        ["Down"]     = (0x28, true),

        // Numpad (no extended flag)
        ["Num0"] = (0x60, false), ["Num1"] = (0x61, false), ["Num2"] = (0x62, false),
        ["Num3"] = (0x63, false), ["Num4"] = (0x64, false), ["Num5"] = (0x65, false),
        ["Num6"] = (0x66, false), ["Num7"] = (0x67, false), ["Num8"] = (0x68, false),
        ["Num9"] = (0x69, false),

        // Modifiers — use left-side VK so we get a non-extended scan code
        ["Shift"] = (0xA0, false), // VK_LSHIFT
        ["Ctrl"]  = (0xA2, false), // VK_LCONTROL
        ["Alt"]   = (0xA4, false), // VK_LMENU
    };

    // Resolved once at class init; keyed by the same strings used in the protocol
    private static readonly Dictionary<string, (ushort scan, bool extended)> _cache
        = new(StringComparer.OrdinalIgnoreCase);

    static KeyMap()
    {
        foreach (var (name, (vk, extended)) in _vkTable)
        {
            var scan = (ushort)MapVirtualKey(vk, MAPVK_VK_TO_VSC);
            if (scan != 0)
                _cache[name] = (scan, extended);
        }
    }

    public static bool TryGet(string name, out ushort scan, out bool extended)
    {
        if (_cache.TryGetValue(name, out var entry))
        {
            scan     = entry.scan;
            extended = entry.extended;
            return true;
        }
        scan = 0; extended = false;
        return false;
    }
}
