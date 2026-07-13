# HANDOFF — Session Devir Dosyası

> **Protokol:** Her session BAŞINDA bu dosya okunur; her session SONUNDA (veya büyük bir iş kapanınca)
> güncellenir. Amaç: memory dosyalarındaki tarihçeyi tekrar tekrar okumadan bağlamı tek dosyadan almak.
> Kısa tut (~150 satır tavan): burası GÜNCEL DURUM özetidir, tarihçe memory'de/git log'da.

**Son güncelleme:** 2026-07-13 (15.tur — **Faz 6'nın canlı testi yapıldı, YENİ kök nedenler bulundu +
düzeltildi**. Kullanıcı Faz 6'yı publish edip test etti: "ilk başlarda hiç saldırmadı, tek tek seçti ama
kombo başlamadı, sonra normale döndü; HUD 'hedef taze karede yok' diyordu." Log+telemetry+replay (bugünkü
7 oturum, 20:04-20:35) derinlemesine incelendi (bkz [[farm-targeting-issues]] 15.tur). **İKİ kod düzeltmesi
yapıldı + build/test YEŞİL (henüz publish/canlı-test YOK)**, ayrıca **kritik bir oyun-bilgisi belirsizliği
bulundu ve kullanıcıya soruldu** (aşağıya bak — "[Random] Wild Tyon" ismi guardian mi yoksa etkinlik-
etiketi mi belli değil, replay kanıtı önceki 2026-07-09 teşhisiyle çelişiyor).)

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

### 🔴 KRİTİK AÇIK SORU — "[Random] Wild Tyon" ismi guardian mı, etkinlik-etiketi mi? (kullanıcıya soruldu)
Replay'lerde (bugünkü ekran görüntüleri) tek seferde 5 GÖRSEL OLARAK ÖZDEŞ Tyon aynı ekranda görüldü,
farklı zamanlarda 9+ FARKLI trackId/pozisyonda "[Random] Wild Tyon" ismi bazen KIRMIZI (guardian-hüküm)
bazen BEYAZ (saldır) okundu — ekranın üstünde bir duyuru/kural şeridi (`hkw.gg/kural...`) vardı. Bu,
**2026-07-09 (10.tur-d) tarihli DOĞRULANMIŞ teşhisle ÇELİŞİYOR**: o gün kullanıcı bilerek TEK guardian'ın
yanında test etmiş ve ismin gerçekten "[Random] Wild Tyon" olduğunu görsel olarak onaylamıştı. Bugünkü
kanıt (aynı anda birden çok, ekranın farklı yerlerinde) tek bir sabit guardian'la uyuşmuyor — ya sunucuda
şu an geçici bir "Wild/Random" etkinliği VAR (birçok Tyon'u geçici olarak bu isimle/renkle etiketliyor,
hepsi VURULABİLİR) ya da bu spotta gerçekten birden fazla guardian var. **Koda DOKUNULMADI** (guardian
rengi/eşiği/mantığı aynı bırakıldı) — bu net cevap gerektirir, yanlış tarafa karar vermek ya gerçek
guardian'a saldırtır ya da etkinlik boyunca farm'ı tamamen durdurur. Kullanıcıya soruldu, cevap BEKLENİYOR.

### Faz 6 (14.tur, hâlâ yürürlükte) — guardian SAME-FRAME + yapı-otoritesi
`WtmVision.ScanTargetBar` isim guardian sınıfını HP-yapısıyla AYNI DXGI karesinde, yapının offset'inde
hesaplıyor (`TargetBarState.NameClass` vb.); `FarmEngine.Targeting` seçim otoritesini çerçeve YAPISINA
bağlıyor. Bu katman DOĞRU/kalıcı — 15.tur yalnız ÜSTÜNE debounce + YOLO-bağımsızlık ekledi, kaldırmadı.

- Git: main = origin/main. 15.tur değişiklikleri HENÜZ COMMIT EDİLMEDİ (kullanıcı onayı + Wild-Tyon
  cevabı bekleniyor — guardian debounce'un davranışı cevaba göre ayarlanabilir).
- Versiyon 1.0.2 hâlâ tek-kaynak; installer'lar 13/14/15.tur commit'lerini İÇERMİYOR.

## 4. AÇIK KONULAR / SIRADAKİ ADIMLAR (öncelik sırasıyla)

1. **ANA GÜNDEM — kullanıcının "[Random] Wild Tyon" sorusuna cevabı bekleniyor** (yukarı bakınız). Cevaba
   göre: (a) "etkinlik etiketi, hepsi vurulabilir" → guardian ref-hue rengi büyük ihtimalle yeniden
   kalibre edilmeli (ya da etkinlik-farkındalı bir istisna eklenmeli), (b) "gerçek guardian, atlanmalı" →
   kod DEĞİŞMEZ, yalnız 15.tur'un debounce+YOLO-bağımsızlık fix'leri canlı testte doğrulanır.
2. **15.tur fix'lerinin canlı testi (henüz yapılmadı).** Publish (Build-Cuda, `release.ps1` veya elle,
   exe kapalı) → aynı spotta oturum. Beklenenler:
   - "Hedef taze karede yok" HUD mesajı ARTIK GÖRÜLMEMELİ (tıklama artık iptal edilmiyor).
   - İlk birkaç saniyede "hiç saldırmama" deseni BİTMELİ (debounce fresh-selection kırmızı flaşını atlar).
   - `[Farm] Guardian kontrol: sonuç=Guardian (2/2 yeniden-örnek doğruladı)` satırı yalnız ISRARLA kırmızı
     kalan hedeflerde görülmeli; "ilk-okuma=Guardian ama yeniden-örnek(...)=Normal" satırı sık görülüyorsa
     15.tur teşhisi (flaş) doğrulanmış olur.
   - `guardian-red` oranı düşmeli (önceki gün bazı oturumlarda %50-70'e varıyordu).
3. **Kullanıcı ayarı (kod dışı, test ÖNCESİ):** bu spot için `RegionMobCount` doğru mu — Wild-Tyon
   cevabına göre 5 normal+1 guardian varsayımı değişebilir.
4. **CUDA hash-pinning (backlog):** gerçek zip dosyalarının SHA-256'sı olmadan gömülü sabit yazılamazdı.
5. **prefer_nhwc gerçek ölçümü (backlog):** `tools/yolo_trainer/replay_benchmark.py` henüz YAZILMADI.
6. Müşteri dağıtım testi: installer'lar hâlâ eski commit'leri içeriyor — test onayından sonra.
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
