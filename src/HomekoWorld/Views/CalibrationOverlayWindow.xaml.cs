using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HomekoWorld.Views;

public partial class CalibrationOverlayWindow : Window
{
    private System.Windows.Point _start;
    private bool _drawing;
    private readonly bool _singleClickMode;
    private readonly bool _twoCorner;                 // iki-köşe modu: 1. tık köşe A, 2. tık karşı köşe
    private bool _twoCornerArmed;                      // köşe A yerleştirildi mi
    private readonly System.Drawing.Size? _fixedSize; // fiziksel piksel cinsinden sabit boyut
    private double _fixedWDip, _fixedHDip;            // WPF DIP karşılığı
    private System.Windows.Point _lastMousePos;

    public System.Drawing.Rectangle SelectedRect { get; private set; }
    public bool Confirmed { get; private set; }

    /// <param name="instruction">Özel talimat metni.</param>
    /// <param name="singleClickMode">true = ilk tıklamada onaylar.</param>
    /// <param name="fixedSize">Sabit fiziksel piksel boyutu — sürükle-bırak yerine fareyi takip eden sabit dikdörtgen.</param>
    /// <param name="twoCorner">true = iki-köşe modu: 1. tık sol-üst köşe, 2. tık karşı (sağ-alt) köşe; dikdörtgen ikisinin sınır kutusudur.</param>
    public CalibrationOverlayWindow(string? instruction = null, bool singleClickMode = false,
                                    System.Drawing.Size? fixedSize = null, bool twoCorner = false)
    {
        _singleClickMode = singleClickMode;
        _fixedSize       = fixedSize;
        _twoCorner       = twoCorner;
        InitializeComponent();

        string suffix = twoCorner
            ? "  İlk köşe → karşı köşe  |  İptal: Escape"
            : fixedSize.HasValue
                ? "  |  İptal: Escape"
                : singleClickMode
                    ? "  |  İptal: Escape"
                    : "  Onay: bırak veya Enter  |  İptal: Escape";

        if (instruction is not null)
            InstructionText.Text = instruction + suffix;

        if (fixedSize.HasValue)
            Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        var (dpiX, dpiY) = GetDpiScale();
        _fixedWDip = _fixedSize!.Value.Width  / dpiX;
        _fixedHDip = _fixedSize!.Value.Height / dpiY;
        SelectionRect.Width     = _fixedWDip;
        SelectionRect.Height    = _fixedHDip;
        SelectionRect.Visibility = Visibility.Visible;
    }

    private void Canvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (_twoCorner)
        {
            var pos = e.GetPosition(DrawCanvas);
            if (!_twoCornerArmed)
            {
                // 1. tık: köşe A'yı sabitle, canlı dikdörtgeni başlat.
                _start = pos;
                _twoCornerArmed = true;
                SelectionRect.Visibility = Visibility.Visible;
                Canvas.SetLeft(SelectionRect, pos.X);
                Canvas.SetTop(SelectionRect,  pos.Y);
                SelectionRect.Width  = 0;
                SelectionRect.Height = 0;
                return;
            }

            // 2. tık: karşı köşe → A ile B'nin sınır kutusu (fiziksel piksel).
            SelectedRect = RectFromCanvas(_start, pos);
            Confirmed = true;
            Close();
            return;
        }

        if (_fixedSize.HasValue)
        {
            var p = ToPhysical(e.GetPosition(DrawCanvas));
            SelectedRect = new System.Drawing.Rectangle(
                p.X, p.Y, _fixedSize.Value.Width, _fixedSize.Value.Height);
            Confirmed = true;
            Close();
            return;
        }

        if (_singleClickMode)
        {
            var p = ToPhysical(e.GetPosition(DrawCanvas));
            SelectedRect = new System.Drawing.Rectangle(p.X, p.Y, 1, 1);
            Confirmed = true;
            Close();
            return;
        }

