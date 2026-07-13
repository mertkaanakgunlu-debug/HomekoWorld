using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;

namespace HomekoWorld.Engine;

public sealed partial class FarmEngine
{
    // ── Tespit üretici thread'i (Sorun 2: decoupled YOLO) ─────────────────────
    // Ayrı arka plan thread'i: sürekli ekran yakalar + YOLO infer + HP/MP okur,
    // sonucu _latestDetections'a atomik yazar. Scanning/combat döngüleri bu snapshot'ı
    // okur → inference'ı asla beklemez. Inferrer buffer'ları tek thread'de kullanıldığı
    // için (yalnız bu döngü çağırır) thread-safe sorunu yok.
    private void DetectionLoop(CancellationToken ct)
    {
        var frameSw = new System.Diagnostics.Stopwatch();
        long lastFpsTime = NowMs();
        int inferred = 0;   // A3: saniyedeki BAŞARILI inference sayısı (gerçek FPS; döngü-turu DEĞİL)
        long frameId = 0;   // Faz B: her başarılı inference'a monotonik kare kimliği (replay/frame-age)
        long lastReplayMs = 0; // Faz B: replay recorder throttle damgası

        // Ekran kaynağı bu thread'de kurulur (DXGI/D3D11 device tek-thread kullanımı için kritik).
        IScreenSource screen = CreateScreenSource(_appState.Farm);
        Program.Log($"[Farm] Ekran yakalama yöntemi: {screen.BackendName}");
        try
        {
            long totalCapMs = 0;
            long totalInfMs = 0;
            long totalWaitMs = 0;
            double totalPrepMs = 0, totalRunMs = 0, totalPostMs = 0; // P0: inference alt-zamanlaması

            while (!ct.IsCancellationRequested)
            {
                frameSw.Restart();
                long now = NowMs();
                if (now - lastFpsTime >= 1000)
                {
                    // Gerçek inference FPS + gecikme bütçesi (A3): yalnız BAŞARILI inference'lar sayılır
                    // (paused/null/capture-fail turları DEĞİL → eski "döngü-turu FPS"i yanıltıcıydı).
                    int denom = Math.Max(1, inferred);
                    Telemetry.InferenceFps  = inferred;
                    Telemetry.CaptureMs     = (int)(totalCapMs  / denom);
                    Telemetry.InferenceMs   = (int)(totalInfMs  / denom);
                    Telemetry.WaitMs        = (int)(totalWaitMs / denom);
                    Telemetry.PreprocessMs  = (int)Math.Round(totalPrepMs / denom);   // P0: CPU preprocess
                    Telemetry.GpuRunMs      = (int)Math.Round(totalRunMs  / denom);   // P0: saf GPU Run
                    Telemetry.PostprocessMs = (int)Math.Round(totalPostMs / denom);   // P0: CPU postprocess

                    totalCapMs = totalInfMs = totalWaitMs = 0;
                    totalPrepMs = totalRunMs = totalPostMs = 0;
                    inferred = 0;
                    lastFpsTime = now;
                    TelemetryUpdated?.Invoke(this, Telemetry);
                    MaybeLogPerfLine();   // 1/dk perf satırı (fine-tuning telemetrisi)
                }
                try
                {
                    var inferrer = Inferrer; // mid-iteration null swap'a karşı yerel kopya
                    if (inferrer is null || _paused)
                    {
                        Thread.Sleep(50);
                        continue;
                    }

                    var s = _appState.Farm;
                    long capStart = NowMs();
                    Bitmap frame;
                    try { frame = screen.Capture(); }
                    catch (Exception capEx) when (screen is not GdiScreenSource)
                    {
                        Program.Log($"[Farm] {screen.BackendName} yakalama hatası — GDI'ye geçiliyor: {capEx.Message}");
                        try { screen.Dispose(); } catch { }
                        screen = new GdiScreenSource();
                        frame  = screen.Capture();
                    }
                    long capEnd = NowMs();

                    // (d) Yeni masaüstü görüntüsü yoksa (yalnız imleç oynadı / DXGI zaman-aşımı) çıkarımı ATLA:
                    // bayat kareye GPU harcanmaz + gerçek FPS şişmez. Son snapshot geçerli kalır (sahnede değişen yok).
                    if (!screen.LastFrameWasNew)
                    {
                        Interlocked.Increment(ref _staleSkips); // perf satırında raporlanır (fps=az'ın nedeni ayrışsın)
                        int spentSkip = (int)frameSw.ElapsedMilliseconds;
                        int minMsSkip = Math.Max(10, s.DetectionMinIntervalMs);
                        if (s.RecordingMode) minMsSkip = Math.Max(minMsSkip, s.RecordingModeMinIntervalMs);
                        if (spentSkip < minMsSkip) Thread.Sleep(minMsSkip - spentSkip);
                        continue;
                    }

                    var rawDets = inferrer.Infer(frame);
                    long infEnd = NowMs();

                    totalCapMs += (capEnd - capStart);
                    totalInfMs += (infEnd - capEnd);
                    inferred++;                       // A3: başarılı inference → gerçek FPS sayacı
                    frameId++;                        // Faz B: kare kimliği

                    var tm = inferrer.LastTimings;    // P0: preprocess/GPU Run/postprocess ayrı
                    totalPrepMs += tm.Preprocess;
                    totalRunMs  += tm.Run;
                    totalPostMs += tm.Postprocess;

                    // P3: ROI-yerel tespitleri ekran-uzayına çevir
                    int roiX = screen.CaptureX, roiY = screen.CaptureY;
                    var dets = OffsetDets(rawDets, roiX, roiY);
                    // Object tracker: her kutuya kalıcı TrackId + (öldürülmüşse) Dead iliştir (#2/#3/#4).
                    dets = _tracker.Update(dets, NowMs());
                    int snapW = screen.FullScreenW, snapH = screen.FullScreenH;

                    // V4: karedeki hedef-penceresi durumu (yapı + renk + offset) — tek tarama, snapshot'a
                    // atomik taşınır. P3: ROI aktifken frame HP bar koordinatlarıyla uyumsuz → null bırak.
                    TargetBarState? barState = null;
                    var wtm = _appState.Wtm;
                    if (roiX == 0 && roiY == 0)
                        barState = WtmVision.ScanTargetBar(frame, wtm, _frameTemplates, _frameRectPhys);

                    _latestDetections = new DetectionSnapshot(
                        frameId, dets, snapW, snapH, capStart, NowMs(), barState);
                    SignalNewSnapshot();   // 14.tur (Faz 3.2): FarmLoop'u anında uyandır

                    PublishOverlay(dets, s, snapW, snapH);

                    // Faz B: replay recorder (yalnız açıkken). Throttle + ayrı worker thread'inde encode/yaz
                    // → detection loop'u BLOKLAMAZ. Kareyi klonla (buffer bir sonraki turda yeniden kullanılır).
                    var recorder = _replay;
                    if (recorder is not null && now - lastReplayMs >= Math.Max(50, s.ReplayIntervalMs))
                    {
                        lastReplayMs = now;
                        recorder.Offer(frameId, capStart, infEnd, frame, dets);
                    }

                    int spent = (int)frameSw.ElapsedMilliseconds;
                    int minMs = Math.Max(10, s.DetectionMinIntervalMs);
                    if (s.RecordingMode) 
                        minMs = Math.Max(minMs, s.RecordingModeMinIntervalMs);
                    
                    long waitStart = NowMs();
                    if (spent < minMs) Thread.Sleep(minMs - spent);
                    totalWaitMs += (NowMs() - waitStart);
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Program.Log($"[Farm] Tespit döngüsü hata: {ex.Message}");
                    Thread.Sleep(200);
                }
            }
        }
        catch { /* CT dispose yarışı vb. — sessizce çık */ }
        finally
        {
            try { screen.Dispose(); } catch { }
            // Çıkışta overlay'i temizle: bu thread tek üretici olduğu için son sözü
            // boş kare olsun → Stop ile yarışsa bile kutular ekranda donmaz (Sorun 4).
            try { DetectionsUpdated?.Invoke(this, new DetectionFrame(Array.Empty<Detection>(), null, 0, 0)); }
            catch { }
        }
    }

