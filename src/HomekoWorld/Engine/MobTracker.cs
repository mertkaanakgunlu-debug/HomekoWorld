using System;
using System.Collections.Generic;
using System.Drawing;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.Engine;

/// <summary>
/// Hafif "ByteTrack-lite" mob takipçisi — zaten hesaplanmış YOLO kutularına kalıcı kimlik (TrackId) atar.
/// EKSTRA inference/GPU YOK; kare başına ~mikrosaniye (ekranda birkaç mob). Çözdüğü sorunlar:
///  • Ceset yok sayma (#3): öldürülen iz <see cref="MarkDead"/> ile "ölü" damgalanır; ceset tespit edilmeye
///    devam etse de aynı ize eşlenir → çıktıda Dead=true ile aday listesinden elenir. Despawn olunca iz düşer.
///  • Doğru overlay (#4): aktif hedef TrackId ile vurgulanır → "en yakın aynı-tür kutu" kayması biter.
///  • Stabil hedef (#2): kare-kare aynı moba aynı kimlik → yakınlık puanı zıplamaz.
/// Eşleme: sabit-hız tahmin + IoU greedy, iki-geçiş (önce yüksek-güven, sonra düşük-güven kutular).
/// Thread: <see cref="Update"/> tespit thread'inden, <see cref="MarkDead"/> combat thread'inden çağrılır → iç kilit korur.
/// </summary>
public sealed class MobTracker
{
    private sealed class Track
    {
        public int        Id;
        public int        ClassId;
        public RectangleF Box;        // son eşleşen kutu (ekran uzayı)
        public float      Vx, Vy;     // merkez hızı (px / kare) — sabit-hız tahmini
        public int        Age;        // toplam eşleşme sayısı
        public int        Missed;     // ardışık eşleşmeme sayısı
        public bool       Dead;       // öldürüldü → aday dışı
        public long       DeadAtMs;   // ölü damgasının vurulduğu an (ömrü sınırlamak için)
        public long       LastSeenMs;
    }

    private readonly object      _lock   = new();
    private readonly List<Track> _tracks = new();
    private int  _nextId = 1;
    private long _lastMs = -1;

    /// <summary>Eşleme IoU eşiği (kare-kare aynı moba kalıcı kimlik).</summary>
    public float IouThreshold { get; set; } = 0.3f;
    /// <summary>Bir iz kaç kare eşleşmezse düşürülür (ceset/kayıp izlerin ömrü).</summary>
    public int   MaxAgeFrames { get; set; } = 15;
    /// <summary>İki-geçiş eşiği: ≥ bu güven = "yüksek-güven" (ilk geçişte eşlenir).</summary>
    public float HighConf     { get; set; } = 0.5f;
    /// <summary>IoU başarısız olunca MERKEZ-mesafesiyle eşle: izin verilen kayma = bu × kutu-boyutu. Kamera/karakter
    /// hareketinde kutu ötelenip IoU kopsa da aynı iz korunur → ID churn'ü (ve ölü-damga kaybı) azalır. 0 = kapalı.</summary>
    public float CenterMatchFactor { get; set; } = 1.0f;
    /// <summary>Ölü iz en fazla bu kadar süre tutulur (corpse despawn'ı garanti; aynı yere respawn yeni iz alır).</summary>
    public int   DeadLingerMs { get; set; } = 12_000;
    /// <summary>Ölü iz'in bu yarıçapı (px, merkez) içinde doğan YENİ (aynı tür) iz de "ölü" sayılır. Mob ölünce
    /// düşme animasyonu + ~85px düşüş bbox'ı değiştirip IoU'yu koparır → ceset yeni kimlik alır; bu köprü onu
    /// yine "ölü" tutar (tekrar tıklama bitsin). Çok büyük tutma: yakın respawn'ı yanlışlıkla elemesin.</summary>
    public int   DeadInheritRadiusPx { get; set; } = 110;

    private const float VelEma = 0.5f;

    /// <summary>Verilen ize "ölü" damgası vurur (combat thread'inden kill onayında).</summary>
    public void MarkDead(int trackId)
    {
        if (trackId < 0) return;
        lock (_lock)
        {
            for (int i = 0; i < _tracks.Count; i++)
                if (_tracks[i].Id == trackId)
                {
                    _tracks[i].Dead     = true;
                    _tracks[i].DeadAtMs = _lastMs;
                    break;
                }
        }
    }

    /// <summary>Yeni farm oturumunda tüm izleri sıfırla.</summary>
    public void Reset()
    {
        lock (_lock) { _tracks.Clear(); _nextId = 1; _lastMs = -1; }
    }

