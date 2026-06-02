using System.Drawing;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace HomekoWorld.Views;

public partial class CalibrationOverlayWindow : Window
{
    private System.Windows.Point _start;
    private bool _drawing;
    private readonly bool _singleClickMode;
    private readonly System.Drawing.Size? _fixedSize; // fiziksel piksel cinsinden sabit boyut
    private double _fixedWDip, _fixedHDip;            // WPF DIP karşılığı
    private System.Windows.Point _lastMousePos;

    public System.Drawing.Rectangle SelectedRect { get; private set; }
    public bool Confirmed { get; private set; }

    /// <param name="instruction">Özel talimat metni.</param>
    /// <param name="singleClickMode">true = ilk tıklamada onaylar.</param>
    /// <param name="fixedSize">Sabit fiziksel piksel boyutu — sürükle-bırak yerine fareyi takip eden sabit dikdörtgen.</param>
    public CalibrationOverlayWindow(string? instruction = null, bool singleClickMode = false,
                                    System.Drawing.Size? fixedSize = null)
    {
        _singleClickMode = singleClickMode;
        _fixedSize       = fixedSize;
        InitializeComponent();

        string suffix = fixedSize.HasValue
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
        if (_fixedSize.HasValue)
        {
            var pos = e.GetPosition(DrawCanvas);
            var (dpiX, dpiY) = GetDpiScale();
            SelectedRect = new System.Drawing.Rectangle(
                (int)(pos.X * dpiX), (int)(pos.Y * dpiY),
                _fixedSize.Value.Width, _fixedSize.Value.Height);
            Confirmed = true;
            Close();
            return;
        }

        if (_singleClickMode)
        {
            var pos = e.GetPosition(DrawCanvas);
            var (dpiX, dpiY) = GetDpiScale();
            SelectedRect = new System.Drawing.Rectangle(
                (int)(pos.X * dpiX), (int)(pos.Y * dpiY), 1, 1);
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
        if (_fixedSize.HasValue) return; // MouseDown zaten kapattı
        if (!_drawing) return;
        _drawing = false;
        DrawCanvas.ReleaseMouseCapture();

        var cur = e.GetPosition(DrawCanvas);
        var x   = Math.Min(_start.X, cur.X);
        var y   = Math.Min(_start.Y, cur.Y);
        var w   = Math.Abs(cur.X - _start.X);
        var h   = Math.Abs(cur.Y - _start.Y);

        if (w > 10 && h > 10)
        {
            var (dpiX, dpiY) = GetDpiScale();
            SelectedRect = new System.Drawing.Rectangle(
                (int)(x * dpiX), (int)(y * dpiY),
                (int)(w * dpiX), (int)(h * dpiY));
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
                var (dpiX, dpiY) = GetDpiScale();
                SelectedRect = new System.Drawing.Rectangle(
                    (int)(_lastMousePos.X * dpiX), (int)(_lastMousePos.Y * dpiY),
                    _fixedSize.Value.Width, _fixedSize.Value.Height);
                Confirmed = true;
                Close();
            }
            else if (SelectionRect.Width > 10)
            {
                var x = Canvas.GetLeft(SelectionRect);
                var y = Canvas.GetTop(SelectionRect);
                var (dpiX, dpiY) = GetDpiScale();
                SelectedRect = new System.Drawing.Rectangle(
                    (int)(x                   * dpiX), (int)(y                    * dpiY),
                    (int)(SelectionRect.Width  * dpiX), (int)(SelectionRect.Height * dpiY));
                Confirmed = true;
                Close();
            }
        }
    }

    private (double X, double Y) GetDpiScale()
    {
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is null) return (1.0, 1.0);
        return (src.CompositionTarget.TransformToDevice.M11,
                src.CompositionTarget.TransformToDevice.M22);
    }
}
