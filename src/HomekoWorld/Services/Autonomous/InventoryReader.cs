using System.Drawing;
using System.Drawing.Imaging;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 33 — Envanter doluluğunu ölçer.
/// Envanter tuşuna basarak pencereyi açar, ızgara ROI'sini yakalar,
/// her yuva için HSV doygunluk ortalamasını hesaplar ve ratio döndürür.
/// </summary>
public sealed class InventoryReader
{
    private readonly AppState            _state;
    private readonly IKeyDeviceTransport _transport;

    public InventoryReader(AppState state, IKeyDeviceTransport transport)
    {
        _state     = state;
        _transport = transport;
    }

    public bool IsReady => _state.Autonomous.IsInventoryGridCalibrated;

    /// <summary>Kapatış I'sinden önce bekleme (ms): açıştan hemen sonraki toggle'ı oyun UI'ı yemesin.</summary>
    private const int InventoryCloseSettleMs = 300;

    /// <summary>
    /// Envanteri açar → ızgarayı yakalar → kapatır → doluluğu [0,1] döndürür.
    /// </summary>
    public async Task<float> CheckAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        await _transport.KeyDownAsync(s.InventoryKey, ct);
        await Task.Delay(50, ct);
        await _transport.KeyUpAsync(s.InventoryKey, CancellationToken.None);
        await Task.Delay(s.InventoryOpenDelayMs, ct);

        var roi = ResolutionMapper.Map(s.InventoryGridX, s.InventoryGridY, s.InventoryGridW, s.InventoryGridH);
        float occupancy;
        using (var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height))
            occupancy = ComputeOccupancy(bmp, s.InventoryRows, s.InventoryCols);

        await Task.Delay(InventoryCloseSettleMs, ct);   // açıştan hemen sonraki I toggle'ını oyun yemesin
        await _transport.KeyDownAsync(s.InventoryKey, ct);
        await Task.Delay(50, ct);
        await _transport.KeyUpAsync(s.InventoryKey, CancellationToken.None);
        await Task.Delay(200, ct);

        return occupancy;
    }

    /// <summary>
    /// CheckAsync ile aynı (aç→ölç→kapat) AMA yakalanan ROI'yi ızgara + her hücrenin içerik%
    /// + dolu(kırmızı)/boş(yeşil) kararıyla işaretleyip exe yanına 'inventory_debug.png' kaydeder.
    /// Yalnız Test/teşhis için (botun tam neyi taradığını görmek). Otonom döngü CheckAsync kullanır.
    /// </summary>
    public async Task<(float ratio, string debugPath)> CheckWithDebugAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        await _transport.KeyDownAsync(s.InventoryKey, ct);
        await Task.Delay(50, ct);
        await _transport.KeyUpAsync(s.InventoryKey, CancellationToken.None);
        await Task.Delay(s.InventoryOpenDelayMs, ct);

        var roi = ResolutionMapper.Map(s.InventoryGridX, s.InventoryGridY, s.InventoryGridW, s.InventoryGridH);
        float  occupancy;
        string path = System.IO.Path.Combine(AppContext.BaseDirectory, "inventory_debug.png");
        using (var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height))
        {
            occupancy = ComputeOccupancy(bmp, s.InventoryRows, s.InventoryCols);
            try   { SaveAnnotated(bmp, s.InventoryRows, s.InventoryCols, path); }
            catch (Exception ex) { path = $"(görsel kaydedilemedi: {ex.Message})"; }
        }

        await Task.Delay(InventoryCloseSettleMs, ct);   // açıştan hemen sonraki I toggle'ını oyun yemesin
        await _transport.KeyDownAsync(s.InventoryKey, ct);
        await Task.Delay(50, ct);
        await _transport.KeyUpAsync(s.InventoryKey, CancellationToken.None);
        await Task.Delay(200, ct);

        return (occupancy, path);
    }

    /// <summary>Yakalanan ROI üzerine ızgara + hücre içerik% + dolu/boş rengi çizip PNG kaydeder (teşhis).</summary>
    private static unsafe void SaveAnnotated(Bitmap src, int rows, int cols, string path)
    {
        int slotW = src.Width / cols, slotH = src.Height / rows;
        if (slotW <= 0 || slotH <= 0) return;

        var fracs = new float[rows, cols];
        var data  = src.LockBits(new Rectangle(0, 0, src.Width, src.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            int   stride = data.Stride;
            byte* scan0  = (byte*)data.Scan0.ToPointer();
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * slotW + 2, y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, src.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, src.Height - 1);
                    fracs[r, c] = InventorySlotScanner.SlotFillFraction(scan0, stride, x0, y0, x1, y1);
                }
        }
        finally { src.UnlockBits(data); }

        using var annotated = new Bitmap(src);
        using (var g    = Graphics.FromImage(annotated))
        using (var font = new Font("Consolas", 9))
        {
            for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    var  rect   = new Rectangle(c * slotW, r * slotH, slotW - 1, slotH - 1);
                    bool filled = fracs[r, c] >= InventorySlotScanner.FillCoverage;
                    var  color  = filled ? Color.Red : Color.Lime;
                    using var pen   = new Pen(color, 2);
                    using var brush = new SolidBrush(color);
                    g.DrawRectangle(pen, rect);
                    g.DrawString($"{(int)(fracs[r, c] * 100)}%", font, brush, rect.X + 3, rect.Y + 3);
                }
        }
        annotated.Save(path, ImageFormat.Png);
    }

    private static unsafe float ComputeOccupancy(Bitmap bmp, int rows, int cols)
    {
        int slotW = bmp.Width  / cols;
        int slotH = bmp.Height / rows;
        if (slotW <= 0 || slotH <= 0) return 0f;

        int total  = rows * cols;
        int filled = 0;

        var data = bmp.LockBits(
            new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly,
            PixelFormat.Format32bppArgb);
        try
        {
            int   stride = data.Stride;
            byte* scan0  = (byte*)data.Scan0.ToPointer();

            for (int r = 0; r < rows; r++)
            {
                for (int c = 0; c < cols; c++)
                {
                    // Inner bounds (2px inset to skip slot borders)
                    int x0 = c * slotW + 2;
                    int y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);

                    if (InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1)) filled++;
                }
            }
        }
        finally
        {
            bmp.UnlockBits(data);
        }

        return (float)filled / total;
    }
}
