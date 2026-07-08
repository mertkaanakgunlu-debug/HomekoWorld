using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Services.Autonomous;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Services.Farm;

/// <summary>
/// Dev 3 — OtoFarm envanter boşaltma (klan bankası). Otonom'un Town/NPC'siz basit hâli.
/// Akış: 'U' menü → 'Clan' sekmesi (template-locate) → hazine sandığı (template-locate, kendi envanter
/// görünümünü de açar) → [depo penceresi doğrula] → doluluk ölç → doluysa depolanabilir eşyalara SAĞ-TIK
/// (tür-başına NCC) → kapat ('U' iki kez — bkz <see cref="CloseAllAsync"/>). 2026-07-04: kullanıcı canlı
/// testte doğruladı — AYRI 'I' envanter-açma/kapatma YOK; sandık kendi görünümünü açıp kapatıyor.
/// Menü ögeleri KÖR TIK DEĞİL, <see cref="TemplateLocator"/> ile bulunur (varlık doğrulama + ince konum).
/// Envanter ROI + satır/sütun, Otonom kalibrasyonundan (AutonomousSettings) veya "Sandık-özel" ROI'den alınır.
/// </summary>
public sealed class ClanBankService
{
    private readonly AppState        _state;
    private readonly TransportRouter _router;

    public event EventHandler<string>? StatusChanged;

    public ClanBankService(AppState state, TransportRouter router)
    {
        _state  = state;
        _router = router;
    }

    /// <summary>OtoFarm envanter boşaltma açık mı (Otonom bunu kullanmaz).</summary>
    public bool Enabled         => _state.ClanBank.Enabled;
    /// <summary>Kaç kill'de bir envanter kontrol edilsin (min 1).</summary>
    public int  CheckEveryKills => Math.Max(1, _state.ClanBank.CheckEveryKills);

