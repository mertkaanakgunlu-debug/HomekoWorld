# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-13 (15.tur — **Faz 6'nın canlı testinden İKİ YENİ kök neden bulunup düzeltildi,
"[Random] Wild Tyon" sorusu netleşti, düzeltmeler CANLI TEST EDİLDİ ("son test başarılı göründü" — kullanıcı)
VE HER İKİ installer (CUDA+DirectML) YENİDEN DERLENİP DAĞITIMA HAZIR HÂLE GETİRİLDİ.** Zincir: kullanıcı
Faz 6'yı test etti ("ilk başlarda hiç saldırmadı… HUD 'hedef taze karede yok' diyordu") → log+telemetry+
replay analiz edildi (bkz [[farm-targeting-issues]] 15.tur) → 2 kod düzeltmesi (taze-karede-yok artık
tıklamayı iptal etmiyor + guardian kırmızı-flaş debounce) → "[Random] Wild Tyon" belirsizliği kullanıcıya
soruldu, cevap: isim aynı, tek ayırt edici RENK — mevcut sistem doğru, dokunulmadı → **publish edilip canlı
test edildi, kullanıcı "başarılı" dedi** → `release.ps1` ile CUDA+DirectML publish + iki installer derlendi
(yol boyunca `release.ps1`'de gerçek bir bug bulunup düzeltildi — bkz altta). Commit'ler: `711ef3f`(fix)
`8b3bd44`(handoff) `f5d974b`(release.ps1 fix), hepsi PUSH'LANDI.)

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

### 15.tur — Faz 6'nın İLK canlı testi analiz edildi (archer, 7 kısa oturum 20:04-20:35, log+7 replay+telemetry)
Kullanıcı raporu: "ilk başlarda hiçbir moba saldırmadı, hepsini tek tek seçti ama kombo başlatmadı; bir
süre sonra normal çalıştı; HUD 'hedef taze karede yok' diyordu." Log (`guardian-red` oturum başına
%20-70! bazı oturumlarda `deneme=44 guardian-red=31`) + replay kareleri (PowerShell `System.Drawing` ile
piksel/HSV elle analiz edildi, bkz [[farm-targeting-issues]] 15.tur) derinlemesine incelendi. **İKİ AYRI
kök neden** bulundu ve **İKİSİ DE düzeltildi** (build+9 test YEŞİL, henüz publish/canlı-test YOK):

