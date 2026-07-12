# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-12 gece (13.tur ÇÖZÜM session'ı — analizden çıkan P1-P5 paketlerinin
TAMAMI UYGULANDI + Build-Cuda publish ALINDI: S5 kamera-hareket kapısı, S3 nüfus muhasebesi,
overlay capture-hariç + çizim seyreltme, tık dış-gecikme inceltme (95ms), ArcherWalkAndFace
log/UI temizliği. Commit'ler `d072d86..2cee3ec` push'lu. **CANLI TEST BEKLİYOR** — protokol
madde 4.1'de. Plan dosyası: ~/.claude/plans/archerwalkandface-*.md)

---

## 1. PROJE HEDEFİ (goal)

**→ `CLAUDE.md`'de** ("🎯 Ürün Hedefi" bölümü, her session otomatik yüklenir, ✅ kullanıcı onaylı
2026-07-10). Bu dosyada tekrar edilmez — nadiren değişen hedef orada, sık değişen durum burada.
Özet tek cümle: **Oto-Farm'da kusursuzluk (track-kaybı=0, başarısız-tıklama=0) mutlak öncelik, fps
sayısı değil.**

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

## 3. GÜNCEL DURUM (2026-07-12 itibarıyla)

- **13.tur P1-P5 tutarlılık+hız paketi UYGULANDI ve PUBLISH EDİLDİ (Build-Cuda 2026-07-12 04:46,
  HUD metrikleri `b96c7e5` dahil):** kamera-hareket kapısı (S5), nüfus muhasebesi (S3), overlay
  capture-hariç + ~30Hz seyreltme, tık 95ms inceltme, archer log/UI temizliği. CANLI TEST BEKLİYOR
  (protokol madde 4.1). Kayıp-nedeni ayrımı için `kayma=` alanı artık her hedef logunda.
- **Performans zinciri çözülmüştü (12.tur):** gpu 9-19ms, fps ekran sunumuna kilitli — normal.
  Overlay-açık fps düşüşünün kökü bulunup düzeltildi (P3): model kendi çizdiği kutuları görüyordu,
  çizim decimation'sızdı.
- **İz stabilitesi bölgeye DUYARLI (13.tur analizi kökeni ayırdı):** %24-38 başarı bandının ana
  yiyicileri kamera-hareketi-sırasında-tık (%74) + ceset tıklamaları — P1/P2 tam bunları hedefliyor.
  Angajman kusursuz (alınan hedeflerin hepsi öldürülüyor); kayıp hedef-ALMA aşamasındaydı.
- **Guardian yanlış-saldırı KAPANDI** (mutlak piksel eşiği 200). Guardian-yoğun bölgede uzun geçişler
  hâlâ olabiliyor (bl-30sn dolaşımı) — açık konu (madde 4.3).
- **Klan bankası:** kod tam, şablon kalibrasyonu kullanıcıda; canlı 0 çalışma henüz.
- **Versiyon 1.0.2** tek-kaynak csproj; Output\ installer'ları 13.tur commit'lerini İÇERMİYOR
  (canlı test onayı sonrası ISCC ile yeniden derle).
