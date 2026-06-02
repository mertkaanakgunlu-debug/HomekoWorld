using HomekoWorld.Models.Farm;

namespace HomekoWorld.Services.Yolo;

/// <summary>YOLO inferrer sözleşmesi — OnnxYoloInferrer tarafından uygulanır.</summary>
public interface IYoloInferrer
{
    IReadOnlyList<Detection> Infer(System.Drawing.Bitmap frame);

    /// <summary>Güven eşiği (0-1). Altındaki tespitler elenir; FarmSettings.ConfidenceThreshold'dan beslenir.</summary>
    float ConfThreshold { get; set; }
}