    /// <summary>
    /// Envanteri aç → doluluk ölç → doluysa klan bankasına boşalt → kapat. Combat DURAKLATILMIŞ (FarmEngine bank
    /// state'i) iken çağrılır. Kısa özet döndürür (HUD/log). İptal → OperationCanceledException fırlar.
    /// </summary>
    public async Task<string> RunAsync(CancellationToken ct)
    {
        var cb = _state.ClanBank;
        var au = _state.Autonomous;
        LastMovedCount = 0;   // erken dönüşte (kalibrasyon eksik/doluluk düşük) önceki turun değeri kalmasın
        Program.Log("[Bank] RunAsync başladı");

        if (!au.IsInventoryGridCalibrated) return LogResult("envanter ROI kalibre değil (Otonom)");
        if (!cb.IsClanTabCalibrated)       return LogResult("Clan sekmesi kalibre değil");
        if (!cb.IsChestCalibrated)         return LogResult("sandık kalibre değil");
        if (!cb.HasItemTypes)              return LogResult("depolanabilir eşya türü öğretilmedi");

        // 1) Menü aç ('U'). 2026-07-04: kullanıcı canlı testte doğruladı — AYRICA 'I' ile envanter açmaya
        // GEREK YOK; hazine sandığına tıklanınca (adım 3) oyun zaten KENDİ envanter görünümünü açıyor
        // (bu yüzden ChestInvGrid/"Sandık-özel" kalibrasyonu zaten vardı). Eski akış gereksiz yere önce
        // 'I' basıp doluluğu SANDIK AÇILMADAN ölçüyordu.
        Status("Klan menüsü açılıyor…");
        await TapAsync(cb.MenuKey, ct);
        await Task.Delay(cb.OpenDelayMs, ct);

        // 2) 'Clan' sekmesini template ile bul + tıkla
        var clan = LocateElement(cb.ClanTabRoiX, cb.ClanTabRoiY, cb.ClanTabRoiW, cb.ClanTabRoiH,
            cb.ClanTabTemplatesB64, cb.MenuMatchThreshold, out float clanScore);
        if (clan is null)
        {
            await CloseAllAsync(cb, ct);
            return LogResult($"Clan sekmesi bulunamadı (skor {clanScore:0.00} < {cb.MenuMatchThreshold:0.00})");
        }
        Status($"Clan sekmesi bulundu (skor {clanScore:0.00}) → tıklanıyor");
        Program.Log($"[Bank] Clan sekmesi bulundu skor={clanScore:0.00} @({clan.Value.X},{clan.Value.Y})");
        await ClickAtAsync(clan.Value, MouseButton.Left, ct);
        await Task.Delay(cb.OpenDelayMs, ct);

        // 3) Hazine sandığını template ile bul + tıkla — bu, oyunun kendi envanter görünümünü de açar.
        var chest = LocateElement(cb.ChestRoiX, cb.ChestRoiY, cb.ChestRoiW, cb.ChestRoiH,
            cb.ChestTemplatesB64, cb.MenuMatchThreshold, out float chestScore);
        if (chest is null)
        {
            await CloseAllAsync(cb, ct);
            return LogResult($"sandık bulunamadı (skor {chestScore:0.00} < {cb.MenuMatchThreshold:0.00})");
        }
        Status($"Sandık bulundu (skor {chestScore:0.00}) → tıklanıyor");
        Program.Log($"[Bank] Sandık bulundu skor={chestScore:0.00} @({chest.Value.X},{chest.Value.Y})");
        await ClickAtAsync(chest.Value, MouseButton.Left, ct);
        await Task.Delay(cb.OpenDelayMs, ct);

        // 4) (Opsiyonel) depo penceresi açıldı doğrula
        if (cb.HasStorageOpenCheck)
        {
            var so = LocateElement(cb.StorageOpenRoiX, cb.StorageOpenRoiY, cb.StorageOpenRoiW, cb.StorageOpenRoiH,
                cb.StorageOpenTemplatesB64, cb.MenuMatchThreshold, out float soScore);
            if (so is null)
            {
                await CloseAllAsync(cb, ct);
                return LogResult($"depo penceresi doğrulanamadı (skor {soScore:0.00})");
            }
            Program.Log($"[Bank] depo penceresi doğrulandı skor={soScore:0.00}");
        }

        // 5) Doluluk ölç — ARTIK sandık açıkken (adım 3'ün açtığı envanter görünümü hazır; eskiden 'I'
        // ile açılan BAŞKA/erken bir görünüm ölçülüyordu).
        float ratio = ScanOccupancy(cb, au);
        int pct = (int)(ratio * 100);
        var (gridRoi, gridRows, gridCols) = ResolveInventoryGrid(cb, au);
        Program.Log($"[Bank] envanter doluluk=%{pct} (eşik=%{(int)(cb.FullThreshold * 100)}) " +
                    $"ROI=({gridRoi.X},{gridRoi.Y} {gridRoi.Width}×{gridRoi.Height}) {gridCols}×{gridRows} " +
                    $"kaynak={(cb.IsChestInvGridCalibrated ? "Sandık-özel" : "Otonom (fallback)")}");
        if (ratio < cb.FullThreshold)
        {
            await CloseAllAsync(cb, ct);
            return LogResult($"envanter %{pct} < %{(int)(cb.FullThreshold * 100)} — kapatıldı, devam");
        }
        Status($"Envanter dolu (%{pct}) — bankaya boşaltılıyor…");

        // 6) Depolanabilir eşyaları sağ-tık ile depoya taşı (tur döngüsü — eşya kayabilir)
        int moved = await StoreLoopAsync(cb, au, ct);

        // 7) Kapat + devam
        await CloseAllAsync(cb, ct);
        return LogResult(moved > 0 ? $"{moved} eşya depoya taşındı" : "eşleşen depolanabilir eşya bulunamadı");
    }

    private static string LogResult(string msg)
    {
        Program.Log($"[Bank] RunAsync bitti → {msg}");
        return msg;
    }

