using System.Drawing;

namespace HomekoWorld.Services.Capture;

/// <summary>
/// Kalibre edilen koordinatları (bir "master" çözünürlükte alınan) çağrı anındaki
/// gerçek ekran çözünürlüğüne <b>anchor-aware</b> ölçekleyerek eşler. Böylece kullanıcı
/// 2560×1600'de bir kez kalibre eder; 1920×1080 gibi başka çözünürlüklerde bot hiç
/// kalibrasyon gerektirmeden çalışır (DefaultData'ya gömülen master + bu eşleme).
///
/// Master statiktir: load'da ve her kalibrasyon save'inde <see cref="SetMaster"/> ile set edilir.
/// Master 0 veya gerçek ekrana eşitse eşleme <b>kimliktir</b> (identity) → kullanıcının kendi
/// makinesi ve master'ı bilinmeyen eski kullanıcılar etkilenmez.
///
/// <para>Heuristik: oyun HUD'u dikey çözünürlükle ölçeklenir (<c>s = ekranH / masterH</c>).
/// Çapalar ekran kenarlarına/merkezine birebir oturur (0→0, yarı→yarı, tam→tam); öğenin
/// çapadan ofseti ve boyutu <c>s</c> ile ölçeklenir. Köşeye sabitli barlar ile merkeze
/// sabitli (top-center) mob HP barı/ismi bu sayede ayrı ayrı doğru taşınır.</para>
/// </summary>
public static class ResolutionMapper
{
    /// <summary>Kalibrasyonun yapıldığı çözünürlük (fiziksel piksel). 0 = bilinmiyor → identity.</summary>
    public static int MasterW { get; private set; }
    public static int MasterH { get; private set; }

    /// <summary>Master referans çözünürlüğü ayarla (load + kalibrasyon save sonrası).</summary>
    public static void SetMaster(int w, int h)
    {
        MasterW = w;
        MasterH = h;
    }

    /// <summary>Geçerli birincil ekran boyutu (fiziksel piksel).</summary>
    public static (int W, int H) ScreenSize() => ScreenCapture.PhysicalSize();

    /// <summary>
    /// Master-çözünürlük dikdörtgenini geçerli ekrana anchor-aware eşler.
    /// Master ayarsız (≤0) veya ekrana eşitse dikdörtgen değişmeden döner.
    /// </summary>
    public static Rectangle Map(int x, int y, int w, int h)
    {
        var (cw, ch) = ScreenSize();
        if (MasterW <= 0 || MasterH <= 0 || cw <= 0 || ch <= 0 ||
            (MasterW == cw && MasterH == ch))
            return new Rectangle(x, y, w, h);

        // Bölge merkezine göre en yakın çapa: ekranı yatay/dikey üçe böl.
        float ccx = x + w / 2f, ccy = y + h / 2f;
        float ax = ccx < MasterW / 3f ? 0f : ccx < 2f * MasterW / 3f ? MasterW / 2f : MasterW;
        float ay = ccy < MasterH / 3f ? 0f : ccy < 2f * MasterH / 3f ? MasterH / 2f : MasterH;

        float ox = x - ax, oy = y - ay;          // sol-üst köşenin çapadan ofseti (master px)
        float s  = (float)ch / MasterH;          // UI ölçeği (dikey heuristik)

        float ax2 = ax * cw / MasterW;           // çapa yeni ekrana birebir oturur
        float ay2 = ay * ch / MasterH;

        int nx = (int)MathF.Round(ax2 + ox * s);
        int ny = (int)MathF.Round(ay2 + oy * s);
        int nw = Math.Max(1, (int)MathF.Round(w * s));
        int nh = Math.Max(1, (int)MathF.Round(h * s));
        return new Rectangle(nx, ny, nw, nh);
    }

    /// <summary>Dikey bir uzunluğu (örn. duyuru kayması Δy) master → geçerli ekrana ölçekler.</summary>
    public static int ScaleLen(int len)
    {
        var (_, ch) = ScreenSize();
        if (MasterH <= 0 || ch <= 0 || MasterH == ch) return len;
        return (int)MathF.Round(len * (float)ch / MasterH);
    }

    /// <summary>Dikey bir uzunluğu geçerli ekran → master'a ölçekler (kalibrasyonda saklamak için).</summary>
    public static int UnscaleLen(int len)
    {
        var (_, ch) = ScreenSize();
        if (MasterH <= 0 || ch <= 0 || MasterH == ch) return len;
        return (int)MathF.Round(len * (float)MasterH / ch);
    }

    /// <summary>
    /// <see cref="Map"/>'in tersi: geçerli ekran koordinatını master uzayına çevirir.
    /// Kalibrasyon tıklamaları bununla saklanır → aynı ekranda <see cref="Map"/> ile birebir
    /// geri gelir (kullanıcının kendi çözünürlüğünde override daima isabetli; yalnız kalibre
    /// edilmemiş baked default'lar ölçekleme heuristiğine bağlı kalır).
    /// </summary>
    public static Rectangle Unmap(int x, int y, int w, int h)
    {
        var (cw, ch) = ScreenSize();
        if (MasterW <= 0 || MasterH <= 0 || cw <= 0 || ch <= 0 ||
            (MasterW == cw && MasterH == ch))
            return new Rectangle(x, y, w, h);

        float s = (float)ch / MasterH;                 // master → ekran ölçeği (Map ile aynı)

        // Çapayı EKRAN uzayı üçlemesine göre seç (Map master-uzayı üçlemesini kullanır; ekran
        // üçleme sınırları master sınırlarına birebir karşılık geldiğinden round-trip identity'dir).
        float ccx = x + w / 2f, ccy = y + h / 2f;
        float ax2 = ccx < cw / 3f ? 0f : ccx < 2f * cw / 3f ? cw / 2f : cw;
        float ay2 = ccy < ch / 3f ? 0f : ccy < 2f * ch / 3f ? ch / 2f : ch;

        float ax = ax2 * MasterW / cw;                 // ekran çapası → master çapası
        float ay = ay2 * MasterH / ch;
        float ox = (x - ax2) / s, oy = (y - ay2) / s;  // ofseti master ölçeğine geri al

        int mx = (int)MathF.Round(ax + ox);
        int my = (int)MathF.Round(ay + oy);
        int mw = Math.Max(1, (int)MathF.Round(w / s));
        int mh = Math.Max(1, (int)MathF.Round(h / s));
        return new Rectangle(mx, my, mw, mh);
    }
}
