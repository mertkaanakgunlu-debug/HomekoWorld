using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using HomekoWorld.Models.Autonomous;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 36 — Satılacak eşya ikonunu örnek (template) ile tanıma.
/// GlyphDigitReader ile aynı NCC deseni; ama RGB (renk + şekil) → altın coin'i kırmızı
/// iksir / mavi rün gibi farklı renk-şekildeki eşyalardan ayırır. Kullanıcı satılacak
/// ikonun çevresine kutu çizip "öğretir"; her envanter yuvası bu örneklerle eşleştirilir.
/// </summary>
public sealed class IconMatcher
{
    public const int N = 24;            // ikon NxN'e küçültülür
    public const int MaxSamples = 8;    // farklı satılabilir ikon/varyasyon

    private readonly List<float[]> _vecs = new();
    private readonly List<string>  _b64s = new();

    /// <summary>En az bir örnek öğretildi mi.</summary>
    public bool IsReady => _vecs.Count > 0;

    /// <summary>Öğretilen örnek sayısı (durum gösterimi).</summary>
    public int Count => _vecs.Count;

    /// <summary>Yeni bir satılacak-ikon örneği ekler. MaxSamples dolunca en eskiyi atar.</summary>
    public void AddTemplate(Bitmap icon)
    {
        using var wh = Resize(icon);
        if (_vecs.Count >= MaxSamples) { _vecs.RemoveAt(0); _b64s.RemoveAt(0); }
        _vecs.Add(Vectorize(wh));
        _b64s.Add(ToB64(wh));
    }

    /// <summary>Tüm örnekleri temizler.</summary>
    public void Clear() { _vecs.Clear(); _b64s.Clear(); }

    /// <summary>Verilen ikon görüntüsünün en yüksek NCC skoru (~[-1,1]). Örnek yoksa -1.</summary>
    public float Match(Bitmap icon)
    {
        if (_vecs.Count == 0) return -1f;
        using var wh = Resize(icon);
        var v = Vectorize(wh);
        float best = -2f;
        foreach (var r in _vecs)
        {
            float ncc = Dot(v, r);
            if (ncc > best) best = ncc;
        }
        return best;
    }

    public void SaveToSettings(AutonomousSettings s) => s.SellTemplatesB64 = _b64s.ToArray();

    public void LoadFromSettings(AutonomousSettings s)
    {
        Clear();
        var arr = s.SellTemplatesB64;
        if (arr is null) return;
        foreach (var b64 in arr)
        {
            if (string.IsNullOrEmpty(b64)) continue;
            try { using var wh = FromB64(b64); _vecs.Add(Vectorize(wh)); _b64s.Add(b64); }
            catch { /* bozuk örnek atlanır */ }
        }
    }

    /// <summary>Öğretilen template'leri &lt;dir&gt;\sell_template_N.png olarak kaydeder (teşhis). Kaydedilen sayıyı döndürür.</summary>
    public int SaveTemplateImages(string dir)
    {
        int n = 0;
        for (int i = 0; i < _b64s.Count; i++)
        {
            try { using var bmp = FromB64(_b64s[i]); bmp.Save(System.IO.Path.Combine(dir, $"sell_template_{i}.png"), ImageFormat.Png); n++; }
            catch { }
        }
        return n;
    }

    // ── yardımcılar ──────────────────────────────────────────────────────────────
    private static Bitmap Resize(Bitmap src)
    {
        var bmp = new Bitmap(N, N, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bmp);
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.DrawImage(src, 0, 0, N, N);
        return bmp;
    }

    /// <summary>NxN ikonu BGR kanallarından düz vektöre çevirir → ortalama-çıkar → L2-norm.
    /// Mean-subtract + normalize sayesinde NCC parlaklık/ölçekten bağımsız, renk+desen yakalar.</summary>
    private static float[] Vectorize(Bitmap wh)
    {
        var rect   = new Rectangle(0, 0, N, N);
        var data   = wh.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int stride = data.Stride;
        var buf    = new byte[stride * N];
        System.Runtime.InteropServices.Marshal.Copy(data.Scan0, buf, 0, buf.Length);
        wh.UnlockBits(data);

        var v = new float[N * N * 3];
        float mean = 0f;
        int k = 0;
        for (int y = 0; y < N; y++)
        for (int x = 0; x < N; x++)
        {
            int i = y * stride + x * 4;
            float b = buf[i + 0], gch = buf[i + 1], r = buf[i + 2];
            v[k++] = b; v[k++] = gch; v[k++] = r;
            mean += b + gch + r;
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
