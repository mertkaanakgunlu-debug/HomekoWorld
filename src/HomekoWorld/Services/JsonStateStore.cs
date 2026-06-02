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
            var json  = File.ReadAllText(_path);
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

            // Migration: ConfidenceThreshold 0.45 → 0.65 (false positive azaltma)
            if (state.Farm.ConfidenceThreshold <= 0.45)
                state.Farm.ConfidenceThreshold = 0.65;

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
                // Combat döngü tick'i: eski kalıcı 30/50 → 15 (AggressiveTimingMigrated 50'yi atlamıştı).
                // Combat takip/re-fire/ölüm tespiti 50ms'de tikliyordu; 15ms ile 3× daha duyarlı.
                if (state.Farm.WtmTickMs is 30 or 50)
                    state.Farm.WtmTickMs = 15;
                state.DetectionFpsBoostMigrated = true;
            }

            // Faz 19 migrations
            if (string.IsNullOrWhiteSpace(state.AutoPot.StartKey))
                state.AutoPot.StartKey = "F11";

            // FarmSettings scan mod defaults — 0 ise default set
            if (state.Farm.ScanIdleMs == 0)      state.Farm.ScanIdleMs      = 2000;
            if (state.Farm.ScanDragPx == 0)      state.Farm.ScanDragPx      = 100;
            if (state.Farm.ScanWaitMsBetween == 0) state.Farm.ScanWaitMsBetween = 1000;
            if (state.Farm.ScanMaxAttempts == 0) state.Farm.ScanMaxAttempts  = 6;

            // WtmSettings guardian / ROI defaults — tolerans 0 ise set
            if (state.Wtm.NameplateColorTol == 0)          state.Wtm.NameplateColorTol = 50;
            if (state.Wtm.HpBarTemporalWindow == 0)        state.Wtm.HpBarTemporalWindow = 6;
            if (state.Wtm.HpBarTemporalMinPositive == 0)   state.Wtm.HpBarTemporalMinPositive = 4;
            if (state.Wtm.HpBarClassifierThreshold == 0f)  state.Wtm.HpBarClassifierThreshold = 0.6f;
            if (state.Wtm.HpBarRoiW == 0) state.Wtm.HpBarRoiW = 240;
            if (state.Wtm.HpBarRoiH == 0) state.Wtm.HpBarRoiH = 60;
            if (string.IsNullOrWhiteSpace(state.Wtm.HpBarClassifierPath))
                state.Wtm.HpBarClassifierPath = "Assets/HpBar/hpbar_classifier.onnx";

            return state;
        }
        catch
        {
            return DefaultData.BuildInitialState();
        }
    }

    public void Save(AppState state)
    {
        try
        {
            var json = JsonSerializer.Serialize(state, _opts);
            File.WriteAllText(_path, json);
        }
        catch { /* non-critical */ }
    }
}
