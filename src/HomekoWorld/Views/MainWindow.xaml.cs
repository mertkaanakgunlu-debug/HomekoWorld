using System.ComponentModel;
using System.Windows;
using System.Windows.Shell;
using Microsoft.Extensions.DependencyInjection;
using HomekoWorld.Engine;
using HomekoWorld.ViewModels;
using HomekoWorld.Services.Vision;

namespace HomekoWorld.Views;

public partial class MainWindow : Window
{
    private FarmHudWindow?          _farmHud;
    private LogHudWindow?           _logHud;
    private DetectionOverlayWindow? _overlayWindow;

    public MainWindow()
    {
        InitializeComponent();

        var chrome = new WindowChrome
        {
            ResizeBorderThickness = new Thickness(6),
            CaptionHeight = 0,
            CornerRadius = new CornerRadius(0),
            GlassFrameThickness = new Thickness(0),
            UseAeroCaptionButtons = false,
        };
        WindowChrome.SetWindowChrome(this, chrome);

        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;

        vm.PropertyChanged += (_, args) =>
        {
            switch (args.PropertyName)
            {
                case nameof(MainViewModel.FarmHudVisible):
                    SyncFarmHud(vm);
                    break;
                case nameof(MainViewModel.LogHudVisible):
                    SyncLogHud(vm);
                    break;
                case nameof(MainViewModel.FarmShowDetectionOverlay):
                    SyncDetectionOverlay(vm);
                    break;
            }
        };

        if (vm.FarmHudVisible)
            SyncFarmHud(vm);

        if (vm.LogHudVisible)
            SyncLogHud(vm);

        if (vm.FarmShowDetectionOverlay)
            SyncDetectionOverlay(vm);
    }

    // Birleşik Ana HUD (combo overlay + farm tek pencerede)
    private void SyncFarmHud(MainViewModel vm)
    {
        if (vm.FarmHudVisible)
        {
            if (_farmHud is null)
            {
                _farmHud = new FarmHudWindow(vm);
                // Sağ alt köşeye konumlandır
                var screen = System.Windows.SystemParameters.WorkArea;
                _farmHud.Left = screen.Right  - 240;
                _farmHud.Top  = screen.Bottom - 180;
            }
            _farmHud.Show();
        }
        else
        {
            _farmHud?.Hide();
        }
    }

    // Ayrı Log HUD — Ana HUD'un üstünde konumlanır, ikisi aynı anda izlenir
    private void SyncLogHud(MainViewModel vm)
    {
        if (vm.LogHudVisible)
        {
            if (_logHud is null)
            {
                _logHud = new LogHudWindow(vm);
                var screen = System.Windows.SystemParameters.WorkArea;
                _logHud.Left = screen.Right  - 320;
                _logHud.Top  = System.Math.Max(0, screen.Bottom - 460);
            }
            _logHud.Show();
        }
        else
        {
            _logHud?.Hide();
        }
    }

    // YOLO tespit kutuları overlay'i — tam ekran, click-through; YOLO yalnızca farm
    // çalışırken Infer ettiği için kutular Başlat'a basıldığında görünür.
    private void SyncDetectionOverlay(MainViewModel vm)
    {
        if (vm.FarmShowDetectionOverlay)
        {
            if (_overlayWindow is null)
            {
                var engine = App.Services.GetRequiredService<FarmEngine>();
                _overlayWindow = new DetectionOverlayWindow(engine);
                _overlayWindow.Owner = this; // Hide from Alt+Tab
            }
            _overlayWindow.ActivateOverlay();
        }
        else
        {
            _overlayWindow?.DeactivateOverlay();
        }
    }

    protected override void OnClosed(EventArgs e)
    {
        _farmHud?.Destroy();
        _logHud?.Destroy();
        _overlayWindow?.Close();
        base.OnClosed(e);
        System.Windows.Application.Current.Shutdown();
    }
}
