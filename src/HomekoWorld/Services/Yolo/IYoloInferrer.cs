using HomekoWorld.Models.Farm;

namespace HomekoWorld.Services.Yolo;

/// <summary>YOLO inferrer sözleşmesi — OnnxYoloInferrer tarafından uygulanır.</summary>
public interface IYoloInferrer
{
    IReadOnlyList<Detection> Infer(System.Drawing.Bitmap frame);
    Task WarmUpAsync(Action<string>? onStatusChanged = null);

    /// <summary>Güven eşiği (0-1). Altındaki tespitler elenir; FarmSettings.ConfidenceThreshold'dan beslenir.</summary>
    float ConfThreshold { get; set; }

    /// <summary>NMS IoU eşiği (0.20-0.80). Dip-dibe mob çift-kutu davranışını ayarlar; FarmSettings.IouThreshold'dan beslenir.</summary>
    float IouThreshold { get; set; }

    /// <summary>
    /// Son <see cref="Infer"/>/<see cref="InferSlot"/> çağrısının alt-zamanlamaları (ms, yüksek çözünürlüklü):
    /// Preprocess (CPU letterbox+tensor) / Run (GPU inference) / Postprocess (CPU parse+NMS).
    /// Darboğaz teşhisi için — "Inf ms" üçünü birden topluyordu; TensorRT yalnız Run'ı hızlandırır.
    /// </summary>
    (double Preprocess, double Run, double Postprocess) LastTimings { get; }

    // ── P2 pipeline (üretici/tüketici): Infer'ı iki aşamaya böler → CPU preprocess GPU Run ile overlap ──
    /// <summary>ÜRETİCİ: frame'i slot'un tensor buffer'ına preprocess eder (GPU'ya dokunmaz); (padX,padY,scale) döner.</summary>
    (float padX, float padY, float scale) PreprocessInto(System.Drawing.Bitmap frame, int slot);
    /// <summary>TÜKETİCİ: slot'un (önce PreprocessInto edilmiş) tensor'ını GPU'da çalıştırır + parse. origW/origH = kaynak kare boyutu.</summary>
    IReadOnlyList<Detection> InferSlot(int slot, float padX, float padY, float scale, int origW, int origH);
}
