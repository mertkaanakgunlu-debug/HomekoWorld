using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 36 — Merchant NPC ile etkileşim ve envanter boşaltma (gerçek KO satış akışı).
/// Akış: sol-tık(seç) → sağ-tık(menü) → "I would like to trade." → "Sell Item" sekmesi →
/// mod-değiştir "Confirm" → ikon-eşleşen yuvaları sağ-tık ile satış çantasına taşı →
/// "Sell" → son "Confirm" → kapat. Satılacak eşyalar IconMatcher (template/NCC) ile seçilir;
/// iksir/parşömen gibi tutulacaklar satılmaz. Sağ-tık ANINDA satmaz — çanta + Sell + Confirm gerekir.
/// </summary>
public sealed class MerchantTrader
{
    private readonly AppState            _state;
    private readonly IKeyDeviceTransport _transport;
    private readonly IconMatcher         _icon;

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    public MerchantTrader(AppState state, IKeyDeviceTransport transport, IconMatcher icon)
    {
        _state     = state;
        _transport = transport;
        _icon      = icon;
    }

    public event EventHandler<string>? StatusChanged;

    /// <summary>
    /// NPC ile etkileşime girer ve ikon-eşleşen envanter yuvalarını satar.
    /// Satılan (çantaya taşınan) yuva sayısını döndürür.
    /// </summary>
    public async Task<int> TradeAsync(CancellationToken ct, bool saveDebug = false)
    {
        var s = _state.Autonomous;

        // 1-2. NPC: sol-tık (seç) → sağ-tık (trade/repair menüsü)
        Status("NPC seçiliyor…");
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int cx = sw / 2 + s.MerchantClickOffsetX;
        int cy = sh / 2 + s.MerchantClickOffsetY;
        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(80, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);
        await Task.Delay(150, ct);
        await _transport.ClickAsync(MouseButton.Right, ct);
        await Task.Delay(s.MerchantInteractDelayMs, ct);

        // 3. "I would like to trade." diyalog butonu
        if (s.IsTradeDialogCalibrated)
        {
            Status("Trade seçiliyor…");
            await ClickMasterAsync(s.TradeDialogX, s.TradeDialogY, MouseButton.Left, ct);
            await Task.Delay(s.TradeDialogDelayMs, ct);
        }
        else Status("⚠ 'I would like to trade.' butonu kalibre edilmedi");

        // 4. "Sell Item" sekmesi
        if (s.IsSellTabCalibrated)
        {
            Status("Sell Item sekmesine geçiliyor…");
            await ClickMasterAsync(s.SellTabX, s.SellTabY, MouseButton.Left, ct);
            await Task.Delay(s.SellTabDelayMs, ct);
        }
        else Status("⚠ 'Sell Item' sekmesi kalibre edilmedi");

        // 5. Purchase→Sell mod değişimi onayı ("Confirm")
        if (s.IsSellModeConfirmCalibrated)
        {
            Status("Satış moduna geçiş onaylanıyor…");
            await ClickMasterAsync(s.SellModeConfirmX, s.SellModeConfirmY, MouseButton.Left, ct);
            await Task.Delay(s.SellModeConfirmDelayMs, ct);
        }

        // 6-8. İkon-eşleşen yuvaları sat (çanta kapasitesi turları)
        int sold;
        if (!s.IsMerchantInvGridCalibrated)
        { Status("⚠ Merchant envanter ROI kalibre edilmedi — satış atlandı"); sold = 0; }
        else if (!_icon.IsReady)
        { Status("⚠ Satılacak ikon öğretilmedi — satış atlandı"); sold = 0; }
        else if (!s.IsSellButtonCalibrated)
        { Status("⚠ 'Sell' butonu kalibre edilmedi — satış atlandı"); sold = 0; }
        else
            sold = await SellLoopAsync(s, ct, saveDebug);

        // 9. Pencereyi kapat
        Status("Merchant penceresi kapatılıyor…");
        string closeKey = string.IsNullOrWhiteSpace(s.MerchantCloseKey) ? "Escape" : s.MerchantCloseKey;
        await _transport.KeyDownAsync(closeKey, ct);
        await Task.Delay(60, ct);
        await _transport.KeyUpAsync(closeKey, CancellationToken.None);
        await Task.Delay(s.MerchantCloseDelayMs, ct);

        Status(sold > 0 ? $"✓ {sold} eşya satıldı" : "Satılacak (eşleşen) eşya bulunamadı");
        return sold;
    }

