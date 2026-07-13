using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace HomekoWorld;

public static class Program
{
    [DllImport("winmm.dll")] private static extern uint timeBeginPeriod(uint uPeriod);
    [DllImport("winmm.dll")] private static extern uint timeEndPeriod(uint uPeriod);

    private static readonly string LogFile = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop),
        "HomekoWorld_log.txt");

    // ── 14.tur (Faz 3.1): async log writer ────────────────────────────────────────────────────
    // ESKİ: her Log() çağrısı senkron File.AppendAllText (aç-yaz-kapat) yapıyordu. Hedefleme sıcak
    // yolunda tık başına 1-3 satır yazılıyor (Targeting/Combat/tarama-teshis) — disk/antivirüs/dosya
    // cache'i geciktiğinde farm thread'i de bekliyordu (ortalama FPS'i değil, P95/P99 tıklama
    // gecikmesini rastgele sıçratıyordu; müşteri PC'lerinde antivirüs daha da belirgin olabilir).
    // YENİ: kuyruğa yaz (asla bloklamaz) → tek arka-plan writer thread'i batch halinde diske yazar.
    private const int LogQueueCapacity = 2048;
    private const int LogBatchLines    = 32;
    private const int LogBatchMs       = 100;
    private static readonly BlockingCollection<string> _logQueue = new(LogQueueCapacity);
    private static Thread? _logWriterThread;
    private static long _logDropped;      // Interlocked — kuyruk dolduğunda düşen (en eski) satır sayısı
    private static int  _writerStopped;   // Interlocked guard — StopLogWriter idempotent olsun

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetDllDirectory(string lpPathName);

    [STAThread]
    public static void Main(string[] args)
    {
        // Fix for Single-File Publish: Ensure DLLs extracted to AppContext.BaseDirectory can be loaded
        // by ONNX Runtime native libraries that are extracted to %TEMP%.
        SetDllDirectory(AppContext.BaseDirectory);

        // 1 ms scheduler resolution — required for Thread.Sleep(1) accuracy in LocalInputTransport
        timeBeginPeriod(1);

        // Makro thread'i biraz daha yüksek zamanlama katmanında yarışsın diye süreç önceliği.
        // High → AboveNormal (A4): High, OBS + oyun + DirectML inference birlikteyken sistemin
        // (DWM/compositor dahil) geri kalanını aç bırakıp imleç takılmasını/donmayı şiddetlendiriyordu.
        // AboveNormal yeterli avantajı verir, sistemi boğmaz. Kritik input worker zaten Highest.
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

        // Log rotasyonu: detaylı telemetri (2026-07-03) dosyayı saatte ~2-4MB büyütebilir — sınırsız
        // büyüme yerine her açılışta >2MB ise .prev'e devril (tek nesil yeter: önceki oturum incelenebilir).
        bool rotated = RotateLogIfLarge();   // rotasyon writer başlamadan ÖNCE (dosya taşınacaksa açık olmamalı)
        StartLogWriter();

        Log("=== Uygulama başlatılıyor ===");
        if (rotated) Log("[Log] önceki günlük döndürüldü (>2MB) → HomekoWorld_log.prev.txt");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"[FATAL] {e.ExceptionObject}");
            StopLogWriter();   // çökmeden önce kuyruktaki satırlar diske insin (MessageBox bekletebilir)
            MessageBox.Show(e.ExceptionObject?.ToString(), "Kritik Hata",
                MessageBoxButton.OK, MessageBoxImage.Error);
        };

        try
        {
            Log("App nesnesi oluşturuluyor...");
            var app = new App();
            Log("InitializeComponent() çağrılıyor...");
            app.InitializeComponent();   // ← App.xaml kaynaklarını ve Startup event'ini yükler
            Log("App.Run() çağrılıyor...");
            app.Run();
            Log("App.Run() tamamlandı.");
        }
        catch (Exception ex)
        {
            Log($"[EXCEPTION] {ex}");
            MessageBox.Show(ex.ToString(), "Başlatma Hatası",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            StopLogWriter();   // kapanışta flush garantisi (queue.CompleteAdding + writer thread Join)
            timeEndPeriod(1);
        }
    }

    public static void Log(string msg)
    {
        string line = $"[{DateTime.Now:HH:mm:ss.fff}] {msg}";
        try
        {
            if (_logQueue.TryAdd(line)) return;
            // Kuyruk dolu (writer disk/AV'da takıldı ya da hiç başlamadı) → en eski satırı düşür,
            // yeniyi ekle (drop-oldest: en taze teşhis en değerlisi). Nadiren gerçekleşir (2048
            // kapasite, tick başına birkaç satır); zararı yalnız eski bir log satırının kaybı.
            if (_logQueue.TryTake(out _)) Interlocked.Increment(ref _logDropped);
            _logQueue.TryAdd(line);
        }
        catch (InvalidOperationException)
        {
            // CompleteAdding sonrası (kapanış sırası) gelen geç bir Log çağrısı — sessizce yok say.
        }
    }

    private static void StartLogWriter()
    {
        _logWriterThread = new Thread(LogWriterLoop)
            { IsBackground = true, Priority = ThreadPriority.BelowNormal, Name = "LogWriter" };
        _logWriterThread.Start();
    }

    /// <summary>Kuyruğu kapatır ve writer thread'in kalan satırları yazıp çıkmasını bekler (en fazla
    /// 2sn). İdempotent — hem UnhandledException hem Main.finally çağırabilir, ikinci çağrı no-op.</summary>
    private static void StopLogWriter()
    {
        if (Interlocked.Exchange(ref _writerStopped, 1) != 0) return;
        try { _logQueue.CompleteAdding(); } catch { }
        try { _logWriterThread?.Join(2000); } catch { }
    }

    private static void LogWriterLoop()
    {
        StreamWriter? writer;
        try { writer = new StreamWriter(LogFile, append: true) { AutoFlush = false }; }
        catch { writer = null; }   // dosya açılamadıysa (kilit/izin) — bu oturumda log diske düşmez

        var sb  = new StringBuilder(4096);
        int     batched = 0;
        var     sw      = Stopwatch.StartNew();
        try
        {
            // BlockingCollection tükenene kadar (CompleteAdding + kuyruk boşalana kadar) döner.
            // TryTake(100ms) hem yeni satır bekler hem de LogBatchMs zaman-aşımı flush'ını mümkün kılar.
            while (!_logQueue.IsCompleted)
            {
                if (_logQueue.TryTake(out var line, LogBatchMs))
                {
                    sb.Append(line).Append(Environment.NewLine);
                    batched++;
                }
                if (batched > 0 && (batched >= LogBatchLines || sw.ElapsedMilliseconds >= LogBatchMs))
                {
                    FlushBatch(writer, sb);
                    batched = 0;
                    sw.Restart();
                }
            }
            if (batched > 0) FlushBatch(writer, sb);
        }
        finally { writer?.Dispose(); }
    }

    private static void FlushBatch(StreamWriter? writer, StringBuilder sb)
    {
        if (writer is not null)
        {
            try { writer.Write(sb.ToString()); writer.Flush(); } catch { }
        }
        sb.Clear();
    }

    /// <summary>Açılışta günlük >2MB ise HomekoWorld_log.prev.txt'e taşır (öncekinin üstüne yazar).
    /// true = rotasyon yapıldı. Sessiz başarısızlık: logger henüz güvenilir değilken hata bildirilemez.</summary>
    private static bool RotateLogIfLarge()
    {
        try
        {
            var fi = new FileInfo(LogFile);
            if (!fi.Exists || fi.Length <= 2 * 1024 * 1024) return false;
            string prev = Path.Combine(fi.DirectoryName!, "HomekoWorld_log.prev.txt");
            if (File.Exists(prev)) File.Delete(prev);
            File.Move(LogFile, prev);
            return true;
        }
        catch { return false; }
    }
}
