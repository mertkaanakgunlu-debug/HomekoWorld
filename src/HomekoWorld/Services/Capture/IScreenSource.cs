using System.Drawing;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// Ekran yakalama soyutlaması (GDI BitBlt / DXGI Desktop Duplication).
/// TEK thread'den çağrılır (FarmEngine.DetectionLoop). Dönen Bitmap KAYNAK tarafından sahiplenilir
/// (yeniden kullanılan tampon) — çağıran Dispose ETMEZ; yalnız bir sonraki <see cref="Capture"/>
/// çağrısına kadar geçerlidir. Bu sözleşme, kare başına ~16MB Bitmap allocation'ını ortadan kaldırır.
/// ROI aktifken Capture() yakalama bölgesinin bitmap'ini döner; <see cref="CaptureX"/>/<see cref="CaptureY"/>
/// ofsetini YOLO tespitlerinin ekran-uzayına çevrilmesi için kullan.
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>
    /// Birincil monitörü fiziksel piksellerle yakalar (ya da ayarlı ROI bölgesini).
    /// Dönen Bitmap'i DISPOSE ETME; sonraki Capture() çağrısına kadar geçerlidir.
    /// </summary>
    Bitmap Capture();

    /// <summary>Tanılama/UI için aktif yöntem adı (ör. "DXGI", "GDI").</summary>
    string BackendName { get; }

    /// <summary>Yakalama bölgesinin ekrandaki sol kenarı (piksel). Tam-ekranda 0.</summary>
    int CaptureX { get; }
    /// <summary>Yakalama bölgesinin ekrandaki üst kenarı (piksel). Tam-ekranda 0.</summary>
    int CaptureY { get; }
    /// <summary>Fiziksel ekranın tam genişliği (piksel). DetectionSnapshot boyutu için.</summary>
    int FullScreenW { get; }
    /// <summary>Fiziksel ekranın tam yüksekliği (piksel). DetectionSnapshot boyutu için.</summary>
    int FullScreenH { get; }
}