- Git: main = origin/main. 13.tur commit'leri: `c774a4f` (analiz docs), `d072d86` (P3), `6072c00` (P1),
  `d796ff6` (P2), `3aedc84` (P4), `2cee3ec` (P5).

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **ANA GÜNDEM — 13.tur P1-P5 paketleri UYGULANDI (2026-07-12), CANLI TEST BEKLİYOR.**
   Arka plan (12:52 testi, log+replay 173 kare): başarı %24, başarısız denemeler oturumun %42'si;
   KN-1 kamera/sahne hareketi sürerken tıklama (kayıpların ~%74'ü — başarılı tıklarda akış ~0.1px/ms,
   kayıplarda 0.4-2.5; kaynak: 180° flip ANİMASYONU + kombo pathing + tık-tetikli yürüme, ArcherWalkAndFace
   DEĞİL — o kurian'da no-op) + KN-2 model cesetleri canlı sanıyor (kamera-sabit 6 kaybın 6'sı; görsel
   kanıt frames 3746/4510/4647). S2a W/H tek-eşik filtresi veriyle zayıfladı (telemetri sinyali kalsın).
   **UYGULANAN PAKETLER (commit'ler, hepsi push'lu + Build-Cuda publish 2026-07-12 04:46):**
   - `d072d86` **P3 overlay:** DetectionOverlay+FarmHud+LogHud `WDA_EXCLUDEFROMCAPTURE` (model artık
     kendi kutularını görmüyor, replay temiz) + çizim latest-wins ~30Hz seyreltme (overlay-açık fps
     düşüşünün ilacı). Yan not: HUD'lar kullanıcının OBS/ekran kaydında da görünmez olur.
   - `6072c00` **P1/S5 kamera-hareket kapısı:** MobTracker.MotionState (px/ms, 150ms pencere) +
     Targeting tık-öncesi kapı (akış>`MotionGatePxPerMs=0.20` VEYA quiet iken ertele; `MotionGateMaxWaitMs=700`
     dolarsa bl'siz vazgeç). Tüm hedef loglarına `kayma=` alanı; özete `kapı: ertelenen/ort/vazgeç`.
     GERİ ALMA: MotionGatePxPerMs=0.
   - `d796ff6` **P2/S3 nüfus muhasebesi:** kill-borcu (Killed'da +1, `MobRespawnMs=24000`'de düşer,
     tavan=RegionMobCount) → beklenen-canlı≤0 iken hareketsiz adaylar tıklanmaz (MovedPx≥45 kesin-canlı
     muaf); teshis satırına `beklenen-canli/borc`; SettingsPage'e 2 ayar (`RegionMobCount=7` spot'a özgü!).
     GERİ ALMA: RegionMobCount=0.
   - `3aedc84` **P4 hız:** ClickPre 15→0, ClickPost 80→0 (migration `ClickDelayTrimMigrated`; ClickAsync
     İÇİ 25/80ms Thread.Sleep'lere kullanıcı kararıyla DOKUNULMADI). Tık başına 95ms kazanç.
   - `2cee3ec` **P5 Archer temizliği:** oturum-başlangıç loguna `efektif=` alanı; archer paneli her
     sınıfta görünür + archer-dışıyken ETKİSİZ uyarısı.
   **CANLI TEST PROTOKOLÜ (aynı bölge, ~2dk, replay+log; taban: %24 başarı / 25 tık-kayıp / 9 ceset-varsayım):**
   - Genel hedef: başarı ≥%50-60, tık-kayıp ≤8, ceset-varsayım ≤2, `geçiş:` medyanı +150ms'i aşmasın.
   - P3: replay karelerinde ÇİZİLİ KUTU OLMAMALI; overlay açık/kapalı fps farkı ≤%10; `[Capture] ...
     hariç tutuldu` log satırları görünmeli. Statik sahnede `skip>0` dönmezse not düş (DWM-present).
   - P1: `hedef-alındı` satırlarında `kayma=` ≤0.20 olmalı; kalan tık-kayıpların `kayma=` değeri düşükse
     eşik kaçırıyor demektir (0.15'e İNDİR), ertelenen çok/geçiş şiştiyse 0.25-0.30'a ÇIKAR.
   - P2: kill dalgası sonrası `nüfus-kapısı` satırları + o pencerede boşa tık olmamalı; respawn'da
     (~25sn) hedefleme kaldığı yerden sürmeli. FARKLI SPOTTA RegionMobCount güncellenmeli (UI'da).
   - P4: `hedef-alındı süre=` medyanı ~230→~140ms; başarı oranı düşmemeli (düşerse ClickPost'u 80'e geri al).
   **Sonraki hamleler (test sonucuna göre):** S2b ceset-sınıfı model (overlay-KAPALI temiz replay'ler
   artık eğitim verisi olabilir; kill-konumu → yarı-otomatik etiket) · ms-telemetri JSONL (format kararı
   verildi; S5 kayma alanı ilk parçası sayılır) · S1/S4 (ucuz tamamlayıcılar, gerekirse).
2. **Müşteri dağıtım testi:** installer'lar Output\'ta 1.0.2 ama 13.tur commit'lerini İÇERMİYOR —
   canlı test onayından sonra ISCC ile yeniden derle. Farklı çözünürlük/DPI + FARKLI sunucu/istemci
   gerçeği: hazır-kalibre başka sunucuda tutmayabilir → ilk testte görülecek; gerekirse sunucu-başına
   kalibre-profil sistemi. Koordinat glyph'leri müşteri öğretmeli (V2).
3. Guardian-yoğun bölge geçiş süreleri (boş-tarama+bl dolaşımı) — davranış iyileştirme adayı
   (S3 nüfus-kapısı boş-tarama iştahını da azaltabilir, canlı testte gözlenecek).
4. FP16 (izin verilirse; ~1.5-2× GPU) → TensorRT (Faz E, büyük iş). Oyun cap=90 denemesi isteğe bağlı
   (fps sayısı artık hedef değil — başarı oranı hedef).
5. Otonom v2 (BEKLEMEDE — kullanıcı: acelesi yok): NPC/portal modeli + kademeli test
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