    // ── Satış döngüsü ─────────────────────────────────────────────────────────────
    // Her tur: ikon-eşleşen yuvaları tara → çanta kapasitesi kadarını sağ-tık ile taşı →
    // "Sell" → son "Confirm". Satış sonrası yeniden tarar (yuva boşaldı/sıkışma olası);
    // eşleşen kalmayınca biter. maxRounds güvenlik valfi (Sell başarısızsa sonsuz döngü olmasın).
    private async Task<int> SellLoopAsync(Models.Autonomous.AutonomousSettings s, CancellationToken ct, bool saveDebug = false)
    {
        int total = 0;
        int cap = Math.Max(1, s.SellBagCapacity);
        const int maxRounds = 8;

        // Teşhis: merchant bot tarafından AÇIK + oyun odakta → güvenilir tarama görseli
        // (taşımadan ÖNCE, tüm coin'ler hâlâ envanterdeyken).
        if (saveDebug)
        {
            try { Status("📷 " + SaveScanDebug(AppContext.BaseDirectory)); }
            catch (Exception ex) { Status($"📷 teşhis hata: {ex.Message}"); }
            await Task.Delay(300, ct);
        }

        for (int round = 1; round <= maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();

            var sellable = ScanSellableCenters(s);
            if (sellable.Count == 0) break;

            int batch = Math.Min(cap, sellable.Count);
            Status($"Tur {round}: {batch} eşya satış çantasına taşınıyor…");

            for (int i = 0; i < batch; i++)
            {
                ct.ThrowIfCancellationRequested();
                await _transport.MoveAbsAsync(sellable[i].X, sellable[i].Y, ct);
                await Task.Delay(60, ct);
                await _transport.ClickAsync(
                    s.SellRightClick ? MouseButton.Right : MouseButton.Left, ct);
                await Task.Delay(s.SellItemDelayMs, ct);
            }

            // "Sell"
            Status("Sell butonuna tıklanıyor…");
            await ClickMasterAsync(s.SellButtonX, s.SellButtonY, MouseButton.Left, ct);
            await Task.Delay(s.SellButtonDelayMs, ct);

            // son "Confirm" (kalibre edilmişse)
            if (s.IsSellConfirmButtonCalibrated)
            {
                await ClickMasterAsync(s.SellConfirmButtonX, s.SellConfirmButtonY, MouseButton.Left, ct);
                await Task.Delay(s.SellConfirmButtonDelayMs, ct);
            }

            total += batch;
        }

        return total;
    }

    // Merchant envanter ROI'sini yakalar; dolu + ikon-eşleşen yuvaların ekran merkezini döndürür.
    private List<Point> ScanSellableCenters(Models.Autonomous.AutonomousSettings s)
    {
        var roi   = ResolutionMapper.Map(s.MerchantInvGridX, s.MerchantInvGridY, s.MerchantInvGridW, s.MerchantInvGridH);
        int rows  = s.MerchantInvRows, cols = s.MerchantInvCols;
        int slotW = roi.Width / cols, slotH = roi.Height / rows;
        var result = new List<Point>();
        if (slotW <= 0 || slotH <= 0) return result;

        using var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);

