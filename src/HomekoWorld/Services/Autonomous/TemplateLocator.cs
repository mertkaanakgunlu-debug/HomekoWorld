using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Dev 3 — sliding-window NCC template LOCATOR (IconMatcher yalnız sabit-boyut SKOR verir, konum vermez).
/// Bir arama bölgesinde (ekran ROI capture) verilen şablon(lar)ın en iyi eşleşme KONUMUNU bulur → menü
/// ögelerini (Clan sekmesi, hazine sandığı) bulmak için. NCC, IconMatcher ile aynı normalize (kanal-birleşik
/// mean-çıkar + L2-norm) → skorlar karşılaştırılabilir. Pencere başına ALLOKASYONSUZ hesaplanır (formül:
/// NCC = Σ(w·t) / √(Σw² − (Σw)²/n), şablon t önceden mean-çıkarılmış + L2-normalize edilmiş, Σt≈0).
/// </summary>
public static class TemplateLocator
{
    /// <summary>Eşleşme sonucu — X,Y = ARAMA BÖLGESİ-YEREL eşleşme MERKEZİ (piksel); Score ≈ [-1,1].</summary>
    public readonly record struct Hit(int X, int Y, float Score);

    /// <summary>Bir base64 PNG şablon; native boyutundan (scaleX,scaleY) ile ölçeklenip NCC vektörüne çevrilir.</summary>
    public readonly record struct Template(float[] Vec, int W, int H);

