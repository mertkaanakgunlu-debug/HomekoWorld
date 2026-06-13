using System.Collections.ObjectModel;
using System.Drawing;
using System.Linq;
using System.Globalization;
using Microsoft.Extensions.DependencyInjection;
using System.Windows;
using System.Windows.Input;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Engine;
using HomekoWorld.Hardware;
using HomekoWorld.Hooks;
using HomekoWorld.Models;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services;
using HomekoWorld.Services.Farm;
using HomekoWorld.Services.Skills;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Yolo;
using Microsoft.Win32;
using System.IO;

namespace HomekoWorld.ViewModels;

public partial class MainViewModel
{
    // ── Oto Farm (Faz 17) ────────────────────────────────────────────────────

    partial void OnFarmEnabledChanged(bool value)
    {
        _state.Farm.Enabled = value;
        _store.Save(_state);
        // Checkbox sadece paneli gösterir/gizler.
        // Farm döngüsünü F9 veya "Başlat/Durdur" butonu başlatır.
        if (!value && FarmRunning)
            StopFarm();
    }

    // ── Farm başlat / durdur ──────────────────────────────────────────────────

    [RelayCommand]
    private void ToggleFarm() => ToggleSelectedMode();

    private void StartFarm()
    {
        // FujiMacro: sidebar'dan Oto Farm seçili olması = mod aktif (eski FarmEnabled kapısı kaldırıldı).
        if (_farmEngine.Inferrer is null)
        {
            FarmStatus = "⚠ Model yüklü değil — .onnx seç";
            return;
        }
        FarmKills = FarmHpPotsUsed = FarmMpPotsUsed = 0;
        FarmCurrentKey = "";
        FarmCurrentMob = "";
        FarmPaused     = false;
        FarmActivityLog.Clear();
        while (_activityQueue.TryDequeue(out _)) { } // bekleyen eski girdileri temizle
        _pendingFarmStatus = null;
        _lastEventText = "";
        StartSessionTimer();
        _farmEngine.Start();
        FarmRunning = true;
    }

    private void StopFarm()
    {
        _farmEngine.Stop();
        StopSessionTimer();
        FarmRunning = false;
        FarmPaused  = false;
        FarmStatus  = "Durduruldu";
    }

    [RelayCommand]
    private void ToggleFarmHud() => FarmHudVisible = !FarmHudVisible;

    [RelayCommand]
    private void ToggleLogHud() => LogHudVisible = !LogHudVisible;

    [RelayCommand]
    private void ToggleHudExpanded() => HudExpanded = !HudExpanded;

    [RelayCommand]
    private void TogglePause()
    {
        if (!FarmRunning) return;
        _farmEngine.TogglePause();
        FarmPaused = _farmEngine.IsPaused;
    }

    // ── Log HUD aktivite akışı (A3: thread-safe kuyruk + UI timer flush) ────────
    private const int ActivityLogCap   = 200;
    private const int ActivityQueueCap = 500; // kuyruk patlamasını önle (UI yavaşsa fazlası düşürülür)
    private string _lastEventText = "";

