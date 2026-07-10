# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-10 (12.tur sonu — hedef KULLANICI ONAYLI; installer 1.0.2 hazır;
sonraki session: adım-bazlı ms telemetrisi)

---

## 1. PROJE HEDEFİ (goal) — ✅ KULLANICI ONAYLADI (2026-07-10)

**Ürün:** Knight Online için "FujiMacro" markalı, müşteriye satılan/kiralanan oyun otomasyon aracı
(WPF/.NET 8, YOLO tabanlı görü). Freelance/ticari iş — kullanıcı geliştirici, müşteriler son kullanıcı.

**Ne yapması bekleniyor (kademeli):**
1. **Kombo/Oto Pot** (ÇALIŞIYOR): tuş kombinasyonları + otomatik pot — temel değer.
2. **Oto-Farm = KUSURSUZLUK HEDEFİ (mutlak öncelik):** gözetimsiz saatlerce mob farm. Başarı metriği
   FPS SAYISI DEĞİL: **hiçbir mob track'ten çıkmasın + başarısız tıklama olmasın** (hedef-alma başarı
   oranı maksimum). Maksimum **tutarlılık-hız-verim** asıl önceliktir.
3. **Tam Otonom:** ACELESİ YOK — Oto-Farm kusursuzlaşana dek beklemede. (Kod zinciri tam; NPC/portal
   modeli eğitilmedi.)

**Dağıtım hedefi:** HERHANGİ bir PC'de sorunsuz çalışacak; sabit-GUI kalibreleri (HP-bar, nameplate vs)
müşteriye HAZIR gidecek (kalibrasyon yükü müşteriden alınacak). Müşteriler FARKLI sunucu/istemciler
kullanıyor → ileride sunucu-başına kalibre-profil sistemi gerekecek (şimdilik kullanıcının sunucusu
baked gider, ilk müşteri testleri gösterecek).

**Anti-tespit:** bu aşamada öncelik DEĞİL. İleride ±10ms rastgele humanize eklenecek — bunun altyapısı
adım-bazlı ms telemetrisidir (sonraki session'ın işi).

**İleri taşıma yolları (öncelik sırasıyla):** (1) adım-bazlı ms telemetri → veri-güdümlü optimizasyon +
kayıp-nedeni ayrımı, (2) başarı oranını maksimuma çekme (ceset-sınıfı model kararı VERİYLE verilecek —
kullanıcı kararsız, önce telemetri), (3) çoklu-PC/sunucu dağıtım robustluğu, (4) model kalitesi
(FP16/TensorRT izin verilirse), (5) otonom v2, (6) humanize.

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

1. **SONRAKİ SESSION ANA GÜNDEMİ — adım-bazlı ms telemetrisi (KULLANICI TALEBİ):** botun yaptığı HER
   adımın süresi loglanacak. Format KARARI VERİLDİ: **ikisi birden** — kritik özetler insan-okur loga,
   ham ms'ler ayrı JSONL dosyasına ({ts, adım, hedef, süre_ms, sonuç}). Amaçlar: (a) veri-güdümlü
   optimizasyon, (b) kalan ~3/dk kaybın ayrımı (gerçek-despawn vs ceset vs iz-churn) → ceset-sınıfı
   model kararı bu veriyle verilecek (kullanıcı kararsız), (c) ±10ms humanize altyapısı.
2. **Prep-paralel duman testi** (müşteriye gitmeden 2dk: tespit çalışıyor + perf'te prep 2-3ms mi).
3. **Müşteri dağıtım testi:** installer'lar hazır (Output\, 1.0.2) — farklı çözünürlük/DPI + FARKLI
   sunucu/istemci gerçeği: hazır-kalibre başka sunucuda tutmayabilir → ilk testte görülecek; gerekirse
   sunucu-başına kalibre-profil sistemi tasarlanacak. Koordinat glyph'leri müşteri öğretmeli (V2).
4. Guardian-yoğun bölge geçiş süreleri (boş-tarama+bl dolaşımı) — davranış iyileştirme adayı.
5. FP16 (izin verilirse; ~1.5-2× GPU) → TensorRT (Faz E, büyük iş). Oyun cap=90 denemesi isteğe bağlı
   (fps sayısı artık hedef değil — başarı oranı hedef).
6. Otonom v2 (BEKLEMEDE — kullanıcı: acelesi yok): NPC/portal modeli + kademeli test
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
