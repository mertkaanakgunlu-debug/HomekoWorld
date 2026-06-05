using System.Collections.Generic;

namespace HomekoWorld.Hardware;

/// <summary>
/// String tuş adı ("W", "F1", "1", "Space"…) → USB HID usage kodu
/// (HID Usage Tables, page 0x07) + modifier bit eşlemesi.
///
/// Android KeyMap.kt'in birebir C# portu — RP2040 firmware ile AYNI usage
/// kodlarını üretir (telefon ve RP2040 köprüleri tuşları aynı yorumlasın).
/// Tüm aramalar büyük/küçük harf duyarsız.
/// </summary>
internal static class HidUsage
{
    // Modifier bit maskeleri (HID keyboard report byte 0) — KeyMap.kt ile birebir.
    public const byte ModLCtrl  = 0x01;
    public const byte ModLShift = 0x02;
    public const byte ModLAlt   = 0x04;
    public const byte ModLGui   = 0x08;
    public const byte ModRCtrl  = 0x10;
    public const byte ModRShift = 0x20;
    public const byte ModRAlt   = 0x40;
    public const byte ModRGui   = 0x80;

    private static readonly Dictionary<string, byte> KeyTable = new(System.StringComparer.OrdinalIgnoreCase)
    {
        // Harfler (0x04–0x1D)
        ["A"]=0x04, ["B"]=0x05, ["C"]=0x06, ["D"]=0x07, ["E"]=0x08,
        ["F"]=0x09, ["G"]=0x0A, ["H"]=0x0B, ["I"]=0x0C, ["J"]=0x0D,
        ["K"]=0x0E, ["L"]=0x0F, ["M"]=0x10, ["N"]=0x11, ["O"]=0x12,
        ["P"]=0x13, ["Q"]=0x14, ["R"]=0x15, ["S"]=0x16, ["T"]=0x17,
        ["U"]=0x18, ["V"]=0x19, ["W"]=0x1A, ["X"]=0x1B, ["Y"]=0x1C,
        ["Z"]=0x1D,

        // Rakam sırası (1→0x1E … 9→0x26, 0→0x27)
        ["1"]=0x1E, ["2"]=0x1F, ["3"]=0x20, ["4"]=0x21, ["5"]=0x22,
        ["6"]=0x23, ["7"]=0x24, ["8"]=0x25, ["9"]=0x26, ["0"]=0x27,

        // F-tuşları (0x3A–0x45)
        ["F1"]=0x3A, ["F2"]=0x3B, ["F3"]=0x3C, ["F4"]=0x3D,
        ["F5"]=0x3E, ["F6"]=0x3F, ["F7"]=0x40, ["F8"]=0x41,
        ["F9"]=0x42, ["F10"]=0x43, ["F11"]=0x44, ["F12"]=0x45,

        // Özel
        ["ENTER"]=0x28, ["ESCAPE"]=0x29, ["BACKSPACE"]=0x2A, ["TAB"]=0x2B,
        ["SPACE"]=0x2C, ["DELETE"]=0x4C, ["INSERT"]=0x49, ["HOME"]=0x4A,
        ["END"]=0x4D, ["PAGEUP"]=0x4B, ["PAGEDOWN"]=0x4E,
        ["RIGHT"]=0x4F, ["LEFT"]=0x50, ["DOWN"]=0x51, ["UP"]=0x52,
        ["CAPSLOCK"]=0x39,

        // Numpad (0x59–0x62)
        ["NUM1"]=0x59, ["NUM2"]=0x5A, ["NUM3"]=0x5B,
        ["NUM4"]=0x5C, ["NUM5"]=0x5D, ["NUM6"]=0x5E,
        ["NUM7"]=0x5F, ["NUM8"]=0x60, ["NUM9"]=0x61,
        ["NUM0"]=0x62,
    };

    /// <summary>Verilen ad bir modifier mı? ("Shift","Ctrl","Alt", L/R/GUI varyantları).</summary>
    public static bool IsModifier(string name) => ModifierBit(name) != 0;

    /// <summary>Modifier adı → bit maskesi; modifier değilse 0. (Android modifierBit ile birebir + GUI eklendi.)</summary>
    public static byte ModifierBit(string? name) => name?.Trim().ToLowerInvariant() switch
    {
        "shift" or "lshift" => ModLShift,
        "rshift"            => ModRShift,
        "ctrl"  or "lctrl"  => ModLCtrl,
        "rctrl"             => ModRCtrl,
        "alt"   or "lalt"   => ModLAlt,
        "ralt"              => ModRAlt,
        "gui" or "win" or "lgui" or "lwin" => ModLGui,
        "rgui" or "rwin"    => ModRGui,
        _                   => 0,
    };

    /// <summary>
    /// Regular (modifier olmayan) tuş adı → HID usage. Modifier'lar için
    /// <see cref="ModifierBit"/> kullan. Bilinmeyen tuşta false döner.
    /// </summary>
    public static bool TryResolve(string? name, out byte usage)
    {
        usage = 0;
        if (string.IsNullOrWhiteSpace(name)) return false;
        return KeyTable.TryGetValue(name.Trim(), out usage);
    }
}
