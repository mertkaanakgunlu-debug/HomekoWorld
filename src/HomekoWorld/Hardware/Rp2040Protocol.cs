namespace HomekoWorld.Hardware;

/// <summary>
/// PC ↔ RP2040 vendor-HID komut sözleşmesi (binary, 64B rapor).
/// Firmware aynası: firmware/rp2040-bridge/src/protocol.h — İKİSİ DAİMA SENKRON.
///
/// Kanal: ayrı "vendor" HID collection (Usage Page 0xFF00), report ID 3.
/// Tel formatı: buf[0]=RidVendor, buf[1]=opcode/event, buf[2..]=payload (little-endian).
/// </summary>
internal static class Rp2040Protocol
{
    // Report ID'leri
    public const byte RidKeyboard = 1;
    public const byte RidMouse    = 2;
    public const byte RidVendor   = 3;

    // Vendor raporu tel üstünde 64 bayt (1 report-id + 63 veri).
    public const int VendorReportSize       = 64;
    public const int VendorDataSize         = 63;
    public const int VendorMaxEvtsPerReport = 15;   // (63 - 2) / 4

    // ── OUT opcode'ları (PC → cihaz), buf[1] ──────────────────────────────
    public const byte OpKeyDown    = 0x01;  // [usage]
    public const byte OpKeyUp      = 0x02;  // [usage]
    public const byte OpMod        = 0x03;  // [modMask] mutlak modifier byte
    public const byte OpMouseRel   = 0x10;  // [dx:i16, dy:i16]
    public const byte OpMouseBtn   = 0x11;  // [btnMask, state] 1=down 0=up
    public const byte OpMouseWheel = 0x12;  // [delta:i8]
    public const byte OpBatchBegin = 0x20;  // [flags] bit0=loop
    public const byte OpBatchEvt   = 0x21;  // [count, {t:u16,type,code}×count]
    public const byte OpBatchEnd   = 0x22;  // []
    public const byte OpCancel     = 0x2F;  // []
    public const byte OpPing          = 0x30;  // [nonce:u16]
    public const byte OpRebootBootsel = 0x3E;  // [] → reset_usb_boot (app-içi fw güncelleme)
    public const byte OpReleaseAll    = 0x3F;  // []

    // ── BATCH_EVT olay tipleri ────────────────────────────────────────────
    public const byte EvtKeyDown = 1;   // code = usage
    public const byte EvtKeyUp   = 2;   // code = usage
    public const byte EvtMod     = 3;   // code = modMask

    // ── BATCH_BEGIN bayrakları ────────────────────────────────────────────
    public const byte BatchFlagLoop = 0x01;

    // ── IN event'leri (cihaz → PC), buf[1] ────────────────────────────────
    public const byte EvPong   = 0x80;  // [nonce:u16]
    public const byte EvAck    = 0x81;  // [seq]
    public const byte EvStatus = 0x82;  // [flags, lastError]

    // ── Fare buton bitleri ────────────────────────────────────────────────
    public const byte BtnLeft   = 0x01;
    public const byte BtnRight  = 0x02;
    public const byte BtnMiddle = 0x04;

    /// <summary>MouseButton → vendor protokol buton biti.</summary>
    public static byte ButtonBit(MouseButton b) => b switch
    {
        MouseButton.Left   => BtnLeft,
        MouseButton.Right  => BtnRight,
        MouseButton.Middle => BtnMiddle,
        _                  => BtnLeft,
    };

    // PC ~1 s PING; cihaz watchdog'u (bkz. protocol.h).
    public const int WatchdogMs = 1500;
}
