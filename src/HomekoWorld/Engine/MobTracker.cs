using System;
using System.Collections.Generic;
using System.Drawing;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Engine;

/// <summary>
/// Hafif "ByteTrack-lite" mob takipçisi — zaten hesaplanmış YOLO kutularına kalıcı kimlik (TrackId) atar.
/// EKSTRA inference/GPU YOK; kare başına ~mikrosaniye (ekranda birkaç mob). Çözdüğü sorunlar:
///  • Ceset yok sayma (#3): öldürülen iz <see cref="MarkDead"/> ile "ölü" damgalanır; ceset tespit edilmeye
///    devam etse de aynı ize eşlenir → çıktıda Dead=true ile aday listesinden elenir. Despawn olunca iz düşer.
///  • Doğru overlay (#4): aktif hedef TrackId ile vurgulanır → "en yakın aynı-tür kutu" kayması biter.
///  • Stabil hedef (#2): kare-kare aynı moba aynı kimlik → yakınlık puanı zıplamaz.
/// Eşleme: sabit-hız tahmin + IoU greedy, iki-geçiş (önce yüksek-güven, sonra düşük-güven kutular).
/// Thread: <see cref="Update"/> tespit thread'inden, <see cref="MarkDead"/> combat thread'inden çağrılır → iç kilit korur.
/// </summary>
public sealed class MobTracker
{
    /// <summary>Ölü damgasının kaynağı — 2026-07-04 (7.tur) itibarıyla SALT TEŞHİS: hareket-tabanlı diriliş
    /// KALDIRILDI (bkz sınıf-altı not), ölü damgası kaynaktan bağımsız KALICIDIR. Kaynak bilgisi loglarda
    /// "bu damga ne kadar güvenilir" sorusuna cevap vermek için tutulur (Confirmed=kesin, Assumed/Inherited=tahmin).</summary>
    public enum DeadSource
    {
        None      = 0,
        /// <summary>Combat kill-onayı (hasar görüldü + pencere kayboldu) — kesin.</summary>
        Confirmed = 1,
        /// <summary>TargetAsync ceset-varsayımı (2 tık HP üretmedi) — tahmin (yanlışsa kamera-flip churn'ü telafi eder).</summary>
        Assumed   = 2,
        /// <summary>miras-DEAD (ölü izin 110px yakınında doğdu) — tahmin (yanlışsa kamera-flip churn'ü telafi eder).</summary>
        Inherited = 3,
    }

    private sealed class Track
    {
        public int        Id;
        public int        ClassId;
        public RectangleF Box;        // son eşleşen kutu (ekran uzayı)
        public float      Vx, Vy;     // merkez hızı (px / kare) — sabit-hız tahmini
        public int        Age;        // toplam eşleşme sayısı
        public int        Missed;     // ardışık eşleşmeme sayısı
        public bool       Dead;       // öldürüldü → aday dışı
        public DeadSource Source;     // ölü damgasının kaynağı (salt teşhis — damga kaynaktan bağımsız kalıcı)
        public long       DeadAtMs;   // ölü damgasının vurulduğu an (ömrü sınırlamak için)
        public long       LastSeenMs;
        public bool       Guardian;   // koruma mobu damgası (2026-07-03) → aday dışı (iz yürüse de takip eder)
        public float      AnchorCx, AnchorCy; // yer-değiştirme çapası (doğum merkezi; global hareketle kayar)
        public float      RelDispPx;  // çapaya göre NET yer değiştirme (kamera kayması medyanla düşülmüş)
        // 2026-07-04 (6.tur, tekil-kare sıçrama koruması) — Anchor'dan FARKLI: "bir önceki EŞLEŞEN kare"
        // konumu. Anchor "ölüm/doğum çapası" (yalnız MarkDead/doğumda sıfırlanır); bu ise HER eşleşen karede
        // güncellenir — ardışık kareler arası tekil-sıçrama tespiti için.
        public float      LastMatchedCx, LastMatchedCy;
        public bool       HasLastMatched; // ilk eşleşmede önceki-kare yok — sıçrama kontrolünü atla
    }

