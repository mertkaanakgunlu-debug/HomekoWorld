using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using HomekoWorld.Models.Autonomous;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 32 — Multi-örnekli glyph eşleştirmeli karakter tanıma.
/// 11 sınıf: 0-9 rakam + 10 = VİRGÜL (Faz 34 birleşik "X, Y" ROI'sinde ayıraç).
/// Her sınıf için en fazla MaxSamples referans saklanır; tahmin: her örnekle NCC, en yüksek skor.
/// </summary>
public sealed class GlyphDigitReader
{
    public const int W = 24, H = 32;
    public const int MaxSamples = 5;
    public const int Comma   = 10;   // virgül sınıf indeksi
    private const int Classes = 11;   // 0-9 + virgül

    private readonly List<float[]>[] _vecs = Enumerable.Range(0, Classes).Select(_ => new List<float[]>()).ToArray();
    private readonly List<string>[]  _b64s = Enumerable.Range(0, Classes).Select(_ => new List<string>()).ToArray();

    /// <summary>0-9 rakamlarının hepsi öğretildi mi (iki-ROI modu için yeterli).</summary>
    public bool IsReady
    {
        get { for (int d = 0; d < 10; d++) if (_vecs[d].Count == 0) return false; return true; }
    }

    /// <summary>Virgül örneği öğretildi mi (birleşik ROI modu için gerekli).</summary>
    public bool HasComma => _vecs[Comma].Count > 0;

    public bool[] TaughtMask()
    {
        var m = new bool[10];
        for (int d = 0; d < 10; d++) m[d] = _vecs[d].Count > 0;
        return m;
    }

    /// <summary>Kaç örnekle öğretildi (durum gösterimi için). cls: 0-9 veya 10=virgül.</summary>
    public int SampleCount(int cls) => cls is >= 0 and < Classes ? _vecs[cls].Count : 0;

    /// <summary>Bir sınıfın (0-9 veya 10=virgül) referansını ekler/günceller. MaxSamples dolunca en eskiyi çıkarır.</summary>
    public void SetGlyph(int cls, Bitmap cell)
    {
        if (cls < 0 || cls >= Classes) return;
        using var wh = ResizeGray(cell);
        if (_vecs[cls].Count >= MaxSamples) { _vecs[cls].RemoveAt(0); _b64s[cls].RemoveAt(0); }
        _vecs[cls].Add(Vectorize(wh));
        _b64s[cls].Add(ToB64(wh));
    }

    /// <summary>Tüm örnekleri temizler.</summary>
    public void Clear()
    {
        for (int d = 0; d < Classes; d++) { _vecs[d].Clear(); _b64s[d].Clear(); }
    }

    /// <summary>
    /// Hücreyi sınıflandırır → (sınıf, en yüksek NCC ~[0,1]). includeComma=false → yalnız 0-9
    /// (iki-ROI modu); true → 0-9 + virgül(10) (birleşik mod). Hazır değilse (-1, 0).
    /// </summary>
    public (int cls, float conf) Predict(Bitmap cell, bool includeComma = false)
    {
        if (!IsReady) return (-1, 0f);
        using var wh = ResizeGray(cell);
        var v = Vectorize(wh);
        float best = -2f; int bi = -1;
        int upper = includeComma ? Classes : 10;
        for (int d = 0; d < upper; d++)
        {
            foreach (var r in _vecs[d])
            {
                float ncc = Dot(v, r);
                if (ncc > best) { best = ncc; bi = d; }
            }
        }
        return (bi, best);
    }

    public void SaveToSettings(AutonomousSettings s)
    {
        var multi = new string[Classes][];
        for (int d = 0; d < Classes; d++)
            multi[d] = _b64s[d].Count > 0 ? _b64s[d].ToArray() : Array.Empty<string>();
        s.DigitGlyphsMulti = multi;
        s.DigitGlyphsVersion = 2;   // V2 normalizasyon (sıkı-kırpma + letterbox) ile üretildi
    }

    public void LoadFromSettings(AutonomousSettings s)
    {
        Clear();
        // V2 kapısı (2026-07-02): eski normalizasyonla (ham-kırpım) kaydedilmiş örnekler yeni sıkı-kırpma +
        // letterbox hücreleriyle NCC≈0 verir (kanıt: aynı "7" iki kırpımda 0.36; 1551→"11" okuma bug'ı).
        // Bozuk okuma üretmektense hiç yükleme → UI "öğretilmedi" gösterir, kullanıcı bir kez yeniden öğretir.
        if (s.DigitGlyphsVersion < 2) return;
        // Yeni çok-örnekli format (0-9 + virgül)
        var multi = s.DigitGlyphsMulti;
        if (multi is not null)
        {
            for (int d = 0; d < Classes && d < multi.Length; d++)
            {
                if (multi[d] is null) continue;
                foreach (var b64 in multi[d])
                {
                    if (string.IsNullOrEmpty(b64)) continue;
                    try { using var wh = FromB64(b64); _vecs[d].Add(Vectorize(wh)); _b64s[d].Add(b64); }
                    catch { }
                }
            }
        }
        // Geriye dönük: eski tek-örnekli alan (sadece boş slotları doldur)
        var legacy = s.DigitGlyphsB64;
        if (legacy is not null)
        {
            for (int d = 0; d < 10 && d < legacy.Length; d++)
            {
                if (_vecs[d].Count > 0 || string.IsNullOrEmpty(legacy[d])) continue;
                try { using var wh = FromB64(legacy[d]); _vecs[d].Add(Vectorize(wh)); _b64s[d].Add(legacy[d]); }
                catch { }
            }
        }
    }

    // ── yardımcılar ──────────────────────────────────────────────────────────
    private static Bitmap ResizeGray(Bitmap src)
    {
        var bmp = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(src, 0, 0, W, H);
        return bmp;
    }

    private static float[] Vectorize(Bitmap wh)
    {
        var rect   = new Rectangle(0, 0, W, H);
        var data   = wh.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf    = new byte[stride * H];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        wh.UnlockBits(data);

        var v = new float[W * H];
        float mean = 0f;
        for (int y = 0; y < H; y++)
        for (int x = 0; x < W; x++)
        {
            int i = y * stride + x * 4;
            float lum = 0.299f * buf[i + 2] + 0.587f * buf[i + 1] + 0.114f * buf[i + 0];
            v[y * W + x] = lum; mean += lum;
        }
        mean /= v.Length;
        float norm = 0f;
        for (int i = 0; i < v.Length; i++) { v[i] -= mean; norm += v[i] * v[i]; }
        norm = MathF.Sqrt(norm);
        if (norm < 1e-6f) norm = 1e-6f;
        for (int i = 0; i < v.Length; i++) v[i] /= norm;
        return v;
    }

    private static float Dot(float[] a, float[] b)
    {
        float s = 0f; int n = Math.Min(a.Length, b.Length);
        for (int i = 0; i < n; i++) s += a[i] * b[i];
        return s;
    }

    private static string ToB64(Bitmap wh)
    {
        using var ms = new MemoryStream();
        wh.Save(ms, ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }

    private static Bitmap FromB64(string b64)
    {
        var bytes = Convert.FromBase64String(b64);
        using var ms  = new MemoryStream(bytes);
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp);
    }
}
