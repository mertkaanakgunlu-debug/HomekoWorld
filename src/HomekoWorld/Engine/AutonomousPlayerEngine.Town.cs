using System.Runtime.InteropServices;
using HomekoWorld.Hardware;
using HomekoWorld.Models.Autonomous;
using HomekoWorld.Services.Autonomous;
using HomekoWorld.Services.Capture;

namespace HomekoWorld.Engine;

/// <summary>
/// Faz 35/40/41 — Town TP, satış, tamir, portal etkileşimi.
/// GoingToTownAsync: NPC/portal modelini ön-yükle → Town TP → yükleme bekle → (koordinatla TP doğrula) → NavToMerchant.
/// SellingTickAsync: NPC'yi YOLO ile bul → MerchantTrader.TradeAsync (kutu-merkezine tık + menü doğrula) →
///   (RepairEnabled ? Repairing : NavToPortal). Menü doğrulanamazsa town turu tekrar.
/// RepairingTickAsync: NPC'yi bul → MerchantTrader.RepairAsync → NavToPortal.
/// UsingPortalAsync: portal'ı YOLO ile bul → kutu-merkezine tık → 'manuel slot' → (koordinat değişimiyle ışınlanmayı doğrula) → NavToFarmSpot.
/// Doğrulama başarısızsa <see cref="TownTourFailedAsync"/>: turu birkaç kez tekrar; tükenince güvenle dur (kör ilerleme YOK).
/// </summary>
public sealed partial class AutonomousPlayerEngine
{
    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    // Faz 41 sabitleri
    private const int NpcInteractRetries    = 2;  // NPC menüsü açılmazsa kaç kez yeniden tespit+tık (sonra tur-fail)
    private const int PortalInteractRetries = 2;  // portal ışınlanması doğrulanmazsa kaç kez yeniden tespit+tık
    private const int MapJumpMinCoords      = 40; // harita değişimi (town↔farm) = koordinatta en az bu kadar sıçrama

    // ── Town TP ───────────────────────────────────────────────────────────────────

    private async Task GoingToTownAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;

        // NPC/portal modelini önceden yükle (farm durdu → GPU boş; ilk-yükleme/ısınma maliyeti merchant'a
        // varmadan, town yükleme beklemesi sırasında ödenir → merchant'ta tespit hazır olur).
        _townDetector.Preload();

        if (!await PressTownTpAsync(ct))
        {
            Log("⚠ Town butonu/tuşu ayarlanmamış", "event");
            SetState(AutoPlayerState.Farming, "Town TP eksik — farm'a dönüldü");
            _farm.Start();
            return;
        }

        StatusChanged?.Invoke(this, $"Town yükleniyor ({s.TownTpWaitMs / 1000} sn)…");
        await Task.Delay(s.TownTpWaitMs, ct);