    private readonly object      _lock   = new();
    private readonly List<Track> _tracks = new();
    private int  _nextId = 1;
    private long _lastMs = -1;
    private long _quietUntilMs = -1; // 9.tur: SuppressMotionCredit penceresi (bot-kamera hareketi)

    /// <summary>Eşleme IoU eşiği (kare-kare aynı moba kalıcı kimlik).</summary>
    public float IouThreshold { get; set; } = 0.3f;
    /// <summary>Bir iz kaç kare eşleşmezse düşürülür (ceset/kayıp izlerin ömrü).</summary>
    public int   MaxAgeFrames { get; set; } = 15;
    /// <summary>İki-geçiş eşiği: ≥ bu güven = "yüksek-güven" (ilk geçişte eşlenir).</summary>
    public float HighConf     { get; set; } = 0.5f;
    /// <summary>IoU başarısız olunca MERKEZ-mesafesiyle eşle: izin verilen kayma = bu × kutu-boyutu. Kamera/karakter
    /// hareketinde kutu ötelenip IoU kopsa da aynı iz korunur → ID churn'ü (ve ölü-damga kaybı) azalır. 0 = kapalı.</summary>
    public float CenterMatchFactor { get; set; } = 1.0f;
    /// <summary>Ölüm anından bu kadar ms sonra: iz bu karede ARTIK EŞLEŞMİYORSA (ceset gerçekten kayboldu/
    /// despawn oldu) hemen düşürülür (aynı yere respawn yeni iz alsın diye). Ceset HÂLÂ eşleşiyorsa (görünürse)
    /// bu süre onu düşürmez — yalnız görünürlüğü kesilince devreye giren bir üst sınırdır, "toplam ölü ömrü" değil.</summary>
    public int   DeadLingerMs { get; set; } = 12_000;
    /// <summary>Ölü iz'in bu yarıçapı (px, merkez) içinde doğan YENİ (aynı tür) iz de "ölü" sayılır. Mob ölünce
    /// düşme animasyonu + ~85px düşüş bbox'ı değiştirip IoU'yu koparır → ceset yeni kimlik alır; bu köprü onu
    /// yine "ölü" tutar (tekrar tıklama bitsin). Çok büyük tutma: yakın respawn'ı yanlışlıkla elemesin.</summary>
    public int   DeadInheritRadiusPx { get; set; } = 110;

    /// <summary>İsteğe bağlı teşhis günlüğü (null = kapalı). FarmEngine bunu Program.Log'a bağlar; YALNIZ seyrek
    /// olaylar yazılır (miras-DEAD tetiklendi: yeni doğan kutu yakındaki ölü izin "ölü" damgasını devraldı) → spam yok.</summary>
    public Action<string>? Log { get; set; }

    private const float VelEma = 0.5f;

