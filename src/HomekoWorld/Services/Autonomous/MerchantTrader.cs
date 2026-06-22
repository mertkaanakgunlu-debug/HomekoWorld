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

    /// <summary>NPC etkileşimi başarısız (sağ-tık menüsü açılmadı = tık ıskaladı). Çağıran turu tekrarlar.
    /// ≥0 dönüş = etkileşim OK (satılan/onarılan yuva sayısı; 0 = açıldı ama satılacak/onarılacak yok).</summary>
    public const int InteractionFailed = -2;

    // Menü-açıldı doğrulaması (Faz 41): imleç çevresindeki bölge bu kadar DEĞİŞTİYSE menü açıldı say.
    private const double MenuMinChangedFrac = 0.05; // ≥%5 piksel değişti → opak menü kutusu belirdi
    private const int    MenuPixelDelta     = 28;   // kanal-başı bu eşiği aşan fark "değişti" sayılır

    /// <summary>
    /// NPC ile etkileşime girer ve ikon-eşleşen envanter yuvalarını satar.
    /// Satılan (çantaya taşınan) yuva sayısını döndürür; menü açılmadıysa <see cref="InteractionFailed"/>.
    /// </summary>
    /// <param name="npcScreenPoint">YOLO ile tespit edilen NPC ekran-noktası. Verilirse buraya (+ ince-ayar
    /// offset) tıklanır ve menü-açıldı doğrulaması yapılır; null ise eski kör merkez+offset tık (doğrulama yok).</param>
    public async Task<int> TradeAsync(CancellationToken ct, System.Drawing.Point? npcScreenPoint = null, bool saveDebug = false)
    {
        var s = _state.Autonomous;

        // 1-2. NPC: sol-tık (seç) → sağ-tık (trade/repair menüsü)
        Status("NPC seçiliyor…");
        var (cx, cy) = ResolveNpcClick(s, npcScreenPoint);

        // Menü doğrulama için tık ÖNCESİ imleç-çevresi (yalnız tespit-tabanlı tıkta; kör fallback'te atlanır).
        var (px, py, pw, ph) = MenuProbeRect(cx, cy);
        System.Drawing.Bitmap? beforeMenu = npcScreenPoint is not null
            ? WtmVision.CaptureRegion(px, py, pw, ph) : null;

        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(80, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);
        await Task.Delay(150, ct);
        await _transport.ClickAsync(MouseButton.Right, ct);
        await Task.Delay(s.MerchantInteractDelayMs, ct);

        // Doğrula: sağ-tık menüsü açıldı mı? Açılmadıysa erken çık — kör satış adımlarına DEVAM ETME.
        if (beforeMenu is not null && !MenuOpened(px, py, pw, ph, beforeMenu))
        {
            Status("⚠ NPC menüsü açılmadı (tık ıskaladı?) — etkileşim iptal");
            await TapCloseAsync(s, ct);   // olası yarım menü/seçimi temizle (sonraki deneme temiz başlasın)
            return InteractionFailed;
        }

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
        await TapCloseAsync(s, ct);

        Status(sold > 0 ? $"✓ {sold} eşya satıldı" : "Satılacak (eşleşen) eşya bulunamadı");
        return sold;
    }

    // ── Tamir (Faz 40) ──────────────────────────────────────────────────────────
    /// <summary>
    /// AYNI merchant NPC'de tamir: NPC sol-seç→sağ-menü → "I want to make repairs." → envanter
    /// (çekiç imleç) açılır → kullanıcının seçtiği giyili-ekipman slotlarına SIRAYLA birer sol tık
    /// (anında onarır, onay penceresi yok) → kapat. Onarılan slot sayısını döndürür.
    /// </summary>
    public async Task<int> RepairAsync(CancellationToken ct, System.Drawing.Point? npcScreenPoint = null)
    {
        var s = _state.Autonomous;

        if (!s.IsRepairDialogCalibrated || !s.IsRepairEquipGridCalibrated)
        { Status("⚠ Tamir kalibre edilmedi (diyalog / ekipman ızgarası) — tamir atlandı"); return 0; }

        var slots = s.RepairSlots;
        if (slots is null || slots.Length == 0)
        { Status("⚠ Tamir edilecek slot seçilmedi — tamir atlandı"); return 0; }

        // 1-2. NPC: sol-tık (seç) → sağ-tık (menü) — satışla aynı tık modeli
        Status("Tamir için NPC seçiliyor…");
        var (cx, cy) = ResolveNpcClick(s, npcScreenPoint);
        var (mpx, mpy, mpw, mph) = MenuProbeRect(cx, cy);
        System.Drawing.Bitmap? beforeMenu = npcScreenPoint is not null
            ? WtmVision.CaptureRegion(mpx, mpy, mpw, mph) : null;
        await _transport.MoveAbsAsync(cx, cy, ct);
        await Task.Delay(80, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);
        await Task.Delay(150, ct);
        await _transport.ClickAsync(MouseButton.Right, ct);
        await Task.Delay(s.MerchantInteractDelayMs, ct);

        if (beforeMenu is not null && !MenuOpened(mpx, mpy, mpw, mph, beforeMenu))
        {
            Status("⚠ Tamir NPC menüsü açılmadı (tık ıskaladı?) — etkileşim iptal");
            await TapCloseAsync(s, ct);
            return InteractionFailed;
        }

        // 3. "I want to make repairs." → envanter açılır, imleç çekiç olur
        Status("Tamir seçiliyor…");
        await ClickMasterAsync(s.RepairDialogX, s.RepairDialogY, MouseButton.Left, ct);
        await Task.Delay(s.RepairDialogDelayMs, ct);

        // 4. Seçili ekipman slotlarına sırayla birer sol tık (çekiç imleç anında onarır)
        var roi   = ResolutionMapper.Map(s.RepairEquipGridX, s.RepairEquipGridY, s.RepairEquipGridW, s.RepairEquipGridH);
        int cols  = s.RepairEquipCols, rows = s.RepairEquipRows;
        int slotW = roi.Width / cols, slotH = roi.Height / rows;
        int repaired = 0;
        if (slotW > 0 && slotH > 0)
        {
            foreach (int idx in slots)
            {
                ct.ThrowIfCancellationRequested();
                if (idx < 0 || idx >= rows * cols) continue;
                int r  = idx / cols, c = idx % cols;
                int px = roi.X + c * slotW + slotW / 2;
                int py = roi.Y + r * slotH + slotH / 2;
                Status($"Slot #{idx + 1} onarılıyor ({px},{py})…");
                await _transport.MoveAbsAsync(px, py, ct);
                await Task.Delay(60, ct);
                await _transport.ClickAsync(MouseButton.Left, ct);
                await Task.Delay(s.RepairItemDelayMs, ct);
                repaired++;
            }
        }

        // 5. Pencereyi kapat
        Status("Tamir penceresi kapatılıyor…");
        await TapCloseAsync(s, ct);

        Status(repaired > 0 ? $"✓ {repaired} ekipman onarıldı" : "Hiç ekipman onarılmadı");
        return repaired;
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

    // ── NPC tık + menü doğrulama yardımcıları (Faz 41) ───────────────────────────

    /// <summary>Tıklanacak ekran noktasını çözer: tespit varsa kutu-merkezi + ince-ayar offset; yoksa kör merkez+offset.</summary>
    private static (int x, int y) ResolveNpcClick(Models.Autonomous.AutonomousSettings s, System.Drawing.Point? npc)
    {
        if (npc is { } p) return (p.X + s.MerchantClickOffsetX, p.Y + s.MerchantClickOffsetY);
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        return (sw / 2 + s.MerchantClickOffsetX, sh / 2 + s.MerchantClickOffsetY);
    }

    /// <summary>İmleç (tık) çevresinde menü-açıldı kontrolü için ekran-içine kıstırılmış dikdörtgen.</summary>
    private static (int x, int y, int w, int h) MenuProbeRect(int cx, int cy)
    {
        int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
        int w = Math.Min(260, sw), h = Math.Min(240, sh);
        int x = Math.Clamp(cx - 60, 0, Math.Max(0, sw - w));
        int y = Math.Clamp(cy - 30, 0, Math.Max(0, sh - h));
        return (x, y, w, h);
    }

    /// <summary>Sağ-tık menüsü açıldı mı: tık-öncesi <paramref name="before"/> ile tık-sonrası bölgeyi karşılaştırır
    /// (opak menü kutusu belirince piksellerin ≥MenuMinChangedFrac'i değişir). before'ı dispose eder.</summary>
    private static bool MenuOpened(int x, int y, int w, int h, System.Drawing.Bitmap before)
    {
        try
        {
            using var after = WtmVision.CaptureRegion(x, y, w, h);
            return ChangedFraction(before, after) >= MenuMinChangedFrac;
        }
        finally { before.Dispose(); }
    }

    /// <summary>İki eş-boyut kareyi karşılaştırır; kanal-başı farkı MenuPixelDelta'yı aşan piksel oranını döner.</summary>
    private static unsafe double ChangedFraction(System.Drawing.Bitmap a, System.Drawing.Bitmap b)
    {
        int w = Math.Min(a.Width, b.Width), h = Math.Min(a.Height, b.Height);
        if (w <= 0 || h <= 0) return 0;
        var ra = a.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var rb = b.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        long changed = 0, total = (long)w * h;
        try
        {
            byte* baseA = (byte*)ra.Scan0, baseB = (byte*)rb.Scan0;
            for (int yy = 0; yy < h; yy++)
            {
                byte* pa = baseA + (long)yy * ra.Stride;
                byte* pb = baseB + (long)yy * rb.Stride;
                for (int xx = 0; xx < w; xx++)
                {
                    int i = xx * 4; // BGRA
                    if (Math.Abs(pa[i]     - pb[i])     > MenuPixelDelta ||
                        Math.Abs(pa[i + 1] - pb[i + 1]) > MenuPixelDelta ||
                        Math.Abs(pa[i + 2] - pb[i + 2]) > MenuPixelDelta)
                        changed++;
                }
            }
        }
        finally { a.UnlockBits(ra); b.UnlockBits(rb); }
        return (double)changed / total;
    }

    /// <summary>Merchant/tamir penceresini kapatma tuşu (Escape varsayılan) — tek atımlık.</summary>
    private async Task TapCloseAsync(Models.Autonomous.AutonomousSettings s, CancellationToken ct)
    {
        string closeKey = string.IsNullOrWhiteSpace(s.MerchantCloseKey) ? "Escape" : s.MerchantCloseKey;
        await _transport.KeyDownAsync(closeKey, ct);
        await Task.Delay(60, ct);
        await _transport.KeyUpAsync(closeKey, CancellationToken.None);
        await Task.Delay(s.MerchantCloseDelayMs, ct);
    }

    private void Status(string msg) => StatusChanged?.Invoke(this, msg);
}
