using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Autonomous;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Faz 34 — Probe-correct koordinat navigasyonu.
/// Algoritma (her adım):
///   1. Konum oku. Tolerans içindeyse → bitti.
///   2. Kısa W probu → koord delta → mevcut heading vektörü.
///   3. Cross/dot → hedefe dönüş açısı → right-drag ile kamerayı çevir.
///   4. Hıza orantılı W → hedefe doğru yürü.
///   5. N adım ilerleme yoksa kurtarma (90° dön + walk).
/// Koordinat sistemi bağımsız: negatif-Y veya pozitif-Y ikisi de çalışır;
/// kamera yönü NavCameraInvert ile kullanıcı tarafından ayarlanır.
/// </summary>
public sealed class WorldNavigator
{
    private readonly CoordinateReader _coordReader;
    private readonly TransportRouter  _router;
    private readonly AppState         _state;

    public event EventHandler<string>? StatusChanged;

    public WorldNavigator(CoordinateReader coordReader, TransportRouter router, AppState state)
    {
        _coordReader = coordReader;
        _router      = router;
        _state       = state;
    }

    /// <summary>Koordinat okuyucu + ROI/glyph kalibrasyonu hazır mı.</summary>
    public bool IsReady => _coordReader.IsReady;

    // ── Ana navigasyon ────────────────────────────────────────────────────────────

    /// <summary>
    /// Oyun koordinatına (targetX, targetY) probe-correct yürüyüşle gider.
    /// İptal veya adım limiti aşılırsa exception atar; varılınca normal döner.
    /// </summary>
    public async Task NavigateToAsync(int targetX, int targetY, CancellationToken ct)
    {
        var s          = _state.Autonomous;
        int totalSteps = 0;
        int stuckCount = 0;

        while (!ct.IsCancellationRequested)
        {
            if (++totalSteps > s.NavMaxSteps)
                throw new TimeoutException($"Navigasyon adım limiti ({s.NavMaxSteps}) aşıldı");

            // ── 1. Mevcut konum ──────────────────────────────────────────────
            var pos = await ReadReliableAsync(s, ct);
            if (pos is null) { await Task.Delay(200, ct); continue; }

            float dx   = targetX - pos.Value.x;
            float dy   = targetY - pos.Value.y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            StatusChanged?.Invoke(this, $"({pos.Value.x},{pos.Value.y}) → ({targetX},{targetY}) · {dist:0} birim");

            if (dist <= s.NavToleranceCoords) return; // ✓ varıldı

            // ── 2. Probe: kısa W → heading vektörü ──────────────────────────
            await _router.KeyDownAsync("W", ct);
            await Task.Delay(s.NavProbeMs, ct);
            await _router.KeyUpAsync("W", CancellationToken.None);
            await Task.Delay(60, ct); // settle

            var probePos = await ReadReliableAsync(s, ct);
            if (probePos is null) continue;

            float pdx  = probePos.Value.x - pos.Value.x;
            float pdy  = probePos.Value.y - pos.Value.y;
            float plen = MathF.Sqrt(pdx * pdx + pdy * pdy);

            if (plen < 1f)
            {
                if (++stuckCount >= s.NavStuckThreshold)
                {
                    StatusChanged?.Invoke(this, $"Takıldı ({stuckCount}) — kurtarma");
                    await RecoverAsync(s, ct);
                    stuckCount = 0;
                }
                else
                    StatusChanged?.Invoke(this, $"Probe hareket yok ({stuckCount}/{s.NavStuckThreshold})");
                continue;
            }

            stuckCount = 0;

            // ── 3. Açı farkı: mevcut heading → hedef yönü ───────────────────
            // Standard 2D: cross(p,t) > 0 → CCW dön; atan2 + formül aşağıda.
            float pnx = pdx / plen, pny = pdy / plen;  // normalize mevcut heading
            float tnx = dx   / dist, tny = dy   / dist; // normalize hedef yön
            float cross    = pnx * tny - pny * tnx;     // sin(θ)
            float dot      = pnx * tnx + pny * tny;     // cos(θ)
            float angleDeg = MathF.Atan2(cross, dot) * (180f / MathF.PI);

            // ── 4. A/D tuşlarıyla karakteri döndür ──────────────────────────
            // angleDeg > 0 → CCW → sola dön → NavTurnKeyLeft (A)
            // angleDeg < 0 → CW  → sağa dön → NavTurnKeyRight (D)
            if (MathF.Abs(angleDeg) > 3f)
            {
                string turnKey = angleDeg > 0 ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                int    turnMs  = Math.Clamp((int)(MathF.Abs(angleDeg) * s.NavTurnMsPerDeg), 50, 3000);
                await _router.KeyDownAsync(turnKey, ct);
                await Task.Delay(turnMs, ct);
                await _router.KeyUpAsync(turnKey, CancellationToken.None);
                await Task.Delay(60, ct);
            }

            // ── 5. Hedefi geçmeyecek kadar yürü ─────────────────────────────
            // Hız tahmini: plen birim / probeMs ms → kalan mesafe için ms.
            // %85 overshoot marjı + NavStepMs üst limiti.
            float speedPms = plen / s.NavProbeMs;
            int   walkMs   = speedPms > 0f
                ? Math.Clamp((int)(dist / speedPms * 0.85f), 150, s.NavStepMs)
                : s.NavStepMs;

            await _router.KeyDownAsync("W", ct);
            await Task.Delay(walkMs, ct);
            await _router.KeyUpAsync("W", CancellationToken.None);

            ct.ThrowIfCancellationRequested();
        }
    }

    // ── Kamera piksel/derece kalibrasyonu ─────────────────────────────────────

