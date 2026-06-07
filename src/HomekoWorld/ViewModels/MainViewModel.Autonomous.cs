using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Models.Autonomous;
using HomekoWorld.Models.Farm;
using HomekoWorld.Services.Capture;
using HomekoWorld.Services.Vision;
using HomekoWorld.Services.Autonomous;

// MainViewModel — Otonom Oyuncu bölümü (partial sınıf devamı)

namespace HomekoWorld.ViewModels;

public partial class MainViewModel
{
    // ── Otonom Oyuncu (Faz 31) ────────────────────────────────────────────────
    [ObservableProperty] private bool _autonomousEnabled;
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(AutonomousStartLabel))]
    private bool _autonomousRunning;
    [ObservableProperty] private string _autonomousStatus    = "Pasif";
    [ObservableProperty] private string _autonomousStateText = "Idle";

    /// <summary>Otonom Oyuncu canlı aktivite akışı (Log HUD ile aynı ActivityEntry tipini kullanır).</summary>
    public ObservableCollection<ActivityEntry> AutonomousActivityLog { get; } = new();

    public string AutonomousStartLabel => AutonomousRunning ? "Otonom Durdur" : "Otonom Başlat";

    private const int AutonomousLogCap = 200;

    /// <summary>Ctor'dan çağrılır — otonom engine event'lerini UI'a bağlar.
    /// (Kalıcı ayar yüklemesi ctor gövdesinde yapılır; MVVMTK0034 gereği backing field yalnız ctor'da set edilir.)</summary>
    private void WireAutonomous()
    {
        // Düşük frekanslı durum geçişleri → doğrudan BeginInvoke (farm'daki coalescing gerekmez).
        _autonomousEngine.StateChanged += (_, s) =>
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                AutonomousStateText = s.ToString();
                AutonomousRunning   = _autonomousEngine.IsRunning;
            });

        _autonomousEngine.StatusChanged += (_, msg) =>
            Application.Current.Dispatcher.BeginInvoke(() => AutonomousStatus = msg);

        _autonomousEngine.ActivityLogged += (_, e) =>
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                AutonomousActivityLog.Add(e);
                while (AutonomousActivityLog.Count > AutonomousLogCap)
                    AutonomousActivityLog.RemoveAt(0);
            });

        // Faz 32: glyph'leri ayarlardan yükle + koordinat durumlarını tazele
        _coordReader.Recognizer.LoadFromSettings(_state.Autonomous);
        RefreshCoordStatus();

        // Faz 32: koordinat ayarlarını yükle
        _coordBinInvert     = _state.Autonomous.CoordBinInvert;
        _coordMinGapPx      = _state.Autonomous.CoordMinGapPx.ToString();
        _coordMinConfidence = _state.Autonomous.MinDigitConfidence.ToString("F2", System.Globalization.CultureInfo.InvariantCulture);

        // Faz 33: envanter ızgara durumunu tazele (satır/sütun KO'da sabit 4×7)
        RefreshInventoryGridStatus();

        // Faz 34: nav ayarlarını yükle + waypoint metinleri
        _navTurnMsPerDeg = _state.Autonomous.NavTurnMsPerDeg;
        RefreshNavWaypoints();

        // Faz 36: merchant satış ayarlarını yükle
        var a36 = _state.Autonomous;
        _merchantClickOffsetX    = a36.MerchantClickOffsetX.ToString();
        _merchantClickOffsetY    = a36.MerchantClickOffsetY.ToString();
        _merchantRightClick      = a36.MerchantRightClick;
        _merchantInteractDelayMs = a36.MerchantInteractDelayMs.ToString();
        _sellTabDelayMs          = a36.SellTabDelayMs.ToString();
        _useSellAllButton        = a36.UseSellAllButton;
        _sellRightClick          = a36.SellRightClick;
        _sellConfirmKey          = a36.SellConfirmKey;
        _sellItemDelayMs         = a36.SellItemDelayMs.ToString();
        _merchantCloseKey        = a36.MerchantCloseKey;
        _merchantCloseDelayMs    = a36.MerchantCloseDelayMs.ToString();
        RefreshMerchantStatus();

        // Faz 37: global hotkey (F10 varsayılan) + telemetri + kalibrasyon özeti
        _autonomousHotKey = a36.HotKey;
        _maxConsecutiveFailures = a36.MaxConsecutiveFailures.ToString();
        _hook.KeyDown += OnAutonomousHotkeyDown;

        // StateChanged tetiklenince telemetriyi de güncelle
        _autonomousEngine.StateChanged += (_, _) =>
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                RefreshAutonomousTelemetry();
                RefreshCalibrationSummary();   // kalibrasyon özeti state değişince tazele
            });

        // Faz 38: 5 sn'de bir telemetri tazele (süre + istatistik canlı gösterimi)
        var telTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(5)
        };
        telTimer.Tick += (_, _) => { if (_autonomousEngine.IsRunning) RefreshAutonomousTelemetry(); };
        telTimer.Start();

        RefreshCalibrationSummary();
        RefreshAutonomousTelemetry();
    }

    partial void OnAutonomousEnabledChanged(bool value)
    {
        _state.Autonomous.Enabled = value;
        _store.Save(_state);
        if (!value && AutonomousRunning) StopAutonomous();
    }

    [RelayCommand]
    private void ToggleAutonomous()
    {
        if (AutonomousRunning) StopAutonomous();
        else                   StartAutonomous();
    }

    private void StartAutonomous()
    {
        if (!Active)
        {
            AutonomousStatus = "⚠ Önce üst menüden 'Başlat' aktif edin";
            return;
        }
        if (_farmEngine.Inferrer is null)
        {
            AutonomousStatus = "⚠ Model yüklü değil — Oto-Farm sekmesinden .onnx seçin";
            return;
        }
        AutonomousActivityLog.Clear();
        _autonomousEngine.Start();
        AutonomousRunning = true;
    }

    private void StopAutonomous()
    {
        _autonomousEngine.Stop();
        AutonomousRunning = false;
        AutonomousStatus  = "Durduruldu";
    }

    // ── Faz 32: Koordinat okuma (ONNX rakam sınıflandırıcı) ───────────────────
    [ObservableProperty] private string _coordGlyphStatus   = "Rakamlar: 0/10 öğretildi";
    [ObservableProperty] private string _coordXRoiStatus    = "X ROI: çizilmedi";
    [ObservableProperty] private string _coordYRoiStatus    = "Y ROI: çizilmedi";
    [ObservableProperty] private string _coordLiveRead      = "—";
    [ObservableProperty] private string _coordSampleX       = "";
    [ObservableProperty] private string _coordSampleY       = "";
    [ObservableProperty] private string _coordCaptureStatus = "";
    [ObservableProperty] private System.Windows.Media.ImageSource? _coordXPreview;
    [ObservableProperty] private System.Windows.Media.ImageSource? _coordYPreview;
    [ObservableProperty] private bool   _coordPreviewVisible;
    [ObservableProperty] private bool   _coordBinInvert;
    [ObservableProperty] private string _coordMinGapPx     = "2";
    [ObservableProperty] private string _coordMinConfidence = "0.60";

    partial void OnCoordBinInvertChanged(bool value)
    {
        _state.Autonomous.CoordBinInvert = value;
        _store.Save(_state);
    }

    partial void OnCoordMinGapPxChanged(string value)
    {
        if (int.TryParse(value, out int v) && v >= 1)
        {
            _state.Autonomous.CoordMinGapPx = v;
            _store.Save(_state);
        }
    }

    partial void OnCoordMinConfidenceChanged(string value)
    {
        if (float.TryParse(value, System.Globalization.NumberStyles.Float,
                           System.Globalization.CultureInfo.InvariantCulture, out float f)
            && f is >= 0.05f and <= 1.0f)
        {
            _state.Autonomous.MinDigitConfidence = f;
            _store.Save(_state);
        }
    }

    private void RefreshCoordStatus()
    {
        var a = _state.Autonomous;
        CoordXRoiStatus = a.CoordXRoiW > 0 ? $"X ROI: ✓ {a.CoordXRoiW}×{a.CoordXRoiH}px" : "X ROI: çizilmedi";
        CoordYRoiStatus = a.CoordYRoiW > 0 ? $"Y ROI: ✓ {a.CoordYRoiW}×{a.CoordYRoiH}px" : "Y ROI: çizilmedi";

        var mask = _coordReader.Recognizer.TaughtMask();
        int n = mask.Count(b => b);
        var counts = string.Join(" ", Enumerable.Range(0, 10)
            .Select(d => $"{d}×{_coordReader.Recognizer.SampleCount(d)}"));
        if (n == 10) CoordGlyphStatus = $"Rakamlar: ✓ 10/10 öğretildi [{counts}]";
        else
        {
            var missing = string.Join(",", Enumerable.Range(0, 10).Where(d => !mask[d]));
            CoordGlyphStatus = $"Rakamlar: {n}/10 öğretildi (eksik: {missing})";
        }
    }

    [RelayCommand]
    private void ResetGlyphs()
    {
        _coordReader.Recognizer.Clear();
        _coordReader.Recognizer.SaveToSettings(_state.Autonomous);
        _store.Save(_state);
        RefreshCoordStatus();
        CoordCaptureStatus = "Tüm glyphler silindi — sıfırdan öğret";
    }

    [RelayCommand]
    private Task CalibrateCoordXRoiAsync() => CalibrateCoordRoiAsync(isX: true);
    [RelayCommand]
    private Task CalibrateCoordYRoiAsync() => CalibrateCoordRoiAsync(isX: false);

    private async Task CalibrateCoordRoiAsync(bool isX)
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            isX ? "Minimap X sayısının üstüne DİKDÖRTGEN çiz (yalnız rakamlar)"
                : "Minimap Y sayısının üstüne DİKDÖRTGEN çiz (yalnız rakamlar)",
            singleClickMode: false);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, ov.SelectedRect.Width, ov.SelectedRect.Height);
        var a = _state.Autonomous;
        if (isX) { a.CoordXRoiX = m.X; a.CoordXRoiY = m.Y; a.CoordXRoiW = Math.Max(1, m.Width); a.CoordXRoiH = Math.Max(1, m.Height); }
        else     { a.CoordYRoiX = m.X; a.CoordYRoiY = m.Y; a.CoordYRoiW = Math.Max(1, m.Width); a.CoordYRoiH = Math.Max(1, m.Height); }
        _store.Save(_state);
        RefreshCoordStatus();
        await PreviewCoordRoisAsync();
    }

    [RelayCommand]
    private async Task PreviewCoordRoisAsync()
    {
        var a = _state.Autonomous;
        if (a.CoordXRoiW <= 0 && a.CoordYRoiW <= 0) { CoordCaptureStatus = "⚠ Önce ROI çiz"; return; }

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        if (a.CoordXRoiW > 0)
        {
            var pr = ResolutionMapper.Map(a.CoordXRoiX, a.CoordXRoiY, a.CoordXRoiW, a.CoordXRoiH);
            using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
            CoordXPreview = BitmapToImageSource(bmp);
        }
        if (a.CoordYRoiW > 0)
        {
            var pr = ResolutionMapper.Map(a.CoordYRoiX, a.CoordYRoiY, a.CoordYRoiW, a.CoordYRoiH);
            using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
            CoordYPreview = BitmapToImageSource(bmp);
        }
        CoordPreviewVisible = true;
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    [RelayCommand]
    private async Task TeachDigitsAsync()
    {
        if (!_state.Autonomous.IsCoordCalibrated) { CoordCaptureStatus = "⚠ Önce X ve Y ROI'lerini çiz"; return; }
        string lx = new string((CoordSampleX ?? string.Empty).Where(char.IsDigit).ToArray());
        string ly = new string((CoordSampleY ?? string.Empty).Where(char.IsDigit).ToArray());
        if (lx.Length == 0 && ly.Length == 0) { CoordCaptureStatus = "⚠ Ekrandaki X ve Y değerlerini yaz"; return; }

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        var xCells = _coordReader.CaptureRoiCells(isX: true);
        var yCells = _coordReader.CaptureRoiCells(isX: false);
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();

        var msg = new System.Text.StringBuilder();
        int taught = TeachFromCells(xCells, lx, msg, "X") + TeachFromCells(yCells, ly, msg, "Y");
        foreach (var c in xCells) c.Dispose();
        foreach (var c in yCells) c.Dispose();

        _coordReader.Recognizer.SaveToSettings(_state.Autonomous);
        _store.Save(_state);
        RefreshCoordStatus();
        CoordCaptureStatus = taught > 0 ? $"{taught} rakam öğretildi (glyph güncellendi). {msg}" : $"Öğretilemedi. {msg}";
    }

    private int TeachFromCells(List<System.Drawing.Bitmap> cells, string label, System.Text.StringBuilder msg, string tag)
    {
        if (label.Length == 0) return 0;
        if (cells.Count != label.Length)
        {
            string hint = cells.Count == 0
                ? "ROI boş — yeniden çiz veya 'Tersine Çevir' dene"
                : cells.Count < label.Length
                    ? $"Rakamlar birleşiyor ({cells.Count} hücre, {label.Length} rakam) — ROI'yi daha geniş/yüksek çiz veya Min Boşluk'u düşür (şu an {_state.Autonomous.CoordMinGapPx}px)"
                    : $"Fazla hücre ({cells.Count} hücre, {label.Length} rakam) — Min Boşluk'u artır";
            msg.Append($"[{tag}: {hint}] ");
            return 0;
        }
        for (int i = 0; i < cells.Count; i++)
            _coordReader.Recognizer.SetGlyph(label[i] - '0', cells[i]);
        return cells.Count;
    }

    [RelayCommand]
    private async Task DiagnoseCoordAsync()
    {
        if (!_state.Autonomous.IsCoordCalibrated) { CoordCaptureStatus = "⚠ Önce X ve Y ROI'lerini çiz"; return; }
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        string dir = AppDomain.CurrentDomain.BaseDirectory;
        var saved = _coordReader.SaveDebugImages(dir);
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        CoordCaptureStatus = saved.Length > 0
            ? $"Teşhis kaydedildi: {string.Join(", ", saved.Select(System.IO.Path.GetFileName))}"
            : "⚠ Kaydedilemedi (ROI eksik?)";
    }

    [RelayCommand]
    private async Task ReadCoordOnceAsync()
    {
        if (!_coordReader.IsReady)
        {
            CoordLiveRead = _coordReader.Recognizer.IsReady ? "ROI kalibre değil" : "Rakamlar öğretilmedi (10/10)";
            return;
        }
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        var (xVal, xDetail) = _coordReader.ReadValueDebug(isX: true);
        var (yVal, yDetail) = _coordReader.ReadValueDebug(isX: false);
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (xVal is not null && yVal is not null)
        {
            CoordLiveRead      = $"X={xVal}  Y={yVal}";
            CoordCaptureStatus = $"X: {xDetail} | Y: {yDetail}";
        }
        else
        {
            CoordLiveRead      = "okunamadı";
            CoordCaptureStatus = $"X: {xDetail} | Y: {yDetail}";
        }
    }

    // ── Faz 34: Navigasyon ────────────────────────────────────────────────────────

    [ObservableProperty] private string _navStatus           = "Hazır";
    [ObservableProperty] private string _navMerchantWaypoint = "ayarlanmadı";
    [ObservableProperty] private string _navPortalWaypoint   = "ayarlanmadı";
    [ObservableProperty] private string _navFarmSpotWaypoint = "ayarlanmadı";
    [ObservableProperty] private string _navTestX            = "";
    [ObservableProperty] private string _navTestY            = "";
    [ObservableProperty] private string _navToleranceCoords  = "15";
    [ObservableProperty] private string _navStepMs           = "800";
    [ObservableProperty] private bool   _navCameraInvert;

    [ObservableProperty]
    private double _navCameraPixPerDeg = 8.5;
    partial void OnNavCameraPixPerDegChanged(double value)
    {
        _state.Autonomous.NavCameraPixPerDeg = (float)value;
        _store.Save(_state);
    }

    [ObservableProperty]
    private double _navTurnMsPerDeg = 10.0;
    partial void OnNavTurnMsPerDegChanged(double value)
    {
        _state.Autonomous.NavTurnMsPerDeg = (float)value;
        _store.Save(_state);
    }

    partial void OnNavCameraInvertChanged(bool value)
    {
        _state.Autonomous.NavCameraInvert = value;
        _store.Save(_state);
    }

    partial void OnNavToleranceCoordsChanged(string value)
    {
        if (int.TryParse(value, out int v) && v > 0)
        {
            _state.Autonomous.NavToleranceCoords = v;
            _store.Save(_state);
        }
    }

    partial void OnNavStepMsChanged(string value)
    {
        if (int.TryParse(value, out int v) && v > 0)
        {
            _state.Autonomous.NavStepMs = v;
            _store.Save(_state);
        }
    }

    private void RefreshNavWaypoints()
    {
        var a = _state.Autonomous;
        NavMerchantWaypoint = (a.MerchantX == 0 && a.MerchantY == 0) ? "ayarlanmadı" : $"({a.MerchantX}, {a.MerchantY})";
        NavPortalWaypoint   = (a.PortalX   == 0 && a.PortalY   == 0) ? "ayarlanmadı" : $"({a.PortalX}, {a.PortalY})";
        NavFarmSpotWaypoint = (a.FarmSpotX == 0 && a.FarmSpotY == 0) ? "ayarlanmadı" : $"({a.FarmSpotX}, {a.FarmSpotY})";
    }

    [RelayCommand]
    private async Task CaptureWaypointMerchantAsync() => await CaptureWaypointAsync("merchant");
    [RelayCommand]
    private async Task CaptureWaypointPortalAsync()   => await CaptureWaypointAsync("portal");
    [RelayCommand]
    private async Task CaptureWaypointFarmSpotAsync() => await CaptureWaypointAsync("farmspot");

    private async Task CaptureWaypointAsync(string kind)
    {
        if (!_coordReader.IsReady)
        {
            NavStatus = "⚠ Koordinat okuyucu hazır değil — Faz 32'yi tamamlayın";
            return;
        }
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);
        var coord = _coordReader.Read();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();

        if (coord is null) { NavStatus = "⚠ Koordinat okunamadı"; return; }

        var a = _state.Autonomous;
        string label;
        switch (kind)
        {
            case "merchant": a.MerchantX = coord.Value.x; a.MerchantY = coord.Value.y; label = "Merchant"; break;
            case "portal"  : a.PortalX   = coord.Value.x; a.PortalY   = coord.Value.y; label = "Portal";   break;
            default        : a.FarmSpotX = coord.Value.x; a.FarmSpotY = coord.Value.y; label = "Farm";     break;
        }
        _store.Save(_state);
        RefreshNavWaypoints();
        NavStatus = $"✓ {label} noktası kaydedildi: ({coord.Value.x}, {coord.Value.y})";
    }

    // ── Dönüş hızı kalibrasyonu (A/D) ──────────────────────────────────────────

    [RelayCommand]
    private async Task CalibrateTurnRateAsync()
    {
        if (!_navigator.IsReady) { NavStatus = "⚠ Koordinat okuyucu hazır değil"; return; }
        NavStatus = "Dönüş kalibrasyonu — D tuşu 2sn basılı kalacak, hazırlan…";
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(800);
        try
        {
            float msPerDeg = await _navigator.CalibrateTurnRateAsync(CancellationToken.None);
            _state.Autonomous.NavTurnMsPerDeg = msPerDeg;
            _store.Save(_state);
            NavTurnMsPerDeg = Math.Round(msPerDeg, 1);
            NavStatus = $"✓ Dönüş hızı: {msPerDeg:0.0} ms/derece ({3600f / msPerDeg:0}°/sn)";
        }
        catch (Exception ex)
        {
            NavStatus = $"Kalibrasyon hatası: {ex.Message}";
        }
        finally
        {
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    // ── Kamera kalibrasyonu (Farm scan overlay için) ─────────────────────────────

    [RelayCommand]
    private async Task CalibrateNavCameraAsync()
    {
        if (!_navigator.IsReady) { NavStatus = "⚠ Koordinat okuyucu hazır değil"; return; }
        NavStatus = "Kamera kalibrasyonu başlıyor — oyun penceresini açın…";
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(600);
        try
        {
            float calibrated = await _navigator.CalibratePixelsPerDegAsync(CancellationToken.None);
            _state.Autonomous.NavCameraPixPerDeg = calibrated;
            _store.Save(_state);
            NavCameraPixPerDeg = Math.Round(calibrated, 1);
            NavStatus = $"✓ Kamera kalibrasyonu: {calibrated:0.0} px/derece";
        }
        catch (Exception ex)
        {
            NavStatus = $"Kalibrasyon hatası: {ex.Message}";
        }
        finally
        {
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    // ── Test navigasyonu ─────────────────────────────────────────────────────────

    private CancellationTokenSource? _navTestCts;

    [RelayCommand]
    private async Task TestNavigateAsync()
    {
        if (!int.TryParse(NavTestX, out int tx) || !int.TryParse(NavTestY, out int ty))
        {
            NavStatus = "⚠ Geçerli X ve Y koordinatı girin";
            return;
        }
        if (!_navigator.IsReady)
        {
            NavStatus = "⚠ Koordinat okuyucu hazır değil — Faz 32'yi tamamlayın";
            return;
        }

        _navTestCts?.Cancel();
        _navTestCts?.Dispose();
        _navTestCts = new CancellationTokenSource();
        var ct = _navTestCts.Token;

        NavStatus = $"Navigasyon başladı → ({tx},{ty})";
        _navigator.StatusChanged += OnNavTestStatus;
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        try
        {
            await _navigator.NavigateToAsync(tx, ty, ct);
            NavStatus = $"✓ Hedefe ulaşıldı: ({tx},{ty})";
        }
        catch (OperationCanceledException)
        {
            NavStatus = "Navigasyon durduruldu";
        }
        catch (Exception ex)
        {
            NavStatus = $"Hata: {ex.Message}";
        }
        finally
        {
            _navigator.StatusChanged -= OnNavTestStatus;
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    [RelayCommand]
    private void StopNavTest()
    {
        _navTestCts?.Cancel();
        NavStatus = "Navigasyon durduruldu";
    }

    private void OnNavTestStatus(object? sender, string msg)
        => Application.Current.Dispatcher.BeginInvoke(() => NavStatus = msg);

    // ── Faz 33: Envanter doluluğu ─────────────────────────────────────────────────

    [ObservableProperty] private string _inventoryGridStatus     = "Izgara: kalibre edilmedi";
    [ObservableProperty] private string _inventoryStatus         = "";
    [ObservableProperty] private string _inventoryCheckEveryKills = "30";
    [ObservableProperty] private double _inventoryFullThreshold  = 0.85;
    [ObservableProperty] private string _inventoryKey            = "I";
    [ObservableProperty] private string _inventoryOpenDelayMs    = "1000";

    partial void OnInventoryCheckEveryKillsChanged(string v)
    {
        if (int.TryParse(v, out int n) && n > 0) { _state.Autonomous.InventoryCheckEveryKills = n; _store.Save(_state); }
    }

    partial void OnInventoryFullThresholdChanged(double v)
    {
        _state.Autonomous.InventoryFullThreshold = (float)v;
        _store.Save(_state);
    }

    partial void OnInventoryKeyChanged(string v)
    {
        _state.Autonomous.InventoryKey = v;
        _store.Save(_state);
    }

    partial void OnInventoryOpenDelayMsChanged(string v)
    {
        if (int.TryParse(v, out int n) && n >= 0) { _state.Autonomous.InventoryOpenDelayMs = n; _store.Save(_state); }
    }

    private void RefreshInventoryGridStatus()
    {
        var a = _state.Autonomous;
        InventoryGridStatus = a.IsInventoryGridCalibrated
            ? $"Izgara: ✓ {a.InventoryGridW}×{a.InventoryGridH}px  ({a.InventoryCols} sütun × {a.InventoryRows} satır = {a.InventoryCols * a.InventoryRows} yuva)"
            : "Izgara: kalibre edilmedi";
    }

    [RelayCommand]
    private async Task CalibrateInventoryGridAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Envanter ızgarasının üstüne DİKDÖRTGEN çiz (yalnız yuva alanı)",
            singleClickMode: false);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, ov.SelectedRect.Width, ov.SelectedRect.Height);
        var a = _state.Autonomous;
        a.InventoryGridX = m.X;
        a.InventoryGridY = m.Y;
        a.InventoryGridW = Math.Max(1, m.Width);
        a.InventoryGridH = Math.Max(1, m.Height);
        _store.Save(_state);
        RefreshInventoryGridStatus();
        InventoryStatus = $"✓ Izgara kaydedildi: {a.InventoryGridW}×{a.InventoryGridH}px";
        await Task.CompletedTask;
    }

    private CancellationTokenSource? _invTestCts;

    [RelayCommand]
    private async Task TestInventoryCheckAsync()
    {
        if (!_inventoryReader.IsReady)
        {
            InventoryStatus = "⚠ Önce ızgara ROI'sini kalibre edin";
            return;
        }
        _invTestCts?.Cancel();
        _invTestCts?.Dispose();
        _invTestCts = new CancellationTokenSource();

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        InventoryStatus = "Envanter kontrol ediliyor…";
        try
        {
            // App minimize olup OYUN öne gelene kadar bekle; yoksa ilk I tuşu hâlâ app'e gider ve
            // kaybolur (envanter açılmaz → kapalı kare okunur; kapatış I'si bu sefer oyuna gidip
            // envanteri AÇIK bırakır). Diğer tüm yakalama komutları da bu 400ms'i kullanır.
            await Task.Delay(400, _invTestCts.Token);
            var (ratio, dbg) = await _inventoryReader.CheckWithDebugAsync(_invTestCts.Token);
            int   pct   = (int)(ratio * 100);
            float thr   = _state.Autonomous.InventoryFullThreshold;
            string head = ratio >= thr
                ? $"DOLU — %{pct} ≥ %{(int)(thr * 100)} (eşik)"
                : $"Boş   — %{pct} < %{(int)(thr * 100)} (eşik)";
            InventoryStatus = $"{head}  ·  teşhis görseli: {dbg}";
        }
        catch (OperationCanceledException)
        {
            InventoryStatus = "İptal edildi";
        }
        catch (Exception ex)
        {
            InventoryStatus = $"Hata: {ex.Message}";
        }
        finally
        {
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    // ── Faz 36: Merchant satış ───────────────────────────────────────────────────

    [ObservableProperty] private string _merchantClickOffsetX    = "0";
    [ObservableProperty] private string _merchantClickOffsetY    = "0";
    [ObservableProperty] private bool   _merchantRightClick      = true;
    [ObservableProperty] private string _merchantInteractDelayMs = "1200";
    [ObservableProperty] private string _sellTabStatus           = "kalibre edilmedi";
    [ObservableProperty] private string _sellTabDelayMs          = "600";
    [ObservableProperty] private bool   _useSellAllButton        = false;
    [ObservableProperty] private string _sellAllButtonStatus     = "kalibre edilmedi";
    [ObservableProperty] private bool   _sellRightClick          = true;
    [ObservableProperty] private string _sellConfirmKey          = "";
    [ObservableProperty] private string _sellItemDelayMs         = "200";
    [ObservableProperty] private string _sellConfirmButtonStatus = "kalibre edilmedi (opsiyonel)";
    [ObservableProperty] private string _merchantCloseKey        = "Escape";
    [ObservableProperty] private string _merchantCloseDelayMs    = "300";
    [ObservableProperty] private string _sellStatus              = "";

    partial void OnMerchantClickOffsetXChanged(string v)
    { if (int.TryParse(v, out int n)) { _state.Autonomous.MerchantClickOffsetX = n; _store.Save(_state); } }
    partial void OnMerchantClickOffsetYChanged(string v)
    { if (int.TryParse(v, out int n)) { _state.Autonomous.MerchantClickOffsetY = n; _store.Save(_state); } }
    partial void OnMerchantRightClickChanged(bool v)
    { _state.Autonomous.MerchantRightClick = v; _store.Save(_state); }
    partial void OnMerchantInteractDelayMsChanged(string v)
    { if (int.TryParse(v, out int n) && n > 0) { _state.Autonomous.MerchantInteractDelayMs = n; _store.Save(_state); } }
    partial void OnSellTabDelayMsChanged(string v)
    { if (int.TryParse(v, out int n) && n >= 0) { _state.Autonomous.SellTabDelayMs = n; _store.Save(_state); } }
    partial void OnUseSellAllButtonChanged(bool v)
    { _state.Autonomous.UseSellAllButton = v; _store.Save(_state); }
    partial void OnSellRightClickChanged(bool v)
    { _state.Autonomous.SellRightClick = v; _store.Save(_state); }
    partial void OnSellConfirmKeyChanged(string v)
    { _state.Autonomous.SellConfirmKey = v; _store.Save(_state); }
    partial void OnSellItemDelayMsChanged(string v)
    { if (int.TryParse(v, out int n) && n >= 0) { _state.Autonomous.SellItemDelayMs = n; _store.Save(_state); } }
    partial void OnMerchantCloseKeyChanged(string v)
    { _state.Autonomous.MerchantCloseKey = v; _store.Save(_state); }
    partial void OnMerchantCloseDelayMsChanged(string v)
    { if (int.TryParse(v, out int n) && n >= 0) { _state.Autonomous.MerchantCloseDelayMs = n; _store.Save(_state); } }

    private void RefreshMerchantStatus()
    {
        var a = _state.Autonomous;
        SellTabStatus       = a.IsSellTabCalibrated          ? $"✓ ({a.SellTabX}, {a.SellTabY})"           : "kalibre edilmedi";
        SellAllButtonStatus = a.IsSellAllButtonCalibrated    ? $"✓ ({a.SellAllButtonX}, {a.SellAllButtonY})" : "kalibre edilmedi";
        SellConfirmButtonStatus = a.IsSellConfirmButtonCalibrated
            ? $"✓ ({a.SellConfirmButtonX}, {a.SellConfirmButtonY})"
            : "kalibre edilmedi (opsiyonel)";
    }

    [RelayCommand]
    private async Task CalibrateSellTabAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow("Satış sekmesine/butonuna TEK TIKLA", singleClickMode: true);
        ov.ShowDialog();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;
        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, 1, 1);
        _state.Autonomous.SellTabX = m.X;
        _state.Autonomous.SellTabY = m.Y;
        _store.Save(_state);
        RefreshMerchantStatus();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CalibrateSellAllButtonAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow("'Hepsini Sat' butonuna TEK TIKLA", singleClickMode: true);
        ov.ShowDialog();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;
        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, 1, 1);
        _state.Autonomous.SellAllButtonX = m.X;
        _state.Autonomous.SellAllButtonY = m.Y;
        _store.Save(_state);
        RefreshMerchantStatus();
        await Task.CompletedTask;
    }

    [RelayCommand]
    private async Task CalibrateSellConfirmButtonAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        var ov = new Views.CalibrationOverlayWindow("Toplu satış ONAY butonuna TEK TIKLA (opsiyonel)", singleClickMode: true);
        ov.ShowDialog();
        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;
        EnsureCalibrationRef();
        var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, 1, 1);
        _state.Autonomous.SellConfirmButtonX = m.X;
        _state.Autonomous.SellConfirmButtonY = m.Y;
        _store.Save(_state);
        RefreshMerchantStatus();
        await Task.CompletedTask;
    }

    private CancellationTokenSource? _sellTestCts;

    [RelayCommand]
    private async Task TestMerchantTradeAsync()
    {
        _sellTestCts?.Cancel();
        _sellTestCts?.Dispose();
        _sellTestCts = new CancellationTokenSource();
        var ct = _sellTestCts.Token;

        SellStatus = "Satış testi başladı…";
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        void OnStatus(object? s, string msg) =>
            Application.Current.Dispatcher.BeginInvoke(() => SellStatus = msg);

        _merchantTrader.StatusChanged += OnStatus;
        try
        {
            int sold = await _merchantTrader.TradeAsync(ct);
            SellStatus = sold < 0 ? "✓ Tümü satıldı (sell-all)" : $"✓ {sold} yuva satıldı";
        }
        catch (OperationCanceledException)
        {
            SellStatus = "İptal edildi";
        }
        catch (Exception ex)
        {
            SellStatus = $"Hata: {ex.Message}";
        }
        finally
        {
            _merchantTrader.StatusChanged -= OnStatus;
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    [RelayCommand]
    private void StopSellTest() { _sellTestCts?.Cancel(); SellStatus = "Durduruldu"; }

    // ── Faz 35: Town TP + portal ──────────────────────────────────────────────────

    [ObservableProperty] private string _townTpKey              = "F7";
    [ObservableProperty] private string _townTpWaitMs           = "10000";
    [ObservableProperty] private string _portalConfirmKey       = "";
    [ObservableProperty] private string _portalWaitMs           = "10000";
    [ObservableProperty] private string _portalClickOffsetX     = "0";
    [ObservableProperty] private string _portalClickOffsetY     = "-60";
    [ObservableProperty] private string _portalInteractDelayMs  = "800";
    [ObservableProperty] private bool   _townRunActive;

    partial void OnTownTpKeyChanged(string v)             { _state.Autonomous.TownTpKey            = v;                             _store.Save(_state); }
    partial void OnTownTpWaitMsChanged(string v)          { if (int.TryParse(v, out int n) && n>0) { _state.Autonomous.TownTpWaitMs = n; _store.Save(_state); } }
    partial void OnPortalConfirmKeyChanged(string v)      { _state.Autonomous.PortalConfirmKey      = v;                             _store.Save(_state); }
    partial void OnPortalWaitMsChanged(string v)          { if (int.TryParse(v, out int n) && n>0) { _state.Autonomous.PortalWaitMs = n; _store.Save(_state); } }
    partial void OnPortalInteractDelayMsChanged(string v) { if (int.TryParse(v, out int n) && n>0) { _state.Autonomous.PortalInteractDelayMs = n; _store.Save(_state); } }
    partial void OnPortalClickOffsetXChanged(string v)    { if (int.TryParse(v, out int n)) { _state.Autonomous.PortalClickOffsetX = n; _store.Save(_state); } }
    partial void OnPortalClickOffsetYChanged(string v)    { if (int.TryParse(v, out int n)) { _state.Autonomous.PortalClickOffsetY = n; _store.Save(_state); } }

    /// <summary>Manuel town yolculuğunu başlat — otonom mod çalışırken farm durur ve GoingToTown'a geçer.</summary>
    [RelayCommand]
    private void GoToTownNow()
    {
        if (!AutonomousRunning)
        {
            NavStatus = "⚠ Otonom mod çalışmıyor — önce 'Otonom Başlat'";
            return;
        }
        _autonomousEngine.RequestGoToTown();
        NavStatus = "Town yolculuğu başlatıldı (GoingToTown)";
    }

    // ── Faz 37: Dayanıklılık + Hotkey + Özet ────────────────────────────────────

    [ObservableProperty] private string _autonomousHotKey          = "F10";
    [ObservableProperty] private string _maxConsecutiveFailures    = "3";
    [ObservableProperty] private string _calibrationSummary        = "";
    [ObservableProperty] private string _autonomousTelemetrySummary = "";

    partial void OnAutonomousHotKeyChanged(string v)
    {
        _state.Autonomous.HotKey = v;
        _store.Save(_state);
    }

    partial void OnMaxConsecutiveFailuresChanged(string v)
    {
        if (int.TryParse(v, out int n) && n >= 1)
        { _state.Autonomous.MaxConsecutiveFailures = n; _store.Save(_state); }
    }

    private void OnAutonomousHotkeyDown(object? sender, Hooks.HookKeyEventArgs e)
    {
        if (!e.Key.ToString().Equals(_state.Autonomous.HotKey, StringComparison.OrdinalIgnoreCase)) return;
        // Hook thread'inden → UI'a yönlendir
        Application.Current.Dispatcher.BeginInvoke(ToggleAutonomous);
    }

    private void RefreshCalibrationSummary()
    {
        var a = _state.Autonomous;
        var mask = _coordReader.Recognizer.TaughtMask();
        bool glyphsOk = mask.All(b => b);

        var lines = new System.Text.StringBuilder();
        lines.AppendLine(a.IsCoordCalibrated && glyphsOk ? "✅ Koordinat okuyucu" : $"❌ Koordinat okuyucu (ROI:{a.IsCoordCalibrated} Glyph:{mask.Count(b => b)}/10)");
        lines.AppendLine(a.IsInventoryGridCalibrated ? "✅ Envanter ızgara" : "❌ Envanter ızgara kalibre edilmedi");
        bool merchantSet = a.MerchantX != 0 || a.MerchantY != 0;
        bool portalSet   = a.PortalX   != 0 || a.PortalY   != 0;
        bool farmSet     = a.FarmSpotX != 0 || a.FarmSpotY != 0;
        lines.AppendLine(merchantSet ? "✅ Merchant waypoint" : "❌ Merchant waypoint ayarlanmadı");
        lines.AppendLine(portalSet   ? "✅ Portal waypoint"   : "❌ Portal waypoint ayarlanmadı");
        lines.AppendLine(farmSet     ? "✅ Farm waypoint"     : "❌ Farm waypoint ayarlanmadı");
        lines.Append    (a.IsFullyConfigured ? "✅ Tam hazır" : "⚠ Kalibrasyon eksik");

        CalibrationSummary = lines.ToString().TrimEnd();
    }

    private void RefreshAutonomousTelemetry()
    {
        var t = _autonomousEngine.Telemetry;
        string elapsed = _autonomousEngine.IsRunning ? $"  |  ⏱ {t.ElapsedText}" : "";
        AutonomousTelemetrySummary =
            $"Tur: {t.TripsCompleted}  |  Satılan: {t.ItemsSold}  |  Hata: {t.Failures}{elapsed}";
    }
}
