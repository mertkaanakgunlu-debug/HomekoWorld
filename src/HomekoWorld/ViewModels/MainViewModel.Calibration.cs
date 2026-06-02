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
    [RelayCommand]
    private async Task CalibrateWtmAsync()
    {
        if (_isWtmCalibrating) return;
        _isWtmCalibrating = true;
        _wtmEngine.PauseForCalibration = true;

        // Make sure the mouse hook is running during calibration
        bool hooksStartedForCalib = false;
        if (!_state.Wtm.Enabled)
        {
            _mouseHook.Start();
            hooksStartedForCalib = true;
        }

        try
        {
            // Step 1 — character center (karakterin üstüne tıkla)
            WtmCalibrationState = "1/2  Karakterinizin üzerine tıklayın…";
            var charPt = await WaitForCalibClickAsync();
            _state.Wtm.CharacterCenterX = charPt.X;
            _state.Wtm.CharacterCenterY = charPt.Y;

            // Step 2 — ring colour (yeşil halkanın üstüne tıkla)
            WtmCalibrationState = "2/2  Yeşil halkanın üzerine tıklayın…";
            var ringPt    = await WaitForCalibClickAsync();
            var ringColor = WtmVision.SamplePixel(ringPt);
            WtmVision.RgbToHsv(ringColor, out float hue, out _, out _);
            _state.Wtm.RingHue = (int)hue;

            _store.Save(_state);
            WtmCalibrationState = "✓ Kalibre edildi";
            await Task.Delay(2000);
        }
        catch (OperationCanceledException)
        {
            WtmCalibrationState = "İptal edildi";
            await Task.Delay(1000);
        }
        finally
        {
            WtmCalibrationState = "";
            _isWtmCalibrating   = false;
            _wtmEngine.PauseForCalibration = false;

            if (hooksStartedForCalib && !_state.Wtm.Enabled)
                _mouseHook.Stop();
        }
    }

    [RelayCommand]
    private async Task CalibrateFarmCenterAsync()
    {
        if (_isFarmCalibrating) return;
        _isFarmCalibrating = true;

        var mainWindow = Application.Current.MainWindow;
        bool success   = false;

        try
        {
            // Oyun ekranı görünsün — ana pencereyi minimize et
            mainWindow.WindowState = WindowState.Minimized;

            // ── Adım 1: Karakter merkezi (kutu çiz) ──────────────────────
            FarmCalibrationState = "1/4  Karakterinin etrafını sürükle…";
            var ov1 = new Views.CalibrationOverlayWindow(
                "Karakterinizin etrafını sürükleyin — merkez otomatik hesaplanır");
            ov1.ShowDialog();
            if (!ov1.Confirmed) throw new OperationCanceledException();

            var r1 = ov1.SelectedRect;
            int cx = r1.X + r1.Width / 2;
            int cy = r1.Y + r1.Height / 2;
            _state.Farm.CharacterCenterX = cx;
            _state.Farm.CharacterCenterY = cy;
            _state.Wtm.CharacterCenterX  = cx;
            _state.Wtm.CharacterCenterY  = cy;

            // ── Adım 2: Kendi HP barı — tam dolu iken kutu çiz ──────────
            FarmCalibrationState = "2/4  KENDİ HP barını DOLU iken soldan sağa çiz…";
            var ov2 = new Views.CalibrationOverlayWindow(
                "KENDİ HP barını TAMAMEN DOLU iken soldan sağa sürükleyin (auto-pot için)");
            ov2.ShowDialog();

            bool hpOk = false;
            if (ov2.Confirmed)
            {
                var r2    = ov2.SelectedRect;
                int hpMidX = r2.X + r2.Width  / 2;
                int hpMidY = r2.Y + r2.Height / 2;
                var hpCol  = WtmVision.SamplePixel(new System.Drawing.Point(hpMidX, hpMidY));

                // Farm auto-pot için kendi HP barı
                _state.Farm.HpBarLeft  = r2.X;
                _state.Farm.HpBarY     = hpMidY;
                _state.Farm.HpBarWidth = r2.Width;
                _state.Farm.HpBarFullR = hpCol.R;
                _state.Farm.HpBarFullG = hpCol.G;
                _state.Farm.HpBarFullB = hpCol.B;
                // NOT: WtmVision.HpBarSample = hedef MOB HP barı — ayrı adımda kalibre edilir
                hpOk = true;
            }

            // ── Adım 3: Kendi MP barı — tam dolu iken kutu çiz ──────────
            FarmCalibrationState = "3/4  KENDİ MP barını DOLU iken soldan sağa çiz…";
            var ov3 = new Views.CalibrationOverlayWindow(
                "MP barını TAMAMEN DOLU iken soldan sağa sürükleyin");
            ov3.ShowDialog();

            bool mpOk = false;
            if (ov3.Confirmed)
            {
                var r3    = ov3.SelectedRect;
                int mpMidY = r3.Y + r3.Height / 2;
                var mpCol  = WtmVision.SamplePixel(
                    new System.Drawing.Point(r3.X + r3.Width / 2, mpMidY));

                _state.Farm.MpBarLeft  = r3.X;
                _state.Farm.MpBarY     = mpMidY;
                _state.Farm.MpBarWidth = r3.Width;
                _state.Farm.MpBarFullR = mpCol.R;
                _state.Farm.MpBarFullG = mpCol.G;
                _state.Farm.MpBarFullB = mpCol.B;
                mpOk = true;
            }

            // ── Adım 4: ML HP bar ROI (sabit boyut: eğitim boyutlarıyla aynı) ────
            FarmCalibrationState = "4/4  Mobu seç → dikdörtgeni HP bar panelinin sol-üst köşesine getir → tıkla";
            var ov4 = new Views.CalibrationOverlayWindow(
                "Dikdörtgeni HP bar panelinin sol-üst köşesine getirip tıklayın (boyut sabittir)",
                singleClickMode: false,
                fixedSize: new System.Drawing.Size(459, 114));
            ov4.ShowDialog();

            bool roiOk = false;
            if (ov4.Confirmed)
            {
                _state.Wtm.HpBarRoiX = ov4.SelectedRect.X;
                _state.Wtm.HpBarRoiY = ov4.SelectedRect.Y;
                _state.Wtm.HpBarRoiW = Math.Max(1, ov4.SelectedRect.Width);
                _state.Wtm.HpBarRoiH = Math.Max(1, ov4.SelectedRect.Height);
                roiOk = true;
            }

            _store.Save(_state);
            success = true;

            FarmCalibrationState =
                $"✓ ({cx},{cy})" +
                (hpOk  ? " HP✓"  : " HP—") +
                (mpOk  ? " MP✓"  : " MP—") +
                (roiOk ? $" ROI✓ {_state.Wtm.HpBarRoiW}×{_state.Wtm.HpBarRoiH}" : " ROI—");
        }
        catch (OperationCanceledException)
        {
            FarmCalibrationState = "İptal edildi";
        }
        catch (Exception ex)
        {
            FarmCalibrationState = $"Hata: {ex.Message}";
        }
        finally
        {
            _isFarmCalibrating = false;
            // Pencereyi geri getir
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
            // Başarılı kalibrasyonda mesajı koruyoruz; hata/iptal durumunda da görünür bırak
            _ = success; // intentional — success state kept in FarmCalibrationState
        }
    }

    // ── Hedef HP bar renk tarama kalibrasyonu ────────────────────────────────

    [RelayCommand]
    private async Task PreviewHpColorScanAreaAsync()
    {
        if (!_state.Wtm.IsHpBarRoiCalibrated) return;

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);

        using var bmp = WtmVision.CaptureRegion(
            _state.Wtm.HpBarRoiX, _state.Wtm.HpBarRoiY,
            _state.Wtm.HpBarRoiW, _state.Wtm.HpBarRoiH);
        HpColorScanPreview = BitmapToImageSource(bmp);
        HpColorScanPreviewVisible = true;

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private static System.Windows.Media.ImageSource BitmapToImageSource(System.Drawing.Bitmap bmp)
    {
        using var ms = new System.IO.MemoryStream();
        bmp.Save(ms, System.Drawing.Imaging.ImageFormat.Png);
        ms.Position = 0;
        var bi = new System.Windows.Media.Imaging.BitmapImage();
        bi.BeginInit();
        bi.StreamSource = ms;
        bi.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    [RelayCommand]
    private async Task CalibrateTargetHpColorAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Hedef HP bar'ının KIRMIZI bölgesine tıklayın — bot bu rengi ve konumu öğrenecek",
            singleClickMode: true);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();

        if (!ov.Confirmed) return;

        int x = ov.SelectedRect.X;
        int y = ov.SelectedRect.Y;

        TargetHpColorCalibStatus = "⏳ Taranıyor…";

        // Tek ekran görüntüsüyle renk örnekle + bar genişliğini tespit et (UI thread'i bloklamaz)
        var (color, halfW) = await Task.Run(() => WtmVision.SampleHpBarAt(x, y));

        // Kırmızı kontrolü: R baskın olmalı
        if (color.R < 100 || color.R < color.G + 40)
        {
            System.Windows.MessageBox.Show(
                $"Seçilen piksel kırmızı görünmüyor (RGB: {color.R},{color.G},{color.B}).\n" +
                "Lütfen HP bar'ının kırmızı iç bölgesine tıklayın.",
                "HP Bar Kalibrasyonu", System.Windows.MessageBoxButton.OK,
                System.Windows.MessageBoxImage.Warning);
            TargetHpColorCalibStatus = "✗ İptal — kırmızı piksel seçilmedi";
            return;
        }

        _state.Wtm.HpColorScanX     = x;
        _state.Wtm.HpColorScanY     = y;
        _state.Wtm.HpColorScanHalfW = halfW;
        _state.Wtm.HpColorR         = color.R;
        _state.Wtm.HpColorG         = color.G;
        _state.Wtm.HpColorB         = color.B;
        _store.Save(_state);

        TargetHpColorCalibStatus =
            $"✓ Kalibre  X={x}  Y={y}  Gen={halfW * 2}px  RGB=({color.R},{color.G},{color.B})";
    }

    partial void OnGuardianDetectionEnabledChanged(bool value)
    {
        _state.Wtm.GuardianDetectionEnabled = value;
        _store.Save(_state);
    }

    // ── Koruma mobu nameplate kalibrasyon komutları ───────────────────────────

    [RelayCommand]
    private async Task CalibrateNormalNameAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Normal mob isminin üzerine tıklayın (MOR renk) — referans renk öğrenilecek",
            singleClickMode: true);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        var color = await Task.Run(() => WtmVision.SamplePixel(
            new System.Drawing.Point(ov.SelectedRect.X, ov.SelectedRect.Y)));
        _state.Wtm.NormalNameR = color.R;
        _state.Wtm.NormalNameG = color.G;
        _state.Wtm.NormalNameB = color.B;
        _store.Save(_state);
        UpdateNameplateCalibStatus();
    }

    [RelayCommand]
    private async Task CalibrateGuardianNameAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Koruma mobu isminin üzerine tıklayın (KIRMIZI renk) — referans renk öğrenilecek",
            singleClickMode: true);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        var color = await Task.Run(() => WtmVision.SamplePixel(
            new System.Drawing.Point(ov.SelectedRect.X, ov.SelectedRect.Y)));
        _state.Wtm.GuardianNameR = color.R;
        _state.Wtm.GuardianNameG = color.G;
        _state.Wtm.GuardianNameB = color.B;
        _store.Save(_state);
        UpdateNameplateCalibStatus();
    }

    private void UpdateNameplateCalibStatus()
    {
        NameplateCalibStatus = _state.Wtm.IsNameplateCalibrated
            ? $"✓ Normal RGB=({_state.Wtm.NormalNameR},{_state.Wtm.NormalNameG},{_state.Wtm.NormalNameB})  " +
              $"Koruma RGB=({_state.Wtm.GuardianNameR},{_state.Wtm.GuardianNameG},{_state.Wtm.GuardianNameB})"
            : "✗ Kalibre edilmedi";
    }

    // İsim bandını dikdörtgenle çiz — HSV kırmızı tespiti bu bandı tarar.
    // HP bar ML ROI kalibrasyonuyla (sürükle-bırak) aynı UX.
    [RelayCommand]
    private async Task CalibrateNameBandAsync()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Yaratığın ismini DİKDÖRTGENLE çiz (soldan sağa) — taranacak bant",
            singleClickMode: false);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        _state.Wtm.NameBandX = ov.SelectedRect.X;
        _state.Wtm.NameBandY = ov.SelectedRect.Y;
        _state.Wtm.NameBandW = Math.Max(1, ov.SelectedRect.Width);
        _state.Wtm.NameBandH = Math.Max(1, ov.SelectedRect.Height);
        _store.Save(_state);

        NameBandCalibStatus = $"✓ İsim bandı  X={_state.Wtm.NameBandX}  Y={_state.Wtm.NameBandY}  " +
                              $"{_state.Wtm.NameBandW}×{_state.Wtm.NameBandH}px";

        await PreviewNameBandAsync(); // çizilen bandı hemen göster
    }

    // Çizilen isim bandını ekrandan yakalayıp önizleme görüntüsü olarak göster.
    [RelayCommand]
    private async Task PreviewNameBandAsync()
    {
        if (!_state.Wtm.IsNameBandCalibrated) return;

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);

        using var bmp = WtmVision.CaptureRegion(
            _state.Wtm.NameBandX, _state.Wtm.NameBandY,
            _state.Wtm.NameBandW, _state.Wtm.NameBandH);
        NameBandPreview = BitmapToImageSource(bmp);
        NameBandPreviewVisible = true;

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
    }

    private void TryLoadHpBarClassifier(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        var resolvedPath = System.IO.Path.IsPathRooted(path)
            ? path
            : System.IO.Path.Combine(AppContext.BaseDirectory, path);
        try
        {
            var clf = new HpBarPresenceClassifier();
            clf.Load(resolvedPath, _state.Farm.InferenceBackend);
            (_farmEngine.HpClassifier as IDisposable)?.Dispose();
            _farmEngine.HpClassifier = clf.IsLoaded ? clf : null;
            if (clf.IsLoaded)
                System.Diagnostics.Debug.WriteLine($"[HpBarClassifier] Yüklendi: {resolvedPath}");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[HpBarClassifier] Yüklenemedi: {ex.Message}");
        }
    }

    // ── Test Click — click injection diagnostic ───────────────────────────────

    // Test Click global hotkey handler — oyun önde iken çalışır
    private void OnTestClickHotkeyDown(object? sender, HookKeyEventArgs e)
    {
        if (!FarmEnabled) return;
        var key = string.IsNullOrWhiteSpace(_state.Farm.TestClickKey) ? "F8" : _state.Farm.TestClickKey;
        if (!e.Key.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return;
        Application.Current.Dispatcher.BeginInvoke(() => _ = RunTestClickAsync());
    }

    [RelayCommand]
    private void CalibrateTestClick()
    {
        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;

        var ov = new Views.CalibrationOverlayWindow(
            "Test tıklaması için bir noktaya tıklayın — bot bu noktaya tıklayacak",
            singleClickMode: true);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();

        if (!ov.Confirmed) return;

        _state.Farm.TestClickX = ov.SelectedRect.X;
        _state.Farm.TestClickY = ov.SelectedRect.Y;
        _store.Save(_state);

        TestClickStatus = $"Nokta kaydedildi: ({_state.Farm.TestClickX}, {_state.Farm.TestClickY})";
    }

    [RelayCommand]
    private async Task RunTestClickAsync()
    {
        int x = _state.Farm.TestClickX;
        int y = _state.Farm.TestClickY;

        if (x == 0 && y == 0)
        {
            TestClickStatus = "⚠ Önce 'Nokta Seç' ile kalibre et";
            return;
        }

        TestClickStatus = $"→ ({x},{y}) — imleç gidiyor…";
        await _router.MoveAbsAsync(x, y);
        await Task.Delay(200); // cursor settle + oyunun hover state güncellemesi
        TestClickStatus = $"→ ({x},{y}) — tıklanıyor…";
        await _router.ClickAsync(Hardware.MouseButton.Left);
        TestClickStatus = $"✓ ({x},{y}) — click gönderildi. Oyunda animasyon/yürüme gördün mü?";
    }
}
