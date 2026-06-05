# RP2040 Zero HID Bridge — Firmware

Homeko bot için **gerçek USB-HID** köprüsü. PC bot, komutları USB üstünden bu
cihaza yollar; cihaz OS'a **gerçek klavye + fare** girdisi basar. Telefon
(BLE/WiFi) köprüsünün yerini alır → OS/WiFi/BLE gecikmesi kalkar (~1–2 ms).

> **Durum: Faz 0 (bring-up).** Şu an cihaz yalnızca USB-CDC üstünden "alive"
> satırı basar. Amaç toolchain + flash akışını doğrulamak. Composite HID
> (klavye+fare+vendor) **Faz 1'de** gelecek.

---

## 1) Toolchain kurulumu (bir kerelik)

En kolay yol: **VS Code "Raspberry Pi Pico" eklentisi** (SDK + ARM GCC + CMake +
Ninja + OpenOCD'yi indirir).

1. Eklenti zaten kuruldu (`raspberry-pi.raspberry-pi-pico`). VS Code'u yeniden başlat.
2. VS Code'da bu klasörü aç: `firmware/rp2040-bridge`.
3. `Ctrl+Shift+P` → **"Raspberry Pi Pico: Import Project"** → bu klasörü seç.
4. SDK sürümü sorulursa **en güncel kararlı** (örn. 2.x) seç → eklenti SDK +
   toolchain'i indirir (birkaç dk, internet gerekir). Bu adım `PICO_SDK_PATH`'i
   ve derleyici yolunu otomatik ayarlar.

> Alternatif (komut satırı): `pico-sdk`'yı klonla, `PICO_SDK_PATH` ortam
> değişkenini ayarla, sonra "Derleme" bölümündeki B yolunu izle.

---

## 2) Derleme

**A) VS Code (önerilen):** Alt baruçtaki **"Compile"** düğmesine bas. Çıktı:
`build/rp2040_bridge.uf2`.

**B) Komut satırı:**
```powershell
$env:PICO_SDK_PATH = "C:\path\to\pico-sdk"
cmake -G Ninja -B build
ninja -C build
```
Çıktı yine `build/rp2040_bridge.uf2`.

---

## 3) Flash (cihaza yükleme)

1. RP2040 Zero'da **BOOT** düğmesini basılı tut.
2. Basılıyken USB'yi PC'ye tak (zaten takılıysa basılıyken **RESET**'e bas/bırak).
3. Bilgisayarda **`RPI-RP2`** adlı bir disk belirir.
4. `build/rp2040_bridge.uf2` dosyasını bu diske **sürükle-bırak**.
5. Cihaz otomatik yeniden başlar.

> İleride picoprobe (2. Pico) ile SWD üzerinden tek tıkla "Run/Debug" da kurulabilir.

---

## 4) Doğrulama (Faz 0 başarı kriteri)

- **Aygıt Yöneticisi → Bağlantı Noktaları (COM & LPT)**'te yeni bir **COM portu**
  görünür (USB Serial Device).
- Bir seri terminal aç (VS Code'un seri monitörü, PuTTY, vb.) → baud önemsiz →
  saniyede bir şu satır akar:
  ```
  RP2040 bridge alive #0  (t=... us)
  RP2040 bridge alive #1  (t=... us)
  ```

Bunu gördüysen: toolchain ✓, USB ✓, flash ✓ → **Faz 1'e geçebiliriz.**

---

## Notlar / sorun giderme

- **OneDrive uyarısı:** Bu repo OneDrive klasöründe. Derleme `build/` altında
  binlerce dosya üretir; OneDrive bunları senkronlamaya çalışıp dosya kilidi /
  yavaşlık yapabilir. Sorun yaşarsan derleme sırasında OneDrive'ı duraklat
  (`build/` zaten `.gitignore`'da).
- **Defender/SmartScreen** indirilen toolchain'i ilk çalıştırmada yavaşlatabilir;
  izin ver.
- `.vscode/` ve `build/` git'e girmez (makineye özel + üretilmiş).

## Yapı (şu an / hedef)

```
firmware/rp2040-bridge/
├─ CMakeLists.txt          # build tanımı
├─ pico_sdk_import.cmake   # SDK locator (standart boilerplate)
├─ .gitignore
├─ README.md               # bu dosya
└─ src/
   └─ main.c               # Faz 0: CDC heartbeat (geçici)

# Faz 1+ eklenecek:
#   src/usb_descriptors.c  # composite: HID kbd + mouse + vendor
#   src/tusb_config.h      # TinyUSB ayarları
#   src/protocol.h         # PC↔cihaz vendor-HID komut sözleşmesi
#   src/scheduler.c        # core1 combo zamanlayıcı
```
