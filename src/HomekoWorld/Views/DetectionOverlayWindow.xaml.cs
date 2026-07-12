using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using HomekoWorld.Engine;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Capture;

namespace HomekoWorld.Views;

public partial class DetectionOverlayWindow : Window
{
    private readonly FarmEngine _engine;
    private bool _isSubscribed;
    
    // UI nesneleri havuzu
    private readonly List<DetectionVisual> _visualPool = new();
    private DetectionVisual? _targetVisual;

    private readonly Brush[] _classBrushes =
    {
        new SolidColorBrush(Color.FromRgb(0xB1, 0x4A, 0xE8)), // purple
        new SolidColorBrush(Color.FromRgb(0x4A, 0xC8, 0xE8)), // cyan
        new SolidColorBrush(Color.FromRgb(0xE8, 0xB1, 0x4A)), // orange
        new SolidColorBrush(Color.FromRgb(0x4A, 0xE8, 0x7A)), // green
        new SolidColorBrush(Color.FromRgb(0xE8, 0x4A, 0x8B)), // pink
        new SolidColorBrush(Color.FromRgb(0xE8, 0xE8, 0x4A)), // yellow
    };

    private readonly Brush _targetBrush = new SolidColorBrush(Color.FromRgb(0x39, 0xFF, 0x14)); // bright green

    // DPI ölçekleme katsayıları (fiziksel piksel → WPF DIP)
    private double _dpiScaleX = 1.0;
    private double _dpiScaleY = 1.0;

    // EMA Yumuşatma
    private Rect? _emaTargetBox;
    private const double EmaAlpha = 0.4;

    public DetectionOverlayWindow(FarmEngine engine)
    {
        InitializeComponent();
        _engine = engine;

        // Pencereyi fiziksel çözünürlük üzerinden hizala ve boyutlandır
        var (w, h) = ScreenCapture.PhysicalSize();
        
        // Window pozisyonunu ekranın tam üstüne oturt
        Left = 0;
        Top = 0;
        Width = w;
        Height = h;

        // Tıklamaları alt pencereye geçirmesi için gerekli Win32 stilleri
        Loaded += (s, e) =>
        {
            var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
            int extendedStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
            SetWindowLong(hwnd, GWL_EXSTYLE, extendedStyle | WS_EX_TRANSPARENT);

            // DPI Hesaplama
            var src = PresentationSource.FromVisual(this);
            if (src?.CompositionTarget != null)
            {
                _dpiScaleX = 1.0 / src.CompositionTarget.TransformToDevice.M11;
                _dpiScaleY = 1.0 / src.CompositionTarget.TransformToDevice.M22;

                // Pencere boyutunu DIP olarak tekrar ayarla
                Width = w * _dpiScaleX;
                Height = h * _dpiScaleY;
            }

            // Kutular DXGI/GDI yakalamasına girmesin: model kendi çizimini girdi olarak
            // görüyordu, replay kareleri kirleniyordu (2026-07-11 replay analizi).
            CaptureExclusion.TryExclude(this, "DetectionOverlay");
        };
    }

    public void ActivateOverlay()
    {
        if (!_isSubscribed)
        {
            _engine.DetectionsUpdated += OnDetectionsUpdated;
            _isSubscribed = true;
        }
        Show();
    }

    public void DeactivateOverlay()
    {
        if (_isSubscribed)
        {
            _engine.DetectionsUpdated -= OnDetectionsUpdated;
            _isSubscribed = false;
        }
        System.Threading.Interlocked.Exchange(ref _pendingFrame, null); // bekleyen kare çizilmesin
        Hide();
        // Temizle
        foreach (var v in _visualPool) v.Hide();
        _targetVisual?.Hide();
    }

    // ── Çizim seyreltme (latest-wins, ~30Hz) ────────────────────────────────
    // Her inference karesi (~60-75Hz) ayrı UI işi kuyruklamak, tam-ekran layered window'da
    // her seferinde DWM'e büyük yüzey pushlatıyordu (GPU'da CUDA ile çekişme = overlay-açık
    // FPS düşüşü). Yalnız SON kare saklanır ve en çok ~30Hz çizilir; ara kareler atlanır.
    // Tespit thread'i hiç bloklanmaz (Exchange + CAS). Stop'un yolladığı boş kare de
    // latest-wins'te en son pending kalır → kutular ekranda asılı kalmaz.
    private DetectionFrame? _pendingFrame;
    private int  _drawScheduled;
    private long _lastDrawMs;
    private const int MinDrawIntervalMs = 33;

    private void OnDetectionsUpdated(object? sender, DetectionFrame frame)
    {
        System.Threading.Interlocked.Exchange(ref _pendingFrame, frame);
        if (System.Threading.Interlocked.CompareExchange(ref _drawScheduled, 1, 0) == 0)
            Dispatcher.InvokeAsync(DrawPendingLoopAsync, System.Windows.Threading.DispatcherPriority.Render);
    }

