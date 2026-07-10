# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-10 (12.tur sonu — 60fps-cap + prep-paralel + installer tazeleme)

---

## 1. PROJE HEDEFİ (goal)

> ⚠ Bu bölüm Claude'un anlayışıdır; kullanıcı onayı/düzeltmesi bekliyor (2026-07-10'da soruldu).

**Ürün:** Knight Online için "FujiMacro" markalı, müşteriye satılan/kiralanan oyun otomasyon aracı
(WPF/.NET 8, YOLO tabanlı görü). Freelance/ticari iş — kullanıcı geliştirici, müşteriler son kullanıcı.

**Ne yapması bekleniyor (kademeli):**
1. **Kombo/Oto Pot** (ÇALIŞIYOR): tuş kombinasyonları + otomatik pot — temel değer.
2. **Oto-Farm** (ANA ODAK, %90+): gözetimsiz saatlerce mob farm — doğru hedef seçimi (guardian'a
   saldırma, cesede tıklama, başka oyuncunun mobunu kapma), hızlı mob-geçişi, envanter dolunca klan
   bankasına boşaltma. "İnsan gibi" akıcı olmalı; müşteri izlerken bot bariz aptallık yapmamalı.
3. **Tam Otonom** (SONRAKİ BÜYÜK HEDEF): farm→envanter dolu→şehre TP→NPC'ye satış+tamir→portal→farm
   noktasına dönüş döngüsü, sıfır müdahale. Kod zinciri tam ama: 2-sınıf NPC/portal YOLO modeli
   EĞİTİLMEDİ + kalibrasyon zinciri müşteri makinesinde kurulmalı.

**"Yeterli" tanımı (Claude'un varsayımı):** Oto-Farm bir müşteri makinesinde 2560×1600 dışı
çözünürlükte de kurulum+kalibrasyonla saatlerce takılmadan çalışıyor ve müşteri şikayet etmiyorsa
v1 yeterli. Otonom şehir döngüsü v2.

**İleri taşıma yolları:** model kalitesi (FP16/TensorRT/daha iyi eğitim seti), tam otonom zincirin
kalibrasyonsuz/robust hale gelmesi (görsel doğrulamalı her adım), çoklu-müşteri dağıtım kolaylığı,
insan-benzerlik (rastgelelik/tempo), RP2040 donanım girdisiyle tespit edilmezlik.

## 2. MİMARİ HARİTA (dosya → sorumluluk)

- `Engine/FarmEngine.*.cs` — farm çekirdeği: Detection (YOLO döngüleri, pipelined üretici/tüketici),
  Targeting (hedef seç/tıkla/HP-doğrula), Combat (angajman/kill-onay), Loop.
- `Engine/MobTracker.cs` — ByteTrack-lite iz takibi; ölü/guardian damgaları, MaxAgeMs=süre-bazlı ömür.
- `Services/Yolo/OnnxYoloInferrer.cs` — ORT inference (zero-copy I/O, paralel preprocess, warmup);
  `OrtEpFactory.cs` — CUDA/DirectML/CPU EP seçimi (`#if HOMEKO_CUDA`).
- `Services/Capture/DxgiScreenSource.cs` — DXGI duplication (bayat-kare atlama `LastFrameWasNew`).
- `Services/Vision/WtmVision.cs` — HP-bar/nameplate/guardian renk analizi (GDI bölge yakalama).
- `Engine/AutonomousPlayerEngine.*` — otonom FSM (Faz 31-41); `WorldNavigator` (koordinat-nav),
  `CoordinateReader` (glyph OCR), `MerchantTrader` (satış/tamir), `TownObjectDetector` (NPC/portal YOLO).
- `ViewModels/MainViewModel.*` — MVVM; `Views/Pages/SettingsPage` — tüm kalibrasyon UI.
- Loglar: `Desktop/HomekoWorld_log.txt` (rotasyon >2MB → .prev). Perf satırı 15sn'de bir + `skip=`;
  açılışta `[YOLO] warmup taban` = saf-GPU referansı.

## 3. GÜNCEL DURUM (2026-07-10 itibarıyla)

- **Performans zinciri ÇÖZÜLDÜ:** kök=oyun-GPU contention. Oyuna 60fps cap → gpu 47→10ms,
  YOLO 21→71-75fps. Prep paralelleştirildi (`cde935f`, canlı test edilmedi) → üretici ~9-10ms bekleniyor.
  90fps için oyun cap'i ≥90 olmalı (bayat-kare atlama fps'i ekran sunumuna kilitler) — trade-off ölçülecek.
