using System.Drawing;
using System.Drawing.Imaging;
using HomekoWorld.Models;
using HomekoWorld.Services.Vision;
using Xunit;

namespace HomekoWorld.Tests;

/// <summary>
/// 14.tur (Faz 6) guardian SAME-FRAME sınıflandırma testleri. Amaç: guardian kararının kök nedenlerini
/// (offset=0 duyuru-şeridi + çapraz-kare okuma) yapısal olarak kapatan `ReadNameplateClassFromFrame`
/// sözleşmesini kilitlemek. Tüm gözlemler tek bir sentetik kare üzerinden — ekran gerektirmez
/// (ResolutionMapper master ayarsızken identity eşler).
///
/// Renk seçimi kalibrasyonla EŞLEŞTİRİLİR: normal isim = mor (~286°), guardian isim = kırmızı (0°).
/// İki hue ~74° ayrık → useRefHue aktif (ayırt edilebilir iki referans).
/// </summary>
public class GuardianClassificationTests
{
    private static readonly Color Purple = Color.FromArgb(255, 160, 32, 200); // normal isim (mor)
    private static readonly Color Red    = Color.FromArgb(255, 200, 0, 0);    // guardian isim (kırmızı)

    private const int BandX = 10, BandY = 10, BandW = 100, BandH = 18;

    private static WtmSettings MakeSettings(bool refHueCalibrated, int announceShiftY = 0)
    {
        var s = new WtmSettings
        {
            GuardianDetectionEnabled = true,
            NameBandX = BandX, NameBandY = BandY, NameBandW = BandW, NameBandH = BandH,
            AnnounceShiftY = announceShiftY,
        };
        if (refHueCalibrated)
        {
            s.NormalNameR = 160; s.NormalNameG = 32; s.NormalNameB = 200; // mor referans
            s.GuardianNameR = 200; s.GuardianNameG = 0; s.GuardianNameB = 0; // kırmızı referans
        }
        return s;
    }

    private static Bitmap MakeFrame(int w, int h) => new(w, h, PixelFormat.Format32bppArgb);

    private static void Fill(Bitmap bmp, int x, int y, int w, int h, Color c)
    {
        for (int yy = y; yy < y + h; yy++)
            for (int xx = x; xx < x + w; xx++)
                bmp.SetPixel(xx, yy, c);
    }

    // ── Temel ayrım: mor=Normal, kırmızı=Guardian (referans-renk modu) ─────────────────────────

    [Fact]
    public void PurpleName_RefHueCalibrated_IsNormal()
    {
        var s = MakeSettings(refHueCalibrated: true);
        using var frame = MakeFrame(200, 120);
        Fill(frame, BandX, BandY, BandW, BandH, Purple);

        var cls = WtmVision.ReadNameplateClassFromFrame(frame, s, 0,
            out _, out _, out bool usedRefHue, out _, out _);

        Assert.True(usedRefHue); // kalibre iki renk → referans-renk modu
        Assert.Equal(WtmVision.NameplateClass.Normal, cls);
    }

    [Fact]
    public void RedName_RefHueCalibrated_IsGuardian()
    {
        var s = MakeSettings(refHueCalibrated: true);
        using var frame = MakeFrame(200, 120);
        Fill(frame, BandX, BandY, BandW, BandH, Red);

        var cls = WtmVision.ReadNameplateClassFromFrame(frame, s, 0,
            out _, out int votes, out bool usedRefHue, out _, out _);

        Assert.True(usedRefHue);
        Assert.True(votes >= s.NameplateRedMinPx, $"guardian oyu eşik altı: {votes}");
        Assert.Equal(WtmVision.NameplateClass.Guardian, cls);
    }

    // ── KÖK NEDEN kilidi: offset DECISIVE ─────────────────────────────────────────────────────
    // 2026-07-13 replay analizi: bu sunucuda pencere fiilen hep +AnnounceShiftY'de; isim ORADA (mor).
    // Offset=0'daki KALICI duyuru şeridi kırmızı/turuncu dekor içerdiğinden, isim yanlışlıkla offset=0'da
    // okununca "guardian" oyu topluyordu. Faz 6: offset ARTIK yapıdan (template) gelir → daima gerçek
    // pencere offset'i kullanılır. Bu test her iki okumayı da yapıp offset'in kararı belirlediğini kanıtlar.

    [Fact]
    public void NameAtStructureOffset_IsNormal_But_AnnouncementStripAtOffsetZero_MisreadsGuardian()
    {
        const int shift = 57;
        var s = MakeSettings(refHueCalibrated: true, announceShiftY: shift);
        using var frame = MakeFrame(200, 160);
        // Offset 0 = duyuru şeridi (kırmızı dekor); gerçek isim +shift'te (mor).
        Fill(frame, BandX, BandY, BandW, BandH, Red);              // şerit @ offset 0
        Fill(frame, BandX, BandY + shift, BandW, BandH, Purple);   // gerçek isim @ +shift

        // Yapının verdiği DOĞRU offset (+shift) → gerçek mor isim → Normal (pipeline artık bunu kullanır).
        var atStruct = WtmVision.ReadNameplateClassFromFrame(frame, s, shift,
            out _, out _, out _, out _, out _);
        Assert.Equal(WtmVision.NameplateClass.Normal, atStruct);

        // Eski renk-offset=0 yolu → duyuru şeridi → yanlış Guardian (regresyon belgeleyici).
        var atZero = WtmVision.ReadNameplateClassFromFrame(frame, s, 0,
            out _, out _, out _, out _, out _);
        Assert.Equal(WtmVision.NameplateClass.Guardian, atZero);
    }

    // ── Fallback modu: referans renk yoksa mor isim yine Normal (sabit-kırmızı bandı moru yakalamaz) ──
    // CheckGuardian bu modda guardian HÜKMÜ vermez; vision katmanı yine de moru Normal döndürmeli.

    [Fact]
    public void PurpleName_FallbackMode_IsNormal_AndUsedRefHueFalse()
    {
        var s = MakeSettings(refHueCalibrated: false);
        using var frame = MakeFrame(200, 120);
        Fill(frame, BandX, BandY, BandW, BandH, Purple);

        var cls = WtmVision.ReadNameplateClassFromFrame(frame, s, 0,
            out _, out _, out bool usedRefHue, out _, out _);

        Assert.False(usedRefHue); // referans renk yok → fallback (sabit kırmızı band)
        Assert.Equal(WtmVision.NameplateClass.Normal, cls); // mor (~286°) kırmızı bandın [<=12 || >=348] dışında
    }
}
