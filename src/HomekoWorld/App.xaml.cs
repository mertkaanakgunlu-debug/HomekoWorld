using System.Diagnostics;
using System.Linq;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Extensions.DependencyInjection;
using HomekoWorld.Engine;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Services;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Skills;
using HomekoWorld.ViewModels;

namespace HomekoWorld;

public partial class App : Application
{
    public static IServiceProvider Services { get; private set; } = null!;

    protected override void OnStartup(StartupEventArgs e)
    {
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
            ShowFatal(args.ExceptionObject?.ToString() ?? "Bilinmeyen hata");

        DispatcherUnhandledException += (_, args) =>
        {
            args.Handled = true;
            ShowFatal(args.Exception.ToString());
        };

        base.OnStartup(e);
    }

    private void Application_Startup(object sender, StartupEventArgs e)
    {
        Program.Log("Application_Startup girdi");

        // WPF binding hatalarını log'a yönlendir → sessiz binding kırıkları (örn. silinmiş
        // bir property'ye binding) smoke testinde [BINDING] olarak görünür, çökme beklemeden yakalanır.
        PresentationTraceSources.Refresh();
        PresentationTraceSources.DataBindingSource.Listeners.Add(new BindingErrorListener());
        PresentationTraceSources.DataBindingSource.Switch.Level = SourceLevels.Error | SourceLevels.Warning;

        try
        {
            // Pre-MainWindow diyalog (CUDA indirme penceresi) kapanınca varsayılan OnLastWindowClose ile
            // uygulama kapanıp MainWindow oluşturulamıyordu ("Application nesnesi kapatılıyor"). MainWindow
            // gösterilene kadar explicit shutdown'a al; sonra OnMainWindowClose'a geçilir.
            this.ShutdownMode = System.Windows.ShutdownMode.OnExplicitShutdown;

            try
            {
                bool isNvidia = HomekoWorld.Services.Yolo.CudaInstallerService.IsNvidiaGpu();
                bool isMissing = HomekoWorld.Services.Yolo.CudaInstallerService.IsMissingLibraries();

                if (isNvidia && isMissing)
                {
                    var cudaWindow = new Views.CudaDownloadWindow();
                    cudaWindow.ShowDialog();
                    // İndirme reddedildiyse veya başarısız olduysa CPU modunda devam eder
                }
            }
            catch (Exception cudaEx)
            {
                // CUDA kontrol/indirme penceresi başlatmayı ASLA engellememeli → logla, CPU/DirectML ile devam.
                Program.Log($"[CUDA] pencere atlandı: {cudaEx.Message}");
            }

            Program.Log("DI services kuruluyor...");
            var services = new ServiceCollection();
            ConfigureServices(services);
            Services = services.BuildServiceProvider();

            // Load skill library on startup (non-blocking; empty if no assets yet)
            var lib = Services.GetRequiredService<SkillLibrary>();
            try { lib.Load(); } catch { /* assets not provided yet — silent */ }

            Program.Log("MainViewModel resolve ediliyor...");
            var mainVm = Services.GetRequiredService<MainViewModel>();

            Program.Log("MainWindow oluşturuluyor...");
            MainWindow = new Views.MainWindow { DataContext = mainVm };

            Program.Log("MainWindow.Show() çağrılıyor...");
            MainWindow.Show();
            // Ana pencere açıldı → normal kapanış semantiği: ana pencere kapanınca uygulama çıkar.
            this.ShutdownMode = System.Windows.ShutdownMode.OnMainWindowClose;
            Program.Log("Pencere gösterildi.");

            // Smoke testi: --smoke argümanıyla başlatılınca pencere açılır, kısa süre sonra
            // kendini kapatır. requireAdministrator olduğu için dışarıdan kill edilemez →
            // kendi kendine Shutdown ile temiz kapanış (otomatik boot-doğrulaması için).
            if (Environment.GetCommandLineArgs().Any(a => a == "--smoke"))
            {
                Program.Log("[SMOKE] Smoke modu — 8 sn sonra otomatik kapanacak");
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(8) };
                timer.Tick += (_, _) => { timer.Stop(); Program.Log("[SMOKE] Otomatik kapanıyor"); Shutdown(0); };
                timer.Start();
            }
        }
        catch (Exception ex)
        {
            Program.Log($"[EXCEPTION in Startup] {ex}");
            ShowFatal(ex.ToString());
        }
    }

    // Re-entrancy guard: tek bir fatal diyalog. MessageBox modal mesaj pompası, layout sırasında
    // oluşan bir exception'ı yeniden tetikleyip ShowFatal'ı özyinelemeli çağırarak STACK OVERFLOW
    // yapıyordu. Guard ile en fazla bir diyalog gösterilir → özyineleme imkânsız.
    private static bool _fatalShown;
    private void ShowFatal(string message)
    {
        if (_fatalShown) return;
        _fatalShown = true;
        Program.Log($"[FATAL] {message}");
        try
        {
            MessageBox.Show(message, "Hata",
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* diyalog gösterilemezse bile sessizce kapat */ }
        Shutdown(1);
    }

    private static void ConfigureServices(IServiceCollection services)
    {
        // Infrastructure — two concrete transports (Local + HidBridge) + a router
        services.AddSingleton<HidBridgeClient>();
        services.AddSingleton<LocalInputTransport>();
        services.AddSingleton<Rp2040HidTransport>();
        services.AddSingleton<TransportRouter>(sp => new TransportRouter(
            sp.GetRequiredService<HidBridgeClient>(),
            sp.GetRequiredService<LocalInputTransport>(),
            sp.GetRequiredService<Rp2040HidTransport>()));
        services.AddSingleton<IKeyDeviceTransport>(sp => sp.GetRequiredService<TransportRouter>());

        services.AddSingleton<GlobalKeyboardHook>();
        services.AddSingleton<GlobalMouseHook>();
        services.AddSingleton<JsonStateStore>();

        // AppState as singleton — loaded once from store, shared across all services
        services.AddSingleton<AppState>(sp => sp.GetRequiredService<JsonStateStore>().Load());

        // Skill bar services
        services.AddSingleton<SkillLibrary>();
        services.AddSingleton<SkillRecognizer>();
        services.AddSingleton<SkillBarResolver>();
        services.AddSingleton<ISkillResolver>(sp => sp.GetRequiredService<SkillBarResolver>());

        // Engine — receives IKeyDeviceTransport (= TransportRouter) via constructor
        services.AddSingleton<ComboEngine>(sp => new ComboEngine(
            sp.GetRequiredService<IKeyDeviceTransport>(),
            sp.GetRequiredService<ISkillResolver>()));

        services.AddSingleton<WalkToMobEngine>(sp => new WalkToMobEngine(
            sp.GetRequiredService<ComboEngine>(),
            sp.GetRequiredService<GlobalMouseHook>(),
            sp.GetRequiredService<GlobalKeyboardHook>(),
            sp.GetRequiredService<AppState>(),
            sp.GetRequiredService<IKeyDeviceTransport>()));

        // Faz 17 — Farm
        services.AddSingleton<MobLibrary>();
        services.AddSingleton<FarmEngine>();

        // Faz 18 — Global AutoPot
        services.AddSingleton<AutoPotService>();

        // Faz 32 — Koordinat okuma (glyph eşleştirmeli rakam tanıma)
        services.AddSingleton<Services.Autonomous.GlyphDigitReader>();
        services.AddSingleton<Services.Autonomous.CoordinateReader>();

        // Faz 34 — WorldNavigator (probe-correct koordinat navigasyonu)
        services.AddSingleton<Services.Autonomous.WorldNavigator>();

        // Faz 33 — InventoryReader (envanter doluluğu tarama)
        services.AddSingleton<Services.Autonomous.InventoryReader>();

        // Faz 36 — MerchantTrader (NPC etkileşimi ve satış)
        services.AddSingleton<Services.Autonomous.MerchantTrader>();

        // Faz 31 — Otonom Oyuncu orkestratörü (FarmEngine üstünde FSM; WorldNavigator bağımlı)
        services.AddSingleton<AutonomousPlayerEngine>();

        // Dispatchers & VMs
        services.AddSingleton<BindingDispatcher>();
        services.AddSingleton<ComboEditorViewModel>();
        services.AddSingleton<MainViewModel>();
        services.AddTransient<TestRunnerViewModel>();
        services.AddTransient<Views.TestRunnerWindow>();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (Services is IDisposable d) d.Dispose();
        base.OnExit(e);
    }

    // WPF binding trace çıktısını Program.Log'a yazar (smoke testinde [BINDING] olarak görünür).
    private sealed class BindingErrorListener : TraceListener
    {
        public override void Write(string? message) { }
        public override void WriteLine(string? message)
        {
            if (!string.IsNullOrWhiteSpace(message))
                Program.Log($"[BINDING] {message}");
        }
    }
}
