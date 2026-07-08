using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Models;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;

// MainViewModel — Dev 2 (Farm lokasyonuna dönüş) + Dev 3 (Klan Bankası boşaltma) — partial devam.

namespace HomekoWorld.ViewModels;

/// <summary>Dev 3 — Klan Bankası eşya-türü satırı (ad + öğretilmiş örnek sayısı) — liste görünümü için.</summary>
public sealed class ClanBankItemTypeRow
{
    public string Name  { get; set; } = "";
    public int    Count { get; set; }
    public string Display => $"{Name}  ({Count} örnek)";
}

public partial class MainViewModel
{
    // ── Dev 2 — Farm lokasyonuna dönüş ────────────────────────────────────────
    [ObservableProperty] private bool   _farmReturnHomeEnabled;
    [ObservableProperty] private string _farmReturnHomeIdleSec = "20";
    [ObservableProperty] private string _farmReturnHomeStray   = "30";

    // ── V4 — Hedef Penceresi Çerçevesi (yapı-tabanlı seçili/canlı/öldü) ─────────
    [ObservableProperty] private string _targetFrameStatus            = "Öğretilmedi — renk yöntemi kullanılıyor";
    [ObservableProperty] private double _targetFrameMatchThreshold    = 0.80;
    [ObservableProperty] private double _targetFrameAbsentThreshold   = 0.65;

    private void LoadTargetFrameSettings()
    {
        var w = _state.Wtm;
        _targetFrameMatchThreshold  = w.TargetFrameMatchThreshold;
        _targetFrameAbsentThreshold = w.TargetFrameAbsentThreshold;
        RefreshTargetFrameStatus();
    }

    private void RefreshTargetFrameStatus()
    {
        var w = _state.Wtm;
        TargetFrameStatus = w.IsTargetFrameCalibrated
            ? $"✓ {w.TargetFrameTemplatesB64.Length} örnek @({w.TargetFrameRectW}×{w.TargetFrameRectH}) — yapı-tabanlı AKTİF"
            : "Öğretilmedi — renk yöntemi kullanılıyor (öneri: öğret)";
    }

    partial void OnTargetFrameMatchThresholdChanged(double value)
    { _state.Wtm.TargetFrameMatchThreshold = (float)value; _store.Save(_state); }
    partial void OnTargetFrameAbsentThresholdChanged(double value)
    { _state.Wtm.TargetFrameAbsentThreshold = (float)value; _store.Save(_state); }

