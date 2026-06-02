using System.ComponentModel;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using HomekoWorld.Engine;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Views;

/// <summary>
/// Tam ekran, şeffaf, tıklama-geçirgen (click-through) YOLO tespit overlay'i.
/// FarmEngine.DetectionsUpdated event'ine abone olur; seçili mob türü kutularını
/// ve saldırılan hedefi (vurgulu) referans görseldeki gibi çizer.
/// Yalnızca görünürken abone kalır — gizliyken sıfır redraw maliyeti.
/// </summary>
public partial class DetectionOverlayWindow : Window
{
    // ── Win32 click-through (genişletilmiş pencere stili) ─────────────────────
    private const int GWL_EXSTYLE       = -20;
    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020; // tıklamalar oyuna geçsin
    private const int WS_EX_TOOLWINDOW  = 0x00000080; // Alt-Tab'da görünmesin

    [DllImport("user32.dll")] private static extern int GetWindowLong(nint hWnd, int nIndex);
    [DllImport("user32.dll")] private static extern int SetWindowLong(nint hWnd, int nIndex, int dwNewLong);

    private readonly FarmEngine      _engine;
    private readonly DetectionCanvas _canvas = new();
    private bool   _realClose;
    private bool   _subscribed;
    private double _dpiX = 1, _dpiY = 1;

    // Frame coalescing: en son kare + kuyrukta bekleyen tek invoke bayrağı (0/1, Interlocked).
    private DetectionFrame? _pendingFrame;
    private int             _renderQueued;

    public DetectionOverlayWindow(FarmEngine engine)
    {
        InitializeComponent();
        _engine = engine;
        Root.Children.Add(_canvas);
        IsVisibleChanged += OnIsVisibleChanged;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // Click-through: pencere fare olaylarını yutmaz, oyuna geçirir.
        var hwnd = new WindowInteropHelper(this).Handle;
        int ex   = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, ex | WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW);

        // DPI: tespitler fiziksel piksel; WPF DIP çizer → DIP = fiziksel / M11(X), M22(Y)
        var src = PresentationSource.FromVisual(this);
        if (src?.CompositionTarget is not null)
        {
            _dpiX = src.CompositionTarget.TransformToDevice.M11;
            _dpiY = src.CompositionTarget.TransformToDevice.M22;
        }
    }

    private void OnIsVisibleChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (IsVisible) Subscribe();
        else           Unsubscribe();
    }

    private void Subscribe()
    {
        if (_subscribed) return;
        _engine.DetectionsUpdated += OnDetections;
        _subscribed = true;
    }

    private void Unsubscribe()
    {
        if (!_subscribed) return;
        _engine.DetectionsUpdated -= OnDetections;
        _subscribed = false;
        _canvas.Update(null, _dpiX, _dpiY); // ekranı temizle
    }

    private void OnDetections(object? sender, DetectionFrame frame)
    {
        // Engine thread'inden UI thread'ine geç — FRAME COALESCING:
        // En son kare kazanır; kuyrukta en fazla 1 invoke bulunur. Farm thread 33 FPS
        // yayınlasa da UI daha yavaşsa fazla kareler düşürülür → Dispatcher kuyruğu
        // şişmez (bellek balonu / donma / çökme biter; "takılı kutu" da bunun yan etkisiydi).
        Volatile.Write(ref _pendingFrame, frame);
        if (Interlocked.Exchange(ref _renderQueued, 1) == 1) return; // zaten kuyrukta

        var disp = Dispatcher;
        if (disp.HasShutdownStarted || disp.HasShutdownFinished)
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            return;
        }

        disp.BeginInvoke(() =>
        {
            Interlocked.Exchange(ref _renderQueued, 0);
            _canvas.Update(Volatile.Read(ref _pendingFrame), _dpiX, _dpiY);
        });
    }

    // ── Yaşam döngüsü (OverlayWindow deseni: Hide, gerçek kapatmada Destroy) ──
    public void Destroy()
    {
        _realClose = true;
        Unsubscribe();
        Close();
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (_realClose) return;
        e.Cancel = true;
        Hide();
    }
}

/// <summary>
/// FarmEngine tespit kutularını DrawingContext ile çizen hafif eleman.
/// Sınıf başına sabit renk; saldırılan hedef parlak yeşil + kalın çerçeve.
/// </summary>
internal sealed class DetectionCanvas : FrameworkElement
{
    private DetectionFrame? _frame;
    private double _dpiX = 1, _dpiY = 1;

