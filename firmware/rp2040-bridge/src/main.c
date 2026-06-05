// ────────────────────────────────────────────────────────────────────────
// RP2040 Zero HID Bridge — Faz 2+3+4
//
//   Faz 2: HID emisyon çekirdeği (klavye 6-key array + relative fare)
//   Faz 3: vendor OUT komut parser + failsafe watchdog + PONG
//   Faz 4: cihaz-üstü combo scheduler (core0 kooperatif, time_us_64 ms offset)
//
// Tek core0 thread'i: tud_task → vendor komutları + scheduler aynı sırada
// işler → AutoPot anlık komutu çalışan combo'yu bloklamaz/iptal etmez, pot
// tuşu combo tuşlarının yanına eklenir (paylaşılan kb state, kilit gerekmez).
//
// Emisyon modeli: "istenen durum + hazır olunca gönder". HID raporları host
// 1ms'de bir poll ettikçe sırayla çıkar; tuş olayları ms-aralıklı olduğundan
// coalescing kayıp yaratmaz.
// ────────────────────────────────────────────────────────────────────────
#include <string.h>
#include "pico/stdlib.h"
#include "pico/bootrom.h"   // reset_usb_boot (app-içi firmware güncelleme)
#include "hardware/pio.h"
#include "tusb.h"
#include "protocol.h"
#include "ws2812.pio.h"     // pico_generate_pio_header ile üretilir (CMake)

// ╔═══ Paylaşılan HID durumu ═══════════════════════════════════════════════╗
static uint8_t kb_mod;
static uint8_t kb_keys[6];
static bool    kb_dirty;

static uint8_t mouse_buttons;
static int32_t pend_dx, pend_dy, pend_wheel;
static bool    mouse_dirty;

static bool     pong_pending;
static uint16_t pong_nonce;

static uint32_t last_cmd_ms;   // failsafe watchdog için
static uint32_t last_action_ms; // LED aktivite göstergesi için

static inline uint32_t now_ms(void) { return (uint32_t)(time_us_64() / 1000u); }

// ── Klavye ──
static void kb_key_down(uint8_t usage) {
    if (usage == 0) return;
    for (int i = 0; i < 6; i++) if (kb_keys[i] == usage) return;   // zaten basılı
    for (int i = 0; i < 6; i++) if (kb_keys[i] == 0) { kb_keys[i] = usage; kb_dirty = true; return; }
    // 6-key dolu → en eskiyi at (combo'da pratikte olmaz)
    kb_keys[0] = usage; kb_dirty = true;
}
static void kb_key_up(uint8_t usage) {
    for (int i = 0; i < 6; i++) if (kb_keys[i] == usage) { kb_keys[i] = 0; kb_dirty = true; }
}
static void kb_set_mod(uint8_t mask) { kb_mod = mask; kb_dirty = true; }

// ── Fare ──
static void mouse_rel(int dx, int dy)   { pend_dx += dx; pend_dy += dy; mouse_dirty = true; }
static void mouse_btn(uint8_t m, uint8_t down) { if (down) mouse_buttons |= m; else mouse_buttons &= (uint8_t)~m; mouse_dirty = true; }
static void mouse_wheel(int d)          { pend_wheel += d; mouse_dirty = true; }

// ── Hepsini bırak (failsafe / CANCEL / unmount) ──
static void release_all(void) {
    kb_mod = 0; memset(kb_keys, 0, sizeof(kb_keys)); kb_dirty = true;
    mouse_buttons = 0; pend_dx = pend_dy = pend_wheel = 0; mouse_dirty = true;
}

static inline bool anything_held(void) {
    if (kb_mod || mouse_buttons) return true;
    for (int i = 0; i < 6; i++) if (kb_keys[i]) return true;
    return false;
}

static inline int8_t clamp8(int32_t v) { return (int8_t)(v > 127 ? 127 : (v < -127 ? -127 : v)); }

// ╔═══ Combo scheduler (core0 kooperatif) ══════════════════════════════════╗
#define MAX_BATCH_EVENTS 256
typedef struct { uint16_t t_ms; uint8_t type; uint8_t code; } batch_evt_t;

static batch_evt_t batch_events[MAX_BATCH_EVENTS];
static int      batch_count;
static bool     batch_collecting;
static bool     batch_loop;
static bool     batch_active;
static int      batch_idx;
static uint32_t batch_start_ms;