1. **"Hedef taze karede yok" kullanıcının teşhis ettiği gibi gereksiz bir iptal noktasıydı.**
   `TargetAsync`'te tıklamadan HEMEN önce YOLO'nun adayı yeniden bulması ZORUNLU tutuluyordu (bulamazsa
   tıklama hiç atılmadan `return false` — "seçti gibi görünüp saldırmadı" hissi buradan geliyordu) +
   `WaitForSelectedTargetAsync` içinde ayrıca 320ms'lik bir "YOLO izi kayboldu" erken-çıkışı vardı (HP-bar
   YAPISI'nın kendi 450ms bütçesini bitirmeden pes ediyordu). **FIX (`FarmEngine.Targeting.cs`):** YOLO
   adayı yeniden bulamazsa artık SON BİLİNEN konuma yine de tıklıyor (sentetik `Detection`, `WithOffset`);
   `WaitForSelectedTargetAsync` artık YOLO'dan TAMAMEN bağımsız, her zaman tam 450ms bütçeyi kullanıyor —
   hüküm tamamen HP-bar YAPISI + isim rengine bırakıldı (kullanıcının istediği kural: "çerçeve bulunduysa +
   isim kırmızı değilse saldır").
2. **Guardian yanlış-pozitifi YENİDEN bulundu — Faz 6'nın "same-frame = seçim-vurgusu yok" varsayımı
   EKSİKTİ.** Replay piksel kanıtı: AYNI mob tam-HP'de (yeni seçilmiş) KIRMIZI isimle, hasar aldıktan
   sonra (mor/beyaz) NORMAL renkte okunuyordu — vurgu tek-kareyle sınırlı değilmiş, bir süre sürebiliyormuş.
   **FIX (`CheckGuardianAndReturnAsync`):** ilk okuma Guardian derse artık hemen hükmetmiyor — 2 bağımsız
   GDI yeniden-örneği (~110ms arayla); herhangi biri Normal derse saldırıya döner, yalnız TÜM örnekler
   ısrarla Guardian derse hükmediliyor (eski 14.tur'da kaldırılan çoklu-örnek fikri, artık yalnız Guardian
   dalında/az maliyetle geri geldi).

### ✅ ÇÖZÜLDÜ — "[Random] Wild Tyon" ismi guardian mı, etkinlik-etiketi mi?
Replay'lerde aynı anda 5 görsel-özdeş Tyon, farklı trackId'lerde aynı "[Random] Wild Tyon" ismini bazen
KIRMIZI bazen BEYAZ taşıyor görülmüştü — 2026-07-09 (10.tur-d) tarihli tekil-guardian teşhisiyle çelişir
gibi durdu. **Kullanıcı netleştirdi: isim HER İKİ türde de aynı ("[Random] Wild Tyon" normal bir mob adı),
tek ayırt edici İSİM RENGİ — kırmızıysa guardian, değilse normal.** Yani mevcut renk-tabanlı sistem
(referans hue karşılaştırması) zaten doğru tasarım; guardian referans renklerine (NormalNameR/G/B,
GuardianNameR/G/B) DOKUNULMADI/dokunulmayacak. Bugünkü guardian-red patlaması muhtemelen tam da 15.tur'da
bulunan tek-kare kırmızı-flaş sorunuydu (bkz yukarı, KÖK NEDEN 2) — debounce fix bunu hedefliyor.

### Faz 6 (14.tur, hâlâ yürürlükte) — guardian SAME-FRAME + yapı-otoritesi
`WtmVision.ScanTargetBar` isim guardian sınıfını HP-yapısıyla AYNI DXGI karesinde, yapının offset'inde
hesaplıyor (`TargetBarState.NameClass` vb.); `FarmEngine.Targeting` seçim otoritesini çerçeve YAPISINA
bağlıyor. Bu katman DOĞRU/kalıcı — 15.tur yalnız ÜSTÜNE debounce + YOLO-bağımsızlık ekledi, kaldırmadı.

### Dağıtım (15.tur sonu) — installer'lar YENİDEN DERLENDİ, güncel
Kullanıcı publish edip aynı spotta canlı test etti: "son test başarılı göründü" → `release.ps1` tam
zincir (CUDA+DirectML publish + iki ISCC installer) çalıştırıldı. **Yol boyunca `release.ps1`'de gerçek
bir bug bulundu:** `_build-post.bat`'ı GÖRECELİ isimle çağırıyordu; arka-planda/etkileşimsiz PowerShell
host'unda `Set-Location` sonrası `Environment.CurrentDirectory` senkron güncellenmediğinden alt-süreç
(`cmd.exe`) dosyayı bulamayıp "tanınmıyor" hatasıyla yarım kalıyordu (ilk deneme başarısız oldu, exit 1).
**FIX:** `Join-Path $repoRoot "_build-post.bat"` ile tam yol (`f5d974b`) — ikinci deneme baştan sona
YEŞİL geçti. **Üretilen installer'lar (`Output\`):**
- `HomekoWorld_Kurulum_NVIDIA.exe` — 164 MB, SHA-256 `AE712E1AD8FF9E923B0B3B015238424D81743BA73B7C940D7B508606C0CB0BB7`
- `HomekoWorld_Kurulum_DirectML.exe` — 92.7 MB, SHA-256 `D2D292563D5E41FE6045674B1B94D1C0891266D7543A7DF0BC0FA1407D40DB94`

İkisi de sürüm 1.0.2, commit `8b3bd44` kaynaklı (15.tur'un iki targeting-fix'ini + `[Random] Wild Tyon`
netleştirmesini içeriyor — `release.ps1` fix'i `f5d974b` yalnız script'i etkiler, exe içeriğini değiştirmez).

- Git: main = origin/main. Commit'ler: `711ef3f` `8b3bd44` `f5d974b` — hepsi PUSH'LANDI.
- Versiyon 1.0.2 hâlâ tek-kaynak; installer'lar artık 15.tur'a kadar GÜNCEL.

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **CUDA hash-pinning (backlog):** gerçek zip dosyalarının SHA-256'sı olmadan gömülü sabit yazılamazdı.
2. **prefer_nhwc gerçek ölçümü (backlog):** `tools/yolo_trainer/replay_benchmark.py` henüz YAZILMADI.
3. Müşteriye installer dağıtımı: `Output\HomekoWorld_Kurulum_NVIDIA.exe`/`_DirectML.exe` hazır — kullanıcı
   ne zaman/nasıl dağıtacağına karar verir (kod tarafında engel yok).
4. Otonom v2 (BEKLEMEDE — kullanıcı: acelesi yok).

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
