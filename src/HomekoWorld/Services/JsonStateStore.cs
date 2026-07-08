using System.IO;
using System.Text.Json;
using HomekoWorld.Models;

namespace HomekoWorld.Services;

public sealed class JsonStateStore
{
    private static readonly JsonSerializerOptions _opts = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _path;

    public JsonStateStore()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "HomekoWorld");
        Directory.CreateDirectory(dir);
        _path = Path.Combine(dir, "state.json");
    }

    public AppState Load()
    {
        if (!File.Exists(_path))
            return DefaultData.BuildInitialState();

        try
        {
            return LoadAndMigrate(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            // Bozuk/yarım state.json: sessizce default'a düşüp tüm kalibrasyonu kaybetmek yerine
            // önce .bak yedeğinden kurtar; o da olmazsa bozuğu arşivle + görünür log bırak.
            Program.Log($"[State] ⚠ state.json okunamadı/bozuk ({ex.Message}) — yedek/default deneniyor");
            var bak = _path + ".bak";
            if (File.Exists(bak))
            {
                try
                {
                    var recovered = LoadAndMigrate(File.ReadAllText(bak));
                    Program.Log("[State] ✓ state.json.bak yedeğinden kurtarıldı");
                    return recovered;
                }
                catch { /* yedek de bozuk → aşağı düş */ }
            }
            try { File.Move(_path, _path + $".corrupt-{DateTime.Now:yyyyMMdd-HHmmss}.json"); }
            catch { /* arşivleme best-effort */ }
            Program.Log("[State] ⚠ Kurtarılamadı — varsayılan kalibrasyona düşüldü (bozuk dosya arşivlendi)");
            return DefaultData.BuildInitialState();
        }
    }

    /// <summary>Ham JSON'u deserialize eder + tüm sürüm/alan migration'larını uygular. Hata fırlatabilir;
    /// çağıran <see cref="Load"/> yedek (.bak) / kurtarma ile ele alır.</summary>
    private AppState LoadAndMigrate(string json)
    {
        {
            var state = JsonSerializer.Deserialize<AppState>(json, _opts)
                        ?? DefaultData.BuildInitialState();

            // Version mismatch: drop built-in defaults (IsCustom=false), keep user combos
            if (state.Version < DefaultData.CurrentVersion)
            {
                state.Combos.RemoveAll(c => !c.IsCustom);
                state.Classes = DefaultData.DefaultClasses();

                // v4 → v5: migrate rogue combos to assassin
                if (state.Version < 5)
                {
                    foreach (var c in state.Combos)
                        if (c.ClassId == "rogue" || c.ClassId == "all")
                            c.ClassId = "assassin";

                    // Migrate farm pot settings → global AutoPot (if not yet migrated)
                    if (!state.AutoPot.HpPotEnabled && state.Farm.HpPotEnabled)
                    {
                        state.AutoPot.HpPotEnabled = state.Farm.HpPotEnabled;
                        state.AutoPot.HpPotPercent = state.Farm.HpPotPercent;
                        state.AutoPot.HpPotKey     = state.Farm.HpPotKey;
                        state.AutoPot.MpPotEnabled = state.Farm.MpPotEnabled;
                        state.AutoPot.MpPotPercent = state.Farm.MpPotPercent;
                        state.AutoPot.MpPotKey     = state.Farm.MpPotKey;
                    }

                    // Migrate default ClassId from legacy "rogue"
                    if (state.ClassId == "rogue")
                        state.ClassId = "assassin";
                }

                state.Version = DefaultData.CurrentVersion;
            }

            // Seed classes if not yet present (upgrade from pre-class state)
            if (state.Classes.Count == 0)
                state.Classes = DefaultData.DefaultClasses();

            // Seed profiles if not yet present (upgrade from pre-profile state)
            if (state.Profiles.Count == 0)
                state.Profiles = DefaultData.DefaultProfiles();

            // Migration: FaceTargetRetapMs 1000 → 500 (hızlı moblarda kaçırma düzeltmesi)
            if (state.Farm.FaceTargetRetapMs == 1000)
                state.Farm.FaceTargetRetapMs = 500;

            // Migration: FaceTargetRetapMs 500 → 250 (dönüş sıklığı 2× arttı)
            if (state.Farm.FaceTargetRetapMs == 500)
                state.Farm.FaceTargetRetapMs = 250;

            // Migration: DefaultEngagementRangePx 100 → 200 → 120
            if (state.Farm.DefaultEngagementRangePx == 100)
                state.Farm.DefaultEngagementRangePx = 120;
            if (state.Farm.DefaultEngagementRangePx == 200)
                state.Farm.DefaultEngagementRangePx = 120;

            // Migration: ConfidenceThreshold 0.35 (eski sabit) → 0.65 (false positive azaltma).
            // Flag ile korunur → kullanıcı sonradan UI'dan bilerek düşürürse (ör. 0.30) ezilmez.
            if (!state.ConfidenceThresholdMigrated)
            {
                if (state.Farm.ConfidenceThreshold <= 0.45)
                    state.Farm.ConfidenceThreshold = 0.65;
                state.ConfidenceThresholdMigrated = true;
            }

            // Migration: WtmTickMs 30/50 → 15 (F1). KOŞULSUZ (flag-bağımsız): bu satır eskiden
            // DetectionFpsBoost flag bloğunun İÇİNDEydi; flag'i zaten set olmuş kullanıcılarda hiç
            // uygulanmadı → combat tick'i 50ms'de takılı kaldı (ölüm tespiti + takip 3× yavaş, log'da
            // her DEATH satırında tick=50ms doğrulandı). 15 hedef değer; WtmTickMs'in UI slider'ı yok.
            if (state.Farm.WtmTickMs is 30 or 50)
                state.Farm.WtmTickMs = 15;

            // Migration: DeadBlacklistMs 4000 → 8000 (F2). Cesetler ~10sn+ ekranda kalıyor; 4sn süre
            // dolunca aynı cesede yeniden tıklanıyordu. 8sn re-probe sıklığını yarıya indirir.
            if (state.Farm.DeadBlacklistMs == 4000)
                state.Farm.DeadBlacklistMs = 8000;

            // Migration: DeadBlacklistMs 8000 → 20000 (2026-07-03). 8sn hâlâ "~10sn+" gözleminin altında
            // kalıyordu — MobTracker.DeadLingerMs (12000) ile birlikte "cesede tıklama" A-belirtisinin
            // ikinci kaynağıydı (bkz. MobTracker fix). Yalnız eski varsayılana dokun.
            if (state.Farm.DeadBlacklistMs == 8000)
                state.Farm.DeadBlacklistMs = 20000;

            // Migration (V3, 2026-07-02): HpDeathConfirmMs 60 → 150. 60ms, tek titrek/donuk DXGI karesiyle
            // sahte-ölüm üretebiliyordu (canlı mob atlama + kill sayacı şişmesi); yalnız eski varsayılana dokun.
            if (state.Farm.HpDeathConfirmMs == 60)
                state.Farm.HpDeathConfirmMs = 150;

            // Faz 22 — agresif tepki süreleri: eski yavaş varsayılanları BİR KEZ hızlıya taşı.
            // Flag ile korunur → kullanıcı sonradan UI'dan elle ayarlarsa tekrar ezilmez.
            if (!state.AggressiveTimingMigrated)
            {
                if (state.Farm.ClickPreDelayMs is 0 or 60)   state.Farm.ClickPreDelayMs  = 15;
                if (state.Farm.ClickPostDelayMs is 0 or 200) state.Farm.ClickPostDelayMs = 80;
                if (state.Farm.FaceTargetRetapMs == 250)     state.Farm.FaceTargetRetapMs = 150;
                if (state.Farm.WtmTickMs is 0 or 30)         state.Farm.WtmTickMs        = 15;
                state.AggressiveTimingMigrated = true;
            }

            // Faz 26 — DXGI yakalama ~1-2ms olduğundan eski GDI-dönemi FPS cap'i (40ms ≈ 25 FPS) artık
            // gereksiz; bir kez 12ms'e (~80 FPS) taşı. Flag ile korunur (kullanıcı slider'dan elle
            // değiştirdiyse ezilmez). Yalnız eski varsayılan 40 ise dokun.
            if (!state.DetectionFpsBoostMigrated)
            {
                if (state.Farm.DetectionMinIntervalMs is 0 or 40)
                    state.Farm.DetectionMinIntervalMs = 12;
                // (WtmTickMs 30/50→15 migration'ı yukarıya, KOŞULSUZ bloğa taşındı — F1: flag'i zaten
                //  set olmuş kullanıcılarda da uygulansın diye.)
                state.DetectionFpsBoostMigrated = true;
            }

            // Faz 19 migrations
            if (string.IsNullOrWhiteSpace(state.AutoPot.StartKey))
                state.AutoPot.StartKey = "F11";

            // FarmSettings scan mod defaults — 0 ise default set
            if (state.Farm.ScanIdleMs == 0)      state.Farm.ScanIdleMs      = 1000;
            if (state.Farm.ScanDragPx == 0)      state.Farm.ScanDragPx      = 100;
            if (state.Farm.ScanWaitMsBetween == 0) state.Farm.ScanWaitMsBetween = 1000;
            if (state.Farm.ScanMaxAttempts == 0) state.Farm.ScanMaxAttempts  = 6;

            // 8.tur (2026-07-08) — akıcılık varsayılanları: eski varsayılanda kalmış kullanıcıyı
            // BİR KEZ yeni değere taşı (elle farklı değer seçtiyse dokunma; flag sayesinde kullanıcı
            // sonradan bilerek 2000/200'e dönerse bir daha ezilmez).
            if (!state.FluidityDefaultsMigrated)
            {
                if (state.Farm.ScanIdleMs == 2000)    state.Farm.ScanIdleMs     = 1000;
                if (state.Farm.LootTapDelayMs == 200) state.Farm.LootTapDelayMs = 120;
                state.FluidityDefaultsMigrated = true;
            }

            // WtmSettings guardian / ROI defaults — tolerans 0 ise set
            if (state.Wtm.NameplateColorTol == 0)          state.Wtm.NameplateColorTol = 50;
            if (state.Wtm.HpBarRoiW == 0) state.Wtm.HpBarRoiW = 240;
            if (state.Wtm.HpBarRoiH == 0) state.Wtm.HpBarRoiH = 60;
            // ML HP-bar modu kaldırıldı (FujiMacro 1.0.0) → eski Ml (=1) veya tanımsız değer HSV'ye düşer.
            if (!Enum.IsDefined(typeof(Models.HpBarDetectionMode), state.Wtm.HpBarMode))
                state.Wtm.HpBarMode = Models.HpBarDetectionMode.Hsv;

            // Kendi HP/MP barı: eski tek-satır (HpBarY mid-row) → iki-köşe dikdörtgen geçişi.
            // Eski kalibrasyonu tek-satır dikdörtgene taşı (Top=eski Y, Height=1) ki kullanıcı
            // yeniden kalibre edene kadar AutoPot bozulmasın. Yeni kalibrasyon Top/Height'i yazar.
            if (state.Farm.HpBarTop == 0 && state.Farm.HpBarY > 0)
            {
                state.Farm.HpBarTop    = state.Farm.HpBarY;
                state.Farm.HpBarHeight = 1;
            }
            if (state.Farm.MpBarTop == 0 && state.Farm.MpBarY > 0)
            {
                state.Farm.MpBarTop    = state.Farm.MpBarY;
                state.Farm.MpBarHeight = 1;
            }

            // Faz 41 — envanter açılma animasyonu: eski 400ms varsayılanı bazen yarı-açık/animasyonlu
            // kare yakalıyordu (hatalı doluluk → sahte %89-100). Eski default 400 → 1000 (animasyon
            // otursun). UI'dan tunable; yalnız eski default'a (400) dokun.
            if (state.Autonomous.InventoryOpenDelayMs == 400)
                state.Autonomous.InventoryOpenDelayMs = 1000;

            return state;
        }
    }

    public void Save(AppState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, _opts);
            // Atomik yazım: önce .tmp'ye yaz → mevcut dosyayı .bak'a yedekle → yerine koy. Yarım yazımda
            // (güç/disk/AV) state.json hiç bozulmaz (kesinti yalnız .tmp'yi etkiler); Load() .bak'tan kurtarır.
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, json);
            if (File.Exists(_path))
            {
                try { File.Copy(_path, _path + ".bak", overwrite: true); } catch { /* yedek best-effort */ }
                File.Replace(tmp, _path, null);
            }
            else
            {
                File.Move(tmp, _path);
            }
        }
        catch { /* non-critical */ }
    }
}