- **İz stabilitesi BÜYÜK ÖLÇÜDE ÇÖZÜLDÜ:** tık-sonrası-kayıp 18.8→3.2/dk, hedef-alma %38 (rekor).
  İz ömrü süre-bazlı (750ms, fps-bağımsız).
- **Guardian yanlış-saldırı KAPANDI** (mutlak piksel eşiği 200). Guardian-yoğun bölgede uzun geçişler
  hâlâ olabiliyor (bl-30sn dolaşımı) — açık konu.
- **Klan bankası:** kod tam, şablon kalibrasyonu kullanıcıda; canlı 0 çalışma henüz.
- **Versiyon 1.0.2** tek-kaynak csproj; installer'lar 2026-07-10'da yeniden derlendi (Output\).
- Git: main = origin/main, temiz. Son commit'ler: `ef84049` (10-12.tur birikimi), `1a8bfcc` (MaxAgeMs),
  `cde935f` (prep-paralel).

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **Prep-paralel canlı doğrulama** + isteğe bağlı oyun cap=90 testi (90fps hedefi hâlâ isteniyorsa).
2. **Müşteri dağıtım testi:** installer'lar müşteriye gidecek — farklı çözünürlük/DPI'da kalibrasyon
   akışının sorunsuzluğu kritik (ResolutionMapper master 2560×1600'den ölçekler; koordinat glyph'leri
   müşteri yeniden öğretmeli — DigitGlyphsVersion=2).
3. Guardian-yoğun bölge geçiş süreleri (boş-tarama+bl dolaşımı) — davranış iyileştirme adayı.
4. Kalan ~3/dk tık-sonrası-kayıp: gerçek-despawn vs iz-churn ayrımı yapılmadı.
5. FP16 (izin verilirse; ~1.5-2× GPU) → TensorRT (Faz E, büyük iş).
6. Otonom v2: 2-sınıf NPC/portal modeli eğitimi (kullanıcıda) + uçtan-uca kademeli test
   ([[autonomous-readiness-plan]] memory'de 9-aşama analizi).

## 5. KRİTİK KOMUTLAR / KURALLAR

- Publish (bat'lar `pause` içerir, otomasyonda AYRI çağır):
  `dotnet publish src\HomekoWorld\HomekoWorld.csproj -c Release -r win-x64 --self-contained true -p:GpuVariant=Cuda|DirectML -o Build-Cuda|Build-DirectML`
  ardından `_build-post.bat <klasör>` (model+DLL kopyalar). Kullanıcı testi Build-Cuda\'dan yapar.
- Installer: `ISCC.exe HomekoWorld_Setup_Cuda.iss` / `_DirectML.iss` → `Output\` (AppVersion .iss'te ELLE).
- Publish öncesi HomekoWorld.exe KAPALI olmalı (exe kilidi publish'i sessizce yarım bırakır).
- Sürüm: csproj `<Version>` tek kaynak; .iss AppVersion elle eşitlenir.
- Git: sık commit+push (kullanıcı talimatı — format kazaları iş kaybettirdi); mesajlar ASCII-Türkçe.
- Ana Başlat (Active/F12) kapalıyken hiçbir mod tuş basmaz (master gate).
- Log analizi: `perf:` satırlarında skip yüksek+fps düşük = ekran az kare sunuyor (normal);
  skip düşük+gpu yüksek = gerçek yavaşlama. `warmup taban` ~8-12ms olmalı (CUDA sağlıklı işareti).
