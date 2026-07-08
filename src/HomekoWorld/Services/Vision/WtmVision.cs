using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
namespace HomekoWorld.Services.Vision;

/// <summary>V4 — bir karedeki hedef-penceresi durumunun TAM özeti (tek yazar: ScanTargetBar, capture thread).
/// StructureKnown=false → çerçeve şablonu öğretilmemiş; tüketiciler eski renk (RedAlive) yoluna düşer.</summary>
public readonly record struct TargetBarState(
    bool  StructureKnown,     // çerçeve şablonu öğretilmiş + yüklü mü
    bool  StructurePresent,   // çerçeve eşleşti (histerezisli) = pencere AÇIK = mob seçili/canlı
    float StructureScore,     // en iyi NCC (teşhis/log)
    int   OffsetY,            // duyuru offseti: yapı bulunduysa yapıdan, yoksa renk taramasından
    int   Red, int Dark, int Total, float DarkFrac, float FillFrac,
    bool  RedAlive,           // BarPresent kararı (legacy canlı + doluluk/hasar ölçümü)
    long  StampMs);

public static class WtmVision
{

    // ── HP bar tek-tıklama kalibrasyon yardımcısı ───────────────────────────
    /// <summary>
    /// (cx,cy) noktasındaki rengi örnekler ve bar genişliğini otomatik tespit eder.
    /// TEK ekran görüntüsü alır — SamplePixel'i 800 kez çağırmak yerine 1 CaptureRegion.
    /// </summary>
    public static (Color color, int halfWidth) SampleHpBarAt(int cx, int cy, int tol = 60)
    {
        const int range = 400;
        int left  = Math.Max(0, cx - range);
        int width = Math.Min(range * 2, 3840 - left); // 4K safe

        using var bmp = CaptureRegion(left, cy, width, 1);

        int ci    = Math.Clamp(cx - left, 0, width - 1);
        var color = bmp.GetPixel(ci, 0);

        int leftExt = 0, rightExt = 0;
        for (int dx = 1; ci - dx >= 0; dx++)
        {
            var c = bmp.GetPixel(ci - dx, 0);
            if (Math.Abs(c.R - color.R) > tol || c.R < 80) break;
            leftExt = dx;
        }
        for (int dx = 1; ci + dx < width; dx++)
        {
            var c = bmp.GetPixel(ci + dx, 0);
            if (Math.Abs(c.R - color.R) > tol || c.R < 80) break;
            rightExt = dx;
        }

        int halfW = Math.Max(Math.Max(leftExt, rightExt), 30) + 10;
        return (color, halfW);
    }

    // ── Dispatching yardımcısı ───────────────────────────────────────────────
    public static bool IsTargetAlive(WtmSettings s)
    {
        if (s.IsTargetHpColorCalibrated)
            return IsTargetSelectedByHpColor(s);
        return false;
    }

    // ── Hedef HP bar renk tarama — yeni birincil yöntem ─────────────────────
    /// <summary>
    /// Hedefin HP bar bölgesini tek satır piksel taramasıyla kontrol eder.
    /// Bar görünüyorsa (mob seçili/canlı) true, kaybolmuşsa (ölü/deselect) false döner.
    /// Template matching'den ~160× daha hızlı; renk değişmeyeceği için daha güvenilir.
    /// </summary>
    public static bool IsTargetSelectedByHpColor(WtmSettings s)
    {
        if (!s.IsTargetHpColorCalibrated) return false;

        // Master → geçerli ekran (tek satır; pozisyon + genişlik ölçeklenir).
        var mapped = ResolutionMapper.Map(
            Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW), s.HpColorScanY,
            s.HpColorScanHalfW * 2, 1);
        int left  = mapped.X;
        int width = mapped.Width;
        if (width <= 0) return false;