    /// <summary>
    /// Herhangi bir motor/hook thread'inden çağrılabilir. Yalnız thread-safe kuyruğa yazar;
    /// ObservableCollection mutasyonu UI thread'inde <see cref="FlushUiCoalesced"/> ile toplu yapılır.
    /// </summary>
    private void EnqueueActivity(string text, string kind)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        // Her-frame yaklaşma px spam'i log'a yazılmaz (durum satırı yine canlı güncellenir).
        if (kind == "event" && (text.StartsWith("Yaklaşıyor") || text.StartsWith("Archer yaklaşıyor"))) return;
        if (_activityQueue.Count >= ActivityQueueCap) return;
        _activityQueue.Enqueue(new Models.Farm.ActivityEntry(
            DateTime.Now.ToString("HH:mm:ss"), text, kind));
    }

    /// <summary>UI thread'de tek bir girdiyi uygular (dedup + Koruma kırmızı vurgu + cap).</summary>
    private void ApplyActivity(Models.Farm.ActivityEntry entry)
    {
        var text = entry.Text;
        var kind = entry.Kind;
        if (kind == "event" && text.Contains("Koruma")) kind = "guard"; // kırmızı vurgu
        if (kind == "event")
        {
            if (text == _lastEventText) return; // ardışık tekrarı ele
            _lastEventText = text;
        }
        FarmActivityLog.Add(kind == entry.Kind
            ? entry
            : new Models.Farm.ActivityEntry(entry.Time, text, kind));
        while (FarmActivityLog.Count > ActivityLogCap)
            FarmActivityLog.RemoveAt(0);
    }

    // ── A3: tek UI timer — farm durumu + telemetri + aktivite log'u toplu uygular ──

    private void StartUiCoalesceTimer()
    {
        _uiCoalesceTimer = new System.Windows.Threading.DispatcherTimer(
            System.Windows.Threading.DispatcherPriority.Background)
        { Interval = TimeSpan.FromMilliseconds(125) }; // ~8 Hz; girdiye/render'a yol verir
        _uiCoalesceTimer.Tick += (_, _) => FlushUiCoalesced();
        _uiCoalesceTimer.Start();
    }

    private void FlushUiCoalesced()
    {
        // Farm durum metni (son değer kazanır)
        var st = _pendingFarmStatus;
        if (st is not null)
        {
            _pendingFarmStatus = null;
            if (st != FarmStatus) FarmStatus = st;
        }

        // Telemetri (5 alan) — yalnız değiştiyse tek seferde
        if (System.Threading.Interlocked.Exchange(ref _telemetryDirty, 0) == 1)
        {
            var t = _farmEngine.Telemetry;
            FarmKills      = t.Kills;
            FarmHpPotsUsed = t.HpPotsUsed;
            FarmMpPotsUsed = t.MpPotsUsed;
            FarmCurrentKey = t.LastKeyTapped;
            FarmCurrentMob = t.CurrentMob;
            FarmInferenceFps = t.InferenceFps;
            FarmCaptureMs    = t.CaptureMs;   // A3: gecikme bütçesi HUD
            FarmInferenceMs  = t.InferenceMs;
            FarmPrepMs       = t.PreprocessMs;   // P0: darboğaz breakdown
            FarmGpuMs        = t.GpuRunMs;
            FarmPostMs       = t.PostprocessMs;
            FarmFrameAgeMs   = t.FrameAgeAtClickMs;   // B1: stale-box ölçümü
        }

        // Aktivite log — tick başına sınırlı sayıda (UI'yı tıkamadan boşalt)
        int budget = 16;
        while (budget-- > 0 && _activityQueue.TryDequeue(out var e))
            ApplyActivity(e);
    }

    // ── Oturum süresi + kill/saat ─────────────────────────────────────────────
    private System.Windows.Threading.DispatcherTimer? _farmSessionTimer;
    private DateTime _farmSessionStart;

    private void StartSessionTimer()
    {
        _farmSessionStart = DateTime.Now;
        FarmElapsed = "00:00";
        FarmKillsPerHour = 0;
        _farmSessionTimer ??= new System.Windows.Threading.DispatcherTimer
            { Interval = TimeSpan.FromSeconds(1) };
        _farmSessionTimer.Tick -= OnSessionTimerTick;
        _farmSessionTimer.Tick += OnSessionTimerTick;
        _farmSessionTimer.Start();
    }

    private void StopSessionTimer() => _farmSessionTimer?.Stop();

    private void OnSessionTimerTick(object? sender, EventArgs e)
    {
        var span = DateTime.Now - _farmSessionStart;
        FarmElapsed = span.TotalHours >= 1
            ? $"{(int)span.TotalHours}:{span.Minutes:00}:{span.Seconds:00}"
            : $"{span.Minutes:00}:{span.Seconds:00}";
        FarmKillsPerHour = span.TotalHours > 0.0001 ? (int)(FarmKills / span.TotalHours) : 0;
    }

    partial void OnFarmMobsJsonPathChanged(string value)
    {
        _state.Farm.MobsJsonPath = value;

        // Migration: eski state.json'da SelectedMobNames yoktu → SelectedMobName'den taşı.
        if (_state.Farm.SelectedMobNames.Count == 0 && !string.IsNullOrEmpty(_state.Farm.SelectedMobName))
            _state.Farm.SelectedMobNames = [_state.Farm.SelectedMobName];

        _store.Save(_state);
        FarmMobs.Clear();
        _mobLibrary.Load(value);
        foreach (var m in _mobLibrary.Mobs) FarmMobs.Add(m);
        RebuildMobCards();
    }

    partial void OnFarmSearchTextChanged(string value) => ApplyMobFilter();

    [RelayCommand]
    private void SelectFarmMob(string nameEn)
    {
        var names = _state.Farm.SelectedMobNames;
        // Toggle: seçiliyse çıkar, değilse ekle
        int idx = names.FindIndex(n => n.Equals(nameEn, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
            names.RemoveAt(idx);
        else
            names.Add(nameEn);
        _state.Farm.SelectedMobNames = names;
        // Geriye uyumluluk: SelectedMobName = ilk seçili (ya da boş)
        _state.Farm.SelectedMobName = names.Count > 0 ? names[0] : "";
        _store.Save(_state);

        foreach (var c in _farmMobCards)
            c.IsSelected = names.Contains(c.NameEn, StringComparer.OrdinalIgnoreCase);
        FarmSelectedMobCount = names.Count;
    }

    private void RebuildMobCards()
    {
        _farmMobCards.Clear();
        var names = _state.Farm.SelectedMobNames;
        foreach (var m in _mobLibrary.Mobs)
            _farmMobCards.Add(new MobCardViewModel(m)
                { IsSelected = names.Contains(m.NameEn, StringComparer.OrdinalIgnoreCase) });
        FarmSelectedMobCount = names.Count;
        ApplyMobFilter();
    }

    private void ApplyMobFilter()
    {
        FarmMobsFiltered.Clear();
        var q = FarmSearchText.Trim().ToLowerInvariant();
        foreach (var c in _farmMobCards)
        {
            if (string.IsNullOrEmpty(q) ||
                c.NameTr.ToLowerInvariant().Contains(q) ||
                c.NameEn.ToLowerInvariant().Contains(q))
                FarmMobsFiltered.Add(c);
        }
    }

    partial void OnFarmModelPathChanged(string value)
    {
        _state.Farm.ModelPath = value;
        _store.Save(_state);

        if (string.IsNullOrWhiteSpace(value) || !System.IO.File.Exists(value))
        {
            FarmStatus = "⚠ Model dosyası bulunamadı";
            return;
        }

        try
        {
            FarmStatus = "Model yükleniyor…";
            var inferrer = new OnnxYoloInferrer();
            // classNames: mobs.json sıralı isimleri
            var names = _mobLibrary.Mobs.OrderBy(m => m.Id).Select(m => m.NameEn).ToList();
            inferrer.Load(value, names.Count > 0 ? names : null, _state.Farm.ModelInputSize, _state.Farm.InferenceBackend);
            // T1: Güven eşiğini ayardan uygula — yoksa sabit 0.35 kalır (ağaç yanlış-pozitifleri).
            inferrer.ConfThreshold = (float)_state.Farm.ConfidenceThreshold;
            inferrer.IouThreshold  = (float)_state.Farm.IouThreshold;   // A6

            // Eski inferrer varsa dispose et
            (_farmEngine.Inferrer as IDisposable)?.Dispose();
            _farmEngine.Inferrer = inferrer;

            FarmStatus = $"Model hazır ({names.Count} sınıf)";

            // GPU (CUDA/DirectML) warm-up'ı arka planda başlat — ilk inference'ın derleme/tahsis
            // maliyetini Başlat'tan önce öder (UI donmasını önler). (TensorRT şu an yok; ileride
            // Faz E sidecar gelince burası engine-build durumunu da gösterebilir.)
            IsEngineBuilding = true;

            // WPF D3D/DXGI init ile GPU context creation aynı anda çalışırsa NVIDIA sürücüsü (dxgi.dll)
            // deadlock/crash yaşayabilir. Bu nedenle WarmUp'ı WPF UI render'ı bittikten (ApplicationIdle)
            // sonraya erteliyoruz.
            System.Windows.Application.Current.Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.ApplicationIdle, new Action(() =>
            {
                _ = inferrer.WarmUpAsync(status =>
                {
                    System.Windows.Application.Current.Dispatcher.BeginInvoke(() => 
                    {
                        EngineBuildStatus = status;
                        if (status.Contains("Hazır") || status.Contains("Hata") || status.Contains("Modu"))
                            IsEngineBuilding = false;
                    });
                });
            }));
        }
        catch (Exception ex)
        {
            FarmStatus = $"❌ Model yüklenemedi: {ex.Message}";
        }
    }

    partial void OnFarmSelectedMobNameChanged(string value)
    {
        // Geriye uyumluluk — kart seçimi SelectFarmMob'da yönetilir.
        _state.Farm.SelectedMobName = value;
        _store.Save(_state);
    }

    partial void OnFarmSelectedComboIdChanged(string value)
    {
        _state.Farm.SelectedComboId = value;
        _store.Save(_state);
    }

    partial void OnFarmHpPotEnabledChanged(bool value)   { _state.Farm.HpPotEnabled = value; _store.Save(_state); }
    partial void OnFarmMpPotEnabledChanged(bool value)   { _state.Farm.MpPotEnabled = value; _store.Save(_state); }
    partial void OnFarmHpPotPercentChanged(int value)    { _state.Farm.HpPotPercent = value; _store.Save(_state); }
    partial void OnFarmMpPotPercentChanged(int value)    { _state.Farm.MpPotPercent = value; _store.Save(_state); }
    partial void OnFarmHpPotKeyChanged(string value)     { _state.Farm.HpPotKey     = value; _store.Save(_state); }
    partial void OnFarmMpPotKeyChanged(string value)     { _state.Farm.MpPotKey     = value; _store.Save(_state); }
    partial void OnFarmHotKeyChanged(string value)       { _state.Farm.HotKey       = value; _store.Save(_state); }
    partial void OnFarmTestClickKeyChanged(string value) { _state.Farm.TestClickKey  = value; _store.Save(_state); }
    partial void OnFarmLootEnabledChanged(bool value)    { _state.Farm.LootEnabled    = value; _store.Save(_state); }
    partial void OnFarmShowDetectionOverlayChanged(bool value) { _state.Farm.ShowDetectionOverlay = value; _store.Save(_state); }
    // B2: replay kaydı toggle — bir SONRAKİ farm Start'ında geçerli olur (DetectionLoop recorder'ı başta kurar).
    partial void OnFarmReplayEnabledChanged(bool value) { _state.Farm.ReplayEnabled = value; _store.Save(_state); }
    // P2: pipelined inference toggle — bir SONRAKİ farm Start'ında geçerli olur (thread yapısı başta kurulur).
    partial void OnFarmPipelinedChanged(bool value) { _state.Farm.PipelinedInference = value; _store.Save(_state); }
    partial void OnFarmRecordingModeChanged(bool value) { _state.Farm.RecordingMode = value; _store.Save(_state); }
    partial void OnFarmArcherModeChanged(bool value)
    {
        _state.Farm.EngageMovement = value
            ? Models.Farm.EngageMovement.ArcherWalkAndFace
            : Models.Farm.EngageMovement.ComboHandlesApproach;
        _store.Save(_state);
    }
    partial void OnFarmArcherRangePxChanged(int value) { _state.Farm.ArcherApproachRangePx = value; _store.Save(_state); }

    // T1: Güven eşiği kaydırıcısı — ayara yaz + çalışan inferrer'a CANLI uygula (yeniden model yüklemeye gerek yok).
    partial void OnFarmConfidenceChanged(double value)
    {
        double v = Math.Clamp(value, 0.30, 0.90);
        _state.Farm.ConfidenceThreshold = v;
        _store.Save(_state);
        if (_farmEngine.Inferrer is { } inf) inf.ConfThreshold = (float)v;
    }

    // A6: IoU eşiği kaydırıcısı — ayara yaz + çalışan inferrer'a CANLI uygula (dip-dibe mob çift-kutu tuning).
    partial void OnFarmIouChanged(double value)
    {
        double v = Math.Clamp(value, 0.20, 0.80);
        _state.Farm.IouThreshold = v;
        _store.Save(_state);
        if (_farmEngine.Inferrer is { } inf) inf.IouThreshold = (float)v;
    }

    // T2: DXGI/GDI yakalama anahtarı. Farm bir sonraki başlatıldığında geçerli olur (DetectionLoop kaynağı başta kurar).
    partial void OnFarmDxgiCaptureChanged(bool value)
    {
        _state.Farm.CaptureBackend = value ? Models.Farm.CaptureBackend.Dxgi : Models.Farm.CaptureBackend.Gdi;
        _store.Save(_state);
    }

    // T4: EP değişince ayarı yaz + yüklü YOLO modelini yeniden kur (EP, session kuruluşunda belirlenir).
    partial void OnFarmInferenceBackendChanged(Models.Farm.InferenceBackend value)
    {
        _state.Farm.InferenceBackend = value;
        _store.Save(_state);
        if (!string.IsNullOrWhiteSpace(_state.Farm.ModelPath) && System.IO.File.Exists(_state.Farm.ModelPath))
            OnFarmModelPathChanged(_state.Farm.ModelPath); // yeniden yükle → yeni EP uygulanır
    }
    partial void OnFarmHpBarModeChanged(Models.HpBarDetectionMode value) { _state.Wtm.HpBarMode = value; _store.Save(_state); }

    // P3: ROI yakalama — bir sonraki farm Start'ında geçerli olur (DetectionLoop kaynağı başta kurulur).
    partial void OnFarmRoiEnabledChanged(bool value)   { _state.Farm.CaptureRoiEnabled = value; _store.Save(_state); }
    partial void OnFarmRoiXChanged(int value)          { _state.Farm.CaptureRoiX = value; _store.Save(_state); }
    partial void OnFarmRoiYChanged(int value)          { _state.Farm.CaptureRoiY = value; _store.Save(_state); }
    partial void OnFarmRoiWChanged(int value)          { _state.Farm.CaptureRoiW = value; _store.Save(_state); }
    partial void OnFarmRoiHChanged(int value)          { _state.Farm.CaptureRoiH = value; _store.Save(_state); }

    // ── Otomatik dosya keşfi ──────────────────────────────────────────────────

    [RelayCommand]
    private void AutoDiscoverFarmFiles()
    {
        // exe'den başlayıp birkaç üst ata dizini topla — böylece hem Debug build
        // (bin\Debug\...\win-x64) hem de publish-single\ exe'sinde çalışır.
        var roots = new List<string>();
        var cur   = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 7 && cur is not null; i++) { roots.Add(cur.FullName); cur = cur.Parent; }

        // Her kökte aranacak yaygın model konumları (alt klasörler dahil).
        var searchDirs = new List<string>();
        foreach (var r in roots)
        {
            searchDirs.Add(r);
            searchDirs.Add(Path.Combine(r, "publish-single"));
            searchDirs.Add(Path.Combine(r, "tools", "yolo_trainer", "out"));
            searchDirs.Add(Path.Combine(r, "tools", "yolo_trainer", "out", "onnx"));
        }

        string? foundOnnx  = null;
        string? foundMobs  = null;

        // TopDirectoryOnly: yalnız klasörün kök seviyesi — tools\yolo_trainer\.venv
        // içindeki yüzlerce test .onnx'i taranmaz (yanlış model seçimini önler).
        // .onnx içinde EN YENİ tarihli dosya seçilir (eski model dosyaları karışmasın; eskiyi
        // kullanmak istersen UI'dan manuel seç). Eskiden "best.onnx" adı önceliklenirdi → güncel
        // bestv2.onnx gibi daha yeni dosyalar atlanırdı.
        foreach (var dir in searchDirs.Where(Directory.Exists))
        {
            foundOnnx ??= Directory.EnumerateFiles(dir, "*.onnx", SearchOption.TopDirectoryOnly)
                              .OrderByDescending(File.GetLastWriteTimeUtc)
                              .FirstOrDefault();
            foundMobs ??= Directory.EnumerateFiles(dir, "mobs.json", SearchOption.TopDirectoryOnly)
                              .FirstOrDefault();
            if (foundOnnx is not null && foundMobs is not null) break;
        }

        if (foundOnnx is not null && (string.IsNullOrWhiteSpace(FarmModelPath) || !System.IO.File.Exists(FarmModelPath)))
            FarmModelPath = foundOnnx;
        if (foundMobs is not null && (string.IsNullOrWhiteSpace(FarmMobsJsonPath) || !System.IO.File.Exists(FarmMobsJsonPath)))
            FarmMobsJsonPath = foundMobs;

        // Show success whenever files were found in filesystem (regardless of whether paths were already set)
        FarmStatus = (foundOnnx is not null || foundMobs is not null)
            ? "✓ Dosyalar bulundu"
            : "Dosya bulunamadı — manuel seçin";
    }

    [RelayCommand]
    private void BrowseMobsJson()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "JSON dosyası|*.json|Tüm dosyalar|*.*",
            Title  = "mobs.json seç",
        };
        if (dlg.ShowDialog() != true) return;
        FarmMobsJsonPath = dlg.FileName;   // triggers OnFarmMobsJsonPathChanged
    }

    [RelayCommand]
    private void BrowseModel()
    {
        var dlg = new OpenFileDialog
        {
            Filter = "ONNX Model|*.onnx|Tüm dosyalar|*.*",
            Title  = "YOLO model seç",
        };
        if (dlg.ShowDialog() != true) return;
        FarmModelPath = dlg.FileName;      // triggers OnFarmModelPathChanged
    }
}
