using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
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

        // 2026-07-13: CaptureExclusion.TryExclude (WDA_EXCLUDEFROMCAPTURE) KALDIRILDI — canlı testte
        // telefon üzerinden uzak masaüstüyle izlenirken HUD hiç görünmüyordu (kök neden: uzak
        // masaüstü görüntülemesi Windows açısından da bir "yakalama"dır, bu bayrak onu da gizler;
        // bilgisayarın başında doğrudan bakınca zaten normal görünüyordu — kod hatası değildi).
        // Bu pencere için exclusion yalnızca kozmetikti (kullanıcının KENDİ ekran kaydında
        // görünmesin) — DetectionOverlayWindow'daki gibi fonksiyonel bir amaç (YOLO'nun kendi
        // kutularını girdi sanması) taşımıyor; kaldırılması artık uzak/telefon izlemede de HUD'un
        // görünür olması gibi bir fayda sağlıyor, maliyeti yalnız kendi ekran kaydında da görünmesi.
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
