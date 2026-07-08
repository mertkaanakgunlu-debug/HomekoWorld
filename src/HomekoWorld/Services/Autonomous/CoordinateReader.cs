using System.Drawing;
using System.Drawing.Imaging;
using HomekoWorld.Models;
using HomekoWorld.Models.Autonomous;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 32 — Minimap X/Y koordinatlarını okur.
/// İki ayrı ROI (X sayısı, Y sayısı) kullanıcı tarafından çizilir; her ROI:
///   yakala (CaptureRegion) → binarize (Otsu/eşik) → dikey-projeksiyon segmentasyonu (rakam hücreleri)
///   → her hücre <see cref="DigitClassifier"/> ile 0-9 → soldan sağa birleştir = tam sayı.
/// Herhangi bir rakam güven eşiğinin altındaysa veya hücre yoksa "okunamadı" (null) döner
/// (loading ekranı/boş kare → bayat değer kullanılmaz).
/// </summary>
public sealed class CoordinateReader
{
    private readonly GlyphDigitReader _glyph;
    private readonly AppState         _state;

    public CoordinateReader(GlyphDigitReader glyph, AppState state)
    {
        _glyph = glyph;
        _state = state;
    }

    public GlyphDigitReader Recognizer => _glyph;
    public bool IsReady => _state.Autonomous.IsCoordComboCalibrated
        ? (_glyph.IsReady && _glyph.HasComma)
        : (_glyph.IsReady && _state.Autonomous.IsCoordCalibrated);

    /// <summary>Anlık (X,Y) okur. Okunamazsa null.</summary>
    public (int x, int y)? Read()
    {
        if (!IsReady) return null;
        if (_state.Autonomous.IsCoordComboCalibrated) return ReadCombo();   // birleşik mod
        int? x = ReadValue(isX: true);
        int? y = ReadValue(isX: false);
        if (x is null || y is null) return null;
        return (x.Value, y.Value);
    }

