# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-10 akşam (13.tur — prep-paralel duman testi GEÇTİ prep=1ms; HUD'a canlı
başarı metrikleri eklendi `b96c7e5`, PUBLISH BEKLİYOR; ana gündem hâlâ ms-telemetri)

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

1. **SONRAKİ SESSION ANA GÜNDEMİ — adım-bazlı ms telemetrisi (KULLANICI TALEBİ):** botun yaptığı HER
   adımın süresi loglanacak. Format KARARI VERİLDİ: **ikisi birden** — kritik özetler insan-okur loga,
   ham ms'ler ayrı JSONL dosyasına ({ts, adım, hedef, süre_ms, sonuç}). Amaçlar: (a) veri-güdümlü
   optimizasyon, (b) kalan ~3/dk kaybın ayrımı (gerçek-despawn vs ceset vs iz-churn) → ceset-sınıfı
   model kararı bu veriyle verilecek (kullanıcı kararsız), (c) ±10ms humanize altyapısı.
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
