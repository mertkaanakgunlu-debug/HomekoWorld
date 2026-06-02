package world.homeko.bridge

/**
 * Maps string key names (from C# protocol, e.g. "F1", "A", "1") to
 * HID usage codes (USB HID Usage Tables, page 0x07).
 */
object KeyMap {

    // Modifier bit masks (byte 0 of HID report)
    const val MOD_LCTRL:  Byte = 0x01
    const val MOD_LSHIFT: Byte = 0x02
    const val MOD_LALT:   Byte = 0x04
    const val MOD_LGUI:   Byte = 0x08
    const val MOD_RCTRL:  Byte = 0x10
    const val MOD_RSHIFT: Byte = 0x20
    const val MOD_RALT:   Byte = 0x40
    const val MOD_RGUI:   Byte = 0x80.toByte()

    private val keyTable: Map<String, Byte> = mapOf(
        // Letters
        "A" to 0x04, "B" to 0x05, "C" to 0x06, "D" to 0x07, "E" to 0x08,
        "F" to 0x09, "G" to 0x0A, "H" to 0x0B, "I" to 0x0C, "J" to 0x0D,
        "K" to 0x0E, "L" to 0x0F, "M" to 0x10, "N" to 0x11, "O" to 0x12,
        "P" to 0x13, "Q" to 0x14, "R" to 0x15, "S" to 0x16, "T" to 0x17,
        "U" to 0x18, "V" to 0x19, "W" to 0x1A, "X" to 0x1B, "Y" to 0x1C,
        "Z" to 0x1D,

        // Number row
        "1" to 0x1E, "2" to 0x1F, "3" to 0x20, "4" to 0x21, "5" to 0x22,
        "6" to 0x23, "7" to 0x24, "8" to 0x25, "9" to 0x26, "0" to 0x27,

        // F-keys
        "F1"  to 0x3A, "F2"  to 0x3B, "F3"  to 0x3C, "F4"  to 0x3D,
        "F5"  to 0x3E, "F6"  to 0x3F, "F7"  to 0x40, "F8"  to 0x41,
        "F9"  to 0x42, "F10" to 0x43, "F11" to 0x44, "F12" to 0x45,

        // Special — UPPERCASE keys so resolve(name.uppercase()) always matches
        "ENTER"     to 0x28,
        "ESCAPE"    to 0x29,
        "BACKSPACE" to 0x2A,
        "TAB"       to 0x2B,
        "SPACE"     to 0x2C,
        "DELETE"    to 0x4C,
        "INSERT"    to 0x49,
        "HOME"      to 0x4A,
        "END"       to 0x4D,
        "PAGEUP"    to 0x4B,
        "PAGEDOWN"  to 0x4E,
        "RIGHT"     to 0x4F,
        "LEFT"      to 0x50,
        "DOWN"      to 0x51,
        "UP"        to 0x52,
        "CAPSLOCK"  to 0x39,

        // Numpad
        "NUM1" to 0x59, "NUM2" to 0x5A, "NUM3" to 0x5B,
        "NUM4" to 0x5C, "NUM5" to 0x5D, "NUM6" to 0x5E,
        "NUM7" to 0x5F, "NUM8" to 0x60, "NUM9" to 0x61,
        "NUM0" to 0x62,
    ).mapValues { it.value.toByte() }

    fun resolve(name: String): Byte? = keyTable[name.uppercase()]

    fun modifierBit(name: String): Byte = when (name.lowercase()) {
        "shift", "lshift" -> MOD_LSHIFT
        "rshift"          -> MOD_RSHIFT
        "ctrl",  "lctrl"  -> MOD_LCTRL
        "rctrl"           -> MOD_RCTRL
        "alt",   "lalt"   -> MOD_LALT
        "ralt"            -> MOD_RALT
        else              -> 0x00
    }
}