static void batch_begin(uint8_t flags) {
    batch_collecting = true;
    batch_active     = false;
    batch_loop       = (flags & BATCH_FLAG_LOOP) != 0;
    batch_count      = 0;
}
static void batch_add(const uint8_t * p, uint16_t avail) {
    if (!batch_collecting || avail < 1) return;
    uint8_t n = p[0];
    for (uint8_t i = 0; i < n; i++) {
        uint16_t off = 1 + (uint16_t)i * 4;
        if (off + 4 > avail) break;
        if (batch_count >= MAX_BATCH_EVENTS) break;
        batch_events[batch_count].t_ms = (uint16_t)(p[off] | (p[off + 1] << 8));
        batch_events[batch_count].type = p[off + 2];
        batch_events[batch_count].code = p[off + 3];
        batch_count++;
    }
}
static void batch_end(void) {
    batch_collecting = false;
    if (batch_count > 0) { batch_active = true; batch_idx = 0; batch_start_ms = now_ms(); }
}
static void batch_cancel(void) {
    batch_collecting = false;
    batch_active     = false;
    batch_count      = 0;
}
static void batch_task(uint32_t t) {
    if (!batch_active) return;
    uint32_t elapsed = t - batch_start_ms;
    while (batch_idx < batch_count && batch_events[batch_idx].t_ms <= elapsed) {
        const batch_evt_t * e = &batch_events[batch_idx++];
        switch (e->type) {
            case EVT_KEY_DOWN: kb_key_down(e->code); break;
            case EVT_KEY_UP:   kb_key_up(e->code);   break;
            case EVT_MOD:      kb_set_mod(e->code);  break;
            default: break;
        }
    }
    if (batch_idx >= batch_count) {
        if (batch_loop) { batch_idx = 0; batch_start_ms = t; }
        else            { batch_active = false; }
    }
}

// ╔═══ Vendor OUT komut dispatch ══════════════════════════════════════════╗
static void vendor_dispatch(uint8_t op, const uint8_t * p, uint16_t avail) {
    last_cmd_ms = now_ms();   // watchdog besle (her komut)
    if (op != OP_PING && op != OP_REBOOT_BOOTSEL) last_action_ms = last_cmd_ms; // aktivite LED'ini PING'de yakma
    switch (op) {
        case OP_KEY_DOWN:    if (avail >= 1) kb_key_down(p[0]); break;
        case OP_KEY_UP:      if (avail >= 1) kb_key_up(p[0]);   break;
        case OP_MOD:         if (avail >= 1) kb_set_mod(p[0]);  break;
        case OP_MOUSE_REL:   if (avail >= 4) mouse_rel((int16_t)(p[0] | (p[1] << 8)),
                                                       (int16_t)(p[2] | (p[3] << 8))); break;
        case OP_MOUSE_BTN:   if (avail >= 2) mouse_btn(p[0], p[1]); break;
        case OP_MOUSE_WHEEL: if (avail >= 1) mouse_wheel((int8_t)p[0]); break;
        case OP_BATCH_BEGIN: if (avail >= 1) batch_begin(p[0]); break;
        case OP_BATCH_EVT:   batch_add(p, avail); break;
        case OP_BATCH_END:   batch_end(); break;
        case OP_CANCEL:      batch_cancel(); release_all(); break;
        case OP_PING:        if (avail >= 2) { pong_nonce = (uint16_t)(p[0] | (p[1] << 8)); pong_pending = true; } break;
        case OP_RELEASE_ALL: batch_cancel(); release_all(); break;
        case OP_REBOOT_BOOTSEL: reset_usb_boot(0, 0); break;   // app-içi firmware güncelleme
        default: break;
    }
}

// TinyUSB: vendor OUT raporu burada gelir (interrupt OUT → report_type INVALID,
// buffer[0]=report id). Control SET_REPORT yolunu da tolere et.
void tud_hid_set_report_cb(uint8_t instance, uint8_t report_id,
                           hid_report_type_t report_type,
                           uint8_t const * buffer, uint16_t bufsize) {
    (void) instance; (void) report_type;
    if (bufsize < 2) return;
    uint8_t rid, op; const uint8_t * payload; uint16_t avail;
    if (report_id != 0) { rid = report_id; op = buffer[0]; payload = buffer + 1; avail = bufsize - 1; }
    else                { rid = buffer[0]; op = buffer[1]; payload = buffer + 2; avail = (uint16_t)(bufsize - 2); }
    if (rid != RID_VENDOR) return;
    vendor_dispatch(op, payload, avail);
}

uint16_t tud_hid_get_report_cb(uint8_t instance, uint8_t report_id,
                               hid_report_type_t report_type,
                               uint8_t * buffer, uint16_t reqlen) {
    (void) instance; (void) report_id; (void) report_type; (void) buffer; (void) reqlen;
    return 0;
}

// ╔═══ Emisyon: hazır olunca tek rapor gönder ═════════════════════════════╗
static void hid_flush(void) {
    if (!tud_hid_ready()) return;

    if (pong_pending) {
        uint8_t r[VENDOR_DATA_SIZE]; memset(r, 0, sizeof(r));
        r[0] = EV_PONG; r[1] = (uint8_t)(pong_nonce & 0xFF); r[2] = (uint8_t)(pong_nonce >> 8);
        if (tud_hid_n_report(0, RID_VENDOR, r, sizeof(r))) pong_pending = false;
        return;
    }
    if (kb_dirty) {
        if (tud_hid_n_keyboard_report(0, RID_KEYBOARD, kb_mod, kb_keys)) kb_dirty = false;
        return;
    }
    if (mouse_dirty) {
        int8_t dx = clamp8(pend_dx), dy = clamp8(pend_dy), wh = clamp8(pend_wheel);
        if (tud_hid_n_mouse_report(0, RID_MOUSE, mouse_buttons, dx, dy, wh, 0)) {
            pend_dx -= dx; pend_dy -= dy; pend_wheel -= wh;
            if (pend_dx == 0 && pend_dy == 0 && pend_wheel == 0) mouse_dirty = false;
        }
        return;
    }
}