        using var bmp  = CaptureRegion(left, mapped.Y, width, 1);
        var       rect = new Rectangle(0, 0, width, 1);
        var       data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int       count = 0;
        try
        {
            // BGRA byte sırası: pos+0=B, pos+1=G, pos+2=R, pos+3=A
            System.Runtime.InteropServices.Marshal.Copy(data.Scan0, _hpColorBuf ??= new byte[4096], 0,
                Math.Min(width * 4, _hpColorBuf.Length));
            int limit = Math.Min(width * 4, _hpColorBuf.Length);
            for (int i = 0; i < limit; i += 4)
            {
                byte b = _hpColorBuf[i], g = _hpColorBuf[i + 1], r = _hpColorBuf[i + 2];
                if (Math.Abs(r - s.HpColorR) <= s.HpColorTol &&
                    Math.Abs(g - s.HpColorG) <= s.HpColorTol &&
                    Math.Abs(b - s.HpColorB) <= s.HpColorTol)
                    count++;
            }
        }
        finally { bmp.UnlockBits(data); }
        return count >= s.HpColorMinPx;
    }

    // ── Hedef HP bar — HSV kırmızı tespiti (koruma mobu yöntemiyle aynı) ─────
    /// <summary>
    /// HP bar bölgesindeki kırmızı-hue pikselleri sayar; eşiği aşarsa bar görünür
    /// (mob canlı/seçili) → true. ÖNCELİK: ML ROI kutusu (HpBarRoiX/Y/W/H) — kullanıcının
    /// ZATEN kalibre ettiği bölge; ayrı HSV kalibrasyonu gerekmez. Bu 2B kutu, bar
    /// yüksekliği boyunca tarandığı için DÜŞÜK HP'de bile bol kırmızı verir → azalan HP
    /// "bar yok oldu" sanılmaz; bar yalnız hedef seçimi düşünce (panel kaybolur) sıfırlanır.
    /// ROI yoksa tek-satır HpColorScan bölgesine düşer. Hue ışıktan bağımsız → kararlı.
    /// </summary>
    public static bool IsTargetAliveByHsv(WtmSettings s) => IsTargetAliveByHsv(s, out _);

    /// <summary>V4 review-fix: offset'i AYNI taramadan (out-param) döner — bkz RedAtAreas out-param notu.</summary>
    public static bool IsTargetAliveByHsv(WtmSettings s, out int offsetY)
    {
        // Tercih: iki-köşe HP bar dikdörtgeni (2B) — düşük HP'ye dayanıklı, koruma mobuyla aynı motor.
        if (s.IsHpBarRoiCalibrated)
        {
            var r = ResolutionMapper.Map(s.HpBarRoiX, s.HpBarRoiY, s.HpBarRoiW, s.HpBarRoiH);
            return RedAtAreas(r.X, r.Y, r.Width, r.Height, s.HpHsvMinPx, s, out offsetY);
        }

        // Fallback: tek-satır renk tarama bölgesi (1px → eşik düşük tutulur).
        if (s.IsTargetHpColorCalibrated)
        {
            var r = ResolutionMapper.Map(
                Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW), s.HpColorScanY,
                s.HpColorScanHalfW * 2, 1);
            return RedAtAreas(r.X, r.Y, r.Width, r.Height, Math.Min(s.HpHsvMinPx, 4), s, out offsetY);
        }
        offsetY = 0;
        return false;
    }

    /// <summary>Son HP-bar taramasının tanılama değerleri (eşik tune'u için): kırmızı/koyu-oluk/toplam piksel,
    /// koyu-oluk oranı ve "bar var (canlı)" kararı. Combat ölüm mantığı bunu LastBarScanAtMs ile birlikte okur.
    /// NOT: tuple yazımı atomik değil — yırtık okuma teoride mümkün; karar tarafı tazelik + çok-kare teyidiyle
    /// tek kötü okumaya dayanıklı (bkz FarmEngine.Combat V3).</summary>
    public static (int red, int dark, int total, float darkFrac, float fillFrac, bool alive) LastBarScan;

    /// <summary>LastBarScan'ın yazıldığı an (monotonik ms). V3 (2026-07-02): combat, ölüm sayaçlarını yalnız
    /// YENİ (damgası değişmiş) + TAZE karelerle ilerletir — tespit thread'i takılıp LastBarScan DONUNCA eski
    /// kod aynı (kötü bir geçiş anına denk gelmiş) kareyi her 30ms'de "bağımsız teyit" sanıp sahte-ölüm
    /// üretebiliyordu (canlı mob atlama + kill sayacı şişmesi).</summary>
    public static long LastBarScanAtMs;

    private static long NowMsMono() =>
        System.Diagnostics.Stopwatch.GetTimestamp() / (System.Diagnostics.Stopwatch.Frequency / 1000);

    // ── V4 — yapı-tabanlı hedef-penceresi tespiti ────────────────────────────
    /// <summary>Son ScanTargetBar sonucu (tek yazar: capture thread). Combat ölüm mantığı bunu okur;
    /// null = HSV taraması çalışmıyor (ColorScan modu / HP bar kalibresiz) → GDI legacy yolu.</summary>
    public static TargetBarState? LastTargetBar;

    private static bool _structHyst; // çerçeve histerezis hafızası (eşik bandında son durum korunur)

    /// <summary>Farm oturumu başında histerezis + son durum sıfırlanır.</summary>
    public static void ResetTargetBar() { _structHyst = false; LastTargetBar = null; }

    /// <summary>V4 — karedeki hedef-penceresi durumunu ÇIKARIR (kare başına BİR kez, capture thread'inde):
    /// (1) renk taraması (mevcut IsTargetAliveByHsvFromFrame yolu; LastBarScan/AtMs/OffsetY statiklerini de
    /// doldurur → UI/teşhis kırılmaz), (2) çerçeve şablonu 2 sabit konumda (offset 0 / +Δy) puanlanır,
    /// histerezisli VAR/YOK kararı verilir. Yapı bulunduysa duyuru offseti YAPIDAN alınır (kırmızı arka
    /// planın offset-0'ı yanlış tetiklemesi biter — guardian isim-bandı doğru konumda arar).</summary>
    public static TargetBarState? ScanTargetBar(Bitmap frame, WtmSettings s,
        System.Collections.Generic.IReadOnlyList<Autonomous.TemplateLocator.Template>? frameTpl,
        Rectangle frameRectPhys)
    {
        if (s.HpBarMode != HpBarDetectionMode.Hsv || !s.IsHpBarLocated) { LastTargetBar = null; return null; }

        // 1) Renk taraması — statikleri (LastBarScan/AtMs/OffsetY) mevcut davranışla doldurur.
        bool redAlive = IsTargetAliveByHsvFromFrame(frame, s, out int colorOffset);
        // V4 review-fix: kilitli oku — BarPresent'in eşzamanlı (GDI/combat-thread) yazımıyla yarışabilir.
        (int red, int dark, int total, float darkFrac, float fillFrac, bool alive) ls;
        lock (_barScanLock) { ls = LastBarScan; }

        // 2) Yapı (çerçeve) — 2 sabit konum + histerezis.
        bool known = frameTpl is { Count: > 0 } && frameRectPhys.Width > 0;
        bool present = false; float score = -2f; int offsetY = colorOffset;
        if (known)
        {
            float s0 = Autonomous.TemplateLocator.ScoreAt(frame, frameRectPhys, frameTpl!);
            float s1 = -2f;
            int   dy = s.AnnounceShiftY > 0 ? ResolutionMapper.ScaleLen(s.AnnounceShiftY) : 0;
            if (dy > 0)
                s1 = Autonomous.TemplateLocator.ScoreAt(frame,
                    new Rectangle(frameRectPhys.X, frameRectPhys.Y + dy, frameRectPhys.Width, frameRectPhys.Height),
                    frameTpl!);
            score = System.Math.Max(s0, s1);
            int structOffset = (s1 > s0) ? dy : 0;
            present = score >= s.TargetFrameMatchThreshold  ? true
                    : score <  s.TargetFrameAbsentThreshold ? false
                    : _structHyst;
            _structHyst = present;
            if (present)
            {
                offsetY        = structOffset;
                LastBarOffsetY = structOffset; // guardian/nameplate yapının offsetini kullansın
            }
        }

        var tb = new TargetBarState(known, present, score, offsetY,
            ls.red, ls.dark, ls.total, ls.darkFrac, ls.fillFrac, redAlive, LastBarScanAtMs);
        LastTargetBar = tb;
        return tb;
    }

    /// <summary>Bar görünür (mob canlı) mü — SAF KIRMIZI (2026-06-20, kullanıcı isteği). Canlı sinyali YALNIZ
    /// kırmızı eşiği: <c>alive = red ≥ redThreshold</c>. Eski "kırmızı VEYA dolu-oran(≥0.6)" mantığı koyu
    /// arka-planı (çalı/UI ~%62-66) sahte-CANLI sayıp engage'i dakikalarca takıyordu (kill hiç algılanmıyordu).
    /// Saf-kırmızı hızlı + güvenilir; tek zayıflığı çok düşük-HP'de kırmızı yok olunca erken "öldü" demesi —
    /// bunu ENGAGE döngüsü çözer: kırmızı gidince <see cref="LastBarScan"/>.darkFrac ile "siyah boş bar var mı"
    /// teyidi yapar (bkz FarmEngine.Combat: emptyBarHere). Bu yüzden dark/darkFrac BURADA da hesaplanır.</summary>
    private static bool BarPresent(int red, int dark, int total, int redThreshold, WtmSettings s)
    {
        float darkFrac = total > 0 ? (float)dark / total : 0f;
        float fillFrac = total > 0 ? (float)(red + dark) / total : 0f;
        // SAF-KIRMIZI TABANI (red≥thr) + DOLU-ORANI (fill≥HpBarFillMinFrac), AND ile → İKİ farklı arka-plan
        // sahte-pozitifini birlikte eler:
        //   (a) KIRMIZI arka plan (kırmızı obje/doku ROI'de): red YÜKSEK ama bar ROI'yi doldurmadığından fill
        //       DÜŞÜK (canlı log: red=2078, dolu=%29) → fill-guard eler.
        //   (b) KOYU arka plan (mağara/çalı): red~0 → saf-kırmızı taban eler.
        // Gerçek HP barı HER HP'de ROI'yi kırmızı+koyu-oluk ile ~%74+ doldurur (düşük-HP dahil: log red=2722
        // dolu=%74) → ikisini de geçer. NOT: eski "red≥thr VEYA fill≥0.6" (OR) koyu-çalıyı (fill dark'tan yüksek)
        // sahte-canlı yapıp geri alınmıştı; AND o regresyonu önler (koyu-çalıda red<thr → elenir).
        // KÖK NEDEN (2026-07-01): saf-kırmızı TEK BAŞINA, ROI'deki kırmızı arka planı "canlı" sanıp hem sahte-hedef
        // hem yanlış duyuru-offset'i (RedAtAreas offset-0'ı tetikleyip guardian ismini yanlış yerde aratma) üretiyordu.
        bool  alive    = BarWouldPass(red, dark, total, redThreshold, s);
        // V4 review-fix: LastBarScan artık kozmetik değil — ScanTargetBar (tespit thread'i) bunu
        // TargetBarState.Red/Dark/... olarak paketleyip Combat'ın hasar-kapısına (Killed/Lost ayrımı)
        // besliyor; AYRICA GDI dalı (IsTargetAliveSmoothed → combat/farm-loop thread'i) da BarPresent'i
        // eşzamanlı çağırabiliyor. 6-alanlı tuple .NET'te atomik değil → kilit olmadan yırtık okuma
        // (iki farklı taramanın karışık alanları) mümkündü. Yazım + ScanTargetBar'daki okuma kilitli.
        lock (_barScanLock)
        {
            LastBarScan     = (red, dark, total, darkFrac, fillFrac, alive);
            LastBarScanAtMs = NowMsMono();
        }
        return alive;
    }

    private static readonly object _barScanLock = new();

    /// <summary>BarPresent'in YAN-ETKİSİZ (LastBarScan yazmayan) karar çekirdeği: red≥eşik VE
    /// fill≥HpBarFillMinFrac. Çift-konum (duyuru) değerlendirmesinde aday konumları LastBarScan'i
    /// kirletmeden kıyaslamak için ayrıldı — kapı mantığı tek yerde kalsın.</summary>
    private static bool BarWouldPass(int red, int dark, int total, int redThreshold, WtmSettings s)
    {
        float fillFrac = total > 0 ? (float)(red + dark) / total : 0f;
        return red >= redThreshold && fillFrac >= s.HpBarFillMinFrac;
    }

    /// <summary>Bar'ın EN SON bulunduğu dikey offset (ekran px): 0 = kalibre konum (duyuru yok),
    /// +Δy = duyuru kayması (duyuru var). Nameplate/guardian kontrolü ismi DOĞRU konumda araması için kullanır.</summary>
    public static int LastBarOffsetY;

    /// <summary>Bar görünür mü (kırmızı). İki geçerli konum: kalibre (duyuru YOK, offset 0) ve +Δy (duyuru VAR).
    /// Bar bulunduğunda <see cref="LastBarOffsetY"/> set edilir. NOT: yalnız bu iki konum geçerli — isim de barla
    /// aynı kadar kayar; eski −Δy denemesi duyuru/isim alanına bakıp guardian'ı şaşırtıyordu (kaldırıldı). Ekrandan yakalar.</summary>
    private static bool RedAtAreas(int x, int y, int w, int h, int threshold, WtmSettings s)
        => RedAtAreas(x, y, w, h, threshold, s, out _);

    /// <summary>V4 review-fix: offset'i AYNI taramadan out-param ile döner (LastBarOffsetY global'ini de
    /// yazar — geriye uyum için). Çağıran (IsTargetAliveSmoothed/IsTargetAliveNow) artık ayrı bir satırda
    /// global'i okumak ZORUNDA değil — tespit thread'i araya girip başka bir taramanın offset'ini
    /// yazamaz (eski hâlde bu, guardian kontrolünü yanlış isim-konumuna yönlendirebiliyordu).
    /// 8.tur (2026-07-08): ilk-eşleşen → EN-GÜÇLÜ-KANIT. Duyuru AÇIKKEN gerçek bar +Δy'deyken, 0
    /// konumundaki ROI'ye kayan içerik (isim bandı/duyuru metni) zayıf kapıyı (red≥5 + fill≥0.60)
    /// geçebiliyor ve ilk-eşleşen 0'ı seçince nameplate yanlış konumda okunup HER mob guardian
    /// sanılıyordu (canlı log 18:28: 82 denemenin 41'i sahte guardian-red, geçiş ort 11.3sn).
    /// Artık iki konum da ölçülür; ikisi de geçerse KIRMIZI SAYISI yüksek olan kazanır (gerçek bar =
    /// dolu kırmızı blok, sızıntı/metin = düşük sayı). LastBarScan'e yalnız SEÇİLEN konum yazılır.</summary>
    private static bool RedAtAreas(int x, int y, int w, int h, int threshold, WtmSettings s, out int offsetY)
    {
        int  red1 = CountRedHsv(x, y, w, h, s, out int d1, out int t1);
        bool p1   = BarWouldPass(red1, d1, t1, threshold, s);
        int  dy   = s.AnnounceShiftY > 0 ? ResolutionMapper.ScaleLen(s.AnnounceShiftY) : 0;
        int  red2 = 0, d2 = 0, t2 = 0;
        bool p2   = false;
        if (dy > 0)
        {
            red2 = CountRedHsv(x, y + dy, w, h, s, out d2, out t2);
            p2   = BarWouldPass(red2, d2, t2, threshold, s);
        }
        if (p1 || p2)
        {
            bool useShift = p2 && (!p1 || red2 > red1);
            BarPresent(useShift ? red2 : red1, useShift ? d2 : d1, useShift ? t2 : t1, threshold, s);
            offsetY = useShift ? dy : 0;
            LastBarOffsetY = offsetY;
            return true;
        }
        // Canlı değil: LastBarScan'i boş-bar teyidinin (Combat emptyBarHere, DarkFrac) işine yarayan
        // konumla yaz — koyu-oluk oranı yüksek olan konum siyah boş barın gerçekte olduğu yerdir.
        bool shiftDarker = dy > 0 && (long)d2 * Math.Max(1, t1) > (long)d1 * Math.Max(1, t2);
        if (shiftDarker) BarPresent(red2, d2, t2, threshold, s);
        else             BarPresent(red1, d1, t1, threshold, s);
        offsetY = 0;
        return false;
    }

    /// <summary>(rx,ry,rw,rh) bölgesini alır; kırmızı-hue piksel sayısını döndürür, koyu-oluk (boş bar) ve
    /// toplam piksel sayısını out ile verir. Koyu-oluk: val≤HpTroughMaxVal ve sat≤HpTroughMaxSat (tek geçişte).</summary>
    private static int CountRedHsv(int rx, int ry, int rw, int rh, WtmSettings s, out int dark, out int total)
    {
        rx = Math.Max(0, rx); ry = Math.Max(0, ry);
        rw = Math.Max(1, rw); rh = Math.Max(1, rh);
        dark = 0; total = rw * rh;

        using var bmp  = CaptureRegion(rx, ry, rw, rh);
        var       rect = new Rectangle(0, 0, rw, rh);
        var       data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int       red  = 0;
        try
        {
            int stride = Math.Abs(data.Stride);
            int needed = stride * rh;
            if (_hpRoiBuf is null || _hpRoiBuf.Length < needed) _hpRoiBuf = new byte[needed];
            var buf = _hpRoiBuf;
            Marshal.Copy(data.Scan0, buf, 0, needed);

            // BGRA byte sırası: pos+0=B, pos+1=G, pos+2=R
            for (int y = 0; y < rh; y++)
            {
                int row = y * stride;
                for (int x = 0; x < rw; x++)
                {
                    int pos = row + x * 4;
                    byte b = buf[pos], g = buf[pos + 1], r = buf[pos + 2];
                    RgbToHsv(r, g, b, out float hue, out float sat, out float val);
                    if (val <= s.HpTroughMaxVal && sat <= s.HpTroughMaxSat) { dark++; continue; } // boş-bar koyu oluğu
                    if (val < s.HpHsvMinVal) continue; // koyu/gölge (oluk eşiği üstü ama kırmızı değil)
                    if (sat < s.HpHsvMinSat) continue; // soluk/gri
                    if (hue <= s.HpHueLo || hue >= s.HpHueHi)
                        red++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return red;
    }

    // ── T2 sinerji: DetectionLoop'un ZATEN aldığı tam kareden hedef-canlı HSV kontrolü ──
    // Combat döngüsü her tick'te ayrı CaptureRegion (GDI CopyFromScreen) yapmak yerine, tespit
    // thread'inin yakaladığı kareyi kullanır → "GDI yükü" kalkar. Kare DetectionLoop thread'inde,
    // yakalamadan hemen sonra (snapshot yayınlanmadan) taranır → kare thread'ler arası paylaşılmaz (race yok).
    public static bool IsTargetAliveByHsvFromFrame(Bitmap frame, WtmSettings s)
        => IsTargetAliveByHsvFromFrame(frame, s, out _);

    /// <summary>Yukarıdakiyle aynı; ek olarak bu ÇAĞRIYLA eşleşen <see cref="LastBarOffsetY"/> değerini
    /// <paramref name="offsetY"/>'ye kopyalar (aynı thread, aynı satırlar — araya başka thread giremez).
    /// Üretici (DetectionLoop) bunu DetectionSnapshot/PipeItem'a taşıyarak guardian kontrolünün, kendi
    /// taramasından SAATLER/tick'ler sonra racy global static'i okumak yerine, TAM BU taramanın offset'ini
    /// kullanmasını sağlar (bkz FarmEngine.Targeting.IsTargetAliveNow).</summary>
    public static bool IsTargetAliveByHsvFromFrame(Bitmap frame, WtmSettings s, out int offsetY)
    {
        if (s.IsHpBarRoiCalibrated)
        {
            var r = ResolutionMapper.Map(s.HpBarRoiX, s.HpBarRoiY, s.HpBarRoiW, s.HpBarRoiH);
            bool alive = RedAtAreasFromFrame(frame, r.X, r.Y, r.Width, r.Height, s.HpHsvMinPx, s);
            offsetY = LastBarOffsetY;
            return alive;
        }
        if (s.IsTargetHpColorCalibrated)
        {
            var r = ResolutionMapper.Map(
                Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW), s.HpColorScanY,
                s.HpColorScanHalfW * 2, 1);
            bool alive = RedAtAreasFromFrame(frame, r.X, r.Y, r.Width, r.Height, Math.Min(s.HpHsvMinPx, 4), s);
            offsetY = LastBarOffsetY;
            return alive;
        }
        offsetY = 0;
        return false;
    }

    /// <summary>RedAtAreas'ın sağlanan tam kareden (yeni yakalama yok) çalışan eşi — kalibre konum (offset 0) +
    /// duyuru +Δy. Bar bulununca <see cref="LastBarOffsetY"/> set eder. 8.tur: RedAtAreas ile aynı
    /// en-güçlü-kanıt seçimi (ilk-eşleşen 0-önyargısı guardian sahte-pozitifinin köküydü — üstteki nota bak).</summary>
    private static bool RedAtAreasFromFrame(Bitmap frame, int x, int y, int w, int h, int threshold, WtmSettings s)
    {
        int  red1 = CountRedHsvFromFrame(frame, x, y, w, h, s, out int d1, out int t1);
        bool p1   = BarWouldPass(red1, d1, t1, threshold, s);
        int  dy   = s.AnnounceShiftY > 0 ? ResolutionMapper.ScaleLen(s.AnnounceShiftY) : 0;
        int  red2 = 0, d2 = 0, t2 = 0;
        bool p2   = false;
        if (dy > 0)
        {
            red2 = CountRedHsvFromFrame(frame, x, y + dy, w, h, s, out d2, out t2);
            p2   = BarWouldPass(red2, d2, t2, threshold, s);
        }
        if (p1 || p2)
        {
            bool useShift = p2 && (!p1 || red2 > red1);
            BarPresent(useShift ? red2 : red1, useShift ? d2 : d1, useShift ? t2 : t1, threshold, s);
            LastBarOffsetY = useShift ? dy : 0;
            return true;
        }
        bool shiftDarker = dy > 0 && (long)d2 * Math.Max(1, t1) > (long)d1 * Math.Max(1, t2);
        if (shiftDarker) BarPresent(red2, d2, t2, threshold, s);
        else             BarPresent(red1, d1, t1, threshold, s);
        return false;
    }

    /// <summary>(rx,ry,rw,rh) bölgesini SAĞLANAN tam kareden (yeni yakalama YOK) tarar; kırmızı-hue piksel sayar,
    /// koyu-oluk (boş bar) + toplam piksel sayısını out ile verir.</summary>
    private static int CountRedHsvFromFrame(Bitmap frame, int rx, int ry, int rw, int rh, WtmSettings s, out int dark, out int total)
    {
        rx = Math.Max(0, rx); ry = Math.Max(0, ry);
        rw = Math.Max(1, rw); rh = Math.Max(1, rh);
        if (rx + rw > frame.Width)  rw = frame.Width  - rx;
        if (ry + rh > frame.Height) rh = frame.Height - ry;
        dark = 0; total = 0;
        if (rw <= 0 || rh <= 0) return 0;
        total = rw * rh;

        var rect = new Rectangle(rx, ry, rw, rh);
        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int red  = 0;
        int darkLocal = 0;
        try
        {
            int stride = data.Stride;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int y = 0; y < rh; y++)
                {
                    byte* rowp = basePtr + (long)y * stride;
                    for (int x = 0; x < rw; x++)
                    {
                        byte* px = rowp + x * 4; // BGRA byte sırası
                        RgbToHsv(px[2], px[1], px[0], out float hue, out float sat, out float val);
                        if (val <= s.HpTroughMaxVal && sat <= s.HpTroughMaxSat) { darkLocal++; continue; } // boş-bar oluğu
                        if (val < s.HpHsvMinVal) continue; // koyu/gölge (oluk eşiği üstü ama kırmızı değil)
                        if (sat < s.HpHsvMinSat) continue; // soluk/gri
                        if (hue <= s.HpHueLo || hue >= s.HpHueHi) red++;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }
        dark = darkLocal;
        return red;
    }

    // ML ROI kutusu HSV taraması için yeniden kullanılan buffer (240×60×4 ≈ 57KB).
    [ThreadStatic] private static byte[]? _hpRoiBuf;

    // Yeniden kullanılan byte buffer — her çağrıda heap allocation önler (thread-safe değil,
    // ama WtmVision tek thread'den çağrılıyor).
    [ThreadStatic] private static byte[]? _hpColorBuf;

    internal static Bitmap CaptureRegion(int x, int y, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.CopyFromScreen(x, y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        return bmp;
    }

    /// <summary>
    /// Checks whether the selection ring is close enough to the character to be considered
    /// "adjacent". Only scans a small square window of CenterWindowRadius around the
    /// calibrated CharacterCenter — distant grass pixels are excluded entirely.
    /// Returns true when at least MinRingPixels matching pixels are found in that window.
    /// </summary>
    public static bool IsRingNearCharacterCenter(WtmSettings s)
    {
        if (s.CharacterCenterX == 0 && s.CharacterCenterY == 0)
            return false; // not calibrated

        using var bmp = ScreenCapture.CaptureScreen();
        return IsRingNearCharacterCenter(bmp, s);
    }

    public static bool IsRingNearCharacterCenter(Bitmap bmp, WtmSettings s)
    {
        int cx = s.CharacterCenterX, cy = s.CharacterCenterY, r = s.CenterWindowRadius;
        int x0 = Math.Max(0, cx - r), y0 = Math.Max(0, cy - r);
        int x1 = Math.Min(bmp.Width  - 1, cx + r);
        int y1 = Math.Min(bmp.Height - 1, cy + r);

        var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
        var data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int stride = Math.Abs(data.Stride);
            var pixels = new byte[stride * bmp.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            int count = 0;
            int minHue = s.RingHue - s.RingHueTolerance;
            int maxHue = s.RingHue + s.RingHueTolerance;

            for (int y = y0; y <= y1; y++)
            {
                int row = y * stride;
                for (int x = x0; x <= x1; x++)
                {
                    int pos = row + x * 4;
                    byte b = pixels[pos], g = pixels[pos + 1], rv = pixels[pos + 2];
                    RgbToHsv(rv, g, b, out float hue, out float sat, out float val);
                    if (hue >= minHue && hue <= maxHue && sat > 0.35f && val > 0.25f)
                        count++;
                }
            }
            return count >= s.MinRingPixels;
        }
        finally
        {
            bmp.UnlockBits(data);
        }
    }

    // Captures one physical pixel — used during calibration to sample colours.
    public static Color SamplePixel(Point physicalPt)
    {
        using var bmp = ScreenCapture.CaptureScreen();
        int x = Math.Clamp(physicalPt.X, 0, bmp.Width  - 1);
        int y = Math.Clamp(physicalPt.Y, 0, bmp.Height - 1);
        return bmp.GetPixel(x, y);
    }

    // Converts an RGB colour to HSV hue (0-360), saturation and value (0-1).
    public static void RgbToHsv(Color c, out float hue, out float saturation, out float value)
        => RgbToHsv(c.R, c.G, c.B, out hue, out saturation, out value);

    internal static void RgbToHsv(byte r, byte g, byte b,
        out float hue, out float saturation, out float value)
    {
        float rf = r / 255f, gf = g / 255f, bf = b / 255f;
        float max = Math.Max(rf, Math.Max(gf, bf));
        float min = Math.Min(rf, Math.Min(gf, bf));
        float delta = max - min;

        value      = max;
        saturation = max == 0f ? 0f : delta / max;

        if (delta == 0f) { hue = 0f; return; }

        if      (max == rf) hue = 60f * (((gf - bf) / delta) % 6f);
        else if (max == gf) hue = 60f * ((bf - rf) / delta + 2f);
        else                hue = 60f * ((rf - gf) / delta + 4f);

        if (hue < 0f) hue += 360f;
    }

    // ── Koruma mobu — nameplate renk tespiti (HSV) ──────────────────────────
    /// <summary>
    /// Koruma isim bandını tarar: önce kullanıcının çizdiği NameBand dikdörtgeni,
    /// kalibre değilse HpColorScanY + NameplateOffsetY ofsetinden türetilen bant.
    /// Her pikseli HSV'ye çevirir; siyah çerçeve/gölge (düşük V) ve beyaz/gri
    /// (düşük S) pikselleri eler. Kalan "renkli yazı" pikselleri içinde kırmızı
    /// hue oranı eşiği geçerse Guardian döner — aksi halde Normal/Unknown (saldır).
    /// Hue ışıktan bağımsız olduğu için RGB mesafesine göre çok daha kararlıdır.
    /// </summary>
    public enum NameplateClass { Unknown, Normal, Guardian }

    public static NameplateClass ReadNameplateClass(WtmSettings s) => ReadNameplateClass(s, LastBarOffsetY);

    /// <summary>Yukarıdakiyle aynı; isim bandını <paramref name="barOffsetY"/>'de arar (global
    /// <see cref="LastBarOffsetY"/> yerine ÇAĞIRANIN sağladığı, kendi HP-bar taramasıyla EŞLEŞEN offset).
    /// KÖK NEDEN (2026-07-01): global static, sürekli çalışan DetectionLoop thread'i tarafından yazılıyor;
    /// no-arg overload'ı DXGI-cache yolundan (IsTargetAliveNow) çağırınca "canlı" onayının kullandığı taramadan
    /// FARKLI (daha yeni/daha eski) bir tick'in offset'ini okuyabiliyordu — duyuru açılır/kapanır anında isim
    /// YANLIŞ konumda aranıp koruma mobu "normal" sanılıyordu (atlanmıyor, vuruluyordu).</summary>
    public static NameplateClass ReadNameplateClass(WtmSettings s, int barOffsetY)
    {
        // Tarama dikdörtgenini seç (master px): önce çizilen bant, yoksa ofset fallback.
        int rx, ry, rw, rh;
        if (s.IsNameBandCalibrated)
        {
            rx = s.NameBandX; ry = s.NameBandY; rw = s.NameBandW; rh = s.NameBandH;
        }
        else if (s.IsTargetHpColorCalibrated)
        {
            rh = 18;
            rx = Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW);
            rw = s.HpColorScanHalfW * 2;
            ry = Math.Max(0, s.HpColorScanY + s.NameplateOffsetY - rh / 2);
        }
        else
        {
            return NameplateClass.Unknown;
        }
        if (rw <= 0 || rh <= 0 || ry <= 0) return NameplateClass.Unknown;

        // Master → geçerli ekran (anchor-aware). İsim bandı top-center çapasına oturur.
        var r = ResolutionMapper.Map(rx, ry, rw, rh);
        // KÖK NEDEN (2026-06-22): isim, HP barıyla AYNI miktar kayar. Eskiden NameBand VE NameBand+Δy ikisi de
        // sınıflandırılıp OR'lanıyordu → duyuru KAPALIYKEN +Δy bandı tam KIRMIZI HP barına denk gelip ("kırmızı
        // isim=guardian") HER mobu koruma sanıyor, engage HİÇ olmuyordu. DÜZELTME: ismi, barın GERÇEKTE bulunduğu
        // offset'te (barOffsetY: 0=duyuru yok, +Δy=duyuru var) TEK konumda sınıflandır → HP barıyla çakışma biter,
        // guardian hem duyuru açık hem kapalıyken doğru çalışır.
        return ClassifyNameRect(r.X, r.Y + barOffsetY, r.Width, r.Height, s);
    }

    /// <summary>Verilen ekran dikdörtgenini yakalar ve HSV ile nameplate sınıfı döndürür.</summary>
    /// <remarks>
    /// İKİ MOD:
    ///  (A) Referans-renk (tercih): kullanıcı Normal + Koruma isim renklerini kalibre ettiyse,
    ///      her renkli-yazı pikseli HUE olarak hangi referansa daha yakınsa ona oy verir.
    ///      Böylece "koruma = kırmızı" varsayımı YOK; oyun normal=X / koruma=Y ne olursa olsun
    ///      iki kalibre rengi ayırt eder (bu oyunda seçili hedef başlığı her zaman kırmızı olduğu
    ///      için sabit "kırmızı=koruma" mantığı her mobu koruma sanıyordu — KÖK NEDEN buydu).
    ///  (B) Fallback (referanslar yoksa/doygun değilse): eski sabit kırmızı-hue bandı.
    /// </remarks>
    private static NameplateClass ClassifyNameRect(int rx, int ry, int rw, int rh, WtmSettings s)
    {
        if (rw <= 0 || rh <= 0 || ry < 0) return NameplateClass.Unknown;

        // Referans hue'ları hesapla — her iki referans da YETERİNCE DOYGUN olmalı (gri ref → anlamsız hue).
        RgbToHsv(s.NormalNameR,   s.NormalNameG,   s.NormalNameB,   out float normalHue,   out float normalSat,   out _);
        RgbToHsv(s.GuardianNameR, s.GuardianNameG, s.GuardianNameB, out float guardianHue, out float guardianSat, out _);
        bool useRefHue = normalSat >= s.NameplateMinSat && guardianSat >= s.NameplateMinSat
                         && HueDist(normalHue, guardianHue) >= 15f; // ayırt edilebilir iki renk

        using var bmp  = CaptureRegion(rx, ry, rw, rh);
        var       rect = new Rectangle(0, 0, rw, rh);
        var       data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int textPixels    = 0; // parlak + doygun (renkli yazı) piksel
        int guardianVotes = 0; // referans-renk modunda korumaya daha yakın; fallback'te kırmızı

        try
        {
            int stride = Math.Abs(data.Stride);
            int needed = stride * rh;
            if (_nameBandBuf is null || _nameBandBuf.Length < needed)
                _nameBandBuf = new byte[needed];
            var buf = _nameBandBuf;
            Marshal.Copy(data.Scan0, buf, 0, needed);

            // BGRA byte sırası: pos+0=B, pos+1=G, pos+2=R, pos+3=A
            for (int y = 0; y < rh; y++)
            {
                int row = y * stride;
                for (int x = 0; x < rw; x++)
                {
                    int pos = row + x * 4;
                    byte b = buf[pos], g = buf[pos + 1], r = buf[pos + 2];
                    RgbToHsv(r, g, b, out float hue, out float sat, out float val);
                    if (val < s.NameplateMinVal) continue; // siyah çerçeve/gölge/koyu arka plan
                    if (sat < s.NameplateMinSat) continue; // beyaz/gri → renkli yazı değil
                    textPixels++;

                    if (useRefHue)
                    {
                        // Hangi referans renge (hue) daha yakın? Koruma'ya yakınsa koruma oyu.
                        if (HueDist(hue, guardianHue) < HueDist(hue, normalHue))
                            guardianVotes++;
                    }
                    else if (hue <= s.NameplateRedHueLo || hue >= s.NameplateRedHueHi)
                    {
                        guardianVotes++;   // fallback: sabit kırmızı bandı
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        // Yeterli koruma-oyu oranı → Guardian (normal mobu yanlış atlamamak için eşikli).
        if (guardianVotes >= s.NameplateRedMinPx && textPixels > 0 &&
            (float)guardianVotes / textPixels >= s.NameplateRedFrac)
            return NameplateClass.Guardian;
        if (textPixels >= s.NameplateRedMinPx)
            return NameplateClass.Normal;
        return NameplateClass.Unknown;
    }

    /// <summary>İki hue (0-360) arasındaki dairesel mesafe (0-180).</summary>
    private static float HueDist(float a, float b)
    {
        float d = Math.Abs(a - b) % 360f;
        return d > 180f ? 360f - d : d;
    }

    // Bant taraması için yeniden kullanılan buffer — her çağrıda heap allocation önler.
    [ThreadStatic] private static byte[]? _nameBandBuf;

    // ── ML HP bar altyapısı — ROI yakalama + temporal smoothing ─────────────

    /// <summary>
    /// Kalibre edilen ROI bölgesini ekrandan alır (ML classifier için girdi).
    /// </summary>
    public static Bitmap CaptureHpBarRoi(WtmSettings s)
    {
        var r = ResolutionMapper.Map(s.HpBarRoiX, s.HpBarRoiY, s.HpBarRoiW, s.HpBarRoiH);
        return CaptureRegion(Math.Max(0, r.X), Math.Max(0, r.Y),
                             Math.Max(1, r.Width), Math.Max(1, r.Height));
    }

    /// <summary>
    /// HP bar varlığını seçili yönteme göre kontrol eder: Hsv (varsayılan) veya ColorScan.
    /// </summary>
    public static bool IsTargetAliveSmoothed(WtmSettings s) => IsTargetAliveSmoothed(s, out _);

    /// <summary>V4 review-fix: offset'i AYNI taramadan (out-param) döner. KÖK NEDEN: çağıran eskiden
    /// bool döndükten SONRA ayrı bir satırda global WtmVision.LastBarOffsetY'yi okuyordu — araya sürekli
    /// çalışan tespit thread'i girip BAŞKA bir taramanın offset'ini yazabiliyordu (guardian ismi yanlış
    /// konumda arandı). Artık offset bu ÇAĞRIYLA atomik — araya kimse giremez.</summary>
    public static bool IsTargetAliveSmoothed(WtmSettings s, out int offsetY)
    {
        // ── HSV kırmızı-oran (varsayılan, en hızlı) ──────────────────────────
        // Mob HP bar ROI kutusu VEYA renk noktası kalibre ise çalışır.
        if (s.HpBarMode == HpBarDetectionMode.Hsv && s.IsHpBarLocated)
            return IsTargetAliveByHsv(s, out offsetY);

        // ── RGB renk taraması ─────────────────────────────────────────────── (offset kavramı yok)
        if (s.HpBarMode == HpBarDetectionMode.ColorScan && s.IsTargetHpColorCalibrated)
        {
            offsetY = 0;
            return IsTargetSelectedByHpColor(s);
        }

        // ── Fallback: seçili mod için kalibrasyon yok → renk taraması ─────────
        offsetY = 0;
        return IsTargetAlive(s);
    }
}
