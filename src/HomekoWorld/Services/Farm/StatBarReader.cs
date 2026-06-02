using System.Drawing;
using System.Drawing.Imaging;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Services.Farm;

/// <summary>
/// HP ve MP barlarını ekrandaki piksel rengine bakarak okur.
/// WtmVision.IsTargetSelected kalıbını takip eder.
/// </summary>
public static class StatBarReader
{
    /// <summary>
    /// HP yüzdesini tahmin eder (0.0 - 1.0).
    /// Bar'ın sol kenarından itibaren x tarama yaparak dolu rengi sayar.
    /// </summary>
    public static float ReadHpPercent(Bitmap frame, FarmSettings s)
        => ReadBarPercent(frame, s.HpBarLeft, s.HpBarY, s.HpBarWidth,
                          s.HpBarFullR, s.HpBarFullG, s.HpBarFullB, s.BarColorTol);

    /// <summary>MP yüzdesini tahmin eder (0.0 - 1.0).</summary>
    public static float ReadMpPercent(Bitmap frame, FarmSettings s)
        => ReadBarPercent(frame, s.MpBarLeft, s.MpBarY, s.MpBarWidth,
                          s.MpBarFullR, s.MpBarFullG, s.MpBarFullB, s.BarColorTol);

    private static float ReadBarPercent(
        Bitmap frame, int left, int y, int width,
        byte r, byte g, byte b, int tol)
    {
        if (width <= 0) return 1f; // kalibre edilmemişse dolu say (pot atmaz)
        if (left  <= 0) return 1f; // sol koordinat kalibre edilmemiş = güvenli
        if (y < 0 || y >= frame.Height) return 1f;

        int scanW = Math.Min(width, frame.Width - left);
        if (scanW <= 0) return 1f;

        // Tek satırı LockBits + unsafe pinned pointer ile tara — GetPixel (piksel-başına kilit/marshal)
        // YERİNE. Eskiden kare başına ~776 GetPixel (HP+MP) CPU yiyordu; ~80 FPS'te (Faz 26) kritik.
        // Inferrer/WtmVision aynı kalıbı kullanır. Frame Format32bppArgb (BGRA byte sırası).
        var data = frame.LockBits(new Rectangle(left, y, scanW, 1),
                                  ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        int matched = 0;
        try
        {
            unsafe
            {
                byte* row = (byte*)data.Scan0; // kilitlenen dikdörtgenin sol-üstü
                for (int x = 0; x < scanW; x++)
                {
                    byte* px = row + x * 4;     // BGRA: px[0]=B, px[1]=G, px[2]=R
                    if (Math.Abs(px[2] - r) <= tol &&
                        Math.Abs(px[1] - g) <= tol &&
                        Math.Abs(px[0] - b) <= tol)
                        matched++;
                }
            }
        }
        finally { frame.UnlockBits(data); }

        return (float)matched / scanW;
    }
}
