using System.Collections.Concurrent;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Services.Telemetry;

/// <summary>
/// Faz B — Replay/benchmark kaydedici. Açıkken (<see cref="FarmSettings.ReplayEnabled"/>) tespit
/// thread'inden throttle'lı gerçek oyun karesi + tespit metadata'sı alır ve AYRI bir worker
/// thread'inde diske yazar (jpg + frames.ndjson + session.json). Amaç: aynı kareler üzerinde
/// model/runtime/conf/iou'yu OBJEKTİF karşılaştırmak (offline: tools/yolo_trainer/src/replay_benchmark.py).
///
/// Tasarım kararı — detection loop'u ASLA bloklamaz/yavaşlatmaz:
///  • <see cref="Offer"/> kareyi klonlar ve BOUNDED kuyruğa atar; kuyruk doluysa kareyi DÜŞÜRÜR.
///  • Encode + disk I/O yalnız worker thread'inde (BelowNormal öncelik).
///  • Tüm I/O try/catch — farm'a asla exception sızdırmaz.
///  • <see cref="FarmSettings.ReplayMaxFrames"/> soft cap → uzun oturumda disk taşmaz.
/// </summary>
public sealed class ReplayRecorder : IDisposable
{
    private readonly string _sessionDir;
    private readonly string _framesDir;
    private readonly string _ndjsonPath;
    private readonly int    _maxFrames;
    private readonly BlockingCollection<Item> _queue;
    private readonly Thread _worker;
    private readonly ImageCodecInfo   _jpegCodec;
    private readonly EncoderParameters _encParams;
    private volatile int  _written;
    private volatile bool _stopped;

    private sealed record Item(long FrameId, long CapturedAtMs, long PublishedAtMs,
                               Bitmap Frame, IReadOnlyList<Detection> Dets);

    /// <summary>Bu oturumun kayıt klasörü (replays/&lt;zaman&gt;).</summary>
    public string SessionDir   => _sessionDir;
    /// <summary>Diske yazılmış kare sayısı.</summary>
    public int    FramesWritten => _written;

    public ReplayRecorder(string baseDir, int maxFrames, int jpegQuality, string sessionInfoJson)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        _sessionDir = Path.Combine(baseDir, "replays", stamp);
        _framesDir  = Path.Combine(_sessionDir, "frames");
        _ndjsonPath = Path.Combine(_sessionDir, "frames.ndjson");
        _maxFrames  = Math.Max(1, maxFrames);
        Directory.CreateDirectory(_framesDir);
        File.WriteAllText(Path.Combine(_sessionDir, "session.json"), sessionInfoJson, Encoding.UTF8);