    /// <summary>
    /// Ayardaki yönteme göre ekran kaynağı kurar (DetectionLoop thread'inde çağrılır).
    /// DXGI seçili ama başlatılamıyorsa (exclusive-fullscreen/RDP/sürücü) sessizce GDI'ye düşülür.
    /// Daima TAM EKRAN yakalanır (eski P3 kısmi-ROI özelliği kaldırıldı — CUDA sonrası faydasızdı).
    /// </summary>
    private static IScreenSource CreateScreenSource(FarmSettings s)
    {
        if (s.CaptureBackend == CaptureBackend.Dxgi)
        {
            try
            {
                return new DxgiScreenSource();
            }
            catch (Exception ex)
            {
                Program.Log($"[Farm] DXGI yakalama başlatılamadı — GDI'ye düşülüyor: {ex.Message}");
            }
        }
        return new GdiScreenSource();
    }

    // P3: ROI-yerel tespitleri ekran-uzayına çevirir (dx/dy != 0 ise).
    private static IReadOnlyList<Detection> OffsetDets(IReadOnlyList<Detection> dets, int dx, int dy)
    {
        if (dx == 0 && dy == 0) return dets;
        var arr = new Detection[dets.Count];
        for (int i = 0; i < dets.Count; i++) arr[i] = dets[i].WithOffset(dx, dy);
        return arr;
    }