// ── Failsafe: PC ~1s PING atar; >VENDOR_WATCHDOG_MS komut yoksa hepsini bırak ──
static void watchdog_task(uint32_t t) {
    if (t - last_cmd_ms > VENDOR_WATCHDOG_MS) {
        if (anything_held() || batch_active) { batch_cancel(); release_all(); }
        last_cmd_ms = t;   // yeniden kur (spin etme)
    }
}

// ── USB keepalive: uygulama bağlı olmadığında host ~3s sessizlikte reset atar ──
// Her 1s'de bir sıfır mouse raporu → bus canlı, host reset atmaz.
// App bağlıyken zaten 1s PING + farm komutları var; bu yalnız idle'ı kapatır.
static uint32_t last_keepalive_ms;
static void keepalive_task(uint32_t t) {
    if (t - last_keepalive_ms < 1000) return;
    last_keepalive_ms = t;
    if (tud_mounted() && !kb_dirty && !mouse_dirty)
        mouse_dirty = true;   // dx=dy=wheel=0, buttons=0 → "buradayım"
}

// USB suspend/mount takibi
static bool     _usb_suspended    = false;
static bool     _has_mounted_once = false;  // ilk mount'a kadar kırmızı göster
static uint32_t _unmount_at_ms    = 0;      // son unmount zamanı (debounce için)

// USB kopması/suspend → host zaten tuşları bırakır; durumu da temizle.
void tud_mount_cb(void)   { _has_mounted_once = true; _usb_suspended = false; }
void tud_umount_cb(void)  {
    if (_has_mounted_once) _unmount_at_ms = now_ms();
    release_all(); batch_cancel();
}
void tud_suspend_cb(bool remote_wakeup_en) { (void)remote_wakeup_en; _usb_suspended = true;  release_all(); batch_cancel(); }
void tud_resume_cb(void)                   { _usb_suspended = false; }

// ╔═══ WS2812 durum LED'i (RP2040-Zero dahili RGB, GPIO16) ════════════════╗
// kırmızı=host yok · mavi=hazır · yeşil=aktivite · camgöbeği=combo çalıyor.
// Cihazı fiziksel tanımlama + durum göstergesi (USB stealth kimliğini etkilemez).
#define WS2812_PIN 16
static PIO      led_pio = pio0;
static uint     led_sm  = 0;
static uint32_t last_led_ms;

static void led_init(void) {
    uint offset = pio_add_program(led_pio, &ws2812_program);
    ws2812_program_init(led_pio, led_sm, offset, WS2812_PIN, 800000.0f, false);
}
static inline void led_put(uint8_t r, uint8_t g, uint8_t b) {
    // RP2040-Zero klonlarında LED genellikle RGB dizilimlidir (orijinal WS2812 GRB'dir).
    // Kullanıcının "Yeşil yerine Kırmızı yanıyor" geri bildirimine istinaden RGB'ye çevrildi:
    uint32_t rgb = ((uint32_t)r << 16) | ((uint32_t)g << 8) | (uint32_t)b;
    pio_sm_put_blocking(led_pio, led_sm, rgb << 8u);
}
static void led_task(uint32_t t) {
    if (t - last_led_ms < 33) return;          // ~30 Hz
    last_led_ms = t;
    // Kırmızı: boot'ta (ilk mount öncesi) VEYA >1000ms kopuşta — xHCI Bus Reset'leri gizle
    bool show_red = !tud_mounted() &&
                    (!_has_mounted_once || (t - _unmount_at_ms > 1000));
    if      (show_red)              led_put(40, 0,  0);   // kırmızı  — boot / gerçek kopuş
    else if (_usb_suspended)        led_put(30, 10, 0);   // turuncu  — USB suspended
    else if (t - last_action_ms < 800) led_put(0,  80, 0);   // yeşil    — komut aktif (800ms pencere)
    else if (batch_active)          led_put(0,  50, 50);  // camgöbeği — combo
    else                            led_put(0,  0,  40);  // mavi     — hazır
}

int main(void) {
    led_init();
    // Boot imzası: kırmızı→yeşil→mavi flash (firmware versiyonunu görsel olarak doğrular)
    led_put(30, 0,  0);  sleep_ms(200);
    led_put(0,  0,  0);  sleep_ms(80);
    led_put(0, 60,  0);  sleep_ms(200);
    led_put(0,  0,  0);  sleep_ms(80);
    led_put(0,  0, 30);  sleep_ms(200);
    led_put(0,  0,  0);  sleep_ms(80);
    tusb_init();
    last_cmd_ms = now_ms();
    last_action_ms = last_cmd_ms;
    while (true) {
        tud_task();
        uint32_t t = now_ms();
        batch_task(t);     // combo scheduler
        hid_flush();       // hazırsa tek rapor
        led_task(t);        // durum LED'i
        watchdog_task(t);   // failsafe
        keepalive_task(t);  // USB bus canlı tut (idle reset önleme)
    }
}
