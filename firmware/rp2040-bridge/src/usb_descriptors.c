// ────────────────────────────────────────────────────────────────────────
// USB descriptor'ları — tek HID interface, 3 top-level collection:
//   rid 1: Keyboard (OS'a)         rid 2: Mouse-relative (OS'a)
//   rid 3: Vendor (UsagePage 0xFF00, 63B in/out) — bot komut kanalı
//
// VID/PID/string'ler PLACEHOLDER (Faz 9'da stealth kimlikle finalize).
// VID/PID, PC tarafı Rp2040HidTransport ile AYNI olmalı.
// ────────────────────────────────────────────────────────────────────────
#include <string.h>
#include "tusb.h"
#include "protocol.h"   // RID_KEYBOARD/MOUSE/VENDOR

// ── HID report descriptor: 3 top-level collection ──
uint8_t const desc_hid_report[] = {
    TUD_HID_REPORT_DESC_KEYBOARD     ( HID_REPORT_ID(RID_KEYBOARD) ),
    TUD_HID_REPORT_DESC_MOUSE        ( HID_REPORT_ID(RID_MOUSE)    ),
    TUD_HID_REPORT_DESC_GENERIC_INOUT( VENDOR_DATA_SIZE, HID_REPORT_ID(RID_VENDOR) ),
};

uint8_t const * tud_hid_descriptor_report_cb(uint8_t instance) {
    (void) instance;
    return desc_hid_report;
}

// ── Device descriptor ──
tusb_desc_device_t const desc_device = {
    .bLength            = sizeof(tusb_desc_device_t),
    .bDescriptorType    = TUSB_DESC_DEVICE,
    .bcdUSB             = 0x0200,
    .bDeviceClass       = 0x00,
    .bDeviceSubClass    = 0x00,
    .bDeviceProtocol    = 0x00,
    .bMaxPacketSize0    = CFG_TUD_ENDPOINT0_SIZE,
    .idVendor           = 0xCafe,   // generic test VID (RP2040 ekosisteminde yaygın)
    .idProduct          = 0x4000,   // PC tarafı Rp2040HidTransport ile AYNI olmalı
    .bcdDevice          = 0x0100,
    .iManufacturer      = 0x01,
    .iProduct           = 0x02,
    .iSerialNumber      = 0x00,
    .bNumConfigurations = 0x01,
};

uint8_t const * tud_descriptor_device_cb(void) {
    return (uint8_t const *) &desc_device;
}

// ── Configuration descriptor ──
enum { ITF_NUM_HID = 0, ITF_NUM_TOTAL };

#define CONFIG_TOTAL_LEN  (TUD_CONFIG_DESC_LEN + TUD_HID_INOUT_DESC_LEN)

#define EPNUM_HID_OUT  0x01
#define EPNUM_HID_IN   0x81

uint8_t const desc_configuration[] = {
    TUD_CONFIG_DESCRIPTOR(1, ITF_NUM_TOTAL, 0, CONFIG_TOTAL_LEN, 0x00, 100),
    // HID In/Out: report desc len, OUT ep, IN ep, ep bufsize, polling 1 ms
    TUD_HID_INOUT_DESCRIPTOR(ITF_NUM_HID, 0, HID_ITF_PROTOCOL_NONE,
                             sizeof(desc_hid_report),
                             EPNUM_HID_OUT, EPNUM_HID_IN, CFG_TUD_HID_EP_BUFSIZE, 1),
};

uint8_t const * tud_descriptor_configuration_cb(uint8_t index) {
    (void) index;
    return desc_configuration;
}

// ── String descriptor'ları ──
char const * string_desc_arr[] = {
    (const char[]){ 0x09, 0x04 },  // 0: dil — English (0x0409)
    "RP2040",                      // 1: Manufacturer
    "HID Bridge",                  // 2: Product
};

static uint16_t _desc_str[32];

uint16_t const * tud_descriptor_string_cb(uint8_t index, uint16_t langid) {
    (void) langid;
    uint8_t chr_count;

    if (index == 0) {
        memcpy(&_desc_str[1], string_desc_arr[0], 2);
        chr_count = 1;
    } else {
        if (index >= sizeof(string_desc_arr) / sizeof(string_desc_arr[0])) return NULL;
        const char * str = string_desc_arr[index];
        chr_count = (uint8_t) strlen(str);
        if (chr_count > 31) chr_count = 31;
        for (uint8_t i = 0; i < chr_count; i++) _desc_str[1 + i] = (uint16_t) str[i];
    }

    _desc_str[0] = (uint16_t) ((TUSB_DESC_STRING << 8) | (2 * chr_count + 2));
    return _desc_str;
}
