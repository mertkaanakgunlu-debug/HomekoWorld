using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
namespace HomekoWorld.Services.Vision;

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

        int left  = Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW);
        int width = s.HpColorScanHalfW * 2;
        if (width <= 0) return false;

        using var bmp  = CaptureRegion(left, s.HpColorScanY, width, 1);
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
    public static bool IsTargetAliveByHsv(WtmSettings s)
    {
        // Tercih: ML ROI kutusu (2B) — düşük HP'ye dayanıklı, ek kalibrasyon yok.
        if (s.IsHpBarRoiCalibrated)
            return CountRedHsv(s.HpBarRoiX, s.HpBarRoiY, s.HpBarRoiW, s.HpBarRoiH, s) >= s.HpHsvMinPx;

        // Fallback: tek-satır renk tarama bölgesi (1px → eşik düşük tutulur).
        if (s.IsTargetHpColorCalibrated)
        {
            int left = Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW);
            int red  = CountRedHsv(left, s.HpColorScanY, s.HpColorScanHalfW * 2, 1, s);
            return red >= Math.Min(s.HpHsvMinPx, 4);
        }
        return false;
    }

    /// <summary>(rx,ry,rw,rh) bölgesini alır, kırmızı-hue (V/S eşikli) piksel sayısını döndürür.</summary>
    private static int CountRedHsv(int rx, int ry, int rw, int rh, WtmSettings s)
    {
        rx = Math.Max(0, rx); ry = Math.Max(0, ry);
        rw = Math.Max(1, rw); rh = Math.Max(1, rh);

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
                    if (val < s.HpHsvMinVal) continue; // koyu/gölge/boş bar arka planı
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
    {
        if (s.IsHpBarRoiCalibrated)
            return CountRedHsvFromFrame(frame, s.HpBarRoiX, s.HpBarRoiY, s.HpBarRoiW, s.HpBarRoiH, s) >= s.HpHsvMinPx;
        if (s.IsTargetHpColorCalibrated)
        {
            int left = Math.Max(0, s.HpColorScanX - s.HpColorScanHalfW);
            int red  = CountRedHsvFromFrame(frame, left, s.HpColorScanY, s.HpColorScanHalfW * 2, 1, s);
            return red >= Math.Min(s.HpHsvMinPx, 4);
        }
        return false;
    }

    /// <summary>(rx,ry,rw,rh) bölgesini SAĞLANAN tam kareden (yeni yakalama YOK) tarar; kırmızı-hue piksel sayar.</summary>
    private static int CountRedHsvFromFrame(Bitmap frame, int rx, int ry, int rw, int rh, WtmSettings s)
    {
        rx = Math.Max(0, rx); ry = Math.Max(0, ry);
        rw = Math.Max(1, rw); rh = Math.Max(1, rh);
        if (rx + rw > frame.Width)  rw = frame.Width  - rx;
        if (ry + rh > frame.Height) rh = frame.Height - ry;
        if (rw <= 0 || rh <= 0) return 0;

        var rect = new Rectangle(rx, ry, rw, rh);
        var data = frame.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int red  = 0;
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
                        if (val < s.HpHsvMinVal) continue; // koyu/gölge/boş bar arka planı
                        if (sat < s.HpHsvMinSat) continue; // soluk/gri
                        if (hue <= s.HpHueLo || hue >= s.HpHueHi) red++;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }
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

    private static void RgbToHsv(byte r, byte g, byte b,
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

    public static NameplateClass ReadNameplateClass(WtmSettings s)
    {
        // Tarama dikdörtgenini seç: önce çizilen bant, yoksa ofset fallback.
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

        using var bmp  = CaptureRegion(rx, ry, rw, rh);
        var       rect = new Rectangle(0, 0, rw, rh);
        var       data = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);

        int textPixels = 0; // parlak + doygun (renkli yazı) piksel
        int redPixels  = 0; // kırmızı hue bandındaki piksel

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
                    if (hue <= s.NameplateRedHueLo || hue >= s.NameplateRedHueHi)
                        redPixels++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        // Yalnız emin olunan kırmızıda Guardian (normal mobu yanlış atlamamak için güvenli varsayılan).
        if (redPixels >= s.NameplateRedMinPx && textPixels > 0 &&
            (float)redPixels / textPixels >= s.NameplateRedFrac)
            return NameplateClass.Guardian;
        if (textPixels >= s.NameplateRedMinPx)
            return NameplateClass.Normal;
        return NameplateClass.Unknown;
    }

    // Bant taraması için yeniden kullanılan buffer — her çağrıda heap allocation önler.
    [ThreadStatic] private static byte[]? _nameBandBuf;

    // ── ML HP bar altyapısı — ROI yakalama + temporal smoothing ─────────────

    /// <summary>
    /// Kalibre edilen ROI bölgesini ekrandan alır (ML classifier için girdi).
    /// </summary>
    public static Bitmap CaptureHpBarRoi(WtmSettings s)
    {
        int x = Math.Max(0, s.HpBarRoiX);
        int y = Math.Max(0, s.HpBarRoiY);
        int w = Math.Max(1, s.HpBarRoiW);
        int h = Math.Max(1, s.HpBarRoiH);
        return CaptureRegion(x, y, w, h);
    }

    // Temporal smoothing: son HpBarTemporalWindow karedeki karar geçmişi.
    // IsTargetAliveSmoothed birden fazla thread'den çağrılmayacak (FarmEngine tek döngü).
    private static readonly Queue<bool> _hpHistory = new();

    public static void ClearHpHistory() => _hpHistory.Clear();

    /// <summary>
    /// ML classifier + temporal smoothing ile HP bar varlığını kontrol eder.
    /// Dispatch sırası: 1) classifier (ONNX) + temporal, 2) renk tarama, 3) false.
    /// </summary>
    public static bool IsTargetAliveSmoothed(WtmSettings s, object? classifier)
    {
        // ── HSV kırmızı-oran (varsayılan, en hızlı) ──────────────────────────
        // ML ROI kutusu VEYA renk noktası kalibre ise çalışır. Smoothing kullanmaz;
        // ölüm debounce'unu FarmEngine.maxHpMisses sağlar.
        if (s.HpBarMode == HpBarDetectionMode.Hsv && s.IsHpBarLocated)
        {
            _hpHistory.Clear();
            return IsTargetAliveByHsv(s);
        }

        // ── RGB renk taraması ────────────────────────────────────────────────
        if (s.HpBarMode == HpBarDetectionMode.ColorScan && s.IsTargetHpColorCalibrated)
        {
            _hpHistory.Clear();
            return IsTargetSelectedByHpColor(s);
        }

        // ── ML ONNX + temporal smoothing ─────────────────────────────────────
        if (s.HpBarMode == HpBarDetectionMode.Ml &&
            classifier is HpBarPresenceClassifier clf && clf.IsLoaded && s.IsHpBarRoiCalibrated)
        {
            using var roi = CaptureHpBarRoi(s);
            float prob = clf.Predict(roi);
            bool rawResult = prob >= s.HpBarClassifierThreshold;

            // Temporal smoothing
            _hpHistory.Enqueue(rawResult);
            while (_hpHistory.Count > s.HpBarTemporalWindow)
                _hpHistory.Dequeue();

            int positives = _hpHistory.Count(x => x);
            bool smoothed = positives >= s.HpBarTemporalMinPositive;

            // Debug log — yalnız açıkça istenirse (senkron disk I/O performansı düşürür → varsayılan KAPALI).
            if (s.HpBarDebugLog)
            {
                try
                {
                    string logPath = System.IO.Path.Combine(AppContext.BaseDirectory, "debug_hp.txt");
                    string line = $"[{DateTime.Now:HH:mm:ss.fff}] prob={prob:F3} raw={rawResult} pos={positives}/{s.HpBarTemporalWindow} smoothed={smoothed}\n";
                    if (new System.IO.FileInfo(logPath) is { Exists: true, Length: > 500_000 })
                        System.IO.File.Delete(logPath);
                    System.IO.File.AppendAllText(logPath, line);
                }
                catch { }
            }

            return smoothed;
        }

        // ── Fallback: seçili mod için kalibrasyon/model yok → renk taraması ───
        _hpHistory.Clear();
        return IsTargetAlive(s);
    }
}