        _start   = e.GetPosition(DrawCanvas);
        _drawing = true;
        SelectionRect.Visibility = Visibility.Visible;
        Canvas.SetLeft(SelectionRect, _start.X);
        Canvas.SetTop(SelectionRect,  _start.Y);
        SelectionRect.Width  = 0;
        SelectionRect.Height = 0;
        DrawCanvas.CaptureMouse();
    }

    private void Canvas_MouseMove(object sender, MouseEventArgs e)
    {
        _lastMousePos = e.GetPosition(DrawCanvas);

        if (_twoCorner)
        {
            if (!_twoCornerArmed) return; // henüz köşe A yok
            var bx = Math.Min(_start.X, _lastMousePos.X);
            var by = Math.Min(_start.Y, _lastMousePos.Y);
            Canvas.SetLeft(SelectionRect, bx);
            Canvas.SetTop(SelectionRect,  by);
            SelectionRect.Width  = Math.Abs(_lastMousePos.X - _start.X);
            SelectionRect.Height = Math.Abs(_lastMousePos.Y - _start.Y);
            return;
        }

        if (_fixedSize.HasValue)
        {
            Canvas.SetLeft(SelectionRect, _lastMousePos.X);
            Canvas.SetTop(SelectionRect,  _lastMousePos.Y);
            return;
        }

        if (!_drawing) return;
        var x = Math.Min(_start.X, _lastMousePos.X);
        var y = Math.Min(_start.Y, _lastMousePos.Y);
        var w = Math.Abs(_lastMousePos.X - _start.X);
        var h = Math.Abs(_lastMousePos.Y - _start.Y);
        Canvas.SetLeft(SelectionRect, x);
        Canvas.SetTop(SelectionRect,  y);
        SelectionRect.Width  = w;
        SelectionRect.Height = h;
    }

    private void Canvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_twoCorner)  return; // iki-köşe modunu tıklamalar yürütür
        if (_fixedSize.HasValue) return; // MouseDown zaten kapattı
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();

        var cur = e.GetPosition(DrawCanvas);
        // En az 10 DIP sürüklendiyse onayla.
        if (Math.Abs(cur.X - _start.X) > 10 && Math.Abs(cur.Y - _start.Y) > 10)
        {
            SelectedRect = RectFromCanvas(_start, cur);
            Confirmed = true;
            Close();
        }
    }

    private void Window_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Confirmed = false;
            Close();
        }
        else if (e.Key == Key.Enter)
        {
            if (_fixedSize.HasValue)
            {
                var p = ToPhysical(_lastMousePos);
                SelectedRect = new System.Drawing.Rectangle(
                    p.X, p.Y, _fixedSize.Value.Width, _fixedSize.Value.Height);
                Confirmed = true;
                Close();
            }
            else if (SelectionRect.Width > 10)
            {
                var tl = new System.Windows.Point(Canvas.GetLeft(SelectionRect), Canvas.GetTop(SelectionRect));
                var br = new System.Windows.Point(tl.X + SelectionRect.Width, tl.Y + SelectionRect.Height);
                SelectedRect = RectFromCanvas(tl, br);
                Confirmed = true;
                Close();
            }
        }
    }

    // ── Koordinat dönüşümü ────────────────────────────────────────────────────
    // WPF DIP fare/canvas koordinatını YAKALAMA uzayındaki fiziksel ekran pikseline çevirir.
    // PointToScreen, süreç DPI-farkındalığını VE pencerenin gerçek ekran konumunu (maximized
    // borderless taşması dahil) otomatik hesaba katar → kalibre dikdörtgen yakalanan alanla
    // birebir hizalı (eski ×TransformToDevice yaklaşımı Canvas orijinini 0,0 varsayıp sağ-alta
    // kaydırıyordu, özellikle yüksek-DPI 2560×1600'de).
    private System.Drawing.Point ToPhysical(System.Windows.Point canvasPoint)
    {
        var s = DrawCanvas.PointToScreen(canvasPoint);
        return new System.Drawing.Point((int)Math.Round(s.X), (int)Math.Round(s.Y));
    }

    // İki canvas-DIP köşesinden fiziksel sınır kutusu (PointToScreen ile).
    private System.Drawing.Rectangle RectFromCanvas(System.Windows.Point a, System.Windows.Point b)
    {
        var pa = ToPhysical(a);
        var pb = ToPhysical(b);
        int x = Math.Min(pa.X, pb.X);
        int y = Math.Min(pa.Y, pb.Y);
        int w = Math.Abs(pb.X - pa.X);
        int h = Math.Abs(pb.Y - pa.Y);
        return new System.Drawing.Rectangle(x, y, Math.Max(1, w), Math.Max(1, h));
    }

    [DllImport("user32.dll")] private static extern int GetSystemMetrics(int nIndex);

    /// <summary>
    /// Yalnız sabit-boyut modunun GÖRSEL dikdörtgenini DIP'e ölçeklemek için (kozmetik).
    /// Yakalama koordinatları artık <see cref="ToPhysical"/>/PointToScreen ile hesaplanır.
    /// </summary>
    private (double X, double Y) GetDpiScale()
    {
        int    physW = GetSystemMetrics(0); // SM_CXSCREEN
        int    physH = GetSystemMetrics(1); // SM_CYSCREEN
        double dipW  = SystemParameters.PrimaryScreenWidth;
        double dipH  = SystemParameters.PrimaryScreenHeight;
        if (physW > 0 && physH > 0 && dipW > 0 && dipH > 0)
            return (physW / dipW, physH / dipH);

        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return (1.0, 1.0);
        return (src.CompositionTarget.TransformToDevice.M11,
                src.CompositionTarget.TransformToDevice.M22);
    }
}
