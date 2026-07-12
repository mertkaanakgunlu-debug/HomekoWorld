using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using HomekoWorld.Services.Capture;
using HomekoWorld.ViewModels;

namespace HomekoWorld.Views;

public partial class FarmHudWindow : Window
{
    private readonly MainViewModel _vm;

    public FarmHudWindow(MainViewModel vm)
    {
        InitializeComponent();
        DataContext = vm;
        _vm = vm;

        // Sürükleme yalnızca üst tutacaktan (kontroller serbest tıklanabilsin)
        DragHandle.MouseLeftButtonDown += (_, __) =>
        {
            try { DragMove(); } catch { /* ignore */ }
        };

        // HUD, DXGI/GDI yakalamasına girmesin (tespit alanına denk gelirse modele sızıyordu).
        Loaded += (_, __) => CaptureExclusion.TryExclude(this, "FarmHud");
    }

    // ── Kapatma koruması: Hide() ile sakla, Destroy() ile gerçekten kapat ──────
    private bool _realClose;

    public void Destroy()
    {
        _realClose = true;
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_realClose) return;
        e.Cancel = true;
        Hide();
        if (_vm is not null)
            _vm.FarmHudVisible = false;
    }
}