    /// <summary>Bu karenin tespitlerini izlere eşler; her tespite TrackId/Dead iliştirilmiş YENİ liste döner.</summary>
    public IReadOnlyList<Detection> Update(IReadOnlyList<Detection> dets, long nowMs)
    {
        lock (_lock)
        {
            // 1) Tahmin: her CANLI izi sabit hızla bir adım ilerlet (eşleşmeyen iz coasts → yeniden yakalamayı
            //    kolaylaştırır). Ölü izler (ceset) SABİT tutulur (#4): kaydırılırsa miras çapı cesedin üstünden
            //    kayar → ceset yeni iz alınca "ölü" devralmaz → tekrar tıklanır.
            foreach (var t in _tracks)
            {
                if (t.Dead) continue;
                t.Box = new RectangleF(t.Box.X + t.Vx, t.Box.Y + t.Vy, t.Box.Width, t.Box.Height);
            }

            int n = dets.Count;
            var assignedTrack = new int[n];                 // det -> track index (-1 = yok)
            for (int i = 0; i < n; i++) assignedTrack[i] = -1;
            var trackMatched = new bool[_tracks.Count];

            // 2) İki-geçiş greedy IoU eşleme: önce yüksek-güven, sonra düşük-güven kutular.
            MatchPass(dets, assignedTrack, trackMatched, highConfOnly: true);
            MatchPass(dets, assignedTrack, trackMatched, highConfOnly: false);
            // 2b) IoU eşleşmeyen kalanları MERKEZ-mesafesiyle eşle (yalnız CANLI izler) → kamera/karakter
            //     hareketinde ötelenen kutu yeni ID almak yerine izini korur (churn ↓). Ölü izler hariç:
            //     gevşek merkez eşleşmesi yakından geçen CANLI mobu yanlışlıkla "ölü" yapmasın (IoU'da kalır).
            if (CenterMatchFactor > 0f) MatchByCenter(dets, assignedTrack, trackMatched);

            var outArr = new Detection[n];

            // 3) Eşleşen tespitleri güncelle; eşleşmeyenlere yeni iz aç.
            for (int i = 0; i < n; i++)
            {
                var d  = dets[i];
                int ti = assignedTrack[i];
                Track tr;
                if (ti >= 0)
                {
                    tr = _tracks[ti];
                    float ncx = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                    // tr.Box şu an TAHMİN edilmiş (Step 1'de +V ilerletildi). Hızı doğru ölçmek için bir önceki
                    // ÖLÇÜLEN merkezi geri al (tahmin − V); yoksa EMA tahminin üstüne kurulur, hız gerçek
                    // hareketin yarısında takılır (yakınsamaz) → tahmin az atar, hızlı mobta ID churn'ü artar.
                    float ocx = tr.Box.X + tr.Box.Width / 2f - tr.Vx, ocy = tr.Box.Y + tr.Box.Height / 2f - tr.Vy;
                    tr.Vx      = VelEma * (ncx - ocx) + (1 - VelEma) * tr.Vx;
                    tr.Vy      = VelEma * (ncy - ocy) + (1 - VelEma) * tr.Vy;
                    tr.Box     = d.BBox;
                    tr.ClassId = d.ClassId;
                    tr.Age++;
                    tr.Missed     = 0;
                    tr.LastSeenMs = nowMs;
                }
                else
                {
                    // Ölü-miras: düşme/pose değişimi IoU'yu koparınca ceset YENİ iz alır → yakın bir
                    // SON-ÖLDÜRÜLEN (aynı tür) izin "ölü" damgasını devral ki bir daha tıklanmasın.
                    bool inheritDead = false; long inheritAt = 0;
                    float ncx = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                    float r2  = (float)DeadInheritRadiusPx * DeadInheritRadiusPx;
                    for (int j = 0; j < _tracks.Count; j++)
                    {
                        var dt = _tracks[j];
                        if (!dt.Dead || dt.ClassId != d.ClassId) continue;
                        float dcx = dt.Box.X + dt.Box.Width / 2f, dcy = dt.Box.Y + dt.Box.Height / 2f;
                        float ddx = ncx - dcx, ddy = ncy - dcy;
                        if (ddx * ddx + ddy * ddy <= r2) { inheritDead = true; inheritAt = dt.DeadAtMs; break; }
                    }
                    tr = new Track
                    {
                        Id = _nextId++, ClassId = d.ClassId, Box = d.BBox,
                        Vx = 0, Vy = 0, Age = 1, Missed = 0,
                        Dead = inheritDead, DeadAtMs = inheritAt, LastSeenMs = nowMs,
                    };
                    _tracks.Add(tr);
                }
                outArr[i] = d with { TrackId = tr.Id, Dead = tr.Dead };
            }

            // 4) Eşleşmeyen izleri yaşlandır + ölü izleri ömrü dolunca düşür.
            for (int k = _tracks.Count - 1; k >= 0; k--)
            {
                var t = _tracks[k];
                if (t.Dead && nowMs - t.DeadAtMs > DeadLingerMs) { _tracks.RemoveAt(k); continue; }
                if (k < trackMatched.Length && !trackMatched[k])
                {
                    t.Missed++;
                    if (t.Missed > MaxAgeFrames) _tracks.RemoveAt(k);
                }
            }

            _lastMs = nowMs;
            return outArr;
        }
    }

