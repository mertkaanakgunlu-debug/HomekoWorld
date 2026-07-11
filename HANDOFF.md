# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-11 öğleden sonra (log+replay ANALİZ session'ı — 12:52 testi, 173 kare
replay ile İKİ kök neden VERİYLE AYRIŞTIRILDI ve ölçüldü: KN-1 kamera hareketi sırasında tıklama
(kayıpların ~%74'ü), KN-2 modelin cesetleri canlı sanması (kamera-sabit kayıpların 6/6'sı; görsel
kanıt karelerde). Yan bulgu: overlay capture'dan hariç değil — model kendi kutularını görüyor,
düzeltme chip'i açıldı. Detay: madde 4.1.)

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

## 3. GÜNCEL DURUM (2026-07-10 itibarıyla)

- **Performans zinciri ÇÖZÜLDÜ + prep-paralel DOĞRULANDI (17:03 testi):** perf'te prep 4→1ms,
  gpu 9-19ms, fps 51-63 (ekran sunumuna kilitli, normal). Duman testi kapandı.
- **İz stabilitesi bölgeye DUYARLI:** önceki testte 3.2/dk kayıp + %38 başarıydı; 17:03 testi
  guardian/ceset-YOĞUN bölgede %17'ye düştü (2:16'da 19 guardian-red, 36 tık-kayıp, 21 ceset-varsayım;
  alınan 17 hedefin 17'si öldürüldü — angajman kusursuz, kayıp hedef-ALMA aşamasında). Kayıp-nedeni
  ayrımı (gerçek-despawn vs churn) ms-telemetri verisiyle yapılacak. İz ömrü süre-bazlı (750ms).
- **HUD canlı metrikleri eklendi (`b96c7e5`):** Ana HUD genişletilmiş bölümde 🎯 başarı %/oran,
  🛡 guardian-red, ort. geçiş, kayıp; stop-donması (durunca FPS asılı kalıyordu) + pause-süresi
  düzeltildi. **Build-Cuda publish ALINAMADI (exe açıktı) — müşteri exe'sinde henüz YOK.**
- **Guardian yanlış-saldırı KAPANDI** (mutlak piksel eşiği 200). Guardian-yoğun bölgede uzun geçişler
  hâlâ olabiliyor (bl-30sn dolaşımı) — açık konu.
