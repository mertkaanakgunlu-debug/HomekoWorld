using System.Drawing;
using System.Drawing.Imaging;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Farm;

/// <summary>
/// Kendi HP ve MP barlarını <b>iki-köşe dikdörtgen + HSV sütun-doluluk</b> ile okur.
/// İki köşe tıklanarak çizilen dikdörtgen barın tüm uzanımını (full track) kaplar; barın
/// kalibrasyon sırasında DOLU olması gerekmez. Her sütunda HSV kırmızı (HP) / mavi (MP)
/// piksel varsa o sütun "dolu" sayılır → <c>oran = dolu_sütun / toplam_sütun</c>. Bu yatay
/// barda gerçek doluluğu verir ve dikey kenar boşluğuna dayanıklıdır (saf piksel/alan
/// oranının "tam doluyken &lt;%100" hatasını önler).
///
/// Koordinatlar master pikselde saklanır; runtime'da <see cref="ResolutionMapper.Map"/> ile
/// geçerli ekrana eşlenir. Koruma mobu / mob HP barı tespitiyle aynı HSV yaklaşımı.
/// </summary>
public static class StatBarReader
{
    // Kırmızı (HP) ve mavi (MP) hue bantları. Kırmızı 360° sarması: hue ≤ Lo veya ≥ Hi.
    private const int RedHueLo  = 12;
    private const int RedHueHi  = 348;
    private const int BlueHueLo = 200;
    private const int BlueHueHi = 260;

    /// <summary>HP yüzdesini tahmin eder (0.0 - 1.0). Kalibre değilse 1.0 (pot atmaz).</summary>
    public static float ReadHpPercent(Bitmap frame, FarmSettings s)
        => ReadBarPercent(frame, s.HpBarLeft, s.HpBarTop, s.HpBarWidth, s.HpBarHeight, isBlue: false, s);

    /// <summary>MP yüzdesini tahmin eder (0.0 - 1.0). Kalibre değilse 1.0 (pot atmaz).</summary>
    public static float ReadMpPercent(Bitmap frame, FarmSettings s)
        => ReadBarPercent(frame, s.MpBarLeft, s.MpBarTop, s.MpBarWidth, s.MpBarHeight, isBlue: true, s);

    private static float ReadBarPercent(
        Bitmap frame, int left, int top, int width, int height, bool isBlue, FarmSettings s)
    {
        // Kalibre edilmemiş → güvenli (dolu say, pot atmaz).
        if (width <= 0 || height <= 0 || left <= 0) return 1f;

        // Master → geçerli ekran (köşeye sabitli bar, anchor-aware).
        var r = ResolutionMapper.Map(left, top, width, height);

        // Frame sınırlarına kırp.
        int x0 = Math.Max(0, r.X);
        int y0 = Math.Max(0, r.Y);
        int x1 = Math.Min(frame.Width,  r.X + r.Width);
        int y1 = Math.Min(frame.Height, r.Y + r.Height);
        int w  = x1 - x0;
        int h  = y1 - y0;
        if (w <= 0 || h <= 0) return 1f;

        int hueLo = isBlue ? BlueHueLo : RedHueLo;
        int hueHi = isBlue ? BlueHueHi : RedHueHi;

        var data = frame.LockBits(new Rectangle(x0, y0, w, h),
                                  ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int filledCols = 0;
        try
        {
            int stride = data.Stride;
            unsafe
            {
                byte* basePtr = (byte*)data.Scan0;
                for (int x = 0; x < w; x++)
                {
                    bool colFilled = false;
                    for (int y = 0; y < h; y++)
                    {
                        byte* px = basePtr + (long)y * stride + x * 4; // BGRA
                        WtmVision.RgbToHsv(px[2], px[1], px[0], out float hue, out float sat, out float val);
                        if (val < s.BarHsvMinVal) continue; // koyu/boş bar arka planı
                        if (sat < s.BarHsvMinSat) continue; // soluk/gri
                        bool match = isBlue ? (hue >= hueLo && hue <= hueHi)
                                            : (hue <= hueLo || hue >= hueHi);
                        if (match) { colFilled = true; break; } // sütunda 1 eşleşen yeter
                    }
                    if (colFilled) filledCols++;
                }
            }
        }
        finally { frame.UnlockBits(data); }

        return Math.Clamp((float)filledCols / w, 0f, 1f);
    }
}