    [RelayCommand]
    private async Task CalibrateTargetFrameRoiAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow(
            "HEDEF SEÇİLİYKEN barın SABİT bir parçasına (çerçeve/köşe süsü — kırmızı/oluk alanına taşma) kutu çiz — sol-üst → sağ-alt",
            twoCorner: true);
        ov.ShowDialog();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;
        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, ov.SelectedRect.Width, ov.SelectedRect.Height);
        var w = _state.Wtm;
        w.TargetFrameRectX = m.X; w.TargetFrameRectY = m.Y;
        w.TargetFrameRectW = Math.Max(1, m.Width); w.TargetFrameRectH = Math.Max(1, m.Height);
        _store.Save(_state);
        RefreshTargetFrameStatus();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task TeachTargetFrameTemplateAsync()
    {
        var w = _state.Wtm;
        if (w.TargetFrameRectW <= 0)
        {
            TargetFrameStatus = "⚠ Önce Arama Bölgesi çiz";
            return;
        }
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(200);
        var pr = ResolutionMapper.Map(w.TargetFrameRectX, w.TargetFrameRectY, w.TargetFrameRectW, w.TargetFrameRectH);
        using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        var list = w.TargetFrameTemplatesB64.ToList();
        if (list.Count >= 3) list.RemoveAt(0); // ≤3 örnek — en eskiyi at
        list.Add(ToB64Png(bmp));
        w.TargetFrameTemplatesB64 = list.ToArray();
        _store.Save(_state);
        RefreshTargetFrameStatus();
    }

    [RelayCommand]
    private void ResetTargetFrame()
    {
        var w = _state.Wtm;
        w.TargetFrameTemplatesB64 = Array.Empty<string>();
        w.TargetFrameRectX = w.TargetFrameRectY = w.TargetFrameRectW = w.TargetFrameRectH = 0;
        _store.Save(_state);
        RefreshTargetFrameStatus();
    }

    [RelayCommand]
    private async Task TestTargetFrameTemplateAsync()
    {
        var w = _state.Wtm;
        if (!w.IsTargetFrameCalibrated) { TargetFrameStatus = "⚠ Önce Arama Bölgesi + Şablon Öğret"; return; }
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(300); // overlay/pencere tam iniversin — hedefteki gerçek pencere görünsün
        var pr = ResolutionMapper.Map(w.TargetFrameRectX, w.TargetFrameRectY, w.TargetFrameRectW, w.TargetFrameRectH);
        float sx = pr.Width  / (float)w.TargetFrameRectW;
        float sy = pr.Height / (float)w.TargetFrameRectH;
        var templates = Services.Autonomous.TemplateLocator.BuildTemplates(w.TargetFrameTemplatesB64, sx, sy);
        using var frame = ScreenCapture.CaptureScreen();
        float score0 = Services.Autonomous.TemplateLocator.ScoreAt(frame,
            new System.Drawing.Rectangle(pr.X, pr.Y, pr.Width, pr.Height), templates);
        float scoreD = -2f; int dy = 0;
        if (w.AnnounceShiftY > 0)
        {
            dy = ResolutionMapper.ScaleLen(w.AnnounceShiftY);
            scoreD = Services.Autonomous.TemplateLocator.ScoreAt(frame,
                new System.Drawing.Rectangle(pr.X, pr.Y + dy, pr.Width, pr.Height), templates);
        }
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        float best = Math.Max(score0, scoreD);
        bool present = best >= w.TargetFrameMatchThreshold;
        // DÜRÜSTLÜK FIX (2026-07-03): eski "duyuru konumu kazandı" etiketi hangi konumun skoru YÜKSEKse onu
        // KOŞULSUZ gösteriyordu — ikisi de eşiğin altındayken (YOK) bile basılıyordu. Bu, ekranda gerçekten
        // duyuru olup olmadığını doğrulayan bağımsız bir sinyal DEĞİL — yalnız iki sabit konumun ham skor
        // karşılaştırması; düşük/gürültü seviyesindeki skorlarda hangisi "kazandığı" anlamsızdır (kullanıcı
        // bulgusu: ekranda duyuru YOKKEN bile bu etiket çıkıyordu). Artık ham skorlar ayrı gösteriliyor;
        // "duyuru konumu" notu yalnız VAR (eşik geçildi) durumunda ekleniyor. NOT: bu buton histerezis
        // UYGULAMAZ (tek-kare anlık okuma) — canlı motor (ScanTargetBar) sınır bandında ÖNCEKİ durumu korur,
        // bu yüzden bu testin "YOK" demesi canlı motorun o an (histerezisle) "VAR" tuttuğu durumdan farklı olabilir.
        string duyuruPart = dy > 0 ? $" duyuru(+{dy}px)={scoreD:F2}" : "";
        string winNote = present && scoreD > score0 ? " — duyuru konumu eşleşti" : "";
        TargetFrameStatus = $"{(present ? "VAR ✓" : "YOK")} skor={best:F2} (eşik VAR≥{w.TargetFrameMatchThreshold:F2} / YOK<{w.TargetFrameAbsentThreshold:F2}) " +
                             $"[normal={score0:F2}{duyuruPart}]{winNote}";
    }

    // ── Dev 3 — Klan Bankası ──────────────────────────────────────────────────
    [ObservableProperty] private bool   _clanBankEnabled;
    [ObservableProperty] private string _clanBankCheckEveryKills = "30";
    [ObservableProperty] private double _clanBankFullThreshold   = 0.85;
    [ObservableProperty] private double _clanBankItemThreshold   = 0.80;
    [ObservableProperty] private double _clanBankMenuThreshold   = 0.80;
    [ObservableProperty] private string _clanBankMenuKey         = "U";
    [ObservableProperty] private string _clanBankInventoryKey    = "I";
    [ObservableProperty] private string _clanBankCloseKey        = "toggle";
    [ObservableProperty] private string _clanBankStatus          = "Pasif";
    [ObservableProperty] private string _clanBankNewItemType     = "";
    [ObservableProperty] private string _clanBankChestInvStatus  = "Sandık-özel envanter ROI'si kalibre edilmedi — Otonom'unki kullanılacak";
    [ObservableProperty] private bool   _clanBankAutoDump        = true;

    /// <summary>Öğretilmiş depolanabilir eşya türleri (ad + örnek sayısı).</summary>
    public ObservableCollection<ClanBankItemTypeRow> ClanBankItemTypes { get; } = new();

    /// <summary>Ctor'dan çağrılır — Dev 2/3 kalıcı ayarlarını backing field'lere yükler (OnChanged tetiklemeden).</summary>
    private void LoadFarmExtrasSettings()
    {
        var f  = _state.Farm;
        _farmReturnHomeEnabled = f.ReturnHomeEnabled;
        _farmReturnHomeIdleSec = (f.ReturnHomeIdleMs / 1000).ToString();
        _farmReturnHomeStray   = f.ReturnHomeMinStrayCoords.ToString();

        var cb = _state.ClanBank;
        _clanBankEnabled         = cb.Enabled;
        _clanBankCheckEveryKills = cb.CheckEveryKills.ToString();
        _clanBankFullThreshold   = cb.FullThreshold;
        _clanBankItemThreshold   = cb.ItemMatchThreshold;
        _clanBankMenuThreshold   = cb.MenuMatchThreshold;
        _clanBankMenuKey         = cb.MenuKey;
        _clanBankInventoryKey    = cb.InventoryKey;
        _clanBankCloseKey        = cb.CloseKey;
        _clanBankAutoDump        = cb.AutoDumpOnMiss;
        RefreshClanBankItemTypes();
        RefreshClanBankStatus();
    }

    private void RefreshClanBankItemTypes()
    {
        ClanBankItemTypes.Clear();
        foreach (var t in _state.ClanBank.ItemTypes)
            ClanBankItemTypes.Add(new ClanBankItemTypeRow { Name = t.Name, Count = t.TemplatesB64?.Length ?? 0 });
    }

    private void RefreshClanBankStatus()
    {
        var cb  = _state.ClanBank;
        string clan  = cb.IsClanTabCalibrated ? $"Clan✓({cb.ClanTabTemplatesB64.Length})" : "Clan✗";
        string chest = cb.IsChestCalibrated   ? $"Sandık✓({cb.ChestTemplatesB64.Length})" : "Sandık✗";
        string inv   = _state.Autonomous.IsInventoryGridCalibrated ? "Envanter✓" : "Envanter✗ (Otonom'da kalibre et)";
        int types    = cb.ItemTypes.Count(t => (t.TemplatesB64?.Length ?? 0) > 0);
        ClanBankStatus = $"{clan}  {chest}  {inv}  Tür:{types}  →  {(cb.IsReady ? "HAZIR" : "eksik")}";
        RefreshClanBankChestInvStatus();
    }

    private void RefreshClanBankChestInvStatus()
    {
        var cb = _state.ClanBank;
        ClanBankChestInvStatus = cb.IsChestInvGridCalibrated
            ? $"✓ Sandık-özel: {cb.ChestInvGridW}×{cb.ChestInvGridH}px ({cb.ChestInvCols}×{cb.ChestInvRows})"
            : "Kalibre edilmedi — Otonom'unki (I tek başına açılan envanter) kullanılacak";
    }

    // ── Dev 2 persist ─────────────────────────────────────────────────────────
    partial void OnFarmReturnHomeEnabledChanged(bool value)
    { _state.Farm.ReturnHomeEnabled = value; _store.Save(_state); }
    partial void OnFarmReturnHomeIdleSecChanged(string value)
    { if (int.TryParse(value, out var s) && s > 0) { _state.Farm.ReturnHomeIdleMs = s * 1000; _store.Save(_state); } }
    partial void OnFarmReturnHomeStrayChanged(string value)
    { if (int.TryParse(value, out var c) && c >= 0) { _state.Farm.ReturnHomeMinStrayCoords = c; _store.Save(_state); } }

    // ── Dev 3 persist ─────────────────────────────────────────────────────────
    partial void OnClanBankEnabledChanged(bool value)
    { _state.ClanBank.Enabled = value; _store.Save(_state); RefreshClanBankStatus(); }
    partial void OnClanBankCheckEveryKillsChanged(string value)
    { if (int.TryParse(value, out var n) && n > 0) { _state.ClanBank.CheckEveryKills = n; _store.Save(_state); } }
    partial void OnClanBankFullThresholdChanged(double value)
    { _state.ClanBank.FullThreshold = (float)value; _store.Save(_state); }
    partial void OnClanBankItemThresholdChanged(double value)
    { _state.ClanBank.ItemMatchThreshold = (float)value; _store.Save(_state); }
    partial void OnClanBankMenuThresholdChanged(double value)
    { _state.ClanBank.MenuMatchThreshold = (float)value; _store.Save(_state); }
    partial void OnClanBankMenuKeyChanged(string value)
    { _state.ClanBank.MenuKey = value; _store.Save(_state); }
    partial void OnClanBankInventoryKeyChanged(string value)
    { _state.ClanBank.InventoryKey = value; _store.Save(_state); }
    partial void OnClanBankCloseKeyChanged(string value)
    { _state.ClanBank.CloseKey = value; _store.Save(_state); }
    partial void OnClanBankAutoDumpChanged(bool value)
    { _state.ClanBank.AutoDumpOnMiss = value; _store.Save(_state); }

    // ── Menü ögesi kalibrasyonu (arama-ROI + şablon) ──────────────────────────
    [RelayCommand]
    private Task CalibrateClanTabRoiAsync() => CalibrateClanBankRoiAsync(
        "Menüdeki 'Clan' sekmesinin ETRAFINA kutu çiz (arama bölgesi — biraz geniş) — sol-üst → sağ-alt",
        (x, y, w, h) => { var cb = _state.ClanBank; cb.ClanTabRoiX = x; cb.ClanTabRoiY = y; cb.ClanTabRoiW = w; cb.ClanTabRoiH = h; });

    [RelayCommand]
    private Task TeachClanTabTemplateAsync() => TeachClanBankTemplateAsync(
        "'Clan' sekmesi ikon/yazısının ETRAFINA TAM kutu çiz (şablon)",
        () => _state.ClanBank.ClanTabTemplatesB64, arr => _state.ClanBank.ClanTabTemplatesB64 = arr);

    [RelayCommand]
    private void ResetClanTab()
    { _state.ClanBank.ClanTabTemplatesB64 = Array.Empty<string>(); _store.Save(_state); RefreshClanBankStatus(); }

    [RelayCommand]
    private Task CalibrateChestRoiAsync() => CalibrateClanBankRoiAsync(
        "Hazine SANDIĞI sembolünün ETRAFINA kutu çiz (arama bölgesi) — sol-üst → sağ-alt",
        (x, y, w, h) => { var cb = _state.ClanBank; cb.ChestRoiX = x; cb.ChestRoiY = y; cb.ChestRoiW = w; cb.ChestRoiH = h; });

    [RelayCommand]
    private Task TeachChestTemplateAsync() => TeachClanBankTemplateAsync(
        "Hazine SANDIĞI sembolünün ETRAFINA TAM kutu çiz (şablon)",
        () => _state.ClanBank.ChestTemplatesB64, arr => _state.ClanBank.ChestTemplatesB64 = arr);

    [RelayCommand]
    private void ResetChest()
    { _state.ClanBank.ChestTemplatesB64 = Array.Empty<string>(); _store.Save(_state); RefreshClanBankStatus(); }

    [RelayCommand]
    private Task CalibrateStorageOpenRoiAsync() => CalibrateClanBankRoiAsync(
        "(Opsiyonel) AÇIK depo penceresinin sabit bir ögesinin ETRAFINA kutu çiz (arama bölgesi)",
        (x, y, w, h) => { var cb = _state.ClanBank; cb.StorageOpenRoiX = x; cb.StorageOpenRoiY = y; cb.StorageOpenRoiW = w; cb.StorageOpenRoiH = h; });

    [RelayCommand]
    private Task TeachStorageOpenTemplateAsync() => TeachClanBankTemplateAsync(
        "(Opsiyonel) AÇIK depo penceresi ögesinin ETRAFINA TAM kutu çiz (doğrulama şablonu)",
        () => _state.ClanBank.StorageOpenTemplatesB64, arr => _state.ClanBank.StorageOpenTemplatesB64 = arr);

    [RelayCommand]
    private void ResetStorageOpen()
    { _state.ClanBank.StorageOpenTemplatesB64 = Array.Empty<string>(); _store.Save(_state); RefreshClanBankStatus(); }

    // ── (Opsiyonel) Sandık AÇIKKEN envanter ızgarası — yalnız Otonom'un tek-başına 'I' envanterinden
    // FARKLI konumda/boyuttaysa gerekir (bazı oyunlarda hazine penceresi envanteri yeniden konumlandırır).
    // Kalibre edilmezse ClanBankService sessizce Otonom'un ızgarasını kullanmaya devam eder.
    [RelayCommand]
    private Task CalibrateChestInvGridAsync() => CalibrateClanBankRoiAsync(
        "Hazine/depo AÇIKKEN kişisel envanterin sol-üst yuvasının sol-üst köşesine, sonra sağ-alt yuvanın " +
        "sağ-alt köşesine tıkla (yalnız normal envanterden FARKLI konumdaysa gerekli — çerçeveyi DIŞARIDA bırak)",
        (x, y, w, h) => { var cb = _state.ClanBank; cb.ChestInvGridX = x; cb.ChestInvGridY = y; cb.ChestInvGridW = w; cb.ChestInvGridH = h; });

    [RelayCommand]
    private void ResetChestInvGrid()
    {
        var cb = _state.ClanBank;
        cb.ChestInvGridX = cb.ChestInvGridY = cb.ChestInvGridW = cb.ChestInvGridH = 0;
        _store.Save(_state);
        RefreshClanBankStatus();
    }

    // ── Görsel teşhis (2026-07-03): sandık/envanter AÇIKKEN skorlu grid dökümü ──
    // TestSellScanAsync (merchant) deseninin klan-banka eşleniği: pencereyi küçült → oyun öne gelsin →
    // SaveScanDebug → geri getir. Döküm Desktop\FujiMacro_Teshis\bank_*\ altına gider.
    [RelayCommand]
    private async Task TestClanBankScanAsync()
    {
        var mainWindow = System.Windows.Application.Current.MainWindow;
        mainWindow.WindowState = System.Windows.WindowState.Minimized;
        ClanBankStatus = "Envanter taranıyor…";
        try
        {
            await Task.Delay(400);   // oyun öne gelsin (sandık/envanter açık olmalı)
            string dir = Services.Farm.ClanBankService.CreateDumpDir();
            ClanBankStatus = $"{_clanBank.SaveScanDebug(dir)} → {dir}";
        }
        catch (Exception ex) { ClanBankStatus = $"Hata: {ex.Message}"; }
        finally
        {
            mainWindow.WindowState = System.Windows.WindowState.Normal;
            mainWindow.Activate();
        }
    }

    [RelayCommand]
    private void OpenClanBankDiagFolder()
    {
        try
        {
            string root = System.IO.Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "FujiMacro_Teshis");
            System.IO.Directory.CreateDirectory(root);
            System.Diagnostics.Process.Start("explorer.exe", root);
        }
        catch (Exception ex) { ClanBankStatus = $"Klasör açılamadı: {ex.Message}"; }
    }

    // ── Depolanabilir eşya türleri ────────────────────────────────────────────
    [RelayCommand]
    private void AddClanBankItemType()
    {
        var name = (ClanBankNewItemType ?? "").Trim();
        if (name.Length == 0) return;
        if (_state.ClanBank.ItemTypes.Any(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase))) return;
        _state.ClanBank.ItemTypes.Add(new ItemTemplateSet { Name = name });
        _store.Save(_state);
        ClanBankNewItemType = "";
        RefreshClanBankItemTypes();
        RefreshClanBankStatus();
    }

    [RelayCommand]
    private void RemoveClanBankItemType(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        _state.ClanBank.ItemTypes.RemoveAll(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        _store.Save(_state);
        RefreshClanBankItemTypes();
        RefreshClanBankStatus();
    }

    [RelayCommand]
    private async Task TeachClanBankItemAsync(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return;
        var set = _state.ClanBank.ItemTypes.FirstOrDefault(t => t.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (set is null) return;

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow($"'{name}' eşya ikonunun ETRAFINA kutu çiz (envanterde) — sol-üst → sağ-alt", twoCorner: true);
        ov.ShowDialog();
        if (ov.Confirmed)
        {
            await Task.Delay(200);   // overlay çizimi ekrandan silinsin
            var rect = ov.SelectedRect;
            if (rect.Width > 2 && rect.Height > 2)
            {
                using var bmp = WtmVision.CaptureRegion(rect.X, rect.Y, rect.Width, rect.Height);
                if (bmp is not null)
                {
                    var list = (set.TemplatesB64 ?? Array.Empty<string>()).ToList();
                    list.Add(ToB64Png(bmp));
                    set.TemplatesB64 = list.ToArray();
                    _store.Save(_state);
                }
            }
        }
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        RefreshClanBankItemTypes();
        RefreshClanBankStatus();
    }

    // ── Ortak kalibrasyon yardımcıları (Otonom TeachSellIcon / CalibrateMerchantInvGrid deseni) ──
    private async Task CalibrateClanBankRoiAsync(string prompt, Action<int, int, int, int> apply)
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow(prompt, twoCorner: true);
        ov.ShowDialog();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;
        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, ov.SelectedRect.Width, ov.SelectedRect.Height);
        apply(m.X, m.Y, Math.Max(1, m.Width), Math.Max(1, m.Height));
        _store.Save(_state);
        RefreshClanBankStatus();
        await Task.CompletedTask;
    }

    private async Task TeachClanBankTemplateAsync(string prompt, Func<string[]> get, Action<string[]> set)
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow(prompt, twoCorner: true);
        ov.ShowDialog();
        if (ov.Confirmed)
        {
            await Task.Delay(200);
            var rect = ov.SelectedRect;
            if (rect.Width > 2 && rect.Height > 2)
            {
                using var bmp = WtmVision.CaptureRegion(rect.X, rect.Y, rect.Width, rect.Height);
                if (bmp is not null)
                {
                    var list = (get() ?? Array.Empty<string>()).ToList();
                    list.Add(ToB64Png(bmp));
                    set(list.ToArray());
                    _store.Save(_state);
                    ClanBankStatus = "Şablon eklendi";
                }
            }
        }
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        RefreshClanBankStatus();
    }

    private static string ToB64Png(System.Drawing.Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        return Convert.ToBase64String(ms.ToArray());
    }
}