    /// <summary>Verilen arama bölgesinde tüm şablonların en iyi NCC eşleşmesini (konum + skor) bulur.
    /// stride: pencere adımı (2 = 4× hız; sabit-konum UI için yeterli).</summary>
    public static unsafe Hit Locate(Bitmap region, IReadOnlyList<Template> templates, int stride = 2)
    {
        Hit best = new(-1, -1, -2f);
        if (region is null || templates is null || templates.Count == 0) return best;
        int SW = region.Width, SH = region.Height;
        stride = System.Math.Max(1, stride);

        var data = region.LockBits(new Rectangle(0, 0, SW, SH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stridePx = data.Stride;
            foreach (var t in templates)
            {
                int tw = t.W, th = t.H;
                if (tw <= 0 || th <= 0 || tw > SW || th > SH) continue;
                int n = tw * th * 3;
                if (t.Vec.Length != n) continue;

                fixed (float* tv0 = t.Vec)
                {
                    for (int y = 0; y + th <= SH; y += stride)
                    for (int x = 0; x + tw <= SW; x += stride)
                    {
                        double sumWT = 0, sumW = 0, sumW2 = 0;
                        int k = 0;
                        for (int yy = 0; yy < th; yy++)
                        {
                            byte* prow = scan0 + (long)(y + yy) * stridePx + (long)x * 4;
                            for (int xx = 0; xx < tw; xx++)
                            {
                                // BGR (Format32bppArgb bellek düzeni = B,G,R,A) — IconMatcher.Vectorize ile aynı sıra
                                float b = prow[0], g = prow[1], r = prow[2];
                                float tb = tv0[k], tg = tv0[k + 1], tr = tv0[k + 2];
                                sumWT += b * tb + g * tg + r * tr;
                                sumW  += b + g + r;
                                sumW2 += b * b + g * g + r * r;
                                k += 3;
                                prow += 4;
                            }
                        }
                        double denom = sumW2 - sumW * sumW / n;
                        if (denom < 1e-6) continue;
                        float ncc = (float)(sumWT / System.Math.Sqrt(denom));
                        if (ncc > best.Score) best = new Hit(x + tw / 2, y + th / 2, ncc);
                    }
                }
            }
        }
        finally { region.UnlockBits(data); }
        return best;
    }

    /// <summary>V4 — SABİT konumda şablon skoru (kayan arama YOK): rect sol-üst köşesi çevresinde ±2px
    /// mikro-arama (3×3, adım 2) içindeki EN İYİ NCC. Hedef penceresi ekranda sabit olduğundan tam-kare
    /// taraması gerekmez (&lt;0.1ms). Şablonlar kendi (ölçekli) W/H'siyle aynı sol-üst köşeden değerlendirilir.</summary>
    public static unsafe float ScoreAt(Bitmap frame, Rectangle rect, IReadOnlyList<Template> templates)
    {
        if (frame is null || templates is null || templates.Count == 0) return -2f;
        int FW = frame.Width, FH = frame.Height;
        var data = frame.LockBits(new Rectangle(0, 0, FW, FH), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        float best = -2f;
        try
        {
            byte* scan0 = (byte*)data.Scan0;
            int stridePx = data.Stride;
            foreach (var t in templates)
            {
                int tw = t.W, th = t.H, n = tw * th * 3;
                if (tw <= 0 || th <= 0 || t.Vec.Length != n) continue;
                fixed (float* tv0 = t.Vec)
                {
                    for (int jy = -2; jy <= 2; jy += 2)
                    for (int jx = -2; jx <= 2; jx += 2)
                    {
                        int x = rect.X + jx, y = rect.Y + jy;
                        if (x < 0 || y < 0 || x + tw > FW || y + th > FH) continue;
                        double sumWT = 0, sumW = 0, sumW2 = 0;
                        int k = 0;
                        for (int yy = 0; yy < th; yy++)
                        {
                            byte* prow = scan0 + (long)(y + yy) * stridePx + (long)x * 4;
                            for (int xx = 0; xx < tw; xx++)
                            {
                                float b = prow[0], g = prow[1], r = prow[2];
                                sumWT += b * tv0[k] + g * tv0[k + 1] + r * tv0[k + 2];
                                sumW  += b + g + r;
                                sumW2 += b * b + g * g + r * r;
                                k += 3;
                                prow += 4;
                            }
                        }
                        double denom = sumW2 - sumW * sumW / n;
                        if (denom < 1e-6) continue;
                        float ncc = (float)(sumWT / System.Math.Sqrt(denom));
                        if (ncc > best) best = ncc;
                    }
                }
            }
        }
        finally { frame.UnlockBits(data); }
        return best;
    }

    /// <summary>Base64 PNG şablonları (scaleX,scaleY) ile ölçekleyip NCC vektörlerine çevirir (Locate girdisi).
    /// Ölçek = mevcut-çözünürlük ROI genişliği / master ROI genişliği → farklı çözünürlükte de eşleşir.</summary>
    public static List<Template> BuildTemplates(IEnumerable<string> b64s, float scaleX, float scaleY)
    {
        var list = new List<Template>();
        if (b64s is null) return list;
        foreach (var b64 in b64s)
        {
            if (string.IsNullOrEmpty(b64)) continue;
            try
            {
                using var src = FromB64(b64);
                int w = System.Math.Max(1, (int)System.Math.Round(src.Width  * scaleX));
                int h = System.Math.Max(1, (int)System.Math.Round(src.Height * scaleY));
                using var scaled = Resize(src, w, h);
                list.Add(new Template(Vectorize(scaled), w, h));
            }
            catch { /* bozuk örnek atlanır */ }
        }
        return list;
    }

    // ── yardımcılar (IconMatcher ile aynı normalize) ─────────────────────────────
    private static Bitmap Resize(Bitmap src, int w, int h)
    {
        var bmp = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(src, 0, 0, w, h);
        return bmp;
    }

    /// <summary>w×h ikonu BGR kanallarından düz vektöre çevirir → kanal-birleşik ortalama-çıkar → L2-norm
    /// (IconMatcher.Vectorize ile birebir aynı; Locate'in σt≈0 varsayımı bununla sağlanır).</summary>
    private static float[] Vectorize(Bitmap wh)
    {
        int W = wh.Width, H = wh.Height;
        var rect = new Rectangle(0, 0, W, H);
        var data = wh.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf = new byte[stride * H];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        wh.UnlockBits(data);

        var v = new float[W * H * 3];
        float mean = 0f;
        int k = 0;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * stride + x * 4;
            float b = buf[i + 0], gch = buf[i + 1], r = buf[i + 2];
            v[k++] = b; v[k++] = gch; v[k++] = r;
            mean += b + gch + r;
        }
        mean /= v.Length;
        float norm = 0f;
        for (int i = 0; i < v.Length; i++) { v[i] -= mean; norm += v[i] * v[i]; }
        norm = System.MathF.Sqrt(norm);
        if (norm < 1e-6f) norm = 1e-6f;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    private static Bitmap FromB64(string b64)
    {
        var bytes = System.Convert.FromBase64String(b64);
        using var ms  = new MemoryStream(bytes);
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp);
    }
}