    // Sınıf başına sabit renk paleti
    private static readonly Color[] Palette =
    {
        Color.FromRgb(0xB1, 0x4A, 0xE8), // mor (referans görseldeki gibi)
        Color.FromRgb(0x4A, 0xC8, 0xE8), // camgöbeği
        Color.FromRgb(0xE8, 0xB1, 0x4A), // turuncu
        Color.FromRgb(0x4A, 0xE8, 0x7A), // yeşil
        Color.FromRgb(0xE8, 0x4A, 0x8B), // pembe
        Color.FromRgb(0xE8, 0xE8, 0x4A), // sarı
    };
    private static readonly Pen[]   ClassPens;
    private static readonly Brush[] ClassFills;
    private static readonly Pen     TargetPen;
    private static readonly Brush   TargetFill;
    private static readonly Typeface Face = new("Segoe UI");

    static DetectionCanvas()
    {
        ClassPens  = new Pen[Palette.Length];
        ClassFills = new Brush[Palette.Length];
        for (int i = 0; i < Palette.Length; i++)
        {
            var pen = new Pen(new SolidColorBrush(Palette[i]), 2); pen.Freeze();
            ClassPens[i] = pen;
            var fill = new SolidColorBrush(Palette[i]); fill.Freeze();
            ClassFills[i] = fill;
        }
        var tColor = Color.FromRgb(0x39, 0xFF, 0x14); // parlak yeşil — saldırılan hedef
        TargetPen  = new Pen(new SolidColorBrush(tColor), 3.5); TargetPen.Freeze();
        TargetFill = new SolidColorBrush(tColor); TargetFill.Freeze();
    }

    public void Update(DetectionFrame? frame, double dpiX, double dpiY)
    {
        _frame = frame;
        _dpiX  = dpiX <= 0 ? 1 : dpiX;
        _dpiY  = dpiY <= 0 ? 1 : dpiY;
        InvalidateVisual();
    }

    protected override void OnRender(DrawingContext dc)
    {
        var f = _frame;
        if (f is null) return;

        float ppd = (float)_dpiX; // pixelsPerDip = M11

        // Geçici bir render hatası (ör. eşzamanlı liste anlık görüntüsü) uygulamayı
        // düşürmesin; App.DispatcherUnhandledException Handled=true ile yutuyor.
        try
        {
            foreach (var d in f.Detections)
            {
                if (ReferenceEquals(d, f.Target)) continue; // hedef ayrıca vurgulu çizilecek — çift kutu olmasın
                int idx = Math.Abs(d.ClassId) % Palette.Length;
                DrawBox(dc, d, ClassPens[idx], ClassFills[idx], ppd);
            }

            // Saldırılan/seçili hedefi en üstte vurgulu (yeşil) çiz
            if (f.Target is { } t)
                DrawBox(dc, t, TargetPen, TargetFill, ppd);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[DetectionOverlay] OnRender hata: {ex.Message}");
        }
    }

    private void DrawBox(DrawingContext dc, Detection d, Pen pen, Brush labelFill, float ppd)
    {
        double x = d.BBox.X      / _dpiX;
        double y = d.BBox.Y      / _dpiY;
        double w = d.BBox.Width  / _dpiX;
        double h = d.BBox.Height / _dpiY;
        if (w <= 0 || h <= 0) return;

        dc.DrawRectangle(null, pen, new Rect(x, y, w, h));

        // Etiket bandı: "isim 0.00" (referans görseldeki dolu etiket)
        string label = $"{d.ClassName} {d.Confidence:0.00}";
        var ft = new FormattedText(label, CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                                   Face, 12, Brushes.White, ppd);
        const double padX = 4, padY = 2;
        double labelW = ft.Width  + padX * 2;
        double labelH = ft.Height + padY * 2;
        double lx = x;
        double ly = y - labelH;
        if (ly < 0) ly = y; // ekranın en üstünü taşmasın — kutunun içine al

        dc.DrawRectangle(labelFill, null, new Rect(lx, ly, labelW, labelH));
        dc.DrawText(ft, new Point(lx + padX, ly + padY));
    }
}
