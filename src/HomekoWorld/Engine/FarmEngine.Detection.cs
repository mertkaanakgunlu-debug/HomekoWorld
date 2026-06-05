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
        int frames = 0;

        // Ekran kaynağı bu thread'de kurulur (DXGI/D3D11 device tek-thread kullanımı için kritik).
        IScreenSource screen = CreateScreenSource(_appState.Farm);
        Program.Log($"[Farm] Ekran yakalama yöntemi: {screen.BackendName}");
        try
        {
            long totalCapMs = 0;
            long totalInfMs = 0;
            long totalWaitMs = 0;

            while (!ct.IsCancellationRequested)
            {
                frameSw.Restart();
                frames++;
                long now = NowMs();
                if (now - lastFpsTime >= 1000)
                {
                    Telemetry.InferenceFps = frames;
                    
                    if (frames > 0)
                    {
                        Program.Log($"[DIAG] FPS: {frames} | AvgCap: {totalCapMs/frames}ms | AvgInf: {totalInfMs/frames}ms | AvgWait: {totalWaitMs/frames}ms");
                    }
                    totalCapMs = 0;
                    totalInfMs = 0;
                    totalWaitMs = 0;
                    
                    frames = 0;
                    lastFpsTime = now;
                    TelemetryUpdated?.Invoke(this, Telemetry);
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
                    var dets = inferrer.Infer(frame);
                    long infEnd = NowMs();

                    totalCapMs += (capEnd - capStart);
                    totalInfMs += (infEnd - capEnd);

                    bool? targetAliveHsv = null;
                    var wtm = _appState.Wtm;
                    if (wtm.HpBarMode == HpBarDetectionMode.Hsv && wtm.IsHpBarLocated)
                        targetAliveHsv = WtmVision.IsTargetAliveByHsvFromFrame(frame, wtm);

                    _latestDetections = new DetectionSnapshot(
                        dets, frame.Width, frame.Height, NowMs(), targetAliveHsv);

                    PublishOverlay(dets, s, frame.Width, frame.Height);

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
    /// </summary>
    private static IScreenSource CreateScreenSource(FarmSettings s)
    {
        if (s.CaptureBackend == CaptureBackend.Dxgi)
        {
            try { return new DxgiScreenSource(); }
            catch (Exception ex)
            {
                Program.Log($"[Farm] DXGI yakalama başlatılamadı — GDI'ye düşülüyor: {ex.Message}");
            }
        }
        return new GdiScreenSource();
    }

    // Seçili mob türü kutuları + (combat'te) vurgulu hedef. Hedefi geçerli kareden eşler
    // ki overlay ReferenceEquals ile çift kutuyu atlasın.
    private void PublishOverlay(IReadOnlyList<Detection> dets, FarmSettings s, int w, int h)
    {
        var handler = DetectionsUpdated;
        if (handler is null) return; // abone yok → çizim maliyeti sıfır

        var mob   = _mobLibrary.FindByName(s.SelectedMobName);
        var shown = mob is null ? dets : dets.Where(d => d.ClassId == mob.Id).ToList();

        Detection? tgt = _currentTargetForOverlay;
        if (tgt is not null)
        {
            var match = shown
                .Where(d => d.ClassId == tgt.ClassId)
                .OrderBy(d => d.DistanceTo(tgt.Center))
                .FirstOrDefault();
            if (match is not null) tgt = match;
        }

        handler.Invoke(this, new DetectionFrame(shown, tgt, w, h));
    }
}
