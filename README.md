# HomekoWorld · Combo Assistant & Auto Farm

Yapay zeka (YOLOv8) destekli, donanımsal (RP2040) USB HID tabanlı Knight Online kombo ve oto-farm asistanı. 
Anti-cheat sistemlerine karşı maksimum güvenlik sağlamak için tüm tuş ve mouse tıklama sinyalleri **Raspberry Pi Pico (RP2040)** üzerinden donanımsal klavye/mouse olarak gönderilir.

## 🚀 Özellikler

- **Donanımsal HID (RP2040):** Bilgisayara takılan bir Raspberry Pi Pico, sanal COM port üzerinden komutları alır ve gerçek bir USB Klavye/Mouse gibi bilgisayara geri tuş basar.
- **Yapay Zeka Oto-Farm (YOLOv8):** Ekranda beliren mobları tespit eder, otomatik Z seçimi yapar ve yanlarına gidip saldırır.
- **NVIDIA TensorRT Desteği:** RTX ve GTX serisi kartlarda 640x640 veya 960x960 çözünürlükteki yapay zeka modellerini 80-100 FPS hızında çalıştırır. TensorRT motoru arka planda otomatik optimize edilir.
- **Otomatik Kütüphane İndirici:** Uygulama, kullanıcının bilgisayarında CUDA/TensorRT yoksa tek tıkla GitHub üzerinden `cuda_libs.zip` dosyasını çekip kurar.
- **Dinamik HP Bar Takibi (HSV):** Mobların can barlarını görüntü işleme (OpenCV/HSV) ile okur. Mob ölünce diğerine geçer.
- **Otomatik Pot (AutoPot):** Karakterin HP ve MP barını okuyarak belirlediğiniz yüzdelerin altına düştüğünde donanımsal olarak tuş basıp pot basar.
- **Gelişmiş Kombo Motoru:** Priest (Helis), Assassin (Minor) vb. kombolarını kusursuz zamanlamayla uygular.

## ⚙️ Gereksinimler

- Windows 10/11 x64
- .NET 8 Desktop Runtime
- NVIDIA Ekran Kartı (Yapay zeka hızlandırması için RTX 20/30/40 serisi önerilir)
- **Raspberry Pi Pico (RP2040)** donanımı ve USB kablosu.

## 🛠 Kurulum

### 1. RP2040 Firmware (Donanım)
1. Raspberry Pi Pico'yu bootloader modunda (BOOTSEL tuşuna basılı tutarak) bilgisayara takın.
2. `firmware/rp2040-bridge` klasöründe derlediğiniz (veya yayınlanan) `.uf2` dosyasını, bilgisayarımda beliren "RPI-RP2" sürücüsünün içine sürükleyip bırakın.
3. Cihaz kendini yeniden başlatacak ve "HomekoWorld HID" olarak tanınacaktır.

### 2. PC Uygulaması
Projeyi derleyip veya hazır `publish-single` klasöründeki dosyaları kullanabilirsiniz:
```bash
cd src/HomekoWorld
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o ../../publish-single
```
Yayınlanan `HomekoWorld.exe` dosyasını çalıştırın. Uygulama ilk açılışta `mobs.json` ve `.onnx` dosyalarınızı otomatik keşfedecektir.

## 🧠 TensorRT İlk Isınma (Warm-Up)

Uygulama açıldığında, yapay zeka motoru ilk kullanıma özel olarak ekran kartınızın mimarisine göre kendini optimize eder. Bu işlem sırasında arayüzün alt kısmında altın sarısı **dönen bir yükleniyor çemberi** göreceksiniz. Yaklaşık 1-3 dakika süren bu işlem sonrası yazı yeşile dönüp `🚀 TensorRT Hazır` olduğunda Oto-Farm'ı 0 donma ile başlatabilirsiniz.

---

*Geliştirme notu: Android Bluetooth HID köprüsü, performans ve tutarlılık sorunları nedeniyle RP2040 USB HID mimarisi ile değiştirilmiştir.*
