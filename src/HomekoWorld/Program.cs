using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
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
        bool rotated = RotateLogIfLarge();

        Log("=== Uygulama başlatılıyor ===");
        if (rotated) Log("[Log] önceki günlük döndürüldü (>2MB) → HomekoWorld_log.prev.txt");

        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
        {
            Log($"[FATAL] {e.ExceptionObject}");
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
            timeEndPeriod(1);
        }
    }

    public static void Log(string msg)
    {
        try
        {
            File.AppendAllText(LogFile,
                $"[{DateTime.Now:HH:mm:ss.fff}] {msg}{Environment.NewLine}");
        }
        catch { }
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
