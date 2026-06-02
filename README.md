# Knight Online Combo Assistant

WPF + Android BT HID hibrit kombo sistemi. Anti-cheat güvenli: tüm tuş sinyalleri gerçek Bluetooth HID klavye olarak gönderilir.

## Gereksinimler

### PC Tarafı
- Windows 10/11 x64
- .NET 8 Runtime (SDK veya Desktop Runtime)
- Bluetooth adaptörü (dahili veya USB dongle)
- Visual Studio 2022 veya `dotnet build`

### Telefon Tarafı
- Android 9+ (API 28+)
- Bluetooth desteği
- Android Studio (derleme için)

---

## Kurulum

### 1. Android Uygulaması (HomekoBridge)

```bash
cd android/HomekoBridge
# Android Studio'da aç → Build APK → telefona yükle
# veya:
./gradlew assembleDebug
adb install app/build/outputs/apk/debug/app-debug.apk
```

#### Bluetooth eşleştirme
1. Telefon: Ayarlar → Bluetooth → **Yeni cihaz ekle / Bağlantı bekleniyor** moduna al
2. PC: Ayarlar → Bluetooth → "HomekoBridge Keyboard" cihazını eşleştir
3. Eşleştirme onaylan (PIN gerekmeyebilir, "Klavye" profili olarak görünmeli)

### 2. C# WPF Uygulaması

```bash
cd src/HomekoWorld

# Font dosyalarını Resources/Fonts/ klasörüne ekle:
# Cinzel-Regular.ttf, Cinzel-SemiBold.ttf
# Inter-Regular.ttf, Inter-Medium.ttf, Inter-SemiBold.ttf, Inter-Bold.ttf
# JetBrainsMono-Regular.ttf, JetBrainsMono-Medium.ttf
# (Google Fonts'tan ücretsiz indirilebilir)

dotnet restore
dotnet build -c Release
dotnet run
```

---

## Kullanım

1. **Telefonu** aç → HomekoBridge → Başlat (IP ekranda görünür)
2. **PC uygulaması** → sol üstte IP ve port gir → **Bağlan**
3. Status bar'da "Bağlı" yazısını gör
4. **F12** ile aktif et (veya toolbar butonu)
5. Bir kombo satırına tıkla → **Düzenle** → Tuş Atama kartına tıkla → tuşa bas
6. Knight Online aç, aynı tuşa bas → telefon BT üzerinden tuş sinyalini iletir

---

## Protokol (PC ↔ Telefon TCP)

```
PC → Telefon:   TAP:F1         (tuşa bas ve bırak)
                HOLD:Shift     (modifier bas ve tut)
                RELEASE:Shift  (modifier bırak)
                PING           (bağlantı testi)
Telefon → PC:   PONG           (PING yanıtı)
```

---

## Sorun Giderme

| Sorun | Çözüm |
|-------|-------|
| PONG alınamadı | Telefon ve PC aynı WiFi'da mı? Güvenlik duvarı 5556 portu açık mı? |
| BT HID bağlantı yok | Telefonu PC'den kaldır, tekrar eşleştir |
| BluetoothHidDevice desteklenmiyor | Telefon Android 9+ ve OEM HID desteği gerekli. Bazı MIUI/OneUI sürümlerinde kısıtlı olabilir. |
| Tuşlar yanlış çıkıyor | `KeyMap.kt`'deki HID usage kodlarını kontrol et. Oyun içi slot atamaları `DefaultData.cs`'deki adım tuşlarıyla eşleşmeli. |

---

## Faz Durumu

- [x] Faz 0 — Proje iskeleti, tema, stiller
- [x] Faz 1 — TCP köprüsü (HidBridgeClient), Android BT HID uygulaması
- [x] Faz 2 — Global hook, veri modeli, JSON store, varsayılan Rogue komboları
- [x] Faz 3 — Kombo motoru (async), binding dispatcher
- [x] Faz 4 — Temel WPF UI (sidebar, workspace, kombo listesi, editor, status bar)
- [ ] Faz 5 — Custom combo builder, drag-drop reorder
- [ ] Faz 6 — Sağ rail panelleri (SkillBar, Overlay, Stats)
- [ ] Faz 7 — Profil sistemi (PK/Farm/Solo/CSW)
- [ ] Faz 8 — i18n TR/EN, polish, hata durumları
