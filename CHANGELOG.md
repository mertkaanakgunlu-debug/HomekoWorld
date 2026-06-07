# HomekoWorld - Proje Geliştirme ve Değişiklik Raporu

Bu belge, projeye son dönemde eklenen yeni özellikleri, altyapı değişikliklerini ve yapılan iyileştirmeleri detaylandırmaktadır.

## 1. Otonom Oyuncu (Autonomous Player) Modülü (Yeni!)
Projeye yepyeni bir otonom yönetim sistemi eklendi. Sadece savaş alanında değil, karakterin genel hayatta kalma ve döngüsel işlemlerini de otonom hale getiren bu sistem aşağıdaki özellikleri içeriyor:
- **Otonom Seyrüsefer (Navigasyon):** Karakterin belirlenen rotalarda (Town, farm slotları vb.) otomatik olarak hareket etmesini sağlayan `AutonomousPlayerEngine.Nav.cs` altyapısı kuruldu.
- **Envanter Yönetimi:** `AutonomousPlayerEngine.Inventory.cs` ile karakterin üzerindeki eşyaları algılama, gerekirse çöpe atma veya bankaya koyma gibi işlemlerin altyapısı hazırlandı.
- **Şehir (Town) Mantığı:** Karakter öldüğünde veya tedarik gerektiğinde Town'a dönüp iksir/ok alma, rpr (tamir) yapma gibi süreçleri yönetecek `AutonomousPlayerEngine.Town.cs` modülü eklendi.
- **Arayüz (UI) Entegrasyonu:** Uygulamanın yan menüsüne (Sidebar) **"Otonom" (🤖)** sekmesi eklendi ve `AutonomousPlayerPage` sayfası oluşturularak kullanıcının otonom ayarlarını yönetebileceği bir arayüz sunuldu.

## 2. CUDA Yapay Zeka Mimari Migrasyonu ve Akıllı Yükleyici
Yapay zeka tabanlı ekran okuma ve nesne tespiti performansını en üst düzeye çıkarmak için köklü mimari değişiklikler yapıldı:
- **YOLOv8 ve ONNX Runtime Entegrasyonu:** Ekrandaki objeleri (moblar vb.) algılamak için `OnnxYoloInferrer` ve `WtmVision.cs` modülleri eklendi.
- **CUDA Execution Provider:** Ekran okuma sürelerini (inference) milisaniyeler seviyesine indirmek için NVIDIA ekran kartlarını tam kapasite kullanan CUDA altyapısı projeye dahil edildi (`OrtEpFactory.cs`).
- **Akıllı CUDA İndiricisi:** Kullanıcıların sisteminde gerekli CUDA kütüphaneleri yoksa otomatik olarak indirip kuran, ilerleme çubuğuna sahip modern bir görsel indirme ekranı (`CudaDownloadWindow.xaml`) eklendi.

## 3. Donanımsal Fare/Klavye Simülasyonu (RP2040 Bridge)
Yazılımsal makroların oyun koruma sistemleri (Anti-Cheat) tarafından algılanmasını önlemek amacıyla %100 donanım tabanlı bir iletişim protokolü kuruldu:
- **RP2040 Firmware Desteği:** Raspberry Pi Pico (RP2040) için C dili ile özel bir Firmware (`firmware/rp2040-bridge`) yazıldı. Bu sayede cihaz, bilgisayara donanımsal bir fare/klavye olarak kendini tanıtıyor (HID aygıtı).
- **C# Bridge (Köprü) Altyapısı:** Windows yazılımının, USB üzerinden RP2040 kartına komut gönderebilmesi için `Rp2040HidTransport.cs`, `Rp2040Protocol.cs` ve `TransportRouter.cs` sınıfları oluşturuldu.

## 4. Oto Farm ve Savaş Motoru İyileştirmeleri
Mevcut Farm (otomatik kasılma) sistemi elden geçirildi ve daha akıllı, modüler bir yapıya taşındı:
- **Savaş & Hedefleme Zekası:** `FarmEngine.Combat.cs`, `FarmEngine.Detection.cs` ve `FarmEngine.Targeting.cs` dosyalarında hedef seçimi, önceliklendirme, kombo mekanikleri daha stabil hale getirildi.
- **Durum (State) Yönetimi:** `AppState.cs` ve `JsonStateStore.cs` kullanılarak uygulamanın o anki ayarlarının ve telemetri (ör: DPS, kill sayısı) verilerinin (`FarmTelemetry.cs`) güvenli ve kalıcı şekilde kaydedilip okunması sağlandı.

## 5. Arayüz (UI) ve Kalibrasyon Geliştirmeleri
- **DPI ve Çözünürlük Farkındalığı:** Yüksek çözünürlüklü ve yüksek DPI ölçeklemeli ekranlarda `CalibrationOverlayWindow`'un (ekran okuma çerçevesinin) yanlış yeri göstermesi veya kayması sorunu kökten çözüldü. Windows API'leri (`GetSystemMetrics` ve `PointToScreen`) kullanılarak fiziksel ekran pikselleri ile WPF DIP değerleri kusursuz biçimde hizalandı.
- **Renk ve Tema Düzenlemeleri:** `MainWindow`, `Sidebar` ve diğer pencerelerde renk paletleri ve stiller daha modern hale getirildi (Resource Dictionary düzenlemeleri).
- **Kurulum Aracı (Inno Setup) Düzeltmeleri:** Uygulamanın derlenip son kullanıcıya `setup.exe` haline getirilmesi sırasındaki eksikler (paketleme) `HomekoWorld_Setup.iss` üzerinde giderildi.