    // Greedy IoU eşleme tek geçişi: kalan en yüksek IoU çiftini eşle, tükenene dek tekrarla.
    // Küçük N/M (birkaç mob) → O(N*M) iterasyon yeterli.
    private void MatchPass(IReadOnlyList<Detection> dets, int[] assignedTrack, bool[] trackMatched, bool highConfOnly)
    {
        while (true)
        {
            float bestIou   = IouThreshold;
            int   bestDet   = -1, bestTrack = -1;
            for (int i = 0; i < dets.Count; i++)
            {
                if (assignedTrack[i] >= 0) continue;
                var  d      = dets[i];
                bool isHigh = d.Confidence >= HighConf;
                if (highConfOnly != isHigh) continue;      // 1. geçiş yüksek, 2. geçiş düşük güven
                for (int k = 0; k < _tracks.Count; k++)
                {
                    if (trackMatched[k]) continue;
                    if (_tracks[k].ClassId != d.ClassId) continue; // aynı tür şartı
                    float iou = Iou(d.BBox, _tracks[k].Box);
                    if (iou > bestIou) { bestIou = iou; bestDet = i; bestTrack = k; }
                }
            }
            if (bestDet < 0) break;
            assignedTrack[bestDet]   = bestTrack;
            trackMatched[bestTrack]  = true;
        }
    }

    // IoU sonrası kalan tespitleri MERKEZ-mesafesiyle eşler (yalnız CANLI izler). İzin verilen kayma kutu
    // boyutuna oranlı; boyut benzerliği de şart (farklı boyut = farklı mob). Greedy: en yakın çiften başla.
    private void MatchByCenter(IReadOnlyList<Detection> dets, int[] assignedTrack, bool[] trackMatched)
    {
        while (true)
        {
            float bestDist = float.MaxValue; int bestDet = -1, bestTrack = -1;
            for (int i = 0; i < dets.Count; i++)
            {
                if (assignedTrack[i] >= 0) continue;
                var   d    = dets[i];
                float ncx  = d.BBox.X + d.BBox.Width / 2f, ncy = d.BBox.Y + d.BBox.Height / 2f;
                float gate = CenterMatchFactor * Math.Max(d.BBox.Width, d.BBox.Height); // izin verilen kayma
                for (int k = 0; k < _tracks.Count; k++)
                {
                    if (trackMatched[k]) continue;
                    if (_tracks[k].Dead) continue;                 // ölü iz yalnız IoU ile (canlı mobu kapma)
                    if (_tracks[k].ClassId != d.ClassId) continue; // aynı tür
                    float dcx = _tracks[k].Box.X + _tracks[k].Box.Width / 2f;
                    float dcy = _tracks[k].Box.Y + _tracks[k].Box.Height / 2f;
                    float dist = MathF.Sqrt((ncx - dcx) * (ncx - dcx) + (ncy - dcy) * (ncy - dcy));
                    if (dist > gate || dist >= bestDist) continue;
                    // boyut benzerliği (yükseklik) ≥ %60 — farklı derinlikteki başka mobu eşlemesin.
                    float hMin = Math.Min(d.BBox.Height, _tracks[k].Box.Height);
                    float hMax = Math.Max(1f, Math.Max(d.BBox.Height, _tracks[k].Box.Height));
                    if (hMin / hMax < 0.6f) continue;
                    bestDist = dist; bestDet = i; bestTrack = k;
                }
            }
            if (bestDet < 0) break;
            assignedTrack[bestDet]  = bestTrack;
            trackMatched[bestTrack] = true;
        }
    }

    private static float Iou(RectangleF a, RectangleF b)
    {
        float x1 = Math.Max(a.X, b.X),     y1 = Math.Max(a.Y, b.Y);
        float x2 = Math.Min(a.Right, b.Right), y2 = Math.Min(a.Bottom, b.Bottom);
        float iw = x2 - x1, ih = y2 - y1;
        if (iw <= 0 || ih <= 0) return 0f;
        float inter = iw * ih;
        float uni   = a.Width * a.Height + b.Width * b.Height - inter;
        return uni <= 0 ? 0f : inter / uni;
    }
}
