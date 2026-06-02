using System.Drawing;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// Tam ekran yakalama soyutlaması (GDI BitBlt / DXGI Desktop Duplication).
/// TEK thread'den çağrılır (FarmEngine.DetectionLoop). Dönen Bitmap KAYNAK tarafından sahiplenilir
/// (yeniden kullanılan tampon) — çağıran Dispose ETMEZ; yalnız bir sonraki <see cref="Capture"/>
/// çağrısına kadar geçerlidir. Bu sözleşme, kare başına ~16MB Bitmap allocation'ını ortadan kaldırır.
/// </summary>
public interface IScreenSource : IDisposable
{
    /// <summary>
    /// Birincil monitörü fiziksel piksellerle yakalar. Dönen Bitmap'i DISPOSE ETME;
    /// sonraki Capture() çağrısına kadar geçerlidir (içerik yeniden kullanılan tampona yazılır).
    /// </summary>
    Bitmap Capture();

    /// <summary>Tanılama/UI için aktif yöntem adı (ör. "DXGI", "GDI").</summary>
    string BackendName { get; }
}
