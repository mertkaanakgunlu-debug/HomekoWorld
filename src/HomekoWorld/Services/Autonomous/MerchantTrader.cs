using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 36 — Merchant NPC ile etkileşim ve envanter boşaltma.
/// Akış: NPC tıkla → satış sekmesi tıkla → dolu yuvaları sat → kapat.
/// Sell-All modu veya yuva-başına sağ/sol-tık ile iki satış yöntemi desteklenir.
/// </summary>
public sealed class MerchantTrader
{
    private readonly AppState            _state;
    private readonly IKeyDeviceTransport _transport;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public MerchantTrader(AppState state, IKeyDeviceTransport transport)
    {
        _state     = state;
        _transport = transport;
    }

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// NPC ile etkileşime girer ve dolu envanter yuvalarını satar.
    /// Satılan yuva sayısını döndürür; sell-all modunda -1 (bilinmiyor) döner.
    /// </summary>
    public async Task<int> TradeAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        // 1. NPC'ye tıkla
        Status("NPC tıklanıyor…");
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int cx = sw / 2 + s.MerchantClickOffsetX;
        int cy = sh / 2 + s.MerchantClickOffsetY;
        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(80, ct);
        await _transport.ClickAsync(
            s.MerchantRightClick ? MouseButton.Right : MouseButton.Left, ct);
        await Task.Delay(s.MerchantInteractDelayMs, ct);

        // 2. Satış sekmesine tıkla (kalibre edilmişse)
        if (s.IsSellTabCalibrated)
        {
            Status("Satış sekmesine tıklanıyor…");
            var tab = ResolutionMapper.Map(s.SellTabX, s.SellTabY, 1, 1);
            await _transport.MoveAbsAsync(tab.X, tab.Y, ct);
            await Task.Delay(80, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
            await Task.Delay(s.SellTabDelayMs, ct);
        }

        // 3. Sat
        int sold;
        if (s.UseSellAllButton && s.IsSellAllButtonCalibrated)
        {
            Status("Tümünü sat butonuna tıklanıyor…");
            var btn = ResolutionMapper.Map(s.SellAllButtonX, s.SellAllButtonY, 1, 1);
            await _transport.MoveAbsAsync(btn.X, btn.Y, ct);
            await Task.Delay(80, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
            await Task.Delay(s.SellConfirmDelayMs, ct);
            if (!string.IsNullOrWhiteSpace(s.SellConfirmKey))
            {
                await _transport.KeyDownAsync(s.SellConfirmKey, ct);
                await Task.Delay(60, ct);
                await _transport.KeyUpAsync(s.SellConfirmKey, CancellationToken.None);
                await Task.Delay(200, ct);
            }
            sold = -1; // sell-all: mevcut miktar bilinmiyor
        }
        else if (s.IsInventoryGridCalibrated)
        {
            sold = await SellSlotsAsync(s, ct);
        }
        else
        {
            Status("⚠ Envanter ızgara ROI kalibre edilmedi — satış atlandı");
            sold = 0;
        }

        // 4. Toplu onay butonu (opsiyonel — bazı KO sürümlerinde tüm sell-area'yı onaylar)
        if (s.IsSellConfirmButtonCalibrated)
        {
            Status("Satış onaylanıyor…");
            var btn = ResolutionMapper.Map(s.SellConfirmButtonX, s.SellConfirmButtonY, 1, 1);
            await _transport.MoveAbsAsync(btn.X, btn.Y, ct);
            await Task.Delay(80, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
            await Task.Delay(s.SellConfirmButtonDelayMs, ct);
        }

        // 5. Pencereyi kapat
        Status("Merchant penceresi kapatılıyor…");
        string closeKey = string.IsNullOrWhiteSpace(s.MerchantCloseKey) ? "Escape" : s.MerchantCloseKey;
        await _transport.KeyDownAsync(closeKey, ct);
        await Task.Delay(60, ct);
        await _transport.KeyUpAsync(closeKey, CancellationToken.None);
        await Task.Delay(s.MerchantCloseDelayMs, ct);

        return sold;
    }

    // ── Slot-başına satış ─────────────────────────────────────────────────────────

    private async Task<int> SellSlotsAsync(Models.Autonomous.AutonomousSettings s, CancellationToken ct)
    {
        var roi    = ResolutionMapper.Map(s.InventoryGridX, s.InventoryGridY, s.InventoryGridW, s.InventoryGridH);
        int slotW  = roi.Width  / s.InventoryCols;
        int slotH  = roi.Height / s.InventoryRows;
        if (slotW <= 0 || slotH <= 0) return 0;

        Status("Envanter taranıyor — dolu yuvalar satılıyor…");

        List<(int px, int py)> filled;
        using (var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height))
            filled = FindFilledSlots(bmp, s.InventoryRows, s.InventoryCols,
                                     roi.X, roi.Y, slotW, slotH);

        if (filled.Count == 0)
        {
            Status("Envanter boş — satacak eşya yok");
            return 0;
        }

        int sold = 0;
        foreach (var (px, py) in filled)
        {
            ct.ThrowIfCancellationRequested();
            await _transport.MoveAbsAsync(px, py, ct);
            await Task.Delay(60, ct);
            await _transport.ClickAsync(
                s.SellRightClick ? MouseButton.Right : MouseButton.Left, ct);

            if (!string.IsNullOrWhiteSpace(s.SellConfirmKey))
            {
                await Task.Delay(s.SellConfirmDelayMs, ct);
                await _transport.KeyDownAsync(s.SellConfirmKey, ct);
                await Task.Delay(60, ct);
                await _transport.KeyUpAsync(s.SellConfirmKey, CancellationToken.None);
            }

            await Task.Delay(s.SellItemDelayMs, ct);
            sold++;
        }

        return sold;
    }

    // ── Bitmap tarama — dolu yuva merkezlerini döndürür ──────────────────────────

    private static unsafe List<(int px, int py)> FindFilledSlots(
        System.Drawing.Bitmap bmp,
        int rows, int cols,
        int originX, int originY, int slotW, int slotH)
    {
        var result = new List<(int, int)>(rows * cols);

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
                    int x0 = c * slotW + 2;
                    int y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);

                    if (InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1))
                    {
                        result.Add((
                            originX + c * slotW + slotW / 2,
                            originY + r * slotH + slotH / 2));
                    }
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        return result;
    }

    private void Status(string msg) => StatusChanged?.Invoke(this, msg);
}
