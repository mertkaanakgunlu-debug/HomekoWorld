using System.Drawing;

namespace HomekoWorld.Models.Farm;

/// <summary>
/// YOLO'nun tek bir karedeki bir nesne için döndürdüğü tespit sonucu.
/// OnnxYoloInferrer.Infer() metodunun çıktı birimi.
/// KeyPoint: pose modelinde eğitilmiş tıklama noktası (isim etiketi).
///           BBox modelinde null — ClickPoint merkeze düşer.
/// </summary>
public sealed record Detection(
    int        ClassId,
    string     ClassName,
    RectangleF BBox,           // ekran koordinatlarında (orijinal piksel)
    float      Confidence,
    PointF?    KeyPoint = null) // pose modelinden gelen tıklama noktası
{
    /// <summary>Bbox'ın geometrik merkezi.</summary>
    public PointF Center => new(BBox.X + BBox.Width / 2f, BBox.Y + BBox.Height / 2f);

    /// <summary>
    /// Gerçek tıklama noktası.
    /// Pose modeli: eğitilmiş keypoint (isim etiketi).
    /// BBox modeli: bbox merkezi — FarmEngine offset ile düzeltir.
    /// </summary>
    public PointF ClickPoint => KeyPoint ?? Center;

    /// <summary>
    /// Başka bir noktaya piksel mesafesi (WtM proximity check için).
    /// </summary>
    public float DistanceTo(PointF other)
    {
        var dx = Center.X - other.X;
        var dy = Center.Y - other.Y;
        return MathF.Sqrt(dx * dx + dy * dy);
    }
}