    // ── Depolama döngüsü ──────────────────────────────────────────────────────────
    // Her tur: dolu + tür-eşleşen slotları tara → hepsine sağ-tık (depoya taşır) → yeniden tara.
    // Eşleşen kalmayınca ya da MaxRounds dolunca biter (Sell akışının kanıtlı deseni).
    private async Task<int> StoreLoopAsync(ClanBankSettings cb, Models.Autonomous.AutonomousSettings au, CancellationToken ct)
    {
        var matchers = BuildItemMatchers(cb);
        Program.Log($"[Bank] StoreLoop başladı: {matchers.Count} eşya-türü matcher hazır (öğretilmiş tür sayısı={cb.ItemTypes.Count})");
        if (matchers.Count == 0) return 0;

        int total = 0;
        int maxRounds = Math.Max(1, cb.MaxRounds);
        for (int round = 1; round <= maxRounds; round++)
        {
            ct.ThrowIfCancellationRequested();
            var storable = ScanStorableCenters(cb, au, matchers, cb.ItemMatchThreshold, round, out int filledSlots);
            Program.Log($"[Bank] Tur {round}: dolu-slot={filledSlots} eşleşen-depolanabilir={storable.Count} " +
                        $"(eşya-eşiği={cb.ItemMatchThreshold:0.00})");

            // Görsel teşhis (2026-07-03): ilk turda dolu slot VAR ama hiçbiri eşleşmediyse — tam canlı testte
            // görülen arıza — sandık hâlâ açıkken ekran durumunun skorlu dökümünü otomatik kaydet.
            if (round == 1 && storable.Count == 0 && filledSlots > 0 && cb.AutoDumpOnMiss)
            {
                try
                {
                    string dir = CreateDumpDir();
                    Program.Log($"[Bank] eşleşen=0 → teşhis dökümü: {SaveScanDebug(dir)} → {dir}");
                }
                catch (Exception ex) { Program.Log($"[Bank] teşhis dökümü hatası: {ex.Message}"); }
            }
            if (storable.Count == 0) break;

            Status($"Tur {round}: {storable.Count} eşya depoya taşınıyor…");
            foreach (var p in storable)
            {
                ct.ThrowIfCancellationRequested();
                await _router.MoveAbsAsync(p.X, p.Y, ct);
                await Task.Delay(60, ct);
                await _router.ClickAsync(MouseButton.Right, ct);   // depo penceresi açıkken sağ-tık = taşı
                await Task.Delay(cb.ItemClickDelayMs, ct);
                total++;
            }
        }
        Program.Log($"[Bank] StoreLoop bitti: {total} eşya taşındı");
        LastMovedCount = total;
        return total;
    }

    /// <summary>Son StoreLoop'ta taşınan eşya sayısı (oturum-özeti telemetrisi için — FarmEngine okur).</summary>
    public int LastMovedCount { get; private set; }

    // ── Etkin envanter ızgarası: ClanBank'ın KENDİ "sandık açıkken" kalibrasyonu varsa onu kullan,
    // yoksa Otonom'un tek-başına 'I' envanter ROI'sine düş (eski davranış — geriye dönük uyumlu). ──
    private static (Rectangle roi, int rows, int cols) ResolveInventoryGrid(
        ClanBankSettings cb, Models.Autonomous.AutonomousSettings au)
    {
        if (cb.IsChestInvGridCalibrated)
            return (ResolutionMapper.Map(cb.ChestInvGridX, cb.ChestInvGridY, cb.ChestInvGridW, cb.ChestInvGridH),
                    cb.ChestInvRows, cb.ChestInvCols);
        return (ResolutionMapper.Map(au.InventoryGridX, au.InventoryGridY, au.InventoryGridW, au.InventoryGridH),
                au.InventoryRows, au.InventoryCols);
    }

