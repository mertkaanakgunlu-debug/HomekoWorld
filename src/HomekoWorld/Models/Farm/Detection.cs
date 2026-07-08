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
    /// <summary>Object tracker'ın atadığı kalıcı iz kimliği (-1 = henüz izlenmedi).
    /// MobTracker.Update bunu `with` ile doldurur; ham YOLO çıktısında -1'dir.</summary>
    public int TrackId { get; init; } = -1;

    /// <summary>Tracker bu izi "ölü" işaretledi mi (öldürülen mob / ceset). true → aday listesinden elenir
    /// (cesetlere tekrar tıklama döngüsünü bitirir; ceset despawn olunca iz düşer).</summary>
    public bool Dead { get; init; }

    /// <summary>Tracker bu izi "koruma mobu" işaretledi mi (2026-07-03). true → aday listesinden elenir.
    /// Konumsal guardian-blacklist'ten farkı: iz mob'u YÜRÜRKEN de takip eder (60px yarıçap kaçağı biter).</summary>
    public bool Guardian { get; init; }

    /// <summary>İzin global-hareket-kompanzasyonlu kümülatif yer değiştirmesi (px, 2026-07-03). Kamera/karakter
    /// kayması medyan-delta ile düşülür → yalnız MOB'UN kendi hareketi birikir. Cesetler yürümez: eşik
    /// (MobTracker.MovingAliveExemptPx) üstündeki aday kesin canlıdır → spatial dead-blacklist'ten muaf tutulur.</summary>
    public float MovedPx { get; init; }

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

    /// <summary>
    /// ROI-yerel koordinatları ekran-uzayına çevirmek için sabit ofset ekler.
    /// </summary>
    public Detection WithOffset(int dx, int dy) => this with
    {
        BBox     = new RectangleF(BBox.X + dx, BBox.Y + dy, BBox.Width, BBox.Height),
        KeyPoint = KeyPoint.HasValue ? new PointF(KeyPoint.Value.X + dx, KeyPoint.Value.Y + dy) : null,
    };
}
