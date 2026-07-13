# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-13 (14.tur — iki dış ChatGPT 5.6 denetimi kaynak koda karşı doğrulandı,
13.tur'un ilk canlı testi (archer, 94sn) analiz edildi, Faz 1-5 + 7-8 UYGULANDI+PUSH'LANDI. Faz 6
canlı-test-verisine bağlı, henüz publish/test edilmedi. Plan dosyası:
~/.claude/plans/bu-bilgiler-nda-yapaca-m-z-ticklish-feigenbaum.md)

---

## 1. PROJE HEDEFİ (goal)

**→ `CLAUDE.md`'de** ("🎯 Ürün Hedefi" bölümü, her session otomatik yüklenir, ✅ kullanıcı onaylı
2026-07-10). Özet: **Oto-Farm'da kusursuzluk (track-kaybı=0, başarısız-tıklama=0) mutlak öncelik,
fps sayısı değil.**

## 2. MİMARİ HARİTA (dosya → sorumluluk)

- `Engine/FarmEngine.*.cs` — farm çekirdeği: Detection (YOLO döngüleri), Targeting (hedef seç/tıkla/
  HP-doğrula), Combat (angajman/kill-onay), Loop (event-driven scanning — 14.tur).
- `Engine/MobTracker.cs` — ByteTrack-lite iz takibi; ölü/guardian damgaları.
- `Services/Yolo/OnnxYoloInferrer.cs` — ORT inference; `OrtEpFactory.cs` — CUDA/DirectML/CPU EP seçimi.
- `Services/Capture/DxgiScreenSource.cs`/`GdiScreenSource.cs` — ekran yakalama.
- `Services/Vision/WtmVision.cs` — HP-bar/nameplate/guardian renk analizi (14.tur: çoklu-örnek +
  topHue tanılaması eklendi).
- `Services/Telemetry/ReplayRecorder.cs` + `TelemetryJsonlWriter.cs` (14.tur, YENİ) — replay + ms-telemetri.
- `tests/HomekoWorld.Tests/` (14.tur, YENİ) — xunit, yalnız MobTracker. `dotnet test tests/HomekoWorld.Tests/HomekoWorld.Tests.csproj`.
- `release.ps1` (14.tur, YENİ) — tek-komut publish+installer zinciri (ELLE çalıştırılır).
- `Engine/AutonomousPlayerEngine.*` — otonom FSM (BEKLEMEDE, öncelik değil).
- Loglar: `Desktop/HomekoWorld_log.txt` (artık ASYNC — 14.tur; batch-yazım, davranış/format aynı).

## 3. GÜNCEL DURUM (2026-07-13 itibarıyla)

### 13.tur P1-P5 paketinin İLK canlı testi (archer, 2026-07-13 06:18, 94sn, replay+log)
- ✅ Tık-kayıp 25→3, alım süresi ~144ms medyan, overlay-açık fps 67-73, replay temiz — S5/P3/P4/P5
  ÇALIŞIYOR.