        _jpegCodec = ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);
        _encParams = new EncoderParameters(1);
        _encParams.Param[0] = new EncoderParameter(
            System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(jpegQuality, 30, 95));

        _queue  = new BlockingCollection<Item>(boundedCapacity: 8);
        _worker = new Thread(WorkerProc)
            { IsBackground = true, Name = "ReplayRecorder", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>
    /// Tespit thread'inden çağrılır. Kareyi KLONLAR (buffer bir sonraki turda yeniden kullanılır) ve
    /// kuyruğa atar; kuyruk doluysa veya cap dolduysa kareyi sessizce DÜŞÜRÜR (asla bloklamaz/fırlatmaz).
    /// </summary>
    public void Offer(long frameId, long capturedAtMs, long publishedAtMs,
                      Bitmap frame, IReadOnlyList<Detection> dets)
    {
        if (_stopped || _written >= _maxFrames) return;
        Bitmap clone;
        try { clone = new Bitmap(frame); } catch { return; } // klon başarısızsa kareyi atla
        try
        {
            if (!_queue.TryAdd(new Item(frameId, capturedAtMs, publishedAtMs, clone, dets)))
                clone.Dispose(); // kuyruk dolu → düşür (loop'u bekletmektense kareyi feda et)
        }
        catch { clone.Dispose(); } // CompleteAdding yarışı (normalde olmaz: detection thread Stop'tan önce join'lenir)
    }

    /// <summary>
    /// P2 pipeline yolu: üretici kareyi ZATEN klonladı (capture buffer'ı tüketiciye geçemez) → yeniden
    /// klonlama, sahipliği AL (yaz + dispose). Tüketici dets ile birlikte çağırır.
    /// </summary>
    public void OfferOwned(long frameId, long capturedAtMs, long publishedAtMs,
                           Bitmap ownedFrame, IReadOnlyList<Detection> dets)
    {
        if (_stopped || _written >= _maxFrames) { ownedFrame.Dispose(); return; }
        try
        {
            if (!_queue.TryAdd(new Item(frameId, capturedAtMs, publishedAtMs, ownedFrame, dets)))
                ownedFrame.Dispose();
        }
        catch { ownedFrame.Dispose(); }
    }

    private void WorkerProc()
    {
        // 14.tur (Faz 5.1 — dış denetim P1-11 bulgusu): foreach'in KENDİSİ eskiden dış try-catch
        // dışındaydı. Stop() 3sn'de kapanmayan worker'ı beklerken çağıranın Dispose()'u _queue'yu
        // dispose edebiliyordu (bkz Dispose() koşullu düzeltmesi) — eğer bu ARADA yine de olursa
        // GetConsumingEnumerable() ObjectDisposedException fırlatabilir; arka-plan thread'inde
        // yakalanmamış istisna .NET runtime'ında PROCESS'İ ÇÖKERTİR. Tüm gövde artık korunuyor.
        try
        {
            var sb = new StringBuilder(512);
            foreach (var it in _queue.GetConsumingEnumerable())
            {
                try
                {
                    if (_written < _maxFrames)
                    {
                        string jpg = Path.Combine(_framesDir, $"{it.FrameId:D6}.jpg");
                        it.Frame.Save(jpg, _jpegCodec, _encParams);
                        File.AppendAllText(_ndjsonPath, BuildNdjsonLine(sb, it), Encoding.UTF8);
                        _written++;
                    }
                }
                catch { /* I/O hatası → bu kareyi atla, kaydı sürdür */ }
                finally { it.Frame.Dispose(); }
            }
        }
        catch { /* queue dispose yarışı / beklenmedik hata — worker'ı sessizce bitir, farm'ı etkileme */ }
    }

    private static string BuildNdjsonLine(StringBuilder sb, Item it)
    {
        sb.Clear();
        sb.Append("{\"frameId\":").Append(it.FrameId)
          .Append(",\"capturedAtMs\":").Append(it.CapturedAtMs)
          .Append(",\"publishedAtMs\":").Append(it.PublishedAtMs)
          .Append(",\"file\":\"frames/").Append(it.FrameId.ToString("D6")).Append(".jpg\"")
          .Append(",\"dets\":[");
        for (int i = 0; i < it.Dets.Count; i++)
        {
            var d = it.Dets[i];
            if (i > 0) sb.Append(',');
            sb.Append("{\"cls\":").Append(d.ClassId)
              .Append(",\"name\":\"").Append(Escape(d.ClassName)).Append('"')
              .Append(",\"conf\":").Append(F(d.Confidence))
              .Append(",\"x\":").Append(F(d.BBox.X)).Append(",\"y\":").Append(F(d.BBox.Y))
              .Append(",\"w\":").Append(F(d.BBox.Width)).Append(",\"h\":").Append(F(d.BBox.Height))
              .Append('}');
        }
        sb.Append("]}\n");
        return sb.ToString();
    }

    private static string F(float v) => v.ToString("0.###", CultureInfo.InvariantCulture);
    private static string Escape(string s) => s.Replace("\\", "\\\\").Replace("\"", "\\\"");

    // 14.tur (Faz 5.1): Join(3000) zaman aşımına uğradıysa worker HÂLÂ diske yazıyor olabilir
    // (yavaş disk/AV). Bu durumda _queue/_encParams DISPOSE EDİLMEZ — worker'ın altındaki koleksiyonu
    // çekmek ObjectDisposedException ile thread'i (ve process'i) çökertebilirdi (bkz WorkerProc notu).
    // Sonuç: nadir zaman-aşımı vakasında bir miktar bellek/handle sızıntısı — çökmeden EVLA tercih.
    private volatile bool _workerAbandoned;

    /// <summary>Kaydı bitirir: kuyruğu kapatır, worker'ı bekler, kalan klonları temizler (worker
    /// zamanında bittiyse). Zaman aşımında "abandoned" işaretlenir — Dispose() bu durumda queue'ya
    /// dokunmaz.</summary>
    public void Stop()
    {
        if (_stopped) return;
        _stopped = true;
        try { _queue.CompleteAdding(); } catch { }
        bool finished = false;
        try { finished = _worker.Join(3000); } catch { }
        if (!finished) { _workerAbandoned = true; return; }
        while (_queue.TryTake(out var it)) it.Frame.Dispose(); // worker erken çıktıysa sızıntı yok
    }

    public void Dispose()
    {
        Stop();
        if (_workerAbandoned)
        {
            Program.Log("[Replay] worker 3sn içinde kapanmadı — kaynaklar bu oturum için terk edildi " +
                        "(çökme riskinden kaçınmak için queue/encParams dispose edilmedi).");
            return;
        }
        try { _queue.Dispose(); }   catch { }
        try { _encParams.Dispose(); } catch { }
    }
}