    // ── Hareket-tabanlı canlılık sabitleri — ayar şişkinliği yerine dokümante const. ──
    // 2026-07-04 (7.tur): HAREKET-TABANLI DİRİLİŞ (ResurrectMovePx, 4.tur'da eklenmişti) KALDIRILDI —
    // 3.tur'un kullanıcı-doğrulamalı "ölü kalıcıdır" semantiğine dönüş. Kanıt (57dk canlı log): dirilişlerin
    // %91'i (32/35) "varsayım" kaynaklıydı = 2-tık-HP-üretmedi ile damgalanan GERÇEK cesetler, kompanzasyon
    // hatası birikimiyle (<2 eşleşen izde kompanzasyon atlanır; W-yürüyüşü tüm ekranı kaydırır) 60px "hareket"
    // toplayıp diriliyor ve TEKRAR TIKLANIYORDU. Eşik 45→60 artışı oranı HİÇ değiştirmedi (0.467→0.479/kill) —
    // sorun eşik değil, RelDispPx'in az-izli sahnede güvenilmezliği. Dirilişin koruduğu senaryo (yanlış-ölü
    // CANLI mob) zaten kamera-flip churn'üyle kendiliğinden düzelir: flip'te IoU kopar, eski ölü iz ≤15 karede
    // düşer, mob TEMİZ kimlik alır (canli=0 ise flip ScanIdleMs=2sn'de gelir → en kötü ~2-6sn duraksama).
    /// <summary>Bu kadar NET yer değiştiren aday, konumsal ölü-blacklist filtresinden MUAF tutulur
    /// (FarmEngine tarama filtresi kullanır): hareket eden aday kesin canlıdır. (İz'in Dead BAYRAĞINI
    /// etkilemez — ölü iz hareket etse de aday olmaz; muafiyet yalnız konum-listesi içindir.)</summary>
    public const float MovingAliveExemptPx = 45f;
    /// <summary>Bir karede (≈33fps, 30ms tick — bkz FarmEngine.Loop.cs tick gecikmesi) bu kadar NET
    /// (kompanzasyon-sonrası) piksel sıçrayan iz FİZİKSEL OLARAK İMKÂNSIZDIR (KO mob yürüyüş hızı bunun çok
    /// altında) — ya kamera-flip (CameraScanStepAsync 180° orta-tuş, ScanIdleMs=2000 ile sık tetiklenir)
    /// uncompensated (&lt;2 eşleşen izle medyan atlanır) ya da tekil ölçüm gürültüsü. Böyle bir sıçrama
    /// RelDispPx'e KREDİLENMEZ — çapa sessizce bu kareye resync edilir. 7.tur'dan itibaren RelDispPx'in tek
    /// tüketicisi MovedPx (konumsal-blacklist hareket-muafiyeti, FarmEngine tarama/loot filtreleri): bu koruma,
    /// kamera-flip artefaktlarının cesede sahte "hareket" yazıp onu konum-listesinden kaçırmasını önler.
    /// Kanıt (2026-07-04): tek karede 220-1331px "sıçramalar" gözlendi (biri 591px/210ms ≈ 2955px/sn,
    /// imkânsız). 220px/kare ≈ 33fps'te ~7260px/sn tavan — gerçek yürüyüşü asla tetiklemez.</summary>
    public const float MaxPerFrameJumpPx = 220f;

    /// <summary>Oturum içi diriliş sayısı. 7.tur'dan itibaren diriliş mekanizması KALDIRILDI — bu sayaç hep 0
    /// kalır; oturum-özeti log formatı bozulmasın diye tutuluyor (diriliş=0 = mekanizmanın kapalı olduğunun
    /// canlı doğrulaması).</summary>
    public int ResurrectionCount { get; private set; }

