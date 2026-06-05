// ────────────────────────────────────────────────────────────────────────
// RP2040 Zero HID Bridge — PC ↔ cihaz vendor-HID komut sözleşmesi
//
// TEK GERÇEK KAYNAK. Bunun C# aynası: src/HomekoWorld/Hardware/Rp2040Protocol.cs
// İki dosya DAİMA senkron tutulmalı (opcode/boyut değişirse ikisini de güncelle).
//
// Kanal: ayrı "vendor" HID top-level collection (Usage Page 0xFF00), report ID 3.
// Klavye (rid1) ve fare (rid2) raporları OS'a gider; komutlar yalnız vendor'dan.
// Tüm çok-baytlı alanlar LITTLE-ENDIAN (hem RP2040 hem x86 LE → çevirme yok).
//
// Tel formatı (her iki yön): buf[0]=report id (RID_VENDOR), buf[1]=opcode/event,
//                            buf[2..]=payload.
// ────────────────────────────────────────────────────────────────────────
#pragma once
#include <stdint.h>

// ── Report ID'leri ────────────────────────────────────────────────────────
enum {
    RID_KEYBOARD = 1,   // OS'a: [mod, reserved, k0..k5]  (8 veri baytı)
    RID_MOUSE    = 2,   // OS'a: [buttons, dx, dy, wheel]  (relative, int8)
    RID_VENDOR   = 3,   // komut kanalı (PC ↔ cihaz)
};

// Vendor raporu tel üstünde 64 bayt (1 report-id + 63 veri).
#define VENDOR_REPORT_SIZE          64
#define VENDOR_DATA_SIZE            63
// BATCH_EVT'de tek rapora sığan azami olay sayısı: (63 - 2) / 4 = 15.
#define VENDOR_MAX_EVTS_PER_REPORT  15

// ── OUT opcode'ları (PC → cihaz), buf[1] ─────────────────────────────────
enum {
    OP_KEY_DOWN    = 0x01,  // [usage]                  klavye tuşu bas
    OP_KEY_UP      = 0x02,  // [usage]                  klavye tuşu bırak
    OP_MOD         = 0x03,  // [modMask]                modifier byte'ı MUTLAK set
    OP_MOUSE_REL   = 0x10,  // [dx:i16, dy:i16]         relative fare
    OP_MOUSE_BTN   = 0x11,  // [btnMask, state]         state 1=down 0=up
    OP_MOUSE_WHEEL = 0x12,  // [delta:i8]               tekerlek
    OP_BATCH_BEGIN = 0x20,  // [flags]                  bit0=loop
    OP_BATCH_EVT   = 0x21,  // [count, {t:u16,type,code}×count]
    OP_BATCH_END   = 0x22,  // []                       combo'yu scheduler'a ver
    OP_CANCEL      = 0x2F,  // []                       combo durdur + release-all
    OP_PING        = 0x30,  // [nonce:u16]              → EV_PONG
    OP_REBOOT_BOOTSEL = 0x3E, // []                     → reset_usb_boot (app-içi firmware güncelleme)
    OP_RELEASE_ALL = 0x3F,  // []                       failsafe: her şeyi bırak
};

// ── BATCH_EVT olay tipleri (combo = yalnız klavye) ───────────────────────
enum {
    EVT_KEY_DOWN = 1,   // code = usage
    EVT_KEY_UP   = 2,   // code = usage
    EVT_MOD      = 3,   // code = modMask (mutlak)
};

// ── BATCH_BEGIN bayrakları ────────────────────────────────────────────────
enum {
    BATCH_FLAG_LOOP = 0x01,
};

// ── IN event'leri (cihaz → PC), buf[1] ────────────────────────────────────
enum {
    EV_PONG   = 0x80,  // [nonce:u16]
    EV_ACK    = 0x81,  // [seq]
    EV_STATUS = 0x82,  // [flags, lastError]   flags bit0=linkAlive bit1=batchRunning
};

// ── Fare buton bitleri (OP_MOUSE_BTN ve fare raporu buttons baytı) ────────
enum {
    BTN_LEFT   = 0x01,
    BTN_RIGHT  = 0x02,
    BTN_MIDDLE = 0x04,
};

// ── Modifier bitleri (HID kbd report byte 0) — Android KeyMap.kt ile birebir ─
enum {
    MOD_LCTRL  = 0x01, MOD_LSHIFT = 0x02, MOD_LALT = 0x04, MOD_LGUI = 0x08,
    MOD_RCTRL  = 0x10, MOD_RSHIFT = 0x20, MOD_RALT = 0x40, MOD_RGUI = 0x80,
};

// ── Failsafe ──────────────────────────────────────────────────────────────
// PC ~1 s'de bir OP_PING atar. Cihaz bu süre kadar OUT görmezse veya USB
// suspend/detach olursa tüm tuş/butonları bırakır (takılı tuş kalmaz).
#define VENDOR_WATCHDOG_MS  1500
