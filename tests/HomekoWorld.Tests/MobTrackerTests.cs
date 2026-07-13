using System.Drawing;
using HomekoWorld.Engine;
using HomekoWorld.Models.Farm;
using Xunit;

namespace HomekoWorld.Tests;

/// <summary>
/// MobTracker deterministik senaryo testleri (14.tur, 2.4). Amaç iki yönlü:
///  • 2.1 düzeltmesinin regresyon kilidi (inherited-dead izin doğduğu karede silinmemesi).
///  • BİLİNÇLİ tasarım kararlarının belgeleyici testleri (görünmez ceset MaxAgeMs'te düşer —
///    bkz MobTracker.DeadLingerMs dokümantasyonu: bu bir bug değil, respawn koruması).
/// Tüm gözlemler DIŞ sözleşme üzerinden (Update dönüşündeki TrackId/Dead) — iç alanlara bakılmaz.
/// </summary>
public class MobTrackerTests
{
    private static Detection Det(float x, float y, float w = 60f, float h = 60f)
        => new(0, "Tyon", new RectangleF(x, y, w, h), 0.9f);

    // ── 2.1 regresyon kilidi ─────────────────────────────────────────────────────────────────

    /// <summary>KÖK SENARYO (2026-07-13 canlı kanıt): ceset 12sn+ görünür kaldıktan sonra poz/IoU
    /// kopması yeni iz doğurur; yeni iz ESKİ DeadAtMs'i miras alır. Eski kodda trackMatched dizisi
    /// yeni izi kapsamadığından iz DOĞDUĞU karede siliniyor, ceset bir sonraki karede miras
    /// bulamayıp CANLI doğuyordu → tekrar tıklanıyordu.</summary>
    [Fact]
    public void InheritedDead_WithStaleStamp_SurvivesItsBirthFrame()
    {
        var tr = new MobTracker();

        // t=0: mob görünür, iz doğar; hemen öldürülür (DeadAtMs=0).
        var first = tr.Update(new[] { Det(100, 100) }, 0)[0];
        tr.MarkDead(first.TrackId);

        // Ceset 12.6sn boyunca AYNI yerde görünür (matched ceset DeadLinger'ı aşsa da silinmez).
        for (long t = 30; t <= 12_600; t += 30)
        {
            var d = tr.Update(new[] { Det(100, 100) }, t)[0];
            Assert.True(d.Dead, $"görünür ceset t={t}'de ölü kalmalıydı");
        }

        // t=12.7sn: poz değişimi — kutu 80px kayar (60px kutuda IoU=0, ölü iz merkez-eşlemeye girmez)
        // → YENİ iz doğar, 80px ≤ DeadInheritRadiusPx → miras-DEAD + eski damga (0ms, linger dolmuş).
        var reborn = tr.Update(new[] { Det(180, 100) }, 12_700)[0];
        Assert.True(reborn.Dead, "miras-DEAD iz doğum karesinde ölü işaretlenmeli");

        // Bir SONRAKİ karede aynı kutu: miras iz hayatta kalmış olmalı (2.1 düzeltmesi) —
        // eski kodda iz doğum karesinde silinir, burada temiz kimlikle CANLI dönerdi.
        var next = tr.Update(new[] { Det(180, 100) }, 12_730)[0];
        Assert.True(next.Dead, "inherited-dead iz doğum karesini ATLATMALI — ceset canlı dirilmemeli");
        Assert.Equal(reborn.TrackId, next.TrackId);
    }

    // ── Miras yarıçapı (14.tur: 110→130) ─────────────────────────────────────────────────────

    [Fact]
    public void DeadInherit_InsideRadius_InheritsDead()
    {
        var tr = new MobTracker();
        var first = tr.Update(new[] { Det(100, 100) }, 0)[0]; // merkez (130,130)
        tr.MarkDead(first.TrackId);

        // 120px sağda doğan kutu (sınır vakası: eski 110 kaçırırdı, yeni 130 kapsar).
        var born = tr.Update(new[] { Det(220, 100) }, 30)[0]; // merkez (250,130) → d=120
        Assert.True(born.Dead, "130px yarıçap içindeki doğum ölü damgayı devralmalı");
    }

    [Fact]
    public void DeadInherit_OutsideRadius_StaysAlive()
    {
        var tr = new MobTracker();
        var first = tr.Update(new[] { Det(100, 100) }, 0)[0];
        tr.MarkDead(first.TrackId);

        // 145px uzakta doğan kutu: yarıçap dışı → canlı (yakın respawn korunur).
        var born = tr.Update(new[] { Det(245, 100) }, 30)[0]; // merkez (275,130) → d=145
        Assert.False(born.Dead, "yarıçap dışındaki doğum canlı kalmalı (respawn koruması)");
    }

    // ── Bilinçli tasarımın belgeleyici testleri ──────────────────────────────────────────────

    /// <summary>DeadLingerMs dokümantasyonundaki sözleşme: görünürlüğü kesilen ceset MaxAgeMs
    /// içinde düşer; aynı yere gelen YENİ doğum (respawn) miras alacak ölü iz bulamaz → CANLI.
    /// Bu bir bug DEĞİL (dış denetim P0-3'ün ana iddiasının reddi kayda geçsin) — kamera-dönüş
    /// boşluğunu S3 nüfus muhasebesi + konumsal blacklist kapatır.</summary>
    [Fact]
    public void InvisibleCorpse_DropsAfterMaxAge_ThenRespawnIsAlive()
    {
        var tr = new MobTracker();
        var first = tr.Update(new[] { Det(100, 100) }, 0)[0];
        tr.MarkDead(first.TrackId);

        // Ceset görünmez (boş kareler) — MaxAgeMs(750) aşılır, iz düşer.
        for (long t = 30; t <= 900; t += 30)
            tr.Update(System.Array.Empty<Detection>(), t);

        var respawn = tr.Update(new[] { Det(100, 100) }, 1000)[0];
        Assert.False(respawn.Dead, "despawn sonrası aynı yerdeki yeni doğum CANLI olmalı");
        Assert.NotEqual(first.TrackId, respawn.TrackId);
    }

    [Fact]
    public void LiveTrack_KeepsIdentityAcrossFrames()
    {
        var tr = new MobTracker();
        var a = tr.Update(new[] { Det(100, 100) }, 0)[0];
        var b = tr.Update(new[] { Det(104, 102) }, 30)[0]; // küçük kayma → IoU eşleşmesi
        Assert.Equal(a.TrackId, b.TrackId);
        Assert.False(b.Dead);
    }
}
