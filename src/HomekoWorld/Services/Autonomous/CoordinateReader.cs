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
            foreach (var cell in cells)
            {
                var (d, conf) = _glyph.Predict(cell);
                // Eşik altı hücre = kenar gürültüsü/boşluk → atla. (Navigasyon güvenilirliği
                // ReadReliableAsync'in kümeleme kapısında sağlanır; tek-okuma yolu yine en iyi tahmini döndürür.)
                if (conf < s.MinDigitConfidence) continue;
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
            long val = 0; int digitCount = 0;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i]);
                sb.Append($"{(d < 0 ? '?' : (char)('0' + d))}={conf:F2}");
                if (conf < s.MinDigitConfidence)
                    sb.Append('~');   // atlandı (gürültü/boşluk — MinDigitConfidence altı)
                else
                    { val = val * 10 + d; digitCount++; }
                if (i < cells.Count - 1) sb.Append(' ');
                if (digitCount > 9) break;
            }
            sb.Append($"] eşik={s.MinDigitConfidence:F2}");
            return digitCount == 0 ? (null, sb.ToString()) : ((int)val, sb.ToString());
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
            foreach (var cell in cells)
            {
                var (d, conf) = _glyph.Predict(cell, includeComma: true);
                // Eşik altı hücre = ROI kenarındaki kırpık glyph/gürültü (ör. baştaki yarım ikon, sondaki
                // virgül artığı) → ATLA. Aralıklı hane-düşmesine karşı koruma navigasyonda ReadReliableAsync'in
                // kümeleme kapısındadır; burada katı "tümünü reddet" geçerli okumaları da boğuyordu.
                if (conf < s.MinDigitConfidence) continue;
                if (d == GlyphDigitReader.Comma) { afterComma = true; continue; }
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
            long xVal = 0, yVal = 0; int xc = 0, yc = 0; bool afterComma = false;
            for (int i = 0; i < cells.Count; i++)
            {
                var (d, conf) = _glyph.Predict(cells[i], includeComma: true);
                char sym = d < 0 ? '?' : (d == GlyphDigitReader.Comma ? ',' : (char)('0' + d));
                sb.Append($"{sym}={conf:F2}");
                if (conf < s.MinDigitConfidence) sb.Append('~');
                else if (d == GlyphDigitReader.Comma) afterComma = true;
                else if (!afterComma) { xVal = xVal * 10 + d; xc++; }
                else                  { yVal = yVal * 10 + d; yc++; }
                if (i < cells.Count - 1) sb.Append(' ');
            }
            sb.Append($"] eşik={s.MinDigitConfidence:F2}");
            int? x = (afterComma && xc > 0) ? (int?)xVal : null;
            int? y = (afterComma && yc > 0) ? (int?)yVal : null;
            return (x, y, sb.ToString());
        }
        finally { foreach (var c in cells) c.Dispose(); }
    }

    // ── Segmentasyon ────────────────────────────────────────────────────────────
    private static List<Bitmap> SegmentDigits(Bitmap roi, AutonomousSettings s)
    {
        int w = roi.Width, h = roi.Height;
        var cells = new List<Bitmap>();
        if (w < 2 || h < 2) return cells;

        var rect = new Rectangle(0, 0, w, h);
        var data = roi.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        roi.UnlockBits(data);

        // luminance ızgarası
        var lum = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;                       // BGRA
            lum[y * w + x] = 0.299f * buf[i + 2] + 0.587f * buf[i + 1] + 0.114f * buf[i + 0];
        }

        int thr = s.CoordBinThreshold;
        if (thr < 0) thr = OtsuThreshold(lum);

        // sütun başına "mürekkep" var mı (sütundaki ink piksel sayısı >= h/8)
        int minColInk = Math.Max(1, h / 8);
        var inkCol = new bool[w];
        for (int x = 0; x < w; x++)
        {
            int ink = 0;
            for (int y = 0; y < h; y++)
            {
                float L = lum[y * w + x];
                bool isInk = s.CoordBinInvert ? (L < thr) : (L >= thr);
                if (isInk) ink++;
            }
            inkCol[x] = ink >= minColInk;
        }

        // ardışık ink sütunlarını rakam run'larına grupla (>= CoordMinGapPx boşluk böler)
        int minGap   = Math.Max(1, s.CoordMinGapPx);
        int minDigit = Math.Max(1, s.CoordMinDigitW);
        int runStart = -1, gap = 0;
        var runs = new List<(int s, int e)>();
        for (int x = 0; x < w; x++)
        {
            if (inkCol[x]) { if (runStart < 0) runStart = x; gap = 0; }
            else if (runStart >= 0)
            {
                gap++;
                if (gap >= minGap) { runs.Add((runStart, x - gap)); runStart = -1; gap = 0; }
            }
        }
        if (runStart >= 0) runs.Add((runStart, w - 1));

        foreach (var (rs, re) in runs)
        {
            int rw = re - rs + 1;
            if (rw < minDigit) continue;                      // gürültü
            var cell = new Bitmap(rw, h, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(cell))
                g.DrawImage(roi, new Rectangle(0, 0, rw, h), new Rectangle(rs, 0, rw, h), GraphicsUnit.Pixel);
            cells.Add(cell);
        }
        return cells;
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

        // Luminance + binarize (same logic as SegmentDigits)
        var rect = new Rectangle(0, 0, w, h);
        var bdata = raw.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = bdata.Stride;
        var buf = new byte[stride * h];
        System.Runtime.InteropServices.Marshal.Copy(bdata.Scan0, buf, 0, buf.Length);
        raw.UnlockBits(bdata);

        var lum = new float[w * h];
        for (int y = 0; y < h; y++)
        for (int x = 0; x < w; x++)
        {
            int i = y * stride + x * 4;
            lum[y * w + x] = 0.299f * buf[i + 2] + 0.587f * buf[i + 1] + 0.114f * buf[i];
        }

        int thr = s.CoordBinThreshold;
        if (thr < 0) thr = OtsuThreshold(lum);

        int minColInk = Math.Max(1, h / 8);
        var inkCol = new bool[w];
        for (int x = 0; x < w; x++)
        {
            int ink = 0;
            for (int y = 0; y < h; y++)
            {
                float L = lum[y * w + x];
                bool isInk = s.CoordBinInvert ? (L < thr) : (L >= thr);
                if (isInk) ink++;
            }
            inkCol[x] = ink >= minColInk;
        }

        int minGap = Math.Max(1, s.CoordMinGapPx), minDigit = Math.Max(1, s.CoordMinDigitW);
        int runStart = -1, gap = 0;
        var runs = new List<(int rs, int re)>();
        for (int x = 0; x < w; x++)
        {
            if (inkCol[x]) { if (runStart < 0) runStart = x; gap = 0; }
            else if (runStart >= 0)
            {
                gap++;
                if (gap >= minGap) { runs.Add((runStart, x - gap)); runStart = -1; gap = 0; }
            }
        }
        if (runStart >= 0) runs.Add((runStart, w - 1));
        var validRuns = runs.Where(r => r.re - r.rs + 1 >= minDigit).ToList();

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
