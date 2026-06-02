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

    [STAThread]
    public static void Main(string[] args)
    {
        // 1 ms scheduler resolution — required for Thread.Sleep(1) accuracy in LocalInputTransport
        timeBeginPeriod(1);

        // Makro thread'i biraz daha yüksek zamanlama katmanında yarışsın diye süreç önceliği.
        // High → AboveNormal (A4): High, OBS + oyun + DirectML inference birlikteyken sistemin
        // (DWM/compositor dahil) geri kalanını aç bırakıp imleç takılmasını/donmayı şiddetlendiriyordu.
        // AboveNormal yeterli avantajı verir, sistemi boğmaz. Kritik input worker zaten Highest.
        Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal;

        Log("=== Uygulama başlatılıyor ===");

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
}
