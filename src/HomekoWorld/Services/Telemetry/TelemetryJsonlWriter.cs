using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Threading;

namespace HomekoWorld.Services.Telemetry;

/// <summary>
/// 14.tur (Faz 4.3): oturum başına ms-telemetri JSONL yazıcısı. <see cref="ReplayRecorder"/> ile
/// AYNI tasarım deseni (bounded queue + arka-plan worker, farm thread'ini asla bloklamaz/geciktirmez)
/// — ama kare/görüntü değil küçük JSON olayları (acq_attempt/engage_end/pop_gate/gate_defer) taşır.
/// Amaç: dış denetimin "ham başarı yüzdesi karışık sinyal ölçüyor" bulgusuna (P1-8) veri kaynağı —
/// 2026-07-13 canlı testte ham "%9" iken gerçek tık-isabeti %84 çıkmıştı; bu ayrımı bir daha piksel/log
/// arkeolojisi gerekmeden, oturum sonrası doğrudan analiz edilebilir bir dosyadan okumak için.
/// </summary>
public sealed class TelemetryJsonlWriter : IDisposable
{
    private readonly string _path;
    private readonly BlockingCollection<string> _queue = new(512);
    private readonly Thread _worker;
    private volatile bool _stopped;

    /// <summary>Bu oturumun JSONL dosya yolu (oturum-başlangıç logunda görünür kılınabilir).</summary>
    public string SessionPath => _path;

    public TelemetryJsonlWriter(string baseDir)
    {
        string stamp = DateTime.Now.ToString("yyyy-MM-dd_HH-mm-ss");
        string dir = Path.Combine(baseDir, "telemetry");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, $"{stamp}.jsonl");
        _worker = new Thread(WorkerProc)
            { IsBackground = true, Name = "TelemetryJsonl", Priority = ThreadPriority.BelowNormal };
        _worker.Start();
    }

    /// <summary>Hazır JSON satırını (tek olay, tırnaksız newline) kuyruğa yazar; asla bloklamaz.
    /// Kuyruk doluysa (worker disk'te takıldıysa) sessizce düşürülür — telemetri kaybı farm
    /// davranışını ASLA etkilemez (ReplayRecorder.Offer ile aynı ilke).</summary>
    public void Offer(string jsonLine)
    {
        if (_stopped) return;
        try { _queue.TryAdd(jsonLine); } catch { }
    }

    private void WorkerProc()
    {
        StreamWriter? writer;
        try { writer = new StreamWriter(_path, append: true) { AutoFlush = false }; }
        catch { writer = null; }

        var sb = new StringBuilder(2048);
        int batched = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            while (!_queue.IsCompleted)
            {
                if (_queue.TryTake(out var line, 100))
                {
                    sb.Append(line).Append('\n');
                    batched++;
                }
                if (batched > 0 && (batched >= 16 || sw.ElapsedMilliseconds >= 200))
                {
                    Flush(writer, sb);
                    batched = 0;
                    sw.Restart();
                }
            }
            if (batched > 0) Flush(writer, sb);
        }
        finally { writer?.Dispose(); }
    }

    private static void Flush(StreamWriter? writer, StringBuilder sb)
    {
        if (writer is not null)
        {
            try { writer.Write(sb.ToString()); writer.Flush(); } catch { }
        }
        sb.Clear();
    }

    public void Dispose()
    {
        if (_stopped) return;
        _stopped = true;
        try { _queue.CompleteAdding(); } catch { }
        try { _worker.Join(2000); } catch { }
        try { _queue.Dispose(); } catch { }
    }
}
