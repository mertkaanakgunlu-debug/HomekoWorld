using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using HomekoWorld.Services.Capture;
using HomekoWorld.ViewModels;

namespace HomekoWorld.Views;

public partial class LogHudWindow : Window
{
    private readonly MainViewModel _vm;

    public LogHudWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;

        // Sürükleme yalnızca başlık çubuğundan
        DragHandle.MouseLeftButtonDown += (_, __) =>
        {
            try { DragMove(); } catch { /* ignore */ }
        };

        // Yeni kayıt geldikçe en alta kaydır
        _vm.FarmActivityLog.CollectionChanged += OnLogChanged;

        // HUD, DXGI/GDI yakalamasına girmesin (tespit alanına denk gelirse modele sızıyordu).
        Loaded += (_, __) => CaptureExclusion.TryExclude(this, "LogHud");
    }

    private bool _scrollQueued;

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action != NotifyCollectionChangedAction.Add) return;

        // KRİTİK: ScrollIntoView'i CollectionChanged'in İÇİNDE çağırmak senkron UpdateLayout
        // tetikler → VirtualizingStackPanel ItemContainerGenerator.Verify() exception fırlatır
        // (koleksiyon değişimi sürerken re-entrant layout). Bu exception App.ShowFatal'ın
        // MessageBox modal pompasıyla özyinelemeye girip STACK OVERFLOW yapıyordu.
        // Çözüm: kaydırmayı koleksiyon değişimi bittikten SONRA, coalesced (kuyrukta tek) yap.
        if (_scrollQueued) return;
        _scrollQueued = true;
        Dispatcher.BeginInvoke(new Action(() =>
        {
            _scrollQueued = false;
            var log = _vm.FarmActivityLog;
            if (log.Count == 0) return;
            try { LogList.ScrollIntoView(log[^1]); }
            catch { /* generator geçici tutarsızlığı — yoksay */ }
        }), System.Windows.Threading.DispatcherPriority.Background);
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
        => _vm.LogHudVisible = false;

    // ── Kapatma koruması: Hide() ile sakla, Destroy() ile gerçekten kapat ──────
    private bool _realClose;

    public void Destroy()
    {
        _realClose = true;
        _vm.FarmActivityLog.CollectionChanged -= OnLogChanged;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_realClose) return;
        e.Cancel = true;
        Hide();
        _vm.LogHudVisible = false;
    }
}