        // Not: Town TP koordinat-doğrulaması bilinçli olarak YOK — tur-tekrar GoingToTown'a dönerken zaten
        // town'da olabiliyoruz (re-TP büyük koord sıçraması üretmez → yanlış "doğrulanamadı"). Town TP başarısız
        // olursa NavToMerchant koordinata ulaşamayıp Recovering'e düşer (mevcut dayanıklılık). Kritik doğrulama
        // PORTAL'da (town→farm koord sıçraması) — "town'dayken farm'a yürüme" hatasını orası keser.
        Log("Town'a gelindi — merchant'a yürünüyor", "event");
        SetState(AutoPlayerState.NavToMerchant, "Merchant'a yürünüyor…");
    }

    /// <summary>
    /// Town TP aksiyonu: UI butonu kalibre ise ona sol-tık, değilse <see cref="AutonomousSettings.TownTpKey"/>.
    /// Hiçbiri ayarlı değilse false (çağıran karar verir). GoingToTown + bağımsız test bunu paylaşır.
    /// </summary>
    private async Task<bool> PressTownTpAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        // Bu oyunda Town TP = sabit UI butonu → kalibre edilmişse ona sol-tık; değilse kısayol fallback.
        if (s.IsTownTpButtonCalibrated)
        {
            var p = ResolutionMapper.Map(s.TownTpButtonX, s.TownTpButtonY, 1, 1);
            Log($"Town butonu tıklanıyor ({p.X},{p.Y})", "event");
            await _transport.MoveAbsAsync(p.X, p.Y, ct);
            await Task.Delay(120, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
            return true;
        }
        if (!string.IsNullOrWhiteSpace(s.TownTpKey))
        {
            Log($"Town TP tuşu: '{s.TownTpKey}' basılıyor (buton kalibre değil)", "event");
            await _transport.KeyDownAsync(s.TownTpKey, ct);
            await Task.Delay(80, ct);
            await _transport.KeyUpAsync(s.TownTpKey, CancellationToken.None);
            return true;
        }
        return false;
    }

    /// <summary>Test: Otonom modu/loop BAŞLATMADAN yalnız Town TP aksiyonunu yapar (kalibrasyon doğrulama).</summary>
    public async Task TestTownTpAsync(CancellationToken ct)
    {
        if (!await PressTownTpAsync(ct))
            throw new InvalidOperationException("Town butonu/tuşu ayarlanmamış");
    }

    // ── Satış (Faz 36: MerchantTrader + Faz 41: YOLO NPC tespiti) ────────────────

    private async Task SellingTickAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        _merchantTrader.StatusChanged += OnMerchantStatus;
        int outcome;
        try
        {
            outcome = await InteractMerchantAsync(ct, isRepair: false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Satış hatası: {ex.Message}", "event");
            outcome = Services.Autonomous.MerchantTrader.InteractionFailed;
        }
        finally { _merchantTrader.StatusChanged -= OnMerchantStatus; }

        if (outcome == Services.Autonomous.MerchantTrader.InteractionFailed)
        {
            await TownTourFailedAsync("Merchant NPC etkileşimi doğrulanamadı", ct);
            return;
        }

        if (outcome > 0) Telemetry.ItemsSold += outcome;
        Log($"Satış tamamlandı ({outcome} yuva satıldı, toplam: {Telemetry.ItemsSold}) — sonraki adım", "event");

        if (s.RepairEnabled)
            SetState(AutoPlayerState.Repairing, "Eşyalar tamir ediliyor…");
        else
            SetState(AutoPlayerState.NavToPortal, "Portala yürünüyor…");
    }

    private void OnMerchantStatus(object? sender, string msg)
    {
        StatusChanged?.Invoke(this, msg);
        Log(msg, "event");
    }

    // ── Tamir (Faz 40: MerchantTrader.RepairAsync) ───────────────────────────────

    private async Task RepairingTickAsync(CancellationToken ct)
    {
        _merchantTrader.StatusChanged += OnMerchantStatus;
        int outcome;
        try
        {
            outcome = await InteractMerchantAsync(ct, isRepair: true);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            Log($"Tamir hatası: {ex.Message}", "event");
            outcome = Services.Autonomous.MerchantTrader.InteractionFailed;
        }
        finally { _merchantTrader.StatusChanged -= OnMerchantStatus; }

        if (outcome == Services.Autonomous.MerchantTrader.InteractionFailed)
        {
            await TownTourFailedAsync("Tamir NPC etkileşimi doğrulanamadı", ct);
            return;
        }

        if (outcome > 0) Telemetry.RepairsDone++;
        Log(outcome > 0
            ? $"Tamir tamamlandı ({outcome} ekipman, tur: {Telemetry.RepairsDone}) — portala yürünüyor"
            : "Tamir atlandı (kalibrasyon/slot eksik) — portala yürünüyor", "event");

        SetState(AutoPlayerState.NavToPortal, "Portala yürünüyor…");
    }

    /// <summary>
    /// NPC'yi YOLO ile bulur ve etkileşime girer (sat veya tamir). Kutu-merkezine tıklanır; MerchantTrader
    /// menü-açıldı doğrulaması yapar. Menü açılmazsa yeniden tespit+tık (en çok <see cref="NpcInteractRetries"/>).
    /// Dönüş: ≥0 = etkileşim OK (satılan/onarılan yuva); <see cref="MerchantTrader.InteractionFailed"/> = başarısız.
    /// Model hazır değilse tek sefer eski kör merkez+offset tık (npcPoint=null ⇒ doğrulama atlanır, geri uyumlu).
    /// </summary>
    private async Task<int> InteractMerchantAsync(CancellationToken ct, bool isRepair)
    {
        var s       = _state.Autonomous;
        int retries = Math.Max(1, NpcInteractRetries);
        string what = isRepair ? "Tamir" : "Satış";

        for (int attempt = 1; attempt <= retries; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var npc = await _townDetector.DetectAsync(TownObjectDetector.NpcClass, s.TownDetectTimeoutMs, ct);

            // Model hazır AMA NPC bulunamadı → yeniden tara (kamera/konum bir sonraki denemede farklı olabilir).
            if (npc is null && _townDetector.IsReady)
            {
                Log($"{what}: NPC bulunamadı (deneme {attempt}/{retries})", "event");
                continue;
            }

            // npc!=null → tespit edilen merkez; npc==null && model hazır değil → kör fallback (doğrulama yok).
            System.Drawing.Point? pt = npc is not null
                ? System.Drawing.Point.Round(npc.Center)
                : (System.Drawing.Point?)null;
            if (pt is not null)
                Log($"{what} NPC @ {pt.Value.X},{pt.Value.Y} (deneme {attempt})", "event");

            int outcome = isRepair
                ? await _merchantTrader.RepairAsync(ct, pt)
                : await _merchantTrader.TradeAsync(ct, npcScreenPoint: pt);

            if (outcome == Services.Autonomous.MerchantTrader.InteractionFailed)
            {
                Log($"{what}: NPC menüsü açılmadı (deneme {attempt}/{retries})", "event");
                continue;
            }
            return outcome;   // ≥0 → etkileşim OK (0 = açıldı ama satılacak/onarılacak yok)
        }
        return Services.Autonomous.MerchantTrader.InteractionFailed;
    }

    // ── Portal etkileşimi (Faz 41: YOLO portal tespiti + koordinat doğrulama) ─────

    private async Task UsingPortalAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        var coordBefore = await ReadCoordStableAsync(ct);   // town konumu (okuyucu hazırsa)
        int retries     = Math.Max(1, PortalInteractRetries);
        bool teleported = false;

        for (int attempt = 1; attempt <= retries && !teleported; attempt++)
        {
            ct.ThrowIfCancellationRequested();

            var portal = await _townDetector.DetectAsync(TownObjectDetector.PortalClass, s.TownDetectTimeoutMs, ct);
            System.Drawing.Point click;
            if (portal is not null)
            {
                click = System.Drawing.Point.Round(portal.Center);
            }
            else
            {
                int sw = GetSystemMetrics(0), sh = GetSystemMetrics(1);
                click = new System.Drawing.Point(sw / 2 + s.PortalClickOffsetX, sh / 2 + s.PortalClickOffsetY);
                Log($"Portal tespit edilemedi — kör offset deneniyor ({click.X},{click.Y}, deneme {attempt})", "event");
            }

            await DoPortalInteractAtAsync(s, click, ct);
            StatusChanged?.Invoke(this, $"Portal yükleniyor ({s.PortalWaitMs / 1000} sn)…");
            await Task.Delay(s.PortalWaitMs, ct);

            // Işınlanma doğrula: konum town'dan belirgin değişti mi? (Doğrulayamıyorsak takılmamak için ışınlandı say.)
            if (coordBefore is null) { teleported = true; break; }     // okuyucu hazır değil → doğrulanamaz
            var coordAfter = await ReadCoordStableAsync(ct);
            if (coordAfter is null || CoordDist(coordBefore.Value, coordAfter.Value) >= MapJumpMinCoords)
            {
                teleported = true;   // konum değişti (town→farm) VEYA okunamıyor (yeni harita yükleniyor) → ışınlandı
                break;
            }
            Log($"Portal ışınlanması doğrulanamadı — konum hâlâ town ({coordAfter.Value.x},{coordAfter.Value.y}, deneme {attempt}/{retries})", "event");
        }

        if (!teleported)
        {
            await TownTourFailedAsync("Portal ışınlanması doğrulanamadı", ct);
            return;
        }

        Log("Farm alanına gelindi — farm noktasına yürünüyor", "event");
        SetState(AutoPlayerState.NavToFarmSpot, "Farm noktasına yürünüyor…");
    }

    /// <summary>Eski kör tık (test/fallback): ekran merkezi + offset üzerinden portal etkileşimi.</summary>
    private Task DoPortalInteractAsync(AutonomousSettings s, CancellationToken ct)
    {
        int cx = GetSystemMetrics(0) / 2 + s.PortalClickOffsetX;
        int cy = GetSystemMetrics(1) / 2 + s.PortalClickOffsetY;
        return DoPortalInteractAtAsync(s, new System.Drawing.Point(cx, cy), ct);
    }

    /// <summary>
    /// Portal oyun mekaniği: verilen ekran-noktasına sol-tık (seç) → sağ-tık (menü) → kalibre "manuel slot"
    /// öğesine sol-tık → anında ışınlanır. "manuel slot" kalibre değilse onay-tuşu (Enter / PortalConfirmKey) fallback.
    /// </summary>
    private async Task DoPortalInteractAtAsync(AutonomousSettings s, System.Drawing.Point click, CancellationToken ct)
    {
        Log($"Portala tıklanıyor ({click.X},{click.Y})", "event");
        await _transport.MoveAbsAsync(click.X, click.Y, ct);
        await Task.Delay(120, ct);
        await _transport.ClickAsync(MouseButton.Left, ct);
        await Task.Delay(150, ct);
        await _transport.ClickAsync(MouseButton.Right, ct);
        await Task.Delay(s.PortalInteractDelayMs, ct); // menü belirmesi için bekle

        if (s.IsPortalMenuSlotCalibrated)
        {
            var p = ResolutionMapper.Map(s.PortalMenuSlotX, s.PortalMenuSlotY, 1, 1);
            Log($"'Manuel slot' seçiliyor ({p.X},{p.Y})", "event");
            await _transport.MoveAbsAsync(p.X, p.Y, ct);
            await Task.Delay(120, ct);
            await _transport.ClickAsync(MouseButton.Left, ct);
        }
        else
        {
            string confirmKey = string.IsNullOrWhiteSpace(s.PortalConfirmKey) ? "Return" : s.PortalConfirmKey;
            Log($"⚠ 'Manuel slot' kalibre değil — onay tuşu '{confirmKey}' (eski davranış)", "event");
            await _transport.KeyDownAsync(confirmKey, ct);
            await Task.Delay(80, ct);
            await _transport.KeyUpAsync(confirmKey, CancellationToken.None);
        }
    }

    /// <summary>Test: Otonom akış/loop BAŞLATMADAN yalnız portal etkileşimini yapar (kalibrasyon doğrulama).</summary>
    public Task TestUsePortalAsync(CancellationToken ct) => DoPortalInteractAsync(_state.Autonomous, ct);

    // ── Tur tekrar / güvenli durdurma + koordinat yardımcıları (Faz 41) ──────────

    /// <summary>NPC menüsü veya portal ışınlanması doğrulanamadığında: turu birkaç kez baştan dene (GoingToTown);
    /// <see cref="AutonomousSettings.TownTourMaxAttempts"/> tükeninince otonom modu güvenle durdur (kör ilerleme YOK).</summary>
    private async Task TownTourFailedAsync(string reason, CancellationToken ct)
    {
        _townTourAttempt++;
        int max = Math.Max(1, _state.Autonomous.TownTourMaxAttempts);
        if (_townTourAttempt < max)
        {
            Log($"⚠ {reason} — town turu yeniden deneniyor ({_townTourAttempt}/{max})", "event");
            StatusChanged?.Invoke(this, $"Tur tekrar ({_townTourAttempt}/{max}): {reason}");
            await Task.Delay(800, ct);
            SetState(AutoPlayerState.GoingToTown, "Town turu yeniden başlıyor…");
        }
        else
        {
            Telemetry.Failures++;
            Log($"⛔ {reason} — {max} denemede başarısız; otonom mod güvenle durduruldu", "event");
            // SetState(Idle) → LoopAsync'in terminal-guard'ı döngüden çıkar (farm zaten durmuş).
            SetState(AutoPlayerState.Idle, $"Tur başarısız: {reason} — durduruldu");
        }
    }

    /// <summary>Minimap koordinatını kararlı okur: iki tutarlı okuma (≤3 fark) elde edilince döner; aksi halde
    /// en iyi çaba (tek okuma) veya null (okuyucu hazır değil / hiç okunamadı). TP/portal doğrulamasında kullanılır.</summary>
    private async Task<(int x, int y)?> ReadCoordStableAsync(CancellationToken ct)
    {
        if (!_coordReader.IsReady) return null;
        (int x, int y)? last = null;
        for (int i = 0; i < 5; i++)
        {
            ct.ThrowIfCancellationRequested();
            var r = _coordReader.Read();
            if (r is not null)
            {
                if (last is not null && CoordDist(last.Value, r.Value) <= 3) return r;
                last = r;
            }
            await Task.Delay(150, ct);
        }
        return last;
    }

    private static double CoordDist((int x, int y) a, (int x, int y) b)
    {
        double dx = a.x - b.x, dy = a.y - b.y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}