    // Seçili mob türü kutuları + (combat'te) vurgulu hedef. Hedefi geçerli kareden eşler
    // ki overlay ReferenceEquals ile çift kutuyu atlasın.
    private void PublishOverlay(IReadOnlyList<Detection> dets, FarmSettings s, int w, int h)
    {
        var handler = DetectionsUpdated;
        if (handler is null) return; // abone yok → çizim maliyeti sıfır

        var selectedIds = _mobLibrary.GetSelectedIds(s.SelectedMobNames);
        var shown = selectedIds.Count == 0
            ? dets
            : dets.Where(d => selectedIds.Contains(d.ClassId)).ToList();

        Detection? tgt = _currentTargetForOverlay;
        if (tgt is not null)
        {
            // #4: vurguyu kalıcı TrackId ile eşle → "en yakın aynı-tür kutu" kayması biter (ölü iz değilse).
            Detection? match = tgt.TrackId >= 0
                ? shown.FirstOrDefault(d => d.TrackId == tgt.TrackId && !d.Dead)
                : null;
            // Track kaybolduysa YALNIZ yakın (≤100px) + ölü-olmayan aynı-tür kutuya düş → vurgu rastgele
            // UZAK/yanlış moba ZIPLAMAZ (eski geniş "en yakın aynı-tür" fallback'i yeşil kutuyu kaydırıyordu).
            // Yakında uygun kutu yoksa: tgt son bilinen değerinde kalır (yanlış mob göstermez).
            match ??= shown
                .Where(d => d.ClassId == tgt.ClassId && !d.Dead && d.DistanceTo(tgt.Center) <= 100f)
                .OrderBy(d => d.DistanceTo(tgt.Center))
                .FirstOrDefault();
            if (match is not null) tgt = match;
        }

        handler.Invoke(this, new DetectionFrame(shown, tgt, w, h));
    }