    /// <summary>Tek bir ROI'yi (X veya Y) okur. Okunamazsa null.</summary>
    public int? ReadValue(bool isX)
    {
        var s = _state.Autonomous;
        var cells = CaptureRoiCells(isX);
        if (cells.Count == 0) return null;
        try
        {
            long val = 0; int digitCount = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i]);
                if (conf < s.MinDigitConfidence)
                {
                    // KENAR hücresi (ilk/son) düşük güvende atlanabilir (ROI kenarında kırpık glyph/ikon artığı).
                    // İÇ hücre düşükse sayı BOZUK çıkar (1551→"11" bug'ı: 5'ler sessizce düşüyordu) →
                    // tüm okumayı reddet; null "okunamadı" güvenlidir, yanlış sayı navigasyonu zehirler.
                    if (i == 0 || i == cells.Count - 1) continue;
                    return null;
                }
                val = val * 10 + d;
                if (++digitCount > 9) return null;
            }
            return digitCount == 0 ? null : (int)val;
        }
        finally { foreach (var c in cells) c.Dispose(); }
    }

    /// <summary>Verbose okuma — her hücrenin güven skorunu da döner. Teşhis için.</summary>
    public (int? value, string detail) ReadValueDebug(bool isX)
    {
        var s = _state.Autonomous;
        var cells = CaptureRoiCells(isX);
        if (cells.Count == 0) return (null, "0 hücre");
        try
        {
            var sb = new System.Text.StringBuilder($"{cells.Count} hücre: [");
            long val = 0; int digitCount = 0; bool fatal = false;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i]);
                sb.Append($"{(d < 0 ? '?' : (char)('0' + d))}={conf:F2}");
                if (conf < s.MinDigitConfidence)
                {
                    // gerçek okumayla aynı kural: kenar hücresi atlanır (~), İÇ hücre okumayı düşürür (✗)
                    if (i == 0 || i == cells.Count - 1) sb.Append('~');
                    else { sb.Append('✗'); fatal = true; }
                }
                else
                    { val = val * 10 + d; digitCount++; }
                if (i < cells.Count - 1) sb.Append(' ');
                if (digitCount > 9) break;
            }
            sb.Append($"] eşik={s.MinDigitConfidence:F2}");
            if (fatal) sb.Append(" — İÇ hücre eşik altı (✗) → okuma reddedildi");
            return (fatal || digitCount == 0) ? (null, sb.ToString()) : ((int)val, sb.ToString());
        }
        finally { foreach (var c in cells) c.Dispose(); }
    }

    /// <summary>
    /// Kalibre ROI'yi ekrandan yakalar ve rakam hücrelerine böler (örnek-toplama ve okuma ortak yolu).
    /// Çağıran döndürülen Bitmap'leri dispose etmelidir. ROI kalibre değilse boş liste.
    /// </summary>
    public List<Bitmap> CaptureRoiCells(bool isX)
    {
        var s  = _state.Autonomous;
        int mx = isX ? s.CoordXRoiX : s.CoordYRoiX;
        int my = isX ? s.CoordXRoiY : s.CoordYRoiY;
        int mw = isX ? s.CoordXRoiW : s.CoordYRoiW;
        int mh = isX ? s.CoordXRoiH : s.CoordYRoiH;
        if (mw <= 0 || mh <= 0) return new List<Bitmap>();

        var pr = ResolutionMapper.Map(mx, my, mw, mh);
        using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
        return SegmentDigits(bmp, s);
    }

    // ── Birleşik "X, Y" ROI (virgül ayıracı) ─────────────────────────────────────

    /// <summary>Birleşik ROI'yi yakalar ve karakter hücrelerine böler.</summary>
    public List<Bitmap> CaptureComboCells()
    {
        var s = _state.Autonomous;
        if (s.CoordComboRoiW <= 0 || s.CoordComboRoiH <= 0) return new List<Bitmap>();
        var pr = ResolutionMapper.Map(s.CoordComboRoiX, s.CoordComboRoiY, s.CoordComboRoiW, s.CoordComboRoiH);
        using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
        return SegmentDigits(bmp, s);
    }

    /// <summary>
    /// Birleşik "X, Y" ROI'sini okur: segment → her hücre 0-9/virgül → VİRGÜLDEN böl
    /// (öncesi X, sonrası Y). Virgül yoksa veya iki taraftan biri boşsa null (bayat değer kullanılmaz).
    /// Hane sayısı (3↔4) değişse de konuma değil virgüle dayandığından doğru okur.
    /// </summary>
    public (int x, int y)? ReadCombo()
    {
        var cells = CaptureComboCells();
        if (cells.Count == 0) return null;
        try
        {
            var s = _state.Autonomous;
            long xVal = 0, yVal = 0; int xc = 0, yc = 0; bool afterComma = false;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i], includeComma: true);
                if (conf < s.MinDigitConfidence)
                {
                    // Yalnız KENAR hücresi (ilk/son = ROI kenarındaki kırpık glyph/ikon artığı) atlanabilir.
                    // İÇ hücre düşükse sayı BOZUK çıkar (1551→"11": 5'ler sessizce düşüyordu) → okumayı reddet.
                    if (i == 0 || i == cells.Count - 1) continue;
                    return null;
                }
                if (d == GlyphDigitReader.Comma)
                {
                    if (afterComma) return null;               // ikinci virgül = bozuk segmentasyon → güvenme
                    afterComma = true; continue;
                }
                if (!afterComma) { xVal = xVal * 10 + d; if (++xc > 9) return null; }
                else             { yVal = yVal * 10 + d; if (++yc > 9) return null; }
            }
            if (!afterComma || xc == 0 || yc == 0) return null;          // virgül + iki taraf şart
            return ((int)xVal, (int)yVal);
        }
        finally { foreach (var c in cells) c.Dispose(); }
    }

    /// <summary>Verbose birleşik okuma — her hücrenin tahminini ve güvenini döner. Teşhis için.</summary>
    public (int? x, int? y, string detail) ReadComboDebug()
    {
        var cells = CaptureComboCells();
        if (cells.Count == 0) return (null, null, "0 hücre");
        try
        {
            var s = _state.Autonomous;
            var sb = new System.Text.StringBuilder($"{cells.Count} hücre: [");
            long xVal = 0, yVal = 0; int xc = 0, yc = 0; bool afterComma = false; bool fatal = false;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i], includeComma: true);
                char sym = d < 0 ? '?' : (d == GlyphDigitReader.Comma ? ',' : (char)('0' + d));
                sb.Append($"{sym}={conf:F2}");
                if (conf < s.MinDigitConfidence)
                {
                    // gerçek okumayla aynı kural: kenar hücresi atlanır (~), İÇ hücre okumayı düşürür (✗)
                    if (i == 0 || i == cells.Count - 1) sb.Append('~');
                    else { sb.Append('✗'); fatal = true; }
                }
                else if (d == GlyphDigitReader.Comma) { if (afterComma) fatal = true; else afterComma = true; }
                else if (!afterComma) { xVal = xVal * 10 + d; xc++; }
                else                  { yVal = yVal * 10 + d; yc++; }
                if (i < cells.Count - 1) sb.Append(' ');
            }
            sb.Append($"] eşik={s.MinDigitConfidence:F2}");
            if (fatal) sb.Append(" — İÇ hücre eşik altı / çift virgül (✗) → okuma reddedildi");
            int? x = (!fatal && afterComma && xc > 0) ? (int?)xVal : null;
            int? y = (!fatal && afterComma && yc > 0) ? (int?)yVal : null;
            return (x, y, sb.ToString());
        }
        finally { foreach (var c in cells) c.Dispose(); }
    }

    // ── Segmentasyon (V2 — 2026-07-02) ──────────────────────────────────────────
    // Hücreler artık ham-kırpım değil: BINARY + SIKI-KIRPMA + oran-koruyan letterbox (24×32).
    // KÖK NEDEN: ince çizgili glyph'lerde ham-luminance NCC'si hücre sınırının 1-2px kaymasına
    // (eşik titremesi / ROI'nin yeniden çizilmesi) aşırı duyarlıydı — AYNI "7" iki farklı kırpımda
    // NCC≈0.36 hatta ≈0 verdi → hane sessizce düşüp 1551 "11" okundu. Sıkı-kırpma glyph'i hücre
    // içindeki konumundan bağımsızlaştırır; letterbox oranı korur (1 ince-dik kalır, virgül küçük blob).
    // Ek: zayıf (anti-alias, 0<ink<h/8) sütun artık run'ı BÖLMEZ — yalnız TAM-BOŞ sütun ayıraçtır;
    // run geçerliliği için ≥1 güçlü sütun şart (arka plan gürültüsü hücre olamaz).

    /// <summary>ROI'nin luminance ızgarası + binarizasyon eşiği + sütun-başına mürekkep sayıları.</summary>
    private static (float[] lum, int thr, int[] ink, int minColInk) AnalyzeColumns(Bitmap roi, AutonomousSettings s)
    {
        int w = roi.Width, h = roi.Height;
        var rect = new Rectangle(0, 0, w, h);
        var data = roi.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        roi.UnlockBits(data);

        var lum = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;                       // BGRA
            lum[y * w + x] = 0.299f * buf[i + 2] + 0.587f * buf[i + 1] + 0.114f * buf[i + 0];
        }

        int thr = s.CoordBinThreshold;
        if (thr < 0) thr = OtsuThreshold(lum);

        int minColInk = Math.Max(1, h / 8);
        var ink = new int[w];
        for (int x = 0; x < w; x++)
        {
            int c = 0;
            for (int y = 0; y < h; y++)
                if (IsInk(lum[y * w + x], thr, s.CoordBinInvert)) c++;
            ink[x] = c;
        }
        return (lum, thr, ink, minColInk);
    }

    private static bool IsInk(float L, int thr, bool invert) => invert ? (L < thr) : (L >= thr);

    /// <summary>Ardışık mürekkepli (ink≥1) sütun run'ları; yalnız ≥minGap TAM-BOŞ (ink=0) sütun böler.</summary>
    private static List<(int s, int e)> FindRuns(int[] ink, AutonomousSettings st)
    {
        int w = ink.Length;
        int minGap = Math.Max(1, st.CoordMinGapPx);
        var runs = new List<(int s, int e)>();
        int runStart = -1, gap = 0;
        for (int x = 0; x < w; x++)
        {
            if (ink[x] >= 1) { if (runStart < 0) runStart = x; gap = 0; }
            else if (runStart >= 0)
            {
                gap++;
                if (gap >= minGap) { runs.Add((runStart, x - gap)); runStart = -1; gap = 0; }
            }
        }
        if (runStart >= 0) runs.Add((runStart, w - 1));
        return runs;
    }

    /// <summary>Run geçerli mi: yeterli genişlik + en az bir güçlü (ink≥h/8) sütun.</summary>
    private static bool RunValid((int s, int e) run, int[] ink, int minColInk, int minDigit)
    {
        if (run.e - run.s + 1 < minDigit) return false;
        for (int x = run.s; x <= run.e; x++) if (ink[x] >= minColInk) return true;
        return false;
    }

    private static List<Bitmap> SegmentDigits(Bitmap roi, AutonomousSettings s)
    {
        int w = roi.Width, h = roi.Height;
        var cells = new List<Bitmap>();
        if (w < 2 || h < 2) return cells;

        var (lum, thr, ink, minColInk) = AnalyzeColumns(roi, s);
        int minDigit = Math.Max(1, s.CoordMinDigitW);

        foreach (var run in FindRuns(ink, s))
        {
            if (!RunValid(run, ink, minColInk, minDigit)) continue;
            var cell = NormalizeCell(lum, w, h, run.s, run.e, thr, s.CoordBinInvert);
            if (cell is not null) cells.Add(cell);
        }
        return cells;
    }

    /// <summary>Run'ı BINARY + sıkı-bbox-kırpma + oran-koruyan letterbox ile 24×32 hücreye çevirir.
    /// Öğretme ve okuma AYNI yoldan geçer → hücre görünümü ROI çizimine/eşik titremesine bağımlı değil.</summary>
    private static Bitmap? NormalizeCell(float[] lum, int w, int h, int rs, int re, int thr, bool invert)
    {
        int minX = int.MaxValue, maxX = -1, minY = int.MaxValue, maxY = -1;
        for (int y = 0; y < h; y++)
        for (int x = rs; x <= re; x++)
            if (IsInk(lum[y * w + x], thr, invert))
            {
                if (x < minX) minX = x;
                if (x > maxX) maxX = x;
                if (y < minY) minY = y;
                if (y > maxY) maxY = y;
            }
        if (maxX < 0) return null;                            // run'da hiç mürekkep yok (teorik)
        int gw = maxX - minX + 1, gh = maxY - minY + 1;

        using var bin = new Bitmap(gw, gh, PixelFormat.Format32bppArgb);
        for (int y = 0; y < gh; y++)
        for (int x = 0; x < gw; x++)
            bin.SetPixel(x, y, IsInk(lum[(minY + y) * w + (minX + x)], thr, invert) ? Color.White : Color.Black);

        int W = GlyphDigitReader.W, H = GlyphDigitReader.H;
        float scale = Math.Min((float)W / gw, (float)H / gh);
        int dw = Math.Max(1, (int)MathF.Round(gw * scale));
        int dh = Math.Max(1, (int)MathF.Round(gh * scale));
        var cell = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(cell))
        {
            g.Clear(Color.Black);
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.Bilinear;
            g.DrawImage(bin, new Rectangle((W - dw) / 2, (H - dh) / 2, dw, dh),
                        new Rectangle(0, 0, gw, gh), GraphicsUnit.Pixel);
        }
        return cell;
    }

    /// <summary>
    /// Her iki ROI için teşhis PNG'si kaydeder (üst=ham, alt=binarize+hücre sınırları).
    /// Dönen dizi kaydedilen dosya yollarını içerir.
    /// </summary>
    public string[] SaveDebugImages(string dir)
    {
        var s = _state.Autonomous;
        var paths = new List<string>();
        if (s.IsCoordComboCalibrated)
            TrySaveDebugRoi(dir, s.CoordComboRoiX, s.CoordComboRoiY, s.CoordComboRoiW, s.CoordComboRoiH, "coord_debug_combo.png", paths);
        else
        {
            TrySaveDebugRoi(dir, s.CoordXRoiX, s.CoordXRoiY, s.CoordXRoiW, s.CoordXRoiH, "coord_debug_x.png", paths);
            TrySaveDebugRoi(dir, s.CoordYRoiX, s.CoordYRoiY, s.CoordYRoiW, s.CoordYRoiH, "coord_debug_y.png", paths);
        }
        return paths.ToArray();
    }

    private void TrySaveDebugRoi(string dir, int mx, int my, int mw, int mh, string filename, List<string> paths)
    {
        var s = _state.Autonomous;
        if (mw <= 0 || mh <= 0) return;

        var pr = ResolutionMapper.Map(mx, my, mw, mh);
        using var raw = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
        int w = raw.Width, h = raw.Height;

        // Segmentasyonla AYNI paylaşılan yol (AnalyzeColumns/FindRuns/RunValid) — PNG gerçeği yansıtır.
        var (lum, thr, ink, minColInk) = AnalyzeColumns(raw, s);
        int minDigit = Math.Max(1, s.CoordMinDigitW);
        var validRuns = FindRuns(ink, s).Where(r => RunValid(r, ink, minColInk, minDigit)).ToList();

        // Compose: top row = raw ×4, bottom row = binarized ×4 + cell markers
        const int scale = 4, margin = 2;
        int outW = Math.Max(w * scale, 80);
        int outH = h * scale * 2 + margin;
        using var canvas = new Bitmap(outW, outH, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.FromArgb(20, 20, 20));
            g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
            g.PixelOffsetMode   = System.Drawing.Drawing2D.PixelOffsetMode.Half;
            g.DrawImage(raw, new Rectangle(0, 0, w * scale, h * scale),
                        new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);

            int binY = h * scale + margin;
            using var bin = new Bitmap(w, h, PixelFormat.Format32bppArgb);
            for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
            {
                float L = lum[y * w + x];
                bool isInk = s.CoordBinInvert ? (L < thr) : (L >= thr);
                bin.SetPixel(x, y, isInk ? Color.White : Color.FromArgb(40, 40, 40));
            }
            g.DrawImage(bin, new Rectangle(0, binY, w * scale, h * scale),
                        new Rectangle(0, 0, w, h), GraphicsUnit.Pixel);

            using var redPen   = new System.Drawing.Pen(Color.Red, 1);
            using var greenPen = new System.Drawing.Pen(Color.Lime, 1);
            foreach (var (rs, re) in validRuns)
            {
                g.DrawLine(redPen,   rs * scale,          binY, rs * scale,          binY + h * scale - 1);
                g.DrawLine(greenPen, (re + 1) * scale - 1, binY, (re + 1) * scale - 1, binY + h * scale - 1);
            }
        }
        var path = System.IO.Path.Combine(dir, filename);
        canvas.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        paths.Add(path);
    }

    /// <summary>Luminance histogramı üzerinden Otsu eşiği (0-255).</summary>
    private static int OtsuThreshold(float[] lum)
    {
        var hist = new int[256];
        foreach (var L in lum)
        {
            int b = (int)L; if (b < 0) b = 0; else if (b > 255) b = 255;
            hist[b]++;
        }
        int total = lum.Length;
        float sum = 0; for (int t = 0; t < 256; t++) sum += t * hist[t];
        float sumB = 0; int wB = 0, thr = 127; float maxVar = -1f;
        for (int t = 0; t < 256; t++)
        {
            wB += hist[t]; if (wB == 0) continue;
            int wF = total - wB; if (wF == 0) break;
            sumB += t * hist[t];
            float mB = sumB / wB, mF = (sum - sumB) / wF;
            float between = (float)wB * wF * (mB - mF) * (mB - mF);
            if (between > maxVar) { maxVar = between; thr = t; }
        }
        return thr;
    }
}
