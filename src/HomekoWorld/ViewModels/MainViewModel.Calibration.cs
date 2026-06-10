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
using HomekoWorld.Services.Capture;
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

        try
        {
            // Oyun ekranı görünsün — ana pencereyi minimize et
            mainWindow.WindowState = WindowState.Minimized;
            await Task.Yield();

            // Master referansını belirle (ilk kalibrasyonsa) — koordinatlar master uzayında saklanır.
            EnsureCalibrationRef();

            // ── Adım 1: Kendi HP barı — iki köşe (sol-üst → sağ-alt) ──────────────
            FarmCalibrationState = "1/4  KENDİ HP barı: sol-üst köşe → sağ-alt köşe (dolu olması gerekmez)";
            var ovHp = new Views.CalibrationOverlayWindow(
                "KENDİ HP barının SOL-ÜST köşesine, sonra SAĞ-ALT köşesine tıkla (dolu olması gerekmez)",
                twoCorner: true);
            ovHp.ShowDialog();
            bool hpOk = false;
            if (ovHp.Confirmed)
            {
                var m = ResolutionMapper.Unmap(
                    ovHp.SelectedRect.X, ovHp.SelectedRect.Y, ovHp.SelectedRect.Width, ovHp.SelectedRect.Height);
                _state.Farm.HpBarLeft   = m.X;
                _state.Farm.HpBarTop    = m.Y;
                _state.Farm.HpBarWidth  = Math.Max(1, m.Width);
                _state.Farm.HpBarHeight = Math.Max(1, m.Height);
                hpOk = true;
            }

            // ── Adım 2: Kendi MP barı — iki köşe ──────────────────────────────────
            FarmCalibrationState = "2/4  KENDİ MP barı: sol-üst köşe → sağ-alt köşe";
            var ovMp = new Views.CalibrationOverlayWindow(
                "KENDİ MP barının SOL-ÜST köşesine, sonra SAĞ-ALT köşesine tıkla",
                twoCorner: true);
            ovMp.ShowDialog();
            bool mpOk = false;
            if (ovMp.Confirmed)
            {
                var m = ResolutionMapper.Unmap(
                    ovMp.SelectedRect.X, ovMp.SelectedRect.Y, ovMp.SelectedRect.Width, ovMp.SelectedRect.Height);
                _state.Farm.MpBarLeft   = m.X;
                _state.Farm.MpBarTop    = m.Y;
                _state.Farm.MpBarWidth  = Math.Max(1, m.Width);
                _state.Farm.MpBarHeight = Math.Max(1, m.Height);
                mpOk = true;
            }

            // ── Adım 3: Mob HP barı — iki köşe (kırmızı bölge) ────────────────────
            FarmCalibrationState = "3/4  MOB HP barının KIRMIZI bölgesi: sol-üst köşe → sağ-alt köşe";
            var ovMob = new Views.CalibrationOverlayWindow(
                "Hedef MOB HP barının KIRMIZI bölgesinin SOL-ÜST sonra SAĞ-ALT köşesine tıkla",
                twoCorner: true);
            ovMob.ShowDialog();
            bool roiOk = false;
            if (ovMob.Confirmed)
            {
                var m = ResolutionMapper.Unmap(
                    ovMob.SelectedRect.X, ovMob.SelectedRect.Y, ovMob.SelectedRect.Width, ovMob.SelectedRect.Height);
                _state.Wtm.HpBarRoiX = m.X;
                _state.Wtm.HpBarRoiY = m.Y;
                _state.Wtm.HpBarRoiW = Math.Max(1, m.Width);
                _state.Wtm.HpBarRoiH = Math.Max(1, m.Height);
                roiOk = true;
            }

            _store.Save(_state);

            // Üstteki "Mob HP barı X=.. Y=.." status'unu TAZELE — yoksa sihirbaz yeni ROI'yi
            // yazar ama o metin yükleme-anı değerinde kalır (kullanıcı yanlış Y görür → "ölü" teşhisi şaşar).
            HpBarRoiPreviewStatus = _state.Wtm.IsHpBarRoiCalibrated
                ? $"✓ Mob HP barı  X={_state.Wtm.HpBarRoiX}  Y={_state.Wtm.HpBarRoiY}  {_state.Wtm.HpBarRoiW}×{_state.Wtm.HpBarRoiH}px"
                : "✗ Kalibre edilmedi (Ana kalibrasyon 3. adım)";

            FarmCalibrationState =
                "✓" +
                (hpOk   ? " HP✓"  : " HP—") +
                (mpOk   ? " MP✓"  : " MP—") +
                (roiOk  ? $" Mob✓ {_state.Wtm.HpBarRoiW}×{_state.Wtm.HpBarRoiH}" : " Mob—");
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
        }
    }

    /// <summary>
    /// Duyuru Δy — oyun bir duyuru gösterince Mob HP barı (+ isim) aşağı kayar. Kullanıcı duyuru
    /// AÇIKKEN kayan barın sol-üst köşesine tek tıklar; normal HpBarRoiY ile farkı AnnounceShiftY
    /// olarak saklanır. Runtime'da alive/nameplate kontrolü alan1 başarısızsa alan2 (+Δy) dener.
    /// </summary>
    [RelayCommand]
    private async Task CalibrateAnnounceShiftAsync()
    {
        if (_isFarmCalibrating) return;
        if (!_state.Wtm.IsHpBarRoiCalibrated)
        {
            FarmCalibrationState = "⚠ Önce Ana Kalibrasyon ile Mob HP barını öğret";
            return;
        }

        _isFarmCalibrating = true;
        var mainWindow = Application.Current.MainWindow;
        try
        {
            mainWindow.WindowState = WindowState.Minimized;
            await Task.Yield();
            EnsureCalibrationRef();

            FarmCalibrationState = "Duyuru AÇIKKEN kayan Mob HP barının SOL-ÜST köşesine TIKLA";
            var ov = new Views.CalibrationOverlayWindow(
                "Duyuru gösterilirken KAYAN Mob HP barının SOL-ÜST köşesine tek tıkla",
                singleClickMode: true);
            ov.ShowDialog();

            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
            if (!ov.Confirmed) { FarmCalibrationState = "İptal edildi"; return; }

            // Tıklanan fiziksel nokta → master uzayı (Unmap). Kayma = kayan Y − normal HpBarRoiY.
            var m = ResolutionMapper.Unmap(ov.SelectedRect.X, ov.SelectedRect.Y, 1, 1);
            int shift = m.Y - _state.Wtm.HpBarRoiY;
            if (shift <= 0)
            {
                FarmCalibrationState =
                    $"⚠ Kayan bar normalden yukarıda/aynı (Δy={shift}). Duyuru AÇIKKEN tekrar dene.";
                return;
            }

            _state.Wtm.AnnounceShiftY = shift;
            _store.Save(_state);
            FarmCalibrationState = $"✓ Duyuru kayması Δy={shift}px kaydedildi";
        }
        catch (OperationCanceledException) { FarmCalibrationState = "İptal edildi"; }
        catch (Exception ex)              { FarmCalibrationState = $"Hata: {ex.Message}"; }
        finally
        {
            _isFarmCalibrating = false;
            mainWindow.WindowState = WindowState.Normal;
            mainWindow.Activate();
        }
    }

    /// <summary>
    /// Master referans çözünürlüğünü belirler: ilk kalibrasyonsa (Ref=0) geçerli fiziksel
    /// ekranı master yapar. Aksi halde mevcut master korunur (baked default 2560×1600);
    /// kalibrasyon tıklamaları <see cref="ResolutionMapper.Unmap"/> ile master uzayına çevrilir
    /// → kullanıcının kendi çözünürlüğünde override daima isabetli kalır.
    /// </summary>
    private void EnsureCalibrationRef()
    {
        if (_state.CalibrationRefWidth <= 0 || _state.CalibrationRefHeight <= 0)
        {
            var (w, h) = ScreenCapture.PhysicalSize();
            _state.CalibrationRefWidth  = w;
            _state.CalibrationRefHeight = h;
        }
        ResolutionMapper.SetMaster(_state.CalibrationRefWidth, _state.CalibrationRefHeight);
    }

    // ── Hedef HP bar renk tarama kalibrasyonu ────────────────────────────────

    [RelayCommand]
    private async Task PreviewHpColorScanAreaAsync()
    {
        if (!_state.Wtm.IsHpBarRoiCalibrated) return;

        var mainWindow = Application.Current.MainWindow;
        mainWindow.WindowState = WindowState.Minimized;
        await Task.Delay(400);

        // Botun GERÇEKTE taradığı (eşlenmiş) bölgeyi göster — kendi ekranında identity.
        var pr = ResolutionMapper.Map(
            _state.Wtm.HpBarRoiX, _state.Wtm.HpBarRoiY, _state.Wtm.HpBarRoiW, _state.Wtm.HpBarRoiH);
        using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
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

        EnsureCalibrationRef();

        // Tıklanan nokta GEÇERLİ ekran pikselidir — örnekleme bu koordinatta yapılır.
        int sx = ov.SelectedRect.X;
        int sy = ov.SelectedRect.Y;

        TargetHpColorCalibStatus = "⏳ Taranıyor…";

        // Tek ekran görüntüsüyle renk örnekle + bar genişliğini tespit et (UI thread'i bloklamaz)
        var (color, halfW) = await Task.Run(() => WtmVision.SampleHpBarAt(sx, sy));

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

        // Master uzayında sakla (Unmap) → kendi ekranında identity geri gelir.
        var mp = ResolutionMapper.Unmap(sx, sy, 1, 1);
        _state.Wtm.HpColorScanX     = mp.X;
        _state.Wtm.HpColorScanY     = mp.Y;
        _state.Wtm.HpColorScanHalfW = ResolutionMapper.UnscaleLen(halfW);
        _state.Wtm.HpColorR         = color.R;
        _state.Wtm.HpColorG         = color.G;
        _state.Wtm.HpColorB         = color.B;
        _store.Save(_state);

        TargetHpColorCalibStatus =
            $"✓ Kalibre  X={mp.X}  Y={mp.Y}  Gen={halfW * 2}px  RGB=({color.R},{color.G},{color.B})";
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
            "Yaratığın isminin SOL-ÜST köşesine, sonra SAĞ-ALT köşesine tıkla — taranacak bant",
            twoCorner: true);
        ov.ShowDialog();

        mainWindow.WindowState = WindowState.Normal;
        mainWindow.Activate();
        if (!ov.Confirmed) return;

        EnsureCalibrationRef();

        // Master uzayında sakla (Unmap) → kendi ekranında identity geri gelir.
        var m = ResolutionMapper.Unmap(
            ov.SelectedRect.X, ov.SelectedRect.Y, ov.SelectedRect.Width, ov.SelectedRect.Height);
        _state.Wtm.NameBandX = m.X;
        _state.Wtm.NameBandY = m.Y;
        _state.Wtm.NameBandW = Math.Max(1, m.Width);
        _state.Wtm.NameBandH = Math.Max(1, m.Height);
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

        // Botun GERÇEKTE taradığı (eşlenmiş) bandı göster — kendi ekranında identity.
        var pr = ResolutionMapper.Map(
            _state.Wtm.NameBandX, _state.Wtm.NameBandY, _state.Wtm.NameBandW, _state.Wtm.NameBandH);
        using var bmp = WtmVision.CaptureRegion(pr.X, pr.Y, pr.Width, pr.Height);
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