- ⚠️ Ceset-varsayımı 9→5 (hedef ≤2'ye inmedi) — 14.tur'da kök neden bulundu (bkz aşağı).
- 🔴 **O günün ana bulgusu: guardian yanlış-pozitifleri** — 63 denemenin 35'i guardian-red (%56).
  Ham "%9 başarı" bu karışımın eseri; gerçek tık-isabeti %84 idi (63'ün 49'u tık attı, 41'i HP
  doğruladı). Replay piksel analiziyle KANITLANDI: (a) `barOffsetY=0` okumaları bu sunucudaki kalıcı
  kayan duyuru şeridini tarıyordu (pencere fiilen hep +57'de), (b) seçim-anı geçici kırmızı vurgu
  tek-GDI-okumasını kirletiyordu (DXGI'de saf mor isim, ~200ms sonraki GDI oy=%94-100).

### 14.tur: iki dış ChatGPT 5.6 denetimi doğrulandı + düzeltmeler uygulandı
Rapor 1 (bug/mimari) ve Rapor 2 (performans) kaynak koda karşı satır satır kontrol edildi. Bayat/
yanlış bulgular (overlay-capture, render-coalescing — zaten 13.turda çözülmüştü) elendi. **Faz 1-5 +
7-8 UYGULANDI, BUILD+TEST YEŞİL, HEPSİ PUSH'LANDI** (henüz publish/canlı-test YOK):

- **Faz 1 (`5144012`) — guardian güvenilirliği:** 3-örnekli vurgu-bağışık okuma (herhangi biri Normal
  → Normal) + yapı-teyitsiz offset'te kısa-bl (30sn-bl/iz-damga YOK) + `topHue` tanılaması.
- **Faz 2 (`c0ffe3e`+temizlik) — tutarlılık:** MobTracker **gerçek bug** düzeltildi (inherited-dead
  iz, `trackMatched` dizisi dışında kaldığından doğduğu karede siliniyordu — ceset temiz kimlikle
  canlı doğuyordu); `DeadInheritRadiusPx` 110→130; `mobStillThere`/`PollHpBar` TrackId-önce +
  `!Dead && !Guardian`; tek-vuruş kill kuralı `comboFiredOnce` şartlı. İlk test altyapısı (xunit, 5 senaryo).
- **Faz 3 — gecikme paketi:** async log writer (senkron `File.AppendAllText` → bounded-queue+batch);
  event-driven scanning (`Task.Delay(30)` → `SemaphoreSlim` sinyali, 50ms fallback); RP2040 atomik
  hareket (tek-delta+doğrulama, eski 120px/2ms servo fallback'e düştü — `AtomicMouseMove` anahtarı).
- **Faz 4 — telemetri:** `ClicksIssued/ClicksConfirmed/...` ayrık sayaçlar (HUD yüzdesi artık
  guardian'dan BAĞIMSIZ tık-isabeti); `telemetry/*.jsonl` (acq_attempt/engage_end/pop_gate/gate_defer).
- **Faz 5 — hijyen:** ReplayRecorder dispose-yarışı (worker abandon → queue'ya dokunma); GDI
  BitBlt hata kontrolü; SessionOptions dispose; farm-çalışırken model-swap kilidi; FarmLoopAsync
  artık `_farmTask`'ta bekleniyor.
- **Faz 7 — dağıtım:** CUDA zip'lerine staging+doğrulama+`.dll`-allowlist+çakışma-kontrolü (SHA-256
  YOK — gerçek hash indirmeden bilinmiyor, backlog); `.iss`'ler `ISCC /DAppVer=` alabiliyor;
  `release.ps1` (elle çalıştırılır).
- **Faz 8 — deneysel:** `prefer_nhwc` anahtarı eklendi (varsayılan KAPALI) — `replay_benchmark.py`
  aracı `tools/yolo_trainer/` altında henüz YAZILMADI, gerçek A/B ölçümü YAPILAMADI.

**Faz 6 (freshness gate + HP-onay DXGI birincilliği) KOŞULLU — Faz 1-5 canlı testinden SONRA.**

- Git: main = origin/main. 14.tur commit'leri: `5144012`(F1) `c0ffe3e`+temizlik(F2) → F3/F4/F5/F7/F8
  (sırayla push'landı, `git log --oneline -10` ile görülebilir).
- Versiyon 1.0.2 hâlâ tek-kaynak; installer'lar 13. VE 14.tur commit'lerini İÇERMİYOR.

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **ANA GÜNDEM — Faz 1-5+7-8 canlı testi (henüz yapılmadı).** Publish (Build-Cuda, `release.ps1`
   veya elle) → aynı spotta ~2dk oturum. Beklenenler:
   - **Guardian-red** 35→≤10 (yalnız gerçek kırmızı-isimliler kalmalı); mor-normal moba 30sn-bl
     YAZILMAMALI (`topHue=` mor iken Guardian satırı = hâlâ hata).
   - **Geçiş medyanı** 8129ms→≤2500ms.
   - **Ceset-varsayımı** 5→≤2 (MobTracker düzeltmesi + 130px yarıçapı).
   - **HUD yüzdesi** artık `ClicksConfirmed/ClicksIssued` — eski `%` ile DOĞRUDAN kıyaslanamaz.
   - `telemetry/*.jsonl` dosyasının oluştuğu + dolu olduğu kontrol edilmeli (ilk gerçek kullanım).
2. **Kullanıcı ayarı (kod dışı, test ÖNCESİ yapılmalı):** bu spot için `RegionMobCount=7` → **5**
   (kullanıcı teyidi: 5 normal + 1 guardian). S3 nüfus muhasebesi şu ana kadar hiç tetiklenmedi.
3. Test sonucuna göre: Faz 6 (freshness gate + DXGI-birincil HP-onay) değerlendirilir; eşik ince-ayarı
   gerekebilir (`MotionGatePxPerMs`, `DeadInheritRadiusPx` — ikisi de düşük risk, geri-alması kolay).
4. **CUDA hash-pinning (backlog, Faz 7'de bilinçli atlandı):** gerçek zip dosyalarının SHA-256'sı
   olmadan gömülü sabit yazılamazdı (indirmek açık kullanıcı izni gerektirir). Kullanıcı isterse
   GitHub'dan zip'leri indirip hash hesaplatabilir, kod `CudaDownloadWindow.xaml.cs`'e eklenir.
5. **prefer_nhwc gerçek ölçümü (backlog):** `tools/yolo_trainer/` altına bir `replay_benchmark.py`
   yazılmalı (mevcut replay session'ları üzerinde ONNX Runtime Python API ile GPU p50/p95 + kutu
   eşleşmesi ölçen script) — bu, HANDOFF Faz E'nin (TensorRT/FP16) de ön-koşulu olabilir.
6. Müşteri dağıtım testi: installer'lar hâlâ eski commit'leri içeriyor — `release.ps1` ile yeniden
   derlenmeli (test onayından sonra).
7. Otonom v2 (BEKLEMEDE — kullanıcı: acelesi yok).

## 5. KRİTİK KOMUTLAR / KURALLAR

- Publish (bat'lar `pause` içerir, otomasyonda AYRI çağır — ya da `release.ps1` kullan):
  `dotnet publish src\HomekoWorld\HomekoWorld.csproj -c Release -r win-x64 --self-contained true -p:GpuVariant=Cuda|DirectML -o Build-Cuda|Build-DirectML`
  ardından `_build-post.bat <klasör>` (model+DLL kopyalar).
- Installer: `ISCC.exe HomekoWorld_Setup_Cuda.iss /DAppVer=X.Y.Z` (14.tur: artık dışarıdan verilebilir;
  vermezsen `.iss` içindeki varsayılan kullanılır, eski davranış).
- Test: `dotnet test tests\HomekoWorld.Tests\HomekoWorld.Tests.csproj` (14.tur, YENİ — yalnız MobTracker).
- Publish öncesi HomekoWorld.exe KAPALI olmalı (`release.ps1` bunu otomatik kontrol eder).
- Sürüm: csproj `<Version>` tek kaynak; `.iss`'ler artık ISCC parametresiyle senkron TUTULABİLİR.
- Git: sık commit+push; mesajlar ASCII-Türkçe.
- Ana Başlat (Active/F12) kapalıyken hiçbir mod tuş basmaz (master gate).
- Log analizi: `perf:` satırlarında skip yüksek+fps düşük = ekran az kare sunuyor (normal);
  skip düşük+gpu yüksek = gerçek yavaşlama. Log artık ASYNC — dosyaya yazım ~100ms'e kadar gecikebilir
  (canlı `tail` sırasında normal, farm davranışını etkilemez).
- 14.tur'dan itibaren HUD `%` = `ClicksConfirmed/ClicksIssued` (guardian-bağımsız tık-isabeti);
  eski `AcqSuccesses/AcqAttempts` oturum-özeti satırında hâlâ var (geriye-dönük kıyas).
