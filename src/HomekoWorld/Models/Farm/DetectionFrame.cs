namespace HomekoWorld.Models.Farm;

/// <summary>
/// Tespit overlay'ine gönderilen tek karelik veri.
/// Detections: seçili mob türünün tespitleri (ekran pikseli koordinatları).
/// Target: o an saldırılan/seçili hedef (vurgulanır); yoksa null.
/// </summary>
public sealed record DetectionFrame(
    IReadOnlyList<Detection> Detections,
    Detection?               Target,
    int                      ScreenW,
    int                      ScreenH);