    /// <summary>
    /// Otomatik kamera kalibrasyonu: testPixels sağa döndürür → probe → açı değişimini ölçer
    /// → pixelsPerDegree hesaplar. Mevcut ayarı GÜNCELLEMEz — çağıran kaydetmeli.
    /// </summary>
    public async Task<float> CalibratePixelsPerDegAsync(CancellationToken ct)
    {
        var s            = _state.Autonomous;
        const int testPx = 300; // 300 piksel → ölçülecek açı

        // İlk heading
        var h0 = await ProbeHeadingAsync(s, ct)
            ?? throw new InvalidOperationException("İlk probe başarısız — koordinat okunamadı");

        // testPx sağa döndür
        await _router.MouseDownAsync(MouseButton.Right, ct);
        await _router.MoveRelAsync(testPx, 0, ct);
        await _router.MouseUpAsync(MouseButton.Right, CancellationToken.None);
        await Task.Delay(120, ct);

        // İkinci heading
        var h1 = await ProbeHeadingAsync(s, ct)
            ?? throw new InvalidOperationException("İkinci probe başarısız — koordinat okunamadı");

        float cross     = h0.x * h1.y - h0.y * h1.x;
        float dot       = h0.x * h1.x + h0.y * h1.y;
        float measuredDeg = MathF.Abs(MathF.Atan2(cross, dot) * (180f / MathF.PI));

        if (measuredDeg < 2f)
            throw new InvalidOperationException(
                $"Kamera dönüşü tespit edilemedi (açı {measuredDeg:0.0}°) — daha geniş bir alanda deneyin");

        // Geri al
        await _router.MouseDownAsync(MouseButton.Right, ct);
        await _router.MoveRelAsync(-testPx, 0, ct);
        await _router.MouseUpAsync(MouseButton.Right, CancellationToken.None);

        return testPx / measuredDeg;
    }

    // ── A/D dönüş hızı kalibrasyonu ──────────────────────────────────────────────

    /// <summary>
    /// D tuşunu testMs süre basar → W probuyla açı değişimini ölçer → ms/derece döner.
    /// Mevcut ayarı GÜNCELLEMEz — çağıran kaydetmeli.
    /// </summary>
    public async Task<float> CalibrateTurnRateAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        const int testMs = 2000;

        var h0 = await ProbeHeadingAsync(s, ct)
            ?? throw new InvalidOperationException("İlk probe başarısız — koordinat okunamadı");

        await _router.KeyDownAsync(s.NavTurnKeyRight, ct);
        await Task.Delay(testMs, ct);
        await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
        await Task.Delay(150, ct);

        var h1 = await ProbeHeadingAsync(s, ct)
            ?? throw new InvalidOperationException("İkinci probe başarısız — koordinat okunamadı");

        float cross      = h0.x * h1.y - h0.y * h1.x;
        float dot        = h0.x * h1.x + h0.y * h1.y;
        float measuredDeg = MathF.Abs(MathF.Atan2(cross, dot) * (180f / MathF.PI));

        if (measuredDeg < 5f)
            throw new InvalidOperationException(
                $"Dönüş tespit edilemedi ({measuredDeg:0.0}°) — D tuşunun karakteri döndürdüğünü doğrulayın");

        // Geri al
        await _router.KeyDownAsync(s.NavTurnKeyLeft, ct);
        await Task.Delay(testMs, ct);
        await _router.KeyUpAsync(s.NavTurnKeyLeft, CancellationToken.None);

        return testMs / measuredDeg;  // ms/derece
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────────

    /// <summary>W probu yaparak normalize heading vektörü döner; hareket yoksa null.</summary>
    private async Task<(float x, float y)?> ProbeHeadingAsync(AutonomousSettings s, CancellationToken ct)
    {
        var pos0 = await ReadReliableAsync(s, ct);
        if (pos0 is null) return null;

        await _router.KeyDownAsync("W", ct);
        await Task.Delay(s.NavProbeMs, ct);
        await _router.KeyUpAsync("W", CancellationToken.None);
        await Task.Delay(60, ct);

        var pos1 = await ReadReliableAsync(s, ct);
        if (pos1 is null) return null;

        float dx = pos1.Value.x - pos0.Value.x;
        float dy = pos1.Value.y - pos0.Value.y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return len < 1f ? null : (dx / len, dy / len);
    }

    /// <summary>90° D tuşuyla sağa dön + kısa walk — engel veya takılma sonrası.</summary>
    private async Task RecoverAsync(AutonomousSettings s, CancellationToken ct)
    {
        int turnMs = Math.Clamp((int)(90f * s.NavTurnMsPerDeg), 300, 3000);
        await _router.KeyDownAsync(s.NavTurnKeyRight, ct);
        await Task.Delay(turnMs, ct);
        await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
        await Task.Delay(80, ct);
        await _router.KeyDownAsync("W", ct);
        await Task.Delay(700, ct);
        await _router.KeyUpAsync("W", CancellationToken.None);
        await Task.Delay(60, ct);
    }

    /// <summary>Koordinatı NavReadRetries × NavReadRetryMs süre dener; hepsinde null ise null döner.</summary>
    private async Task<(int x, int y)?> ReadReliableAsync(AutonomousSettings s, CancellationToken ct)
    {
        for (int i = 0; i < s.NavReadRetries; i++)
        {
            var r = _coordReader.Read();
            if (r is not null) return r;
            if (i < s.NavReadRetries - 1)
            {
                try { await Task.Delay(s.NavReadRetryMs, ct); }
                catch (OperationCanceledException) { return null; }
            }
        }
        return null;
    }
}