    // ── P2: PIPELINED inference — ÜRETİCİ (capture + preprocess) ──────────────
    // Kareyi yakalar, boş bir slot'a PreprocessInto eder (GPU'ya dokunmaz), HSV+replay klonunu hazırlar,
    // (slot+meta)'yı latest-wins mailbox'a koyar. Tüketici GPU Run'ı eşzamanlı yapar → CPU/GPU overlap.
    private void CaptureProduceLoop(CancellationToken ct)
    {
        long frameId = 0, lastReplayMs = 0;
        IScreenSource screen = CreateScreenSource(_appState.Farm);
        Program.Log($"[Farm] Ekran yakalama yöntemi: {screen.BackendName} (pipelined)");
        // P3: ROI meta — thread başlangıcında bir kez oku (screen ömrü boyunca sabit).
        int roiX = screen.CaptureX, roiY = screen.CaptureY;
        int fullW = screen.FullScreenW, fullH = screen.FullScreenH;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var inferrer = Inferrer;
                if (inferrer is null || _paused) { Thread.Sleep(20); continue; }
                var s = _appState.Farm;

                int slot;
                lock (_pipeLock) { slot = _freeSlots.Count > 0 ? _freeSlots.Pop() : -1; }
                if (slot < 0) { Thread.Sleep(1); continue; } // 3-slot ile normalde olmaz

                try
                {
                    long capStart = NowMs();
                    Bitmap frame;
                    try { frame = screen.Capture(); }
                    catch (Exception capEx) when (screen is not GdiScreenSource)
                    {
                        Program.Log($"[Farm] {screen.BackendName} yakalama hatası — GDI'ye: {capEx.Message}");
                        try { screen.Dispose(); } catch { }
                        screen = new GdiScreenSource();
                        roiX = 0; roiY = 0;
                        fullW = screen.FullScreenW; fullH = screen.FullScreenH;
                        frame = screen.Capture();
                    }
                    long capEnd = NowMs();

                    // (d) Yeni masaüstü görüntüsü yoksa üretme: slotu iade et → tüketici son taze kareyi
                    // işlemeye devam eder; bayat kareye preprocess/GPU harcanmaz + gerçek FPS şişmez.
                    if (!screen.LastFrameWasNew)
                    {
                        Interlocked.Increment(ref _staleSkips); // perf satırında raporlanır (fps=az'ın nedeni ayrışsın)
                        lock (_pipeLock) { _freeSlots.Push(slot); }
                        Thread.Sleep(1);
                        continue;
                    }

                    var (padX, padY, scale) = inferrer.PreprocessInto(frame, slot);
                    double prepMs = inferrer.LastTimings.Preprocess;
                    frameId++;

                    // V4: karedeki hedef-penceresi durumu (yapı + renk + offset) — tek tarama, üretici thread'de.
                    // P3: ROI aktifken frame koordinatları uyumsuz → null bırak.
                    TargetBarState? barState = null;
                    var wtm = _appState.Wtm;
                    if (roiX == 0 && roiY == 0)
                        barState = WtmVision.ScanTargetBar(frame, wtm, _frameTemplates, _frameRectPhys);

                    // Replay (throttle): capture buffer tüketiciye geçemez → ÜRETİCİ klonlar, tüketici dets ile yazar.
                    Bitmap? replayClone = null;
                    long now = NowMs();
                    if (_replay is not null && now - lastReplayMs >= Math.Max(50, s.ReplayIntervalMs))
                    {
                        lastReplayMs = now;
                        try { replayClone = new Bitmap(frame); } catch { replayClone = null; }
                    }

                    var item = new PipeItem(slot, frameId, capStart, padX, padY, scale,
                        frame.Width, frame.Height, barState, capEnd - capStart, prepMs, replayClone,
                        roiX, roiY, fullW, fullH);

                    // latest-wins mailbox: eski pending'i DÜŞÜR (slot'unu iade, klonu dispose) → tüketici hep en taze.
                    lock (_pipeLock)
                    {
                        if (_pending is { } old) { _freeSlots.Push(old.Slot); old.ReplayClone?.Dispose(); }
                        _pending = item;
                    }
                    _pipeSignal.Set();
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex)
                {
                    Program.Log($"[Farm] Üretici hata: {ex.Message}");
                    lock (_pipeLock) { _freeSlots.Push(slot); } // slotu geri ver
                    Thread.Sleep(50);
                }
            }
        }
        catch { /* CT yarışı vb. */ }
        finally { try { screen.Dispose(); } catch { } }
    }

    /// <summary>15 sn'de 1 perf satırı loglar (2026-07-03 telemetri; 2026-07-10 60sn→15sn): Telemetry perf
    /// alanları zaten 1/sn ölçülüyor ama yalnız UI HUD'a gidiyordu — fine-tuning için kalıcı kayıt gerek.
    /// 60 sn kısa oturumda TEK satır bırakıyordu (o da farm-başlangıç ısınma penceresi) → steady-state hiç
    /// görünmüyordu. skip = o aralıkta atlanan bayat kare (d): fps düşükse nedeni ayrışır — skip yüksekse
    /// ekran az kare sunuyor (oyun FPS'i düşük), skip düşük + gpu yüksekse çıkarımın kendisi yavaş.
    /// Hangi tespit döngüsü (seri/pipelined) aktifse yalnız o çağırır → tek throttle alanı yeter.</summary>
    private void MaybeLogPerfLine()
    {
        long now = NowMs();
        if (now - _lastPerfLogMs < 15_000) return;
        _lastPerfLogMs = now;
        int skips = Interlocked.Exchange(ref _staleSkips, 0);
        Program.Log($"[Farm] perf: fps={Telemetry.InferenceFps} skip={skips} yakalama={Telemetry.CaptureMs}ms " +
                    $"çıkarım={Telemetry.InferenceMs}ms (prep={Telemetry.PreprocessMs} gpu={Telemetry.GpuRunMs} " +
                    $"post={Telemetry.PostprocessMs}) bekleme={Telemetry.WaitMs}ms");
    }

    // ── P2: PIPELINED inference — TÜKETİCİ (GPU Run + postprocess + publish) ──
    // Mailbox'tan en taze (slot+meta)'yı alır, InferSlot ile GPU'da çalıştırır, snapshot/overlay/telemetri
    // yayınlar, slotu iade eder. Telemetri tek yerde burada toplanır (Cap/Prep üreticiden PipeItem ile gelir).
    private void InferConsumeLoop(CancellationToken ct)
    {
        long lastFpsTime = NowMs();
        int  inferred = 0;
        long totalCap = 0, totalWait = 0;
        double totalPrep = 0, totalRun = 0, totalPost = 0;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                long now = NowMs();
                if (now - lastFpsTime >= 1000)
                {
                    int denom = Math.Max(1, inferred);
                    Telemetry.InferenceFps  = inferred;
                    Telemetry.CaptureMs     = (int)(totalCap / denom);
                    Telemetry.PreprocessMs  = (int)Math.Round(totalPrep / denom);
                    Telemetry.GpuRunMs      = (int)Math.Round(totalRun  / denom);
                    Telemetry.PostprocessMs = (int)Math.Round(totalPost / denom);
                    Telemetry.InferenceMs   = Telemetry.PreprocessMs + Telemetry.GpuRunMs + Telemetry.PostprocessMs;
                    Telemetry.WaitMs        = (int)(totalWait / denom);
                    totalCap = totalWait = 0; totalPrep = totalRun = totalPost = 0; inferred = 0;
                    lastFpsTime = now;
                    TelemetryUpdated?.Invoke(this, Telemetry);
                    MaybeLogPerfLine();   // 1/dk perf satırı (fine-tuning telemetrisi)
                }

                PipeItem? item;
                lock (_pipeLock) { item = _pending; _pending = null; }
                if (item is null)
                {
                    long w0 = NowMs();
                    _pipeSignal.WaitOne(50);   // üretici Set() ya da 50ms timeout (ct re-check)
                    totalWait += NowMs() - w0;
                    continue;
                }

                var inferrer = Inferrer;
                if (inferrer is null)
                {
                    lock (_pipeLock) { _freeSlots.Push(item.Slot); }
                    item.ReplayClone?.Dispose();
                    continue;
                }

                try
                {
                    var rawDets = inferrer.InferSlot(item.Slot, item.PadX, item.PadY, item.Scale, item.W, item.H);
                    double runMs = inferrer.LastTimings.Run, postMs = inferrer.LastTimings.Postprocess;

                    // P3: ROI-yerel tespitleri ekran-uzayına çevir; snapshot için gerçek ekran boyutunu kullan.
                    var dets = OffsetDets(rawDets, item.RoiX, item.RoiY);
                    // Object tracker: her kutuya kalıcı TrackId + (öldürülmüşse) Dead iliştir (#2/#3/#4).
                    dets = _tracker.Update(dets, NowMs());
                    int snapW = item.FullW > 0 ? item.FullW : item.W;
                    int snapH = item.FullH > 0 ? item.FullH : item.H;

                    _latestDetections = new DetectionSnapshot(
                        item.FrameId, dets, snapW, snapH, item.CapturedAtMs, NowMs(), item.Bar);
                    SignalNewSnapshot();   // 14.tur (Faz 3.2): FarmLoop'u anında uyandır
                    PublishOverlay(dets, _appState.Farm, snapW, snapH);

                    if (item.ReplayClone is { } clone)
                    {
                        var rec = _replay;
                        if (rec is not null) rec.OfferOwned(item.FrameId, item.CapturedAtMs, NowMs(), clone, dets);
                        else clone.Dispose();
                    }

                    totalCap  += (long)item.CapMs;
                    totalPrep += item.PrepMs;
                    totalRun  += runMs;
                    totalPost += postMs;
                    inferred++;
                }
                catch (OperationCanceledException) { break; }
                catch (Exception ex) { Program.Log($"[Farm] Tüketici hata: {ex.Message}"); }
                finally { lock (_pipeLock) { _freeSlots.Push(item.Slot); } } // slotu iade (tek kez)
            }
        }
        catch { /* CT yarışı vb. */ }
        finally
        {
            try { DetectionsUpdated?.Invoke(this, new DetectionFrame(Array.Empty<Detection>(), null, 0, 0)); }
            catch { }
        }
    }
}