- **Klan bankası:** kod tam, şablon kalibrasyonu kullanıcıda; canlı 0 çalışma henüz.
- **Versiyon 1.0.2** tek-kaynak csproj; installer'lar 2026-07-10'da yeniden derlendi (Output\).
- Git: main = origin/main, temiz. Son commit'ler: `ef84049` (10-12.tur birikimi), `1a8bfcc` (MaxAgeMs),
  `cde935f` (prep-paralel).

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **ANA GÜNDEM — hedef-alma başarısızlığı: İKİ KÖK NEDEN VERİYLE AYRIŞTIRILDI (2026-07-11 12:52
   testi: log + replay 173 kare @~507ms, YOLO Box overlay AÇIKken; analiz scriptleri scratchpad'de).**
   Test özeti: 87sn, 15 kill, deneme=62 başarı=%24; başarısız denemeler oturumun **%42'sini** yedi
   (tık-kayıp 25 olay→17.0sn, ceset-varsayımı 9→13.7sn, guardian-red 11→4.9sn, taze-karede-yok 1→0.7sn).
   - **KN-1 — kamera hareketi sırasında tıklama (kayıpların ~%74'ü):** başarılı 15 tıkta tık-anı
     kare-arası medyan kayma **54px**/507ms; kayıp 23 tıkta **334px** (208-1281px). Seçim→tık zinciri
     ~250ms; bu sürede ekran 100-600px kayıyor → bayat koordinata tık ıskalıyor + iz IoU kopuyor
     ("iz-kayıp"). 172 kare-geçişinin 72'si >80px — ArcherWalkAndFace kamerayı sürekli döndürüyor.
     19/25 kayıpta tıklanan noktada ±500ms örneklemde hiç det yok (nokta çoktan kaymış).
   - **KN-2 — model cesetleri canlı sanıyor (kamera-SABİT 6 kaybın 6'sı ceset):** görsel kanıt
     frames 3746/4510/4647 — yatık cesetlere 0.74-0.91 conf kutu; iki üst üste ceset tek kutu bile.
     15 kill/87sn → yerde sürekli 2-4 ceset; taramadaki "canlı" sayıları şişik. Ceset-varsayımı
     2-tık ceremesi olay başına ~1.5sn.
   - **S2a (W/H yatıklık filtresi) ZAYIFLADI:** canlı-ref medyan 1.65 / p75 1.88 / max 2.74 (Tyon
     zaten geniş mob); ceset örnekleri 2.2-2.5 AMA kamera açısına göre 1.18'e düşebiliyor → tek-eşik
     filtre yanlış-pozitif üretir. Telemetri sinyali olarak kalsın, filtre olarak DEĞİL.
   **Güncel çözüm sıralaması (etki sırasıyla; plan sonraki session):**
   - **S5 (YENİ, veriden doğdu — EN GÜÇLÜ):** tık-anı kamera-hareket kapısı: son kare-arası medyan
     kayma > ~80px iken tıklamayı ertele (kamera duraksamasını bekle) + tık koordinatını en-taze
     kareden yeniden çöz. Başarılı tıkların p75=61px → 80px eşiği doğal ayırıcı. KN-1'i hedefler.
   - **S3 (nüfus muhasebesi):** 7 normal sabit + kill-borcu(~25sn respawn) → beklenen-canlı=0 iken
     tıklama iştahını kes. KN-2'nin kamera-sabit ceset tıklarını keser.
   - **S2b (ceset-sınıfı model) GÜÇLENDİ:** görsel kanıt ikna edici (kullanıcı şüpheliydi → kareleri
     gör). Etiket yarı-otomatik: kill-onayı konumu civarı kutular ceset adayı; overlay-KAPALI replay şart.
   - S1 (ölü izleri MaxAgeMs'ten muaf) + S4 (flip'te konum-bl temizle): ucuz, kısmi fayda — kamera
     sabitken flicker/bl kirlenmesini azaltır; tek başına yetmez.
   - Guardian gerçeği değişmedi: tıklama-öncesi sinyal yok → flip-sonrası ilk dokunuş kaçınılmaz.
   **YAN BULGU (KRİTİK):** `DetectionOverlayWindow` capture'dan hariç DEĞİL (SetWindowDisplayAffinity
   çağrısı yok) → YOLO Box açıkken model KENDİ çizdiği kutuları girdi olarak görüyor; replay kareleri
   eğitim/benchmark için KİRLİ. Düzeltme chip'i açıldı (WDA_EXCLUDEFROMCAPTURE, task_bbcd865b).
   Overlay-açık testler önceki testlerle tam karşılaştırılamaz (bugünkü %24 buna maruz).
   **ms-telemetri (format kararı verildi, hâlâ yapılacak):** insan-okur özet + JSONL
   ({ts, adım, hedef, süre_ms, sonuç} + kutu W/H + iz-doğum/ölüm + **tık-anı kamera-kayması**);
   yazıcı System.Threading.Channels. S5 eşik kalibrasyonunu ve S2a dağılımını besler.
   **Teşhis protokolü İŞLEDİ:** replay+log birlikte toplama bu analizi mümkün kıldı — sürdür.
2. **Publish (Build-Cuda) — HUD değişikliği müşteri exe'sine girsin** (exe kapalıyken; sonra
   installer yeniden derlenirse Output\ da tazelenir).
3. **Müşteri dağıtım testi:** installer'lar hazır (Output\, 1.0.2 — ama HUD commit'i İÇERMİYOR,
   publish sonrası yeniden derle) — farklı çözünürlük/DPI + FARKLI
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
