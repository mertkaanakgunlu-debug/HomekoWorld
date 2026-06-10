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
    /// Oyun koordinatına gider. NavContinuous açıksa AKICI mod (W sürekli basılı + yürürken A/D düzelt),
    /// kapalıysa probe-correct (dur-kalk). İptal/adım limiti → exception; varılınca döner.
    /// </summary>
    public async Task NavigateToAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
    {
        if (_state.Autonomous.NavContinuous)
            await NavigateContinuousAsync(targetX, targetY, ct, debugLog);
        else
            await NavigateProbeCorrectAsync(targetX, targetY, ct, debugLog);
    }

    // Akıcı mod yönlendirici: W'yi bırakma (saf akıcı) vs hibrit (yürü-dur-düzelt).
    private async Task NavigateContinuousAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
    {
        if (_state.Autonomous.NavContinuousHold)
            await NavigateHoldAsync(targetX, targetY, ct, debugLog);
        else
            await NavigateHybridAsync(targetX, targetY, ct, debugLog);
    }

    // ── Saf akıcı: W SÜREKLİ basılı, yürürken oku + A/D ile YÜRÜRKEN düzelt ──────────
    // Koordinat okuması güvenilirse (3↔8 vb. çözülünce) en akıcı. A/D'nin W basılıyken
    // döndürmesi şart; döndürmüyorsa hibride dön (toggle kapat).
    private async Task NavigateHoldAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
    {
        var   s      = _state.Autonomous;
        int   tol    = Math.Max(s.NavToleranceCoords, 2);
        int   readMs = Math.Max(80, s.NavContinuousReadMs);
        float gain   = Math.Clamp(s.NavContinuousSteerGain, 0.1f, 1.5f);
        int   totalSteps = 0, stuck = 0;

        if (debugLog)
        {
            try
            {
                System.IO.File.WriteAllText(NavLogPath,
                    $"# Nav SAF AKICI(W basılı) hedef({targetX},{targetY}) {DateTime.Now:HH:mm:ss}\r\n" +
                    $"# msPerDeg={s.NavTurnMsPerDeg:0.0} gain={gain:0.0} readMs={readMs} invert={s.NavTurnInvert} tol={tol}\r\n");
            }
            catch { }
        }

        var lastPos = await ReadReliableAsync(s, ct, null);
        if (lastPos is null) { StatusChanged?.Invoke(this, "Koordinat okunamadı — başlanamadı"); return; }
        (int x, int y)? lastGood = lastPos;

        await _router.KeyDownAsync("W", ct);   // W SÜREKLİ basılı
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (++totalSteps > s.NavMaxSteps)
                    throw new TimeoutException($"Navigasyon adım limiti ({s.NavMaxSteps}) aşıldı");

                await Task.Delay(readMs, ct);   // yürürken bir miktar ilerle (W basılı)

                var pos = await ReadReliableAsync(s, ct, lastGood);
                if (pos is null) continue;
                lastGood = pos;

                float dx = targetX - pos.Value.x, dy = targetY - pos.Value.y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                StatusChanged?.Invoke(this, $"({pos.Value.x},{pos.Value.y}) → ({targetX},{targetY}) · {dist:0} birim");
                if (dist <= tol) break;   // ✓ varıldı

                float mvx = pos.Value.x - lastPos.Value.x, mvy = pos.Value.y - lastPos.Value.y;
                float mlen = MathF.Sqrt(mvx * mvx + mvy * mvy);
                string turnInfo = "düz";

                if (mlen < 3f)
                {
                    if (++stuck >= s.NavStuckThreshold)
                    {
                        turnInfo = "kurtarma";
                        await _router.KeyDownAsync(s.NavTurnKeyRight, ct);
                        await Task.Delay(Math.Clamp((int)(90f * s.NavTurnMsPerDeg), 300, 2000), ct);
                        await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
                        stuck = 0;
                    }
                }
                else
                {
                    stuck = 0;
                    float hx = mvx / mlen, hy = mvy / mlen;
                    float tnx = dx / dist, tny = dy / dist;
                    float cross = hx * tny - hy * tnx;
                    float dot   = hx * tnx + hy * tny;
                    float angle = MathF.Atan2(cross, dot) * (180f / MathF.PI);

                    if (MathF.Abs(angle) > 4f)
                    {
                        bool   left    = (angle > 0) != s.NavTurnInvert;
                        string turnKey = left ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                        int    turnMs  = Math.Clamp((int)(MathF.Abs(angle) * s.NavTurnMsPerDeg * gain), 25, 500);
                        turnInfo = $"{turnKey}×{turnMs}ms (açı {angle:0}°)";
                        await _router.KeyDownAsync(turnKey, ct);   // W BASILI iken → kıvrılır
                        await Task.Delay(turnMs, ct);
                        await _router.KeyUpAsync(turnKey, CancellationToken.None);
                    }
                }

                if (debugLog)
                    AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} hareket={mlen:0} | {turnInfo}");
                lastPos = pos;
            }
        }
        finally
        {
            await _router.KeyUpAsync("W", CancellationToken.None);
        }
    }

    // ── Hibrit: bir segment YÜRÜ → kısa DUR → oku + dönüş → tekrar yürü (güvenilir) ──
    private async Task NavigateHybridAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
    {
        var   s      = _state.Autonomous;
        int   tol    = Math.Max(s.NavToleranceCoords, 2);
        int   walkMs = Math.Max(200, s.NavContinuousReadMs);   // yürüme segmenti (= heading probu)
        float gain   = Math.Clamp(s.NavContinuousSteerGain, 0.1f, 1.5f);
        int   totalSteps = 0, stuck = 0;

        if (debugLog)
        {
            try
            {
                System.IO.File.WriteAllText(NavLogPath,
                    $"# Nav AKICI(yürü-dur-düzelt) hedef({targetX},{targetY}) {DateTime.Now:HH:mm:ss}\r\n" +
                    $"# msPerDeg={s.NavTurnMsPerDeg:0.0} gain={gain:0.0} walkMs={walkMs} invert={s.NavTurnInvert} tol={tol} tuşlar={s.NavTurnKeyLeft}/{s.NavTurnKeyRight}\r\n");
            }
            catch { }
        }

        var lastPos = await ReadReliableAsync(s, ct, null);
        if (lastPos is null) { StatusChanged?.Invoke(this, "Koordinat okunamadı — başlanamadı"); return; }
        (int x, int y)? lastGood = lastPos;

        while (!ct.IsCancellationRequested)
        {
            if (++totalSteps > s.NavMaxSteps)
                throw new TimeoutException($"Navigasyon adım limiti ({s.NavMaxSteps}) aşıldı");

            // Bir segment YÜRÜ → DUR → minimap otursun → oku (durunca güvenilir okuma+dönüş).
            await _router.KeyDownAsync("W", ct);
            await Task.Delay(walkMs, ct);
            await _router.KeyUpAsync("W", CancellationToken.None);
            await Task.Delay(140, ct);   // minimap otursun (Y=895 tipi çöp okumayı önler)

            var pos = await ReadReliableAsync(s, ct, lastGood);
            if (pos is null) continue;
            lastGood = pos;

            float dx = targetX - pos.Value.x, dy = targetY - pos.Value.y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            StatusChanged?.Invoke(this, $"({pos.Value.x},{pos.Value.y}) → ({targetX},{targetY}) · {dist:0} birim");
            if (dist <= tol) return;   // ✓ varıldı

            // hareket vektörü = yürünen yön (heading)
            float mvx = pos.Value.x - lastPos.Value.x, mvy = pos.Value.y - lastPos.Value.y;
            float mlen = MathF.Sqrt(mvx * mvx + mvy * mvy);
            string turnInfo = "düz";

            if (mlen < 3f)
            {
                if (++stuck >= s.NavStuckThreshold)
                {
                    turnInfo = "kurtarma";
                    await _router.KeyDownAsync(s.NavTurnKeyRight, ct);
                    await Task.Delay(Math.Clamp((int)(90f * s.NavTurnMsPerDeg), 300, 2000), ct);
                    await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
                    stuck = 0;
                }
            }
            else
            {
                stuck = 0;
                float hx = mvx / mlen, hy = mvy / mlen;
                float tnx = dx / dist, tny = dy / dist;
                float cross = hx * tny - hy * tnx;
                float dot   = hx * tnx + hy * tny;
                float angle = MathF.Atan2(cross, dot) * (180f / MathF.PI);

                if (MathF.Abs(angle) > 4f)
                {
                    bool   left    = (angle > 0) != s.NavTurnInvert;
                    string turnKey = left ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                    int    turnMs  = Math.Clamp((int)(MathF.Abs(angle) * s.NavTurnMsPerDeg * gain), 25, 600);
                    turnInfo = $"{turnKey}×{turnMs}ms (açı {angle:0}°)";
                    // DURURKEN dön (A/D durunca güvenilir döndürür — Test 4 kanıtladı)
                    await _router.KeyDownAsync(turnKey, ct);
                    await Task.Delay(turnMs, ct);
                    await _router.KeyUpAsync(turnKey, CancellationToken.None);
                }
            }

            if (debugLog)
                AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} hareket={mlen:0} | {turnInfo}");
            lastPos = pos;
        }
    }

    /// <summary>Probe-correct (dur-kalk) — fallback (NavContinuous kapalıyken). Her adım dur→W probu→yön→dön→yürü.</summary>
    private async Task NavigateProbeCorrectAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
    {
        var s          = _state.Autonomous;
        int tol        = Math.Max(s.NavToleranceCoords, 2);   // kullanıcı toleransı (DOĞRU kalibrasyonla 2 bile çalışır)
        int totalSteps = 0;
        int stuckCount = 0;
        (int x, int y)? lastGood = null;

        if (debugLog)
        {
            try
            {
                System.IO.File.WriteAllText(NavLogPath,
                    $"# Nav teşhis hedef({targetX},{targetY}) {DateTime.Now:HH:mm:ss}\r\n" +
                    $"# msPerDeg={s.NavTurnMsPerDeg:0.0} invert={s.NavTurnInvert} tol={s.NavToleranceCoords} probeMs={s.NavProbeMs} stepMs={s.NavStepMs} tuşlar={s.NavTurnKeyLeft}/{s.NavTurnKeyRight}\r\n");
            }
            catch { }
        }

        while (!ct.IsCancellationRequested)
        {
            if (++totalSteps > s.NavMaxSteps)
                throw new TimeoutException($"Navigasyon adım limiti ({s.NavMaxSteps}) aşıldı");

            // ── 1. Mevcut konum ──────────────────────────────────────────────
            var pos = await ReadReliableAsync(s, ct, lastGood);
            if (pos is null) { await Task.Delay(200, ct); continue; }
            lastGood = pos;

            float dx   = targetX - pos.Value.x;
            float dy   = targetY - pos.Value.y;
            float dist = MathF.Sqrt(dx * dx + dy * dy);
            StatusChanged?.Invoke(this, $"({pos.Value.x},{pos.Value.y}) → ({targetX},{targetY}) · {dist:0} birim");

            if (dist <= tol) return; // ✓ varıldı

            // ── 2. Probe: kısa W → heading vektörü ──────────────────────────
            await _router.KeyDownAsync("W", ct);
            await Task.Delay(s.NavProbeMs, ct);
            await _router.KeyUpAsync("W", CancellationToken.None);
            await Task.Delay(60, ct); // settle

            var probePos = await ReadReliableAsync(s, ct, lastGood);
            if (probePos is null) continue;
            lastGood = probePos;

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
            string turnInfo = "yok";
            if (MathF.Abs(angleDeg) > 3f)
            {
                // angleDeg>0 → CCW → sol/A varsayımı; NavTurnInvert koordinat el-yönünü düzeltir.
                bool   left    = (angleDeg > 0) != s.NavTurnInvert;
                string turnKey = left ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                int    turnMs  = Math.Clamp((int)(MathF.Abs(angleDeg) * s.NavTurnMsPerDeg), 50, 3000);
                turnInfo       = $"{turnKey}×{turnMs}ms";
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

            if (debugLog)
                AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} | yön({pnx:0.00},{pny:0.00})→hedef({tnx:0.00},{tny:0.00}) açı={angleDeg:0}° | dön={turnInfo} yürü={walkMs}ms");

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
        const int burstMs   = 400;   // her burst < 180° (180°/sn'de 72°) → atan2 SARMASI yok
        const int maxBursts = 6;

        // KÖK DÜZELTME: tek 2sn dönüş 180°'yi aşınca açı sarıp yanlış (çok büyük) ms/derece
        // veriyordu → aşırı dönüş. Çözüm: kısa burst'ler, her birini ölç, TOPLA.
        var hPrev = await ProbeHeadingAsync(s, ct)
            ?? throw new InvalidOperationException("İlk probe başarısız — koordinat okunamadı");

        float totalDeg = 0f; int totalMs = 0;
        for (int i = 0; i < maxBursts; i++)
        {
            await _router.KeyDownAsync(s.NavTurnKeyRight, ct);
            await Task.Delay(burstMs, ct);
            await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
            await Task.Delay(120, ct);

            var hNow = await ProbeHeadingAsync(s, ct);
            if (hNow is null) continue;
            float cross = hPrev.x * hNow.Value.y - hPrev.y * hNow.Value.x;
            float dot   = hPrev.x * hNow.Value.x + hPrev.y * hNow.Value.y;
            float deg   = MathF.Abs(MathF.Atan2(cross, dot) * (180f / MathF.PI));  // < 180° (sarma yok)
            totalDeg += deg; totalMs += burstMs;
            hPrev = hNow.Value;
            if (totalDeg >= 100f) break;   // yeterli ölçü toplandı
        }

        if (totalDeg < 20f)
            throw new InvalidOperationException(
                $"Dönüş ölçülemedi ({totalDeg:0}°) — D tuşunun karakteri DÖNDÜRDÜĞÜNÜ (strafe değil) doğrulayın");

        // Geri al (yaklaşık — aynı toplam süre sola)
        await _router.KeyDownAsync(s.NavTurnKeyLeft, ct);
        await Task.Delay(totalMs, ct);
        await _router.KeyUpAsync(s.NavTurnKeyLeft, CancellationToken.None);

        return totalMs / totalDeg;  // ms/derece (doğru)
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

    /// <summary>
    /// Koordinatı birkaç kez (≥5) okuyup eksen bazında MEDYAN döner — hareket sırasında
    /// tek-tük yanlış okumaları (örn. Y'nin 417↔40 sıçraması) filtreler. Okuma yapılan
    /// aralık (~N × NavReadRetryMs) minimap'in oturmasına da zaman tanır. Hiç okuma yoksa null.
    /// </summary>
    private async Task<(int x, int y)?> ReadReliableAsync(AutonomousSettings s, CancellationToken ct, (int x, int y)? near = null)
    {
        // Medyan-of-3 + plausibility yeter (birleşik ROI güvenilir) → az okuma = daha akıcı
        // hareket (her okumadaki ~600ms duraklamayı ~halve eder). Eskiden 5'ti.
        int n = 3;
        var pts = new List<(int x, int y)>(n);
        for (int i = 0; i < n; i++)
        {
            var r = _coordReader.Read();
            if (r is not null) pts.Add(r.Value);
            if (i < n - 1)
            {
                try { await Task.Delay(s.NavReadRetryMs, ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        if (pts.Count == 0) return null;

        // Plausibility: near verilmişse, ondan > NavMaxJumpCoords sıçrayan okumaları ele
        // (virgül-misdetection gibi anlık çöp; karakter bir okuma arası o kadar gidemez).
        var pool = pts;
        if (near is not null)
        {
            int mj = s.NavMaxJumpCoords;
            var f = pts.Where(p => Math.Abs(p.x - near.Value.x) <= mj && Math.Abs(p.y - near.Value.y) <= mj).ToList();
            if (f.Count > 0) pool = f;
        }
        var xs = pool.Select(p => p.x).OrderBy(v => v).ToList();
        var ys = pool.Select(p => p.y).OrderBy(v => v).ToList();
        return (xs[xs.Count / 2], ys[ys.Count / 2]);  // eksen-bazında ortanca (filtrelenmişten)
    }

    // ── Teşhis logu (TestNavigate'te exe yanına nav_debug.txt) ───────────────────
    private static readonly string NavLogPath =
        System.IO.Path.Combine(AppContext.BaseDirectory, "nav_debug.txt");

    private static void AppendNavLog(string line)
    {
        try { System.IO.File.AppendAllText(NavLogPath, line + "\r\n"); }
        catch { }
    }
}
