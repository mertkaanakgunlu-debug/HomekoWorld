using HomekoWorld.Hardware;
using HomekoWorld.Models;
using HomekoWorld.Models.Autonomous;

namespace HomekoWorld.Services.Autonomous;

/// <summary>
/// Koordinat navigasyonu — tek "adaptif akıcı" kontrolör.
/// Algoritma (her döngü, W SÜREKLİ basılı = akıcı):
///   1. Güvenilir konum oku (ReadReliableAsync: kümeleme ile hane-düşmesi/çöp eler). Tolerans içindeyse → bitti.
///   2. Heading = son iki KABUL edilen konumun farkı (gerçek hareket yönü).
///   3. Hedefe açı hatası → W basılıyken A/D kısa tap ile KIVRIL (akıcı dönüş).
///   4. Dönüş yönü ÇALIŞIRKEN kendini kalibre eder: tap'ten sonra açı hatası büyürse yön ters çevrilir
///      (NavTurnInvert tahminine gerek kalmaz — "hedeften uzaklaşma"nın 1 numaralı sebebini yok eder).
///   5. Takılınca kademeli recovery (bekle → duvar boyu kay → artan açı dolan → S ile geri → pes et).
/// Koordinat sistemi bağımsız; el-yönünü (CW/CCW) self-kalibrasyon halleder.
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

    /// <summary>Oyun koordinatına gider. İptal/adım limiti/ulaşılamama → exception; varılınca döner.</summary>
    public Task NavigateToAsync(int targetX, int targetY, CancellationToken ct, bool debugLog = false)
        => NavigateAdaptiveAsync(targetX, targetY, ct, debugLog);

    // ── Adaptif akıcı kontrolör ─────────────────────────────────────────────────────
    private async Task NavigateAdaptiveAsync(int targetX, int targetY, CancellationToken ct, bool debugLog)
    {
        var   s        = _state.Autonomous;
        int   tol      = Math.Max(s.NavToleranceCoords, 2);
        int   readMs   = Math.Max(80, s.NavContinuousReadMs);                 // segment yürüme + okuma aralığı
        float gain     = Math.Clamp(s.NavContinuousSteerGain, 0.1f, 1.5f);
        float msPerDeg = Math.Clamp(s.NavTurnMsPerDeg, 1f, 60f);

        int   headDist      = Math.Max(8, tol / 2);   // heading'i ANCAK bu kadar yol katedince ölç (uzun baz = temiz açı)
        int   totalSteps    = 0;
        int   readFailStreak = 0;
        int   recoverCount  = 0;
        int   noMove        = 0;   // üst üste GERÇEK ilerleme olmayan okuma (takılma tespiti)
        int   steersNoProgress = 0; // üst üste yaklaştırmayan dönüş (dönüp duruyor)
        float bestDist      = float.MaxValue;

        bool  invert        = s.NavTurnInvert;   // seed; aşağıda çalışırken düzeltilir
        int   wrongDirStreak = 0;
        float prevErrAbs    = float.NaN;
        bool  lastWasTurn   = false;

        if (debugLog)
        {
            try
            {
                System.IO.File.WriteAllText(NavLogPath,
                    $"# Nav ADAPTİF(akıcı, W basılı) hedef({targetX},{targetY}) {DateTime.Now:HH:mm:ss}\r\n" +
                    $"# msPerDeg={msPerDeg:0.0} gain={gain:0.0} readMs={readMs} tol={tol} invertSeed={invert} tuşlar={s.NavTurnKeyLeft}/{s.NavTurnKeyRight}\r\n");
            }
            catch { }
        }

        // İlk konum (karakter dururken net okunmalı; gerekirse settle + tekrar dene)
        var lastGood = await ReadReliableAsync(s, ct, null);
        if (lastGood is null) { await Task.Delay(200, ct); lastGood = await ReadReliableAsync(s, ct, null); }
        if (lastGood is null) { StatusChanged?.Invoke(this, "Koordinat okunamadı — başlanamadı"); return; }
        (int x, int y) anchor   = lastGood.Value;   // mevcut heading ölçümünün başlangıç noktası
        (int x, int y) moveMark = lastGood.Value;   // takılma tespiti: son "gerçekten ilerlediği" konum
        float lastDist = MathF.Sqrt((float)(targetX - anchor.x) * (targetX - anchor.x) +
                                    (float)(targetY - anchor.y) * (targetY - anchor.y));
        float minDist  = lastDist;                   // overshoot emniyeti için görülen en yakın mesafe

        await _router.KeyDownAsync("W", ct);   // akıcı: W sürekli basılı
        try
        {
            // Isınma: karakter dururken ilk hareketin yön artefaktını dışla → İLK heading temiz olsun
            // (kalkış anının kısa/gürültülü ölçümü yanlış ilk dönüşe yol açıyordu).
            await Task.Delay(350, ct);
            var warm = await ReadReliableAsync(s, ct, lastGood);
            if (warm is not null) { lastGood = warm; anchor = warm.Value; moveMark = warm.Value; }

            while (!ct.IsCancellationRequested)
            {
                if (++totalSteps > s.NavMaxSteps)
                    throw new TimeoutException($"Navigasyon adım limiti ({s.NavMaxSteps}) aşıldı");

                // Hedefe yaklaşınca okuma sıklığını artır (kısa segment) → tol içine girip dur, geçip gitme.
                // W basılı kalır (akıcı); sadece daha sık kontrol → adım küçülür → overshoot olmaz.
                int walkMs = lastDist < 25f ? Math.Clamp((int)(lastDist * 10f), 120, readMs) : readMs;
                await Task.Delay(walkMs, ct);   // bir miktar yürü (W basılı), sonra oku

                var pos = await ReadReliableAsync(s, ct, lastGood);
                if (pos is null)
                {
                    // Üst üste okunamıyorsa: kısa "dur-ölç" düşüşü (minimap otursun), sonra W'yi tekrar bas.
                    if (++readFailStreak >= 4)
                    {
                        await _router.KeyUpAsync("W", CancellationToken.None);
                        await Task.Delay(180, ct);
                        pos = await ReadReliableAsync(s, ct, lastGood);
                        await _router.KeyDownAsync("W", ct);
                        readFailStreak = 0;
                    }
                    if (pos is null) continue;
                }
                readFailStreak = 0;
                lastGood = pos;

                float dx = targetX - pos.Value.x, dy = targetY - pos.Value.y;
                float dist = MathF.Sqrt(dx * dx + dy * dy);
                lastDist = dist;
                if (dist < minDist) minDist = dist;
                StatusChanged?.Invoke(this, $"({pos.Value.x},{pos.Value.y}) → ({targetX},{targetY}) · {dist:0} birim");
                if (dist <= tol) { StatusChanged?.Invoke(this, $"✓ Varıldı ({pos.Value.x},{pos.Value.y})"); return; }
                // Overshoot emniyeti: hedefe yeterince yaklaşıp (≤ ~2.5×tol) sonra ondan tol kadar UZAKLAŞTIYSA
                // hedefi geçmişiz → en yakın noktada dur (adım > tol olsa bile sonsuz salınımı önler).
                if (minDist <= tol * 1.5f && dist > minDist + tol)
                { StatusChanged?.Invoke(this, $"✓ Hedefe ulaşıldı (~{minDist:0} birim; overshoot emniyeti)"); return; }

                // ── Takılma tespiti: 'moveMark'tan ≥4 birim ilerleyemezsek N okuma sonra recovery ──
                // (Tek okumada ~1-2 birim ilerlemek NORMAL — KO yürüyüş hızı; bunu "takıldı" sayma.)
                float mdx = pos.Value.x - moveMark.x, mdy = pos.Value.y - moveMark.y;
                if (MathF.Sqrt(mdx * mdx + mdy * mdy) >= 4f) { moveMark = pos.Value; noMove = 0; }
                else noMove++;

                if (noMove >= 6 || steersNoProgress >= 5)
                {
                    // Hedefe ÇOK yakınken takıldıysak (engel tam hedefin önünde, ör. portal taşı) → en yakın
                    // noktada dur; geri fırlatan detour'a girme (zaten etkileşim menzilindeyiz). Dürüst mesaj.
                    if (dist <= tol * 2.5f)
                    { StatusChanged?.Invoke(this, $"⚠ Hedefin ~{dist:0} birim yakınında engel — en yakın noktada durdu (tol {tol})"); return; }
                    if (++recoverCount > 12)
                        throw new TimeoutException($"Navigasyon: engel aşılamadı — hedefe en yakın {minDist:0} birim. Waypoint ekleyin veya hedefi açık bir noktaya alın.");
                    if (debugLog) AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} | RECOVERY #{recoverCount} (noMove={noMove} steersNoProg={steersNoProgress})");
                    await RecoverAsync(s, recoverCount - 1, msPerDeg, ct);
                    var rp = (await ReadReliableAsync(s, ct, lastGood)) ?? pos.Value;
                    lastGood = rp; anchor = rp; moveMark = rp;
                    noMove = 0; steersNoProgress = 0;
                    prevErrAbs = float.NaN; lastWasTurn = false;
                    continue;
                }

                // ── Heading'i YETERLİ yol katedince ölç + hedefe doğru KIVRIL (W basılı kalır = akıcı) ──
                float ax = pos.Value.x - anchor.x, ay = pos.Value.y - anchor.y;
                float alen = MathF.Sqrt(ax * ax + ay * ay);
                string info = "yürü";
                if (alen >= headDist)
                {
                    float hx = ax / alen, hy = ay / alen;          // güvenilir heading (yeterli mesafe → temiz açı)
                    float tnx = dx / dist, tny = dy / dist;
                    float cross = hx * tny - hy * tnx;
                    float dot   = hx * tnx + hy * tny;
                    float angle = MathF.Atan2(cross, dot) * (180f / MathF.PI);
                    float errAbs = MathF.Abs(angle);

                    // İlerleme bekçisi: SADECE hedefe dönükken (errAbs küçük) yaklaşmıyorsak "dönüp duruyor".
                    // Büyük açıda hâlâ yönelirken dist'in artması NORMAL (geri dönüyoruz) → cezalandırma.
                    if (dist < bestDist - 2f) { bestDist = dist; steersNoProgress = 0; recoverCount = 0; }
                    else if (errAbs < 30f) steersNoProgress++;

                    // Kendini kalibre eden dönüş yönü: son dönüşten sonra hata BÜYÜDÜYSE → yön yanlış
                    if (lastWasTurn && !float.IsNaN(prevErrAbs) && prevErrAbs > 12f)
                    {
                        if (errAbs > prevErrAbs + 6f)
                        {
                            if (++wrongDirStreak >= 2) { invert = !invert; wrongDirStreak = 0; info = "(yön↔ters) "; }
                        }
                        else wrongDirStreak = 0;
                    }

                    if (errAbs > 40f)
                    {
                        // BÜYÜK sapma → YERİNDE dön (W'yi bırak). Yürürken kıvırmak yetersiz kalıp
                        // engele tosllatıyordu; önce hedefe dön, SONRA yürü.
                        await _router.KeyUpAsync("W", CancellationToken.None);
                        bool   left    = (angle > 0) != invert;
                        string turnKey = left ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                        // TAM açı dön (kalibrasyona güven — kullanıcı ms/derece'yi ölçtü). Eski 0.8 kazanç +
                        // 2000ms tavan, 174°'yi 126°'ye kırpıp eksik döndürüyordu. Kalan hatayı sonraki ölçüm toplar.
                        int    turnMs  = Math.Clamp((int)(errAbs * msPerDeg), 80, (int)(190f * msPerDeg));
                        info += $"[yerinde] {turnKey}×{turnMs}ms (açı {angle:0}°)";
                        await TapAsync(turnKey, turnMs, ct);
                        await _router.KeyDownAsync("W", ct);       // tekrar akıcı yürü
                        lastWasTurn = true; prevErrAbs = errAbs;
                        // Yön değişti → heading'i sıfırdan ölç; yerinde dönüş "takılma" SAYILMASIN.
                        var rp = (await ReadReliableAsync(s, ct, lastGood)) ?? pos.Value;
                        lastGood = rp; anchor = rp; moveMark = rp; noMove = 0;
                        if (debugLog) AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} yol={alen:0} | {info}");
                        continue;
                    }
                    if (errAbs > 4f)
                    {
                        // KÜÇÜK sapma → yürürken KIVRIL (akıcı)
                        bool   left    = (angle > 0) != invert;
                        string turnKey = left ? s.NavTurnKeyLeft : s.NavTurnKeyRight;
                        int    turnMs  = Math.Clamp((int)(errAbs * msPerDeg * gain), 25, 700);
                        info += $"{turnKey}×{turnMs}ms (açı {angle:0}°)";
                        await _router.KeyDownAsync(turnKey, ct);   // W basılı iken → kıvrılır
                        await Task.Delay(turnMs, ct);
                        await _router.KeyUpAsync(turnKey, CancellationToken.None);
                        lastWasTurn = true;
                    }
                    else lastWasTurn = false;
                    prevErrAbs = errAbs;
                    anchor = pos.Value;                            // sonraki heading ölçümü buradan
                }

                if (debugLog)
                    AppendNavLog($"#{totalSteps} konum({pos.Value.x},{pos.Value.y}) uzaklık={dist:0} yol={alen:0} | {info}");
            }
            ct.ThrowIfCancellationRequested();
        }
        finally
        {
            // Hangi sebeple çıkılırsa çıkılsın hiçbir hareket/dönüş tuşu basılı kalmasın.
            await _router.KeyUpAsync("W", CancellationToken.None);
            await _router.KeyUpAsync(s.NavTurnKeyLeft, CancellationToken.None);
            await _router.KeyUpAsync(s.NavTurnKeyRight, CancellationToken.None);
            await _router.KeyUpAsync("S", CancellationToken.None);
        }
    }

    // ── Kademeli recovery (mixed engel: duvar + NPC) ────────────────────────────────
    // attempt arttıkça yükselir; çağıran ilerleme olmazsa tekrar çağırır, çok olunca pes eder.
    // Giriş/çıkışta W invariant'ı korunur: başta W bırakılır, sonunda yeniden basılır.
    private async Task RecoverAsync(AutonomousSettings s, int attempt, float msPerDeg, CancellationToken ct)
    {
        await _router.KeyUpAsync("W", CancellationToken.None);
        await Task.Delay(60, ct);

        // Kör kaçış (harita/pathfinding YOK): her denemede AYNI yöne ~55° "sweep" → zamanla tüm yönleri
        // tarar; + ARTAN mesafede UZUN ileri detour → engeli gerçekten aşacak kadar yürür (eski ~5 birimlik
        // detour yetmiyordu, duvara geri kayıyordu). attempt≥2'de önce S ile geri çekilip sıkışmayı bozar.
        // recoverCount 12'ye varınca çağıran pes eder (TimeoutException → FSM).
        string turnKey = s.NavTurnKeyRight;
        int    sweepMs = Math.Clamp((int)(55f * msPerDeg), 200, 1600);
        int    walkMs  = Math.Clamp(1000 + 500 * attempt, 1000, 4000);

        try
        {
            if (attempt >= 2)
            {
                StatusChanged?.Invoke(this, "Recovery: geri çekil (S)");
                await TapAsync("S", 1400, ct);   // S ~5× yavaş → uzun bas
            }
            StatusChanged?.Invoke(this, $"Recovery #{attempt + 1}: ~55° dön + {walkMs}ms ileri (detour)");
            await TapAsync(turnKey, sweepMs, ct);
            await TapAsync("W", walkMs, ct);
        }
        finally
        {
            // Akıcı döngü W'yi basılı bekliyor → yeniden bas.
            await _router.KeyDownAsync("W", ct);
        }
    }

    /// <summary>Tuşu süre kadar basılı tutup bırakır (bırakma iptalde de garanti).</summary>
    private async Task TapAsync(string key, int ms, CancellationToken ct)
    {
        await _router.KeyDownAsync(key, ct);
        try { await Task.Delay(ms, ct); }
        finally { await _router.KeyUpAsync(key, CancellationToken.None); }
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

    // ── A/D dönüş hızı kalibrasyonu (seed; adaptif döngü kendini düzelttiğinden kritik değil) ──

    /// <summary>
    /// D tuşunu testMs süre basar → W probuyla açı değişimini ölçer → ms/derece döner.
    /// Mevcut ayarı GÜNCELLEMEz — çağıran kaydetmeli.
    /// </summary>
    public async Task<float> CalibrateTurnRateAsync(CancellationToken ct)
    {
        var s = _state.Autonomous;
        const int burstMs   = 400;   // her burst < 180° (180°/sn'de 72°) → atan2 SARMASI yok
        const int maxBursts = 6;

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

        return totalMs / totalDeg;  // ms/derece
    }

    // ── Yardımcılar ───────────────────────────────────────────────────────────────

    /// <summary>
    /// W basarak normalize heading vektörü ölçer. SABİT SÜRE değil, SABİT MESAFE (≈8 birim) katedene
    /// kadar yürür — KO yürüyüş hızı düşük olduğundan 500ms ≈ 3-4 birim → ±1 gürültüde açı çok oynar.
    /// Mesafe-bazlı = hızdan bağımsız temiz heading. Engelliyse maxMs'te bırakır; yeterli ilerleme yoksa null.
    /// </summary>
    private async Task<(float x, float y)?> ProbeHeadingAsync(AutonomousSettings s, CancellationToken ct)
    {
        var pos0 = await ReadReliableAsync(s, ct);
        if (pos0 is null) return null;

        const int minDist = 8, maxMs = 3000;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        (int x, int y) pos1 = pos0.Value;

        await _router.KeyDownAsync("W", ct);
        try
        {
            while (sw.ElapsedMilliseconds < maxMs && !ct.IsCancellationRequested)
            {
                await Task.Delay(150, ct);
                var p = await ReadReliableAsync(s, ct, pos1);
                if (p is not null) pos1 = p.Value;
                float ddx = pos1.x - pos0.Value.x, ddy = pos1.y - pos0.Value.y;
                if (MathF.Sqrt(ddx * ddx + ddy * ddy) >= minDist) break;
            }
        }
        finally { await _router.KeyUpAsync("W", CancellationToken.None); }
        await Task.Delay(80, ct);

        float dx = pos1.x - pos0.Value.x, dy = pos1.y - pos0.Value.y;
        float len = MathF.Sqrt(dx * dx + dy * dy);
        return len < 4f ? null : (dx / len, dy / len);   // yeterince ilerleyemedi → güvenilmez
    }

    /// <summary>
    /// GÜVENİLİR konum okuma. Kısa aralıklarla 3 hızlı okuma alır; en kalabalık "sıkı küme"yi
    /// (eksen başına ≤ <c>Tight</c> birim) bulup medyanını döner. Hane-düşmesi (örn. 1585↔158) gibi
    /// tek-tük 10× sıçramalar küme dışında kalıp elenir. Sıkı küme oluşmazsa null (bayat/çöp kullanılmaz).
    /// <paramref name="near"/> verilmişse ışınlanma filtresi (NavMaxJumpCoords) ikincil emniyet olarak uygulanır.
    /// </summary>
    private async Task<(int x, int y)?> ReadReliableAsync(AutonomousSettings s, CancellationToken ct, (int x, int y)? near = null)
    {
        const int n = 3;
        const int Tight = 25;   // ~30ms'lik pencerede gerçek hareket << 25 birim; hane-düşmesi >> 25
        var pts = new List<(int x, int y)>(n);
        for (int i = 0; i < n; i++)
        {
            var r = _coordReader.Read();
            if (r is not null)
            {
                bool plausible = near is null ||
                    (Math.Abs(r.Value.x - near.Value.x) <= s.NavMaxJumpCoords &&
                     Math.Abs(r.Value.y - near.Value.y) <= s.NavMaxJumpCoords);
                if (plausible) pts.Add(r.Value);
            }
            if (i < n - 1)
            {
                try { await Task.Delay(Math.Max(12, s.NavReadRetryMs / 4), ct); }
                catch (OperationCanceledException) { break; }
            }
        }
        if (pts.Count == 0) return null;
        // Tek okuma: yalnızca bilinen-iyi bir 'near'a göre doğrulandıysa kabul (digit-drop near'dan
        // NavMaxJumpCoords kadar uzak olur → zaten yukarıda elenmiştir).
        if (pts.Count == 1) return near is null ? null : pts[0];

        // En kalabalık sıkı küme
        int bestI = 0, bestCount = 0;
        for (int i = 0; i < pts.Count; i++)
        {
            int c = 0;
            foreach (var p in pts)
                if (Math.Abs(p.x - pts[i].x) <= Tight && Math.Abs(p.y - pts[i].y) <= Tight) c++;
            if (c > bestCount) { bestCount = c; bestI = i; }
        }
        if (bestCount < 2) return null;   // 3 okuma da birbirinden uzak → güvenilmez

        var cluster = pts.Where(p => Math.Abs(p.x - pts[bestI].x) <= Tight && Math.Abs(p.y - pts[bestI].y) <= Tight).ToList();
        var xs = cluster.Select(p => p.x).OrderBy(v => v).ToList();
        var ys = cluster.Select(p => p.y).OrderBy(v => v).ToList();
        return (xs[xs.Count / 2], ys[ys.Count / 2]);
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