    // ── Envanter doluluğu (dolu slot / toplam) ────────────────────────────────────
    private float ScanOccupancy(ClanBankSettings cb, Models.Autonomous.AutonomousSettings au)
    {
        var (roi, rows, cols) = ResolveInventoryGrid(cb, au);
        int total = rows * cols;
        if (total <= 0) return 0f;

        using var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (bmp is null)
        {
            Program.Log($"[Bank] ScanOccupancy: yakalama BAŞARISIZ ROI=({roi.X},{roi.Y} {roi.Width}×{roi.Height})");
            return 0f;
        }
        int slotW = bmp.Width / cols, slotH = bmp.Height / rows;
        if (slotW <= 0 || slotH <= 0) return 0f;

        int filled = 0;
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride;
                byte* scan0 = (byte*)data.Scan0.ToPointer();
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * slotW + 2, y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);
                    if (InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1)) filled++;
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        return (float)filled / total;
    }

    // ── Dolu + tür-eşleşen slotların ekran merkezleri ─────────────────────────────
    private List<Point> ScanStorableCenters(ClanBankSettings cb, Models.Autonomous.AutonomousSettings au,
        List<(string name, IconMatcher m)> matchers, float threshold, int round, out int filledSlots)
    {
        filledSlots = 0;
        var (roi, rows, cols) = ResolveInventoryGrid(cb, au);
        var result = new List<Point>();

        using var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (bmp is null)
        {
            Program.Log($"[Bank] ScanStorableCenters: yakalama BAŞARISIZ ROI=({roi.X},{roi.Y} {roi.Width}×{roi.Height})");
            return result;
        }
        int slotW = bmp.Width / cols, slotH = bmp.Height / rows;
        if (slotW <= 0 || slotH <= 0) return result;

        // 1) dolu hücreleri bul (boş yuvalarda NCC harcanmaz)
        var filled = new List<(int r, int c)>();
        var data = bmp.LockBits(new Rectangle(0, 0, bmp.Width, bmp.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                int stride = data.Stride;
                byte* scan0 = (byte*)data.Scan0.ToPointer();
                for (int r = 0; r < rows; r++)
                for (int c = 0; c < cols; c++)
                {
                    int x0 = c * slotW + 2, y0 = r * slotH + 2;
                    int x1 = Math.Min(x0 + slotW - 4, bmp.Width  - 1);
                    int y1 = Math.Min(y0 + slotH - 4, bmp.Height - 1);
                    if (InventorySlotScanner.IsSlotFilled(scan0, stride, x0, y0, x1, y1)) filled.Add((r, c));
                }
            }
        }
        finally { bmp.UnlockBits(data); }
        filledSlots = filled.Count;

        // 2) dolu yuvaları tür şablonlarıyla eşleştir (herhangi bir tür ≥ eşik → depolanabilir).
        // Slot-başına en-iyi skor LOGLANIR (2026-07-03): "eşleşen=0" arızasında eşik mi / şablon mu / ROI mi
        // sorusu ancak skorlarla cevaplanır (canlı log: dolu-slot=13 eşleşen=0, skorlar görünmüyordu).
        var bests = new List<float>(filled.Count);
        foreach (var (r, c) in filled)
        {
            int x0 = c * slotW + 2, y0 = r * slotH + 2;
            int w = Math.Min(slotW - 4, bmp.Width  - x0);
            int h = Math.Min(slotH - 4, bmp.Height - y0);
            if (w <= 1 || h <= 1) continue;
            var (best, typeName, insetPct) = MatchSlotMultiInset(bmp, x0, y0, w, h, matchers);
            bests.Add(best);
            bool store = best >= threshold;
            Program.Log($"[Bank] slot r{r}c{c}: skor={best:0.00} tür={typeName} inset=%{insetPct}{(store ? " → DEPOLA" : "")}");
            if (store)
                result.Add(new Point(roi.X + c * slotW + slotW / 2, roi.Y + r * slotH + slotH / 2));
        }
        if (bests.Count > 0)
        {
            var sorted = bests.OrderBy(v => v).ToList();
            Program.Log($"[Bank] Tur {round} skor-dağılımı: min={sorted[0]:0.00} " +
                        $"medyan={sorted[sorted.Count / 2]:0.00} max={sorted[^1]:0.00} eşik={threshold:0.00}");
        }
        return result;
    }

    // Merkez-kırpma oranları: %0 (tam hücre), %12, %25. Öğretme kutusu ikonun ETRAFINA DAR çizilirken tarama
    // hücrenin TAMAMINI (ikon + dolgu) görür — ikisi de 24×24'e inince ikon farklı ölçekte kalır. İç kırpımlar
    // dolguyu atarak dar şablonla hizalanır; en iyi skor hangi kırpımdan gelirse o kullanılır.
    private static readonly float[] SlotInsets = { 0f, 0.12f, 0.25f };

    /// <summary>Bir slot hücresini 3 merkez-kırpımla tüm tür-matcher'larına karşı dener; en iyi
    /// (skor, tür-adı, inset-yüzdesi) döner. SaveScanDebug ile GERÇEK taramanın aynı yolu kullanması için ortak.</summary>
    private static (float best, string typeName, int insetPct) MatchSlotMultiInset(
        Bitmap bmp, int x0, int y0, int w, int h, List<(string name, IconMatcher m)> matchers)
    {
        float best = -2f; string typeName = "-"; int insetPct = 0;
        foreach (float inset in SlotInsets)
        {
            int iw = Math.Max(8, (int)(w * (1 - inset)));
            int ih = Math.Max(8, (int)(h * (1 - inset)));
            int ix = x0 + (w - iw) / 2, iy = y0 + (h - ih) / 2;
            if (ix + iw > bmp.Width || iy + ih > bmp.Height) continue;
            using var crop = bmp.Clone(new Rectangle(ix, iy, iw, ih), PixelFormat.Format32bppArgb);
            foreach (var (name, m) in matchers)
            {
                float s = m.Match(crop);
                if (s > best) { best = s; typeName = name; insetPct = (int)(inset * 100); }
            }
        }
        return (best, typeName, insetPct);
    }

    // ── Menü ögesi template-locate (arama-ROI'de en iyi eşleşme → mutlak ekran noktası) ──
    private System.Drawing.Point? LocateElement(int mx, int my, int mw, int mh, string[] templatesB64, float threshold, out float score)
    {
        score = -2f;
        if (mw <= 0 || mh <= 0 || templatesB64 is null || templatesB64.Length == 0) return null;

        var roi = ResolutionMapper.Map(mx, my, mw, mh);
        using var region = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (region is null) return null;

        float sx = mw > 0 ? roi.Width  / (float)mw : 1f;
        float sy = mh > 0 ? roi.Height / (float)mh : 1f;
        var templates = TemplateLocator.BuildTemplates(templatesB64, sx, sy);
        if (templates.Count == 0) return null;

        var hit = TemplateLocator.Locate(region, templates, stride: 2);
        score = hit.Score;
        if (hit.Score < threshold || hit.X < 0) return null;
        return new System.Drawing.Point(roi.X + hit.X, roi.Y + hit.Y);
    }

    private List<(string name, IconMatcher m)> BuildItemMatchers(ClanBankSettings cb)
    {
        var list = new List<(string, IconMatcher)>();
        foreach (var t in cb.ItemTypes)
        {
            if (t.TemplatesB64 is null || t.TemplatesB64.Length == 0) continue;
            var m = new IconMatcher();
            m.LoadFrom(t.TemplatesB64);
            if (m.IsReady) list.Add((string.IsNullOrWhiteSpace(t.Name) ? "adsız" : t.Name, m));
        }
        return list;
    }

    // ── Görsel teşhis (2026-07-03) — MerchantTrader.SaveScanDebug'ın klan-banka eşleniği ────────────

    /// <summary>Teşhis döküm klasörü: Desktop\FujiMacro_Teshis\bank_{zaman}\ oluşturur; kökteki eski
    /// dökümleri en yeni 20'ye budar (sınırsız birikme olmasın). Klasör yolunu döndürür.</summary>
    public static string CreateDumpDir()
    {
        string root = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FujiMacro_Teshis");
        Directory.CreateDirectory(root);
        string dir = Path.Combine(root, $"bank_{DateTime.Now:yyyyMMdd_HHmmss}");
        Directory.CreateDirectory(dir);
        try
        {
            var old = Directory.GetDirectories(root)
                .OrderByDescending(Directory.GetCreationTime).Skip(20);
            foreach (var d in old)
                try { Directory.Delete(d, recursive: true); } catch { /* kilitli klasör atlanır */ }
        }
        catch { /* budama best-effort */ }
        return dir;
    }

    /// <summary>
    /// Teşhis: etkin envanter ROI'sini yakalar, her yuvanın dolu/eşleşme skorunu + en iyi türü işaretleyip
    /// &lt;dir&gt;\clan_bank_debug.png kaydeder; ayrıca her eşya türünün öğretilen şablonlarını
    /// &lt;dir&gt;\tur_{ad}\ altına HAM boyutlarıyla dışa aktarır (öğretme-kırpım boyutu = kanıt).
    /// Sandık/envanter AÇIKKEN çağrılmalı. GERÇEK taramayla aynı yol (multi-inset) kullanılır. Özet döndürür.
    /// </summary>
    public string SaveScanDebug(string dir)
    {
        var cb = _state.ClanBank;
        var au = _state.Autonomous;
        if (!au.IsInventoryGridCalibrated && !cb.IsChestInvGridCalibrated)
            return "⚠ envanter ROI kalibre edilmedi (ne Otonom ne Sandık-özel)";

        var matchers = BuildItemMatchers(cb);
        var (roi, rows, cols) = ResolveInventoryGrid(cb, au);
        int slotW = roi.Width / cols, slotH = roi.Height / rows;
        if (slotW <= 0 || slotH <= 0) return "⚠ ROI çok küçük / geçersiz";

        using var bmp = WtmVision.CaptureRegion(roi.X, roi.Y, roi.Width, roi.Height);
        if (bmp is null) return "⚠ ekran yakalama başarısız";
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
                float score = -1f; string tName = "-";
                if (fill[r, c])
                {
                    filledCount++;
                    int cx0 = x0 + 2, cy0 = y0 + 2;
                    int w = Math.Min(slotW - 4, bmp.Width  - cx0);
                    int h = Math.Min(slotH - 4, bmp.Height - cy0);
                    if (w > 1 && h > 1 && matchers.Count > 0)
                        (score, tName, _) = MatchSlotMultiInset(bmp, cx0, cy0, w, h, matchers);
                }
                bool match = fill[r, c] && score >= cb.ItemMatchThreshold;
                if (match) matchCount++;

                using var pen = new Pen(match ? Color.Lime : (fill[r, c] ? Color.Red : Color.DimGray), match ? 2f : 1f);
                g.DrawRectangle(pen, x0, y0, slotW - 1, slotH - 1);
                if (fill[r, c])
                {
                    string label = tName.Length > 6 ? $"{score:0.00} {tName[..6]}" : $"{score:0.00} {tName}";
                    g.DrawString(label, font, match ? Brushes.Lime : Brushes.Orange, x0 + 2, y0 + 2);
                }
            }
        }

        try { draw.Save(Path.Combine(dir, "clan_bank_debug.png"), ImageFormat.Png); }
        catch (Exception ex) { return $"⚠ Kaydedilemedi: {ex.Message}"; }

        // Tür şablonlarını dışa aktar — HAM b64 boyutlarıyla (öğretme-kırpım boyutu görünür kalsın).
        int tmpl = 0;
        foreach (var (name, m) in matchers)
        {
            string safe = string.Concat(name.Select(ch => char.IsLetterOrDigit(ch) ? ch : '_'));
            string tDir = Path.Combine(dir, $"tur_{safe}");
            try { Directory.CreateDirectory(tDir); tmpl += m.SaveTemplateImages(tDir); } catch { }
        }

        string src = cb.IsChestInvGridCalibrated ? "Sandık-özel" : "Otonom";
        return $"ROI {roi.Width}×{roi.Height} {cols}×{rows} ({src}) | dolu {filledCount}, eşleşen {matchCount} " +
               $"(eşik {cb.ItemMatchThreshold:0.00}, {matchers.Count} tür) → clan_bank_debug.png + {tmpl} şablon";
    }

    // ── Girdi yardımcıları ────────────────────────────────────────────────────────
    private async Task TapAsync(string key, CancellationToken ct)
    {
        await _router.KeyDownAsync(key, ct);
        await Task.Delay(50, ct);
        await _router.KeyUpAsync(key, CancellationToken.None);
    }

    private async Task ClickAtAsync(System.Drawing.Point p, MouseButton btn, CancellationToken ct)
    {
        await _router.MoveAbsAsync(p.X, p.Y, ct);
        await Task.Delay(60, ct);
        await _router.ClickAsync(btn, ct);
    }

    /// <summary>Menüleri kapatır. Kill-switch footgun: sentetik Esc, Farm.KillSwitchKey Esc ise botu durdurur →
    /// VARSAYILAN kapatma toggle tuşuyla (2026-07-04: 'U' İKİ KEZ — kullanıcı canlı testte doğruladı) yapılır.
    /// Hazine sandığı görünümü 'U' menüsü içinde iç-içe (Clan sekmesi → sandık) navigasyon; geri çıkmak da
    /// aynı tuşla iki seviye gerektiriyor. Envanter artık RunAsync'te ayrıca 'I' ile AÇILMADIĞINDAN (sandık
    /// kendi görünümünü açıyor) kapatılacak ayrı bir "I-durumu" da yok — eski U+I kapatması ARTIK YANLIŞ.
    /// CloseKey yalnız "toggle" DEĞİLSE ve Farm.KillSwitchKey ile aynı değilse tuş olarak (iki kez) kullanılır.</summary>
    private async Task CloseAllAsync(ClanBankSettings cb, CancellationToken ct)
    {
        Status("Menüler kapatılıyor…");
        bool useKey = !string.IsNullOrWhiteSpace(cb.CloseKey)
                      && !string.Equals(cb.CloseKey, "toggle", StringComparison.OrdinalIgnoreCase)
                      && !string.Equals(cb.CloseKey, _state.Farm.KillSwitchKey, StringComparison.OrdinalIgnoreCase);
        if (useKey)
        {
            await TapAsync(cb.CloseKey, ct); await Task.Delay(cb.ClickDelayMs, ct);
            await TapAsync(cb.CloseKey, ct); await Task.Delay(cb.ClickDelayMs, ct);
        }
        else
        {
            await TapAsync(cb.MenuKey, ct); await Task.Delay(cb.ClickDelayMs, ct);  // sandık sekmesinden geri
            await TapAsync(cb.MenuKey, ct); await Task.Delay(cb.ClickDelayMs, ct);  // ana menüyü kapat
        }
    }

    private void Status(string msg) => StatusChanged?.Invoke(this, msg);
}