        // 1) dolu hücreleri bul (parlaklık/içerik kapısı — boş yuvalarda Match harcanmaz)
        var filled = new List<(int r, int c)>();
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int   stride = data.Stride;
                byte* scan0  = (byte*)data.Scan0.ToPointer();
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * slotW + 2;
                    int y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);
                    if (InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1))
                        filled.Add((r, c));
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        // 2) dolu yuvaları satılacak-ikon template'iyle eşleştir
        foreach (var (r, c) in filled)
        {
            int x0 = c * slotW + 2;
            int y0 = r * slotH + 2;
            int w  = Math.Min(slotW - 4, bmp.Width  - x0);
            int h  = Math.Min(slotH - 4, bmp.Height - y0);
            if (w <= 1 || h <= 1) continue;
            using var crop = bmp.Clone(new Rectangle(x0, y0, w, h), PixelFormat.Format32bppArgb);
            if (_icon.Match(crop) >= s.SellMatchThreshold)
                result.Add(new Point(
                    roi.X + c * slotW + slotW / 2,
                    roi.Y + r * slotH + slotH / 2));
        }

        return result;
    }

    private async Task ClickMasterAsync(int mx, int my, MouseButton btn, CancellationToken ct)
    {
        var p = ResolutionMapper.Map(mx, my, 1, 1);
        await _transport.MoveAbsAsync(p.X, p.Y, ct);
        await Task.Delay(60, ct);
        await _transport.ClickAsync(btn, ct);
    }

    /// <summary>
    /// Teşhis: merchant envanter ROI'sini yakalar, her yuvanın dolu/eşleşme skorunu işaretleyip
    /// &lt;dir&gt;\merchant_sell_debug.png kaydeder (+ öğretilen ikonları sell_template_N.png). Özet döndürür.
    /// Merchant penceresi SATIŞ MODUNDA açıkken çağrılmalı (envanter görünür olsun).
    /// </summary>
    public string SaveScanDebug(string dir)
    {
        var s = _state.Autonomous;
        if (!s.IsMerchantInvGridCalibrated) return "⚠ ⑤ Merchant envanter ROI kalibre edilmedi";

        var roi   = ResolutionMapper.Map(s.MerchantInvGridX, s.MerchantInvGridY, s.MerchantInvGridW, s.MerchantInvGridH);
        int rows  = s.MerchantInvRows, cols = s.MerchantInvCols;
        int slotW = roi.Width / cols, slotH = roi.Height / rows;
        if (slotW <= 0 || slotH <= 0) return "⚠ ROI çok küçük / geçersiz";

        using var bmp  = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);
        using var draw = new Bitmap(bmp);

        // Dolu hücreler — gerçek scan ile AYNI kapı (InventorySlotScanner).
        var fill = new bool[rows, cols];
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height),
            ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int   stride = data.Stride;
                byte* scan0  = (byte*)data.Scan0.ToPointer();
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * slotW + 2, y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);
                    fill[r, c] = InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1);
                }
            }
        }
        finally { bmp.UnlockBits(data); }

        int filledCount = 0, matchCount = 0;
        using (var g    = Graphics.FromImage(draw))
        using (var font = new Font("Consolas", 8f))
        {
            for (int r = 0; r < rows; r++)
            for (int c = 0; c < cols; c++)
            {
                int x0 = c * slotW, y0 = r * slotH;
                float score = -1f;
                if (fill[r, c])
                {
                    filledCount++;
                    int cx0 = x0 + 2, cy0 = y0 + 2;
                    int w = Math.Min(slotW - 4, bmp.Width  - cx0);
                    int h = Math.Min(slotH - 4, bmp.Height - cy0);
                    if (w > 1 && h > 1)
                    {
                        using var crop = bmp.Clone(new Rectangle(cx0, cy0, w, h), PixelFormat.Format32bppArgb);
                        score = _icon.Match(crop);
                    }
                }
                bool match = fill[r, c] && score >= s.SellMatchThreshold;
                if (match) matchCount++;

                using var pen = new Pen(match ? Color.Lime : (fill[r, c] ? Color.Red : Color.DimGray), match ? 2f : 1f);
                g.DrawRectangle(pen, x0, y0, slotW - 1, slotH - 1);
                if (fill[r, c])
                    g.DrawString(score.ToString("0.00"), font, match ? Brushes.Lime : Brushes.Orange, x0 + 2, y0 + 2);
            }
        }

        try { draw.Save(System.IO.Path.Combine(dir, "merchant_sell_debug.png"), ImageFormat.Png); }
        catch (Exception ex) { return $"⚠ Kaydedilemedi: {ex.Message}"; }
        int tmpl = _icon.SaveTemplateImages(dir);

        string iconInfo = _icon.IsReady ? $"{_icon.Count} örnek" : "YOK";
        string thr = s.SellMatchThreshold.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture);
        return $"ROI {roi.Width}×{roi.Height} {cols}×{rows} | dolu {filledCount}, eşleşen {matchCount} " +
               $"(eşik {thr}, ikon {iconInfo}) → merchant_sell_debug.png + {tmpl} template";
    }

    private void Status(string msg) => StatusChanged?.Invoke(this, msg);
}