    /// <summary>Verilen ize "ölü" damgası vurur — damga KALICIDIR (7.tur: hareket-tabanlı diriliş kaldırıldı).
    /// Kaynak salt teşhis: Confirmed (combat kill-onayı, varsayılan — kesin) veya Assumed (TargetAsync
    /// ceset-varsayımı — tahmin; yanlışsa kamera-flip churn'ü izi düşürüp temiz kimlik verir).</summary>
    public void MarkDead(int trackId, DeadSource source = DeadSource.Confirmed)
    {
        if (trackId < 0) return;
        lock (_lock)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Id == trackId)
                {
                    _tracks[i].Dead     = true;
                    _tracks[i].Source   = source;
                    _tracks[i].DeadAtMs = _lastMs;
                    // Damga anında çapayı sıfırla: MovedPx (konumsal-blacklist muafiyeti) "ölü damgasından
                    // SONRAKİ hareket"i ölçsün — angajmanda birikmiş meşru yaklaşma hareketi taşınmasın.
                    _tracks[i].AnchorCx  = _tracks[i].Box.X + _tracks[i].Box.Width  / 2f;
                    _tracks[i].AnchorCy  = _tracks[i].Box.Y + _tracks[i].Box.Height / 2f;
                    _tracks[i].RelDispPx = 0f;
                    break;
                }
        }
    }

    /// <summary>Verilen ize "koruma mobu" damgası vurur (2026-07-03). Konumsal blacklist'ten farkı: iz mob'u
    /// YÜRÜRKEN takip eder → 60px yarıçap kaçağı (aynı guardian'ın 4× yeniden seçilmesi) biter. Miras YOK:
    /// guardian'ın yanından geçen canlı mob zehirlenmesin; iz churn olursa konumsal liste yedek.</summary>
    public void MarkGuardian(int trackId)
    {
        if (trackId < 0) return;
        lock (_lock)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Id == trackId) { _tracks[i].Guardian = true; break; }
        }
    }

    /// <summary>Bot KENDİ kamerasını oynattı (kamera-scan 180° flip / nav dönüşü / roam yürüyüşü) — bu
    /// pencere boyunca ekran-uzayı kayması izlerin "kendi hareketi" DEĞİLDİR: eşleşen her izin çapası o
    /// karenin konumuna resync edilir, RelDispPx SIFIRLANIR → ceset kamera hareketinden "hareketli=canlı"
    /// konum-blacklist muafiyeti devralamaz (9.tur; kanıt: 180° flip'te iz 1sn'de ~1000px "yürüdü", 90sn
    /// escalation bl'si aynı noktada 5× bypass edildi — teshis satırı spatial-bl=0 hareket-muaf=1).
    /// Ölü damgasına DOKUNMAZ ("ölü kalıcıdır") — yalnız MovedPx muafiyet metriğinin birikimi kesilir.</summary>
    public void SuppressMotionCredit(long untilMs)
    {
        lock (_lock) { if (untilMs > _quietUntilMs) _quietUntilMs = untilMs; }
    }

    /// <summary>Yeni farm oturumunda tüm izleri sıfırla.</summary>
    public void Reset()
    {
        lock (_lock) { _tracks.Clear(); _nextId = 1; _lastMs = -1; ResurrectionCount = 0; _quietUntilMs = -1; }
    }

    /// <summary>Bu karenin tespitlerini izlere eşler; her tespite TrackId/Dead iliştirilmiş YENİ liste döner.</summary>
    public IReadOnlyList<Detection> Update(IReadOnlyList<Detection> dets, long nowMs)
    {
        lock (_lock)
        {
            // 1) Tahmin: her CANLI izi sabit hızla bir adım ilerlet (eşleşmeyen iz coasts → yeniden yakalamayı
            //    kolaylaştırır). Ölü izler (ceset) SABİT tutulur (#4): kaydırılırsa miras çapı cesedin üstünden
            //    kayar → ceset yeni iz alınca "ölü" devralmaz → tekrar tıklanır.
            foreach (var t in _tracks)
            {
                if (t.Dead) continue;
                t.Box = new RectangleF(t.Box.X + t.Vx, t.Box.Y + t.Vy, t.Box.Width, t.Box.Height);
            }

            int n = dets.Count;
            var assignedTrack = new int[n];                 // det -> track index (-1 = yok)
            for (int i = 0; i < n; i++) assignedTrack[i] = -1;
            var trackMatched = new bool[_tracks.Count];

            // 2) İki-geçiş greedy IoU eşleme: önce yüksek-güven, sonra düşük-güven kutular.
            MatchPass(dets, assignedTrack, trackMatched, highConfOnly: true);
            MatchPass(dets, assignedTrack, trackMatched, highConfOnly: false);
            // 2b) IoU eşleşmeyen kalanları MERKEZ-mesafesiyle eşle (yalnız CANLI izler) → kamera/karakter
            //     hareketinde ötelenen kutu yeni ID almak yerine izini korur (churn ↓). Ölü izler hariç:
            //     gevşek merkez eşleşmesi yakından geçen CANLI mobu yanlışlıkla "ölü" yapmasın (IoU'da kalır).
            if (CenterMatchFactor > 0f) MatchByCenter(dets, assignedTrack, trackMatched);

            // 2c) Global hareket kompanzasyonu (2026-07-03): kamera/karakter hareketi TÜM kutuları birlikte
            // kaydırır — ekran-uzayı deltası tek başına "mob hareketi" kanıtı değildir. Eşleşen çiftlerin
            // ölçülen delta'larının bileşen-bazlı MEDYANI ≈ kamera kayması; çapalar bu medyanla birlikte
            // kaydırılır → RelDispPx yalnız mob'un KENDİ net hareketini biriktirir. <2 eşleşen izle medyan
            // güvenilmez → (0,0) varsay — kompanzasyonsuz delta çapaya yazılır (tek-iz sahnede W-yürüyüşü
            // MovedPx'i şişirebilir; 7.tur'dan beri RelDispPx yalnız konum-blacklist muafiyetini besler,
            // ölü damgasını KALDIRAMAZ — en kötü etki muafiyetin yanlış tetiklenmesi, sıçrama-koruması sınırlar).
            float medDx = 0f, medDy = 0f;
            {
                var dxs = new List<float>(n); var dys = new List<float>(n);
                for (int i = 0; i < n; i++)
                {
                    int ti = assignedTrack[i];
                    if (ti < 0) continue;
                    var tr2 = _tracks[ti];
                    float ncx2 = dets[i].BBox.X + dets[i].BBox.Width / 2f;
                    float ncy2 = dets[i].BBox.Y + dets[i].BBox.Height / 2f;
                    // Önceki ÖLÇÜLEN merkez: canlı iz Step 1'de +V ilerletildi → geri al; ölü iz sabit tutuldu.
                    float pcx = tr2.Box.X + tr2.Box.Width  / 2f - (tr2.Dead ? 0f : tr2.Vx);
                    float pcy = tr2.Box.Y + tr2.Box.Height / 2f - (tr2.Dead ? 0f : tr2.Vy);
                    dxs.Add(ncx2 - pcx); dys.Add(ncy2 - pcy);
                }
                if (dxs.Count >= 2)
                {
                    medDx = Median(dxs); medDy = Median(dys);
                    // LastMatchedCx/Cy, AnchorCx/Cy ile AYNI koordinat çerçevesinde kalmalı — aksi hâlde
                    // meşru bir kamera-kaymasında tekil-kare sıçrama koruması (aşağıda, adım 3) yanlışlıkla
                    // tetiklenir (kompanse-edilmiş Anchor'a karşı kompanse-EDİLMEMİŞ LastMatched karşılaştırılırdı).
                    foreach (var t in _tracks) { t.AnchorCx += medDx; t.AnchorCy += medDy; t.LastMatchedCx += medDx; t.LastMatchedCy += medDy; }
                }
            }

            var outArr = new Detection[n];

            // 9.tur: bot-kamera penceresi aktif mi? (bkz SuppressMotionCredit) — kare başına bir kez oku.
            bool camQuiet = nowMs <= _quietUntilMs;

            // 3) Eşleşen tespitleri güncelle; eşleşmeyenlere yeni iz aç.
            for (int i = 0; i < n; i++)
            {
                var d  = dets[i];
                int ti = assignedTrack[i];
                Track tr;
                if (ti >= 0)
                {
                    tr = _tracks[ti];
                    float ncx = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                    // tr.Box şu an TAHMİN edilmiş (Step 1'de +V ilerletildi). Hızı doğru ölçmek için bir önceki
                    // ÖLÇÜLEN merkezi geri al (tahmin − V); yoksa EMA tahminin üstüne kurulur, hız gerçek
                    // hareketin yarısında takılır (yakınsamaz) → tahmin az atar, hızlı mobta ID churn'ü artar.
                    float ocx = tr.Box.X + tr.Box.Width / 2f - tr.Vx, ocy = tr.Box.Y + tr.Box.Height / 2f - tr.Vy;
                    tr.Vx      = VelEma * (ncx - ocx) + (1 - VelEma) * tr.Vx;
                    tr.Vy      = VelEma * (ncy - ocy) + (1 - VelEma) * tr.Vy;
                    tr.Box     = d.BBox;
                    tr.ClassId = d.ClassId;
                    tr.Age++;
                    tr.Missed     = 0;
                    tr.LastSeenMs = nowMs;

                    if (camQuiet)
                    {
                        // 9.tur: bot KENDİ kamerasını oynatıyor (SuppressMotionCredit penceresi) — bu karedeki
                        // ekran-uzayı kayması izin "kendi hareketi" DEĞİL: çapa bu kareye resync edilir, birikim
                        // SIFIRLANIR (pencere içindeki ilk eşleşme flip-ÖNCESİ birikimi de siler). 180° süpürme
                        // ~30px/kare ile MaxPerFrameJumpPx(220)'nin ALTINDA kaldığından tekil-kare koruması bunu
                        // yakalayAMIYORDU; medyan-kompanzasyon da <2 eşleşen izde/rotasyonel paralaksta yetersiz →
                        // ceset sahte MovedPx toplayıp "hareketli=canlı" muafiyetiyle 90sn blacklist'i deliyordu.
                        // Dead bayrağına dokunmaz ("ölü kalıcıdır") — yalnız muafiyet metriği kesilir.
                        tr.AnchorCx = ncx; tr.AnchorCy = ncy;
                        tr.RelDispPx = 0f;
                    }
                    else
                    {
                        // Tekil-kare sıçrama koruması (6.tur): anchor'a göre DEĞİL, EN SON eşleşen kareye göre bak.
                        // Global medyan-kompanzasyonu (adım 2c) zaten uygulandı (LastMatchedCx/Cy, AnchorCx/Cy ile
                        // AYNI medyanla kaydı). Büyük tek-kare sıçrama (kamera-flip / <2 eşleşen izle kompanzasyon
                        // atlandı) → RelDispPx'e (MovedPx muafiyet metriği) KREDİLENMEZ, çapa BU KAREYE resync
                        // edilir — flip artefaktı cesede sahte "hareket" yazıp konum-blacklist'ten kaçıramaz.
                        if (tr.HasLastMatched)
                        {
                            float jdx = ncx - tr.LastMatchedCx, jdy = ncy - tr.LastMatchedCy;
                            float jumpPx = MathF.Sqrt(jdx * jdx + jdy * jdy);
                            if (jumpPx >= MaxPerFrameJumpPx)
                            {
                                // Resync: çapa BU kareye taşınır (aksi hâlde sonraki karelerde RelDispPx sıçramayı
                                // taşımaya devam ederdi).
                                tr.AnchorCx = ncx; tr.AnchorCy = ncy;
                                Log?.Invoke($"[Track] sicrama-yoksay: iz#{tr.Id} cls={tr.ClassId} {(int)jumpPx}px tek-karede " +
                                            $"(esik={MaxPerFrameJumpPx:0}px) — MovedPx kredisi verilmedi, capa resync");
                            }
                        }

                        // Net yer değiştirme (MovedPx): çapa global hareketle (medyan) kaydırıldığından bu mesafe
                        // yalnız mob'un KENDİ hareketidir → FarmEngine konumsal-blacklist hareket-muafiyeti okur.
                        // 7.tur: HAREKET-TABANLI DİRİLİŞ KALDIRILDI — ölü iz eşik üstü "hareket" etse de damga
                        // KALKMAZ (dirilişlerin %91'i kompanzasyon-hatasıyla şişen GERÇEK cesetlerdi → tekrar
                        // tıklanıyordu). Yanlış-ölü CANLI mob senaryosu kamera-flip churn'üyle kendiliğinden düzelir
                        // (flip'te iz düşer, temiz kimlik doğar — en kötü ~2-6sn; bkz sınıf-üstü 7.tur notu).
                        float adx = ncx - tr.AnchorCx, ady = ncy - tr.AnchorCy;
                        tr.RelDispPx = MathF.Sqrt(adx * adx + ady * ady);
                    }
                    tr.LastMatchedCx = ncx; tr.LastMatchedCy = ncy; tr.HasLastMatched = true;
                }
                else
                {
                    // Ölü-miras: düşme/pose değişimi IoU'yu koparınca ceset YENİ iz alır → yakın bir
                    // SON-ÖLDÜRÜLEN (aynı tür) izin "ölü" damgasını devral ki bir daha tıklanmasın.
                    bool inheritDead = false; long inheritAt = 0;
                    int  inheritFrom = -1; float inheritD2 = 0f;   // teşhis: hangi ölü izden, ne kadar uzaktan
                    float ncx = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                    float r2  = (float)DeadInheritRadiusPx * DeadInheritRadiusPx;
                    for (int j = 0; j < _tracks.Count; j++)
                    {
                        var dt = _tracks[j];
                        if (!dt.Dead || dt.ClassId != d.ClassId) continue;
                        float dcx = dt.Box.X + dt.Box.Width / 2f, dcy = dt.Box.Y + dt.Box.Height / 2f;
                        float ddx = ncx - dcx, ddy = ncy - dcy;
                        float dd2 = ddx * ddx + ddy * ddy;
                        if (dd2 <= r2) { inheritDead = true; inheritAt = dt.DeadAtMs; inheritFrom = dt.Id; inheritD2 = dd2; break; }
                    }
                    tr = new Track
                    {
                        Id = _nextId++, ClassId = d.ClassId, Box = d.BBox,
                        Vx = 0, Vy = 0, Age = 1, Missed = 0,
                        Dead = inheritDead, DeadAtMs = inheritAt, LastSeenMs = nowMs,
                        Source   = inheritDead ? DeadSource.Inherited : DeadSource.None,
                        AnchorCx = ncx, AnchorCy = ncy, RelDispPx = 0f, // çapa = doğum merkezi
                    };
                    _tracks.Add(tr);
                    // Teşhis (seyrek): cesedin DeadInheritRadiusPx'i içinde doğan kutu "ölü" devraldı. Ceset
                    // düşüşünde NORMALdir; devralan kutu CANLI mob ise B-belirtisidir — 7.tur: diriliş YOK,
                    // telafi kamera-flip churn'ü (flip'te bu iz düşer, mob temiz kimlik alır).
                    if (inheritDead)
                        Log?.Invoke($"[Track] miras-DEAD: yeni#{tr.Id} <- olu#{inheritFrom} d={(int)MathF.Sqrt(inheritD2)}px " +
                                    $"cls={d.ClassId} (yaricap={DeadInheritRadiusPx}px — damga kalıcı, churn telafi eder)");
                }
                outArr[i] = d with { TrackId = tr.Id, Dead = tr.Dead, Guardian = tr.Guardian, MovedPx = tr.RelDispPx };
            }

            // 4) Eşleşmeyen izleri yaşlandır + ölü izleri ömrü dolunca düşür.
            // KÖK NEDEN FIX (2026-07-03, canlı log kanıtı — "A-belirtisi"): DeadLingerMs dolan bir ölü iz
            // bu karede HÂLÂ eşleşse (ceset hâlâ görünür) bile ESKİDEN koşulsuz düşürülüyordu → bir sonraki
            // karede aynı ceset "yeni" tespit olarak gelip (yakında başka ölü iz yoksa miras alacak kimse
            // bulamayıp) canlı aday olarak DİRİLİYOR, tekrar tıklanıyordu. Artık yalnız bu karede EŞLEŞMEDİĞİNDE
            // (ceset gerçekten kayboldu/despawn oldu) düşürülür; hâlâ görünen ceset ömür sınırı olmadan ölü
            // kalır. Respawn'ı yanlışlıkla ölü sayma riski hâlâ sınırlı: eşleşme kesilir kesilmez (despawn/oklüzyon)
            // birkaç kare içinde (MaxAgeFrames) düşer, aynı yerdeki yeni doğum o zaman miras alacak ölü iz bulamaz.
            for (int k = _tracks.Count - 1; k >= 0; k--)
            {
                var t = _tracks[k];
                bool matchedNow = k < trackMatched.Length && trackMatched[k];
                if (t.Dead && nowMs - t.DeadAtMs > DeadLingerMs && !matchedNow)
                {
                    _tracks.RemoveAt(k); continue;
                }
                if (!matchedNow)
                {
                    t.Missed++;
                    if (t.Missed > MaxAgeFrames) _tracks.RemoveAt(k);
                }
            }

            _lastMs = nowMs;
            return outArr;
        }
    }

    // Greedy IoU eşleme tek geçişi: kalan en yüksek IoU çiftini eşle, tükenene dek tekrarla.
    // Küçük N/M (birkaç mob) → O(N*M) iterasyon yeterli.
    private void MatchPass(IReadOnlyList<Detection> dets, int[] assignedTrack, bool[] trackMatched, bool highConfOnly)
    {
        while (true)
        {
            float bestIou   = IouThreshold;
            int   bestDet   = -1, bestTrack = -1;
            for (int i = 0; i < dets.Count; i++)
            {
                if (assignedTrack[i] >= 0) continue;
                var  d      = dets[i];
                bool isHigh = d.Confidence >= HighConf;
                if (highConfOnly != isHigh) continue;      // 1. geçiş yüksek, 2. geçiş düşük güven
                for (int k = 0; k < _tracks.Count; k++)
                {
                    if (trackMatched[k]) continue;
                    if (_tracks[k].ClassId != d.ClassId) continue; // aynı tür şartı
                    float iou = Iou(d.BBox, _tracks[k].Box);
                    if (iou > bestIou) { bestIou = iou; bestDet = i; bestTrack = k; }
                }
            }
            if (bestDet < 0) break;
            assignedTrack[bestDet]   = bestTrack;
            trackMatched[bestTrack]  = true;
        }
    }

    // IoU sonrası kalan tespitleri MERKEZ-mesafesiyle eşler (yalnız CANLI izler). İzin verilen kayma kutu
    // boyutuna oranlı; boyut benzerliği de şart (farklı boyut = farklı mob). Greedy: en yakın çiften başla.
    private void MatchByCenter(IReadOnlyList<Detection> dets, int[] assignedTrack, bool[] trackMatched)
    {
        while (true)
        {
            float bestDist = float.MaxValue; int bestDet = -1, bestTrack = -1;
            for (int i = 0; i < dets.Count; i++)
            {
                if (assignedTrack[i] >= 0) continue;
                var   d    = dets[i];
                float ncx  = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                float gate = CenterMatchFactor * Math.Max(d.BBox.Width, d.BBox.Height); // izin verilen kayma
                for (int k = 0; k < _tracks.Count; k++)
                {
                    if (trackMatched[k]) continue;
                    if (_tracks[k].Dead) continue;                 // ölü iz yalnız IoU ile (canlı mobu kapma)
                    if (_tracks[k].ClassId != d.ClassId) continue; // aynı tür
                    float dcx = _tracks[k].Box.X + _tracks[k].Box.Width / 2f;
                    float dcy = _tracks[k].Box.Y + _tracks[k].Box.Height / 2f;
                    float dist = MathF.Sqrt((ncx - dcx) * (ncx - dcx) + (ncy - dcy) * (ncy - dcy));
                    if (dist > gate || dist >= bestDist) continue;
                    // boyut benzerliği (yükseklik) ≥ %60 — farklı derinlikteki başka mobu eşlemesin.
                    float hMin = Math.Min(d.BBox.Height, _tracks[k].Box.Height);
                    float hMax = Math.Max(1f, Math.Max(d.BBox.Height, _tracks[k].Box.Height));
                    if (hMin / hMax < 0.6f) continue;
                    bestDist = dist; bestDet = i; bestTrack = k;
                }
            }
            if (bestDet < 0) break;
            assignedTrack[bestDet]  = bestTrack;
            trackMatched[bestTrack] = true;
        }
    }

    // Bileşen-bazlı medyan (küçük N — kare başına birkaç mob): çift eleman sayısında iki ortancanın
    // ortalaması (2 örnekte ikisinin ortalaması → tek yürüyen mob + tek ceset sahnesinde yanlılık yarıya iner).
    private static float Median(List<float> v)
    {
        v.Sort();
        int mid = v.Count / 2;
        return v.Count % 2 == 1 ? v[mid] : (v[mid - 1] + v[mid]) / 2f;
    }

    private static float Iou(RectangleF a, RectangleF b)
    {
        float x1 = Math.Max(a.X, b.X),     y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        float iw = x2 - x1, ih = y2 - y1;
        if (iw <= 0 || ih <= 0) return 0f;
        float inter = iw * ih;
        float uni   = a.Width * a.Height + b.Width * b.Height - inter;
        return uni <= 0 ? 0f : inter / uni;
    }
}