    private async void DrawPendingLoopAsync()
    {
        // UI thread'inde koşar (Task.Delay continuation'ı da dispatcher'a döner). Çizim bitince
        // yeni pending birikmişse döngü sürer; bayrak bırakıldıktan sonra gelen kare üreticinin
        // CAS'ıyla yeni döngü başlatır — kare kaçmaz, kuyruk birikmez.
        try
        {
            while (true)
            {
                long wait = MinDrawIntervalMs - (Environment.TickCount64 - _lastDrawMs);
                if (wait > 0) await System.Threading.Tasks.Task.Delay((int)wait);

                var frame = System.Threading.Interlocked.Exchange(ref _pendingFrame, null);
                if (frame is not null)
                {
                    UpdateVisuals(frame);
                    _lastDrawMs = Environment.TickCount64;
                }

                System.Threading.Volatile.Write(ref _drawScheduled, 0);
                if (System.Threading.Volatile.Read(ref _pendingFrame) is null) return;
                if (System.Threading.Interlocked.CompareExchange(ref _drawScheduled, 1, 0) != 0) return;
            }
        }
        catch
        {
            System.Threading.Volatile.Write(ref _drawScheduled, 0);
        }
    }

    private void UpdateVisuals(DetectionFrame frame)
    {
        int poolIndex = 0;

        foreach (var d in frame.Detections)
        {
            if (frame.Target != null && ReferenceEquals(d, frame.Target)) continue;

            var visual = GetOrCreateVisual(poolIndex++);
            var brush = _classBrushes[Math.Abs(d.ClassId) % _classBrushes.Length];
            
            UpdateVisual(visual, d.BBox.Left, d.BBox.Top, d.BBox.Width, d.BBox.Height, d.ClassName, d.Confidence, brush, 2);
        }

        // Fazlalık visual'ları gizle
        for (int i = poolIndex; i < _visualPool.Count; i++)
        {
            _visualPool[i].Hide();
        }

        // Hedefi EMA ile çiz
        if (frame.Target != null)
        {
            var t = frame.Target;
            var currentBox = new Rect(t.BBox.Left, t.BBox.Top, t.BBox.Width, t.BBox.Height);

            if (_emaTargetBox == null)
            {
                _emaTargetBox = currentBox;
            }
            else
            {
                var ema = _emaTargetBox.Value;
                _emaTargetBox = new Rect(
                    ema.X + EmaAlpha * (currentBox.X - ema.X),
                    ema.Y + EmaAlpha * (currentBox.Y - ema.Y),
                    ema.Width + EmaAlpha * (currentBox.Width - ema.Width),
                    ema.Height + EmaAlpha * (currentBox.Height - ema.Height));
            }

            if (_targetVisual == null) _targetVisual = CreateVisual();
            UpdateVisual(_targetVisual, (float)_emaTargetBox.Value.Left, (float)_emaTargetBox.Value.Top, 
                (float)_emaTargetBox.Value.Width, (float)_emaTargetBox.Value.Height, 
                t.ClassName, t.Confidence, _targetBrush, 3.5);
        }
        else
        {
            _emaTargetBox = null;
            _targetVisual?.Hide();
        }
    }

    private DetectionVisual GetOrCreateVisual(int index)
    {
        while (_visualPool.Count <= index)
        {
            _visualPool.Add(CreateVisual());
        }
        return _visualPool[index];
    }

    private DetectionVisual CreateVisual()
    {
        var border = new Border
        {
            BorderBrush = Brushes.White,
            BorderThickness = new Thickness(2),
            Background = Brushes.Transparent,
            RenderTransform = new TranslateTransform()
        };

        var labelBg = new Border
        {
            Background = Brushes.White,
            Padding = new Thickness(2),
            RenderTransform = new TranslateTransform()
        };

        var text = new TextBlock
        {
            Foreground = Brushes.White,
            FontSize = 12,
            FontWeight = FontWeights.Bold
        };
        
        labelBg.Child = text;

        OverlayCanvas.Children.Add(border);
        OverlayCanvas.Children.Add(labelBg);

        return new DetectionVisual { Box = border, LabelBg = labelBg, Text = text, Transform = (TranslateTransform)border.RenderTransform, LabelTransform = (TranslateTransform)labelBg.RenderTransform };
    }

    private void UpdateVisual(DetectionVisual visual, float x, float y, float w, float h, string className, float conf, Brush brush, double thickness)
    {
        visual.Box.Visibility = Visibility.Visible;
        visual.LabelBg.Visibility = Visibility.Visible;

        visual.Box.BorderBrush = brush;
        visual.Box.BorderThickness = new Thickness(thickness);
        visual.LabelBg.Background = brush;
        visual.Text.Text = $"{className} {conf:0.00}";

        // DPI dönüşümü: fiziksel pikselleri WPF DIP'e çevir
        double dipX = x * _dpiScaleX;
        double dipY = y * _dpiScaleY;
        double dipW = w * _dpiScaleX;
        double dipH = h * _dpiScaleY;

        visual.Box.Width = dipW;
        visual.Box.Height = dipH;
        visual.Transform.X = dipX;
        visual.Transform.Y = dipY;

        // Label'ı kutunun üstüne koy
        visual.LabelBg.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double labelH = visual.LabelBg.DesiredSize.Height;
        
        double labelY = dipY - labelH;
        if (labelY < 0) labelY = dipY; // Ekrandan taşıyorsa içine al

        visual.LabelTransform.X = dipX;
        visual.LabelTransform.Y = labelY;
    }

    private class DetectionVisual
    {
        public Border Box { get; set; } = null!;
        public Border LabelBg { get; set; } = null!;
        public TextBlock Text { get; set; } = null!;
        public TranslateTransform Transform { get; set; } = null!;
        public TranslateTransform LabelTransform { get; set; } = null!;

        public void Hide()
        {
            Box.Visibility = Visibility.Hidden;
            LabelBg.Visibility = Visibility.Hidden;
        }
    }

    // Win32 Imports for Click-through
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);
}
