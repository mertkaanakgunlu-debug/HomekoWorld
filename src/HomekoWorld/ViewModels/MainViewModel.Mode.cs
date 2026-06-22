using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Hooks;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

/// <summary>
/// FujiMacro birleşik mod sistemi. Tek anda tek ana mod çalışır (Kombo / Oto Farm /
/// Otonom / Yarı-Oto). Tek başlat/durdur tuşu (varsayılan F12) seçili modu aç/kapatır.
/// <see cref="Active"/> = "seçili mod çalışıyor mu". Oto Pot bağımsızdır (F11).
/// </summary>
public partial class MainViewModel
{
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ActiveModeLabel))]
    [NotifyPropertyChangedFor(nameof(FarmStartLabel))]
    private OperationMode _selectedMode = OperationMode.Kombo;

    /// <summary>HUD'da gösterilen aktif mod adı (sabit "Oto Farm" yerine). SelectedMode'a bağlı.</summary>
    public string ActiveModeLabel => SelectedMode switch
    {
        OperationMode.OtoFarm => "Oto Farm",
        OperationMode.Otonom  => "Otonom",
        OperationMode.YariOto => "Yarı-Oto",
        _                     => "Kombo",
    };

    /// <summary>CurrentPage (sidebar seçimi) → çalıştırılacak mod.</summary>
    private static OperationMode PageToMode(string? page) => page switch
    {
        "farm"       => OperationMode.OtoFarm,
        "autonomous" => OperationMode.Otonom,
        "semi"       => OperationMode.YariOto,
        _            => OperationMode.Kombo,
    };

    /// <summary>Seçili modu CurrentPage'den türet (sidebar seçimi modu belirler).</summary>
    private void SyncSelectedModeFromPage() => SelectedMode = PageToMode(CurrentPage);

    /// <summary>Tek başlat/durdur giriş noktası (F12 + toolbar/sayfa butonları).</summary>
    [RelayCommand]
    private void ToggleSelectedMode()
    {
        if (Active) StopSelectedMode();
        else        StartSelectedMode();
    }

    private void StartSelectedMode()
    {
        switch (SelectedMode)
        {
            case OperationMode.Kombo:
                _dispatcher.SetActive(true);
                Active = true;
                LogMessage = "Kombo modu aktif — tuş kombinasyonları dinleniyor.";
                break;

            case OperationMode.OtoFarm:
                StartFarm();
                Active = FarmRunning;           // model yoksa StartFarm başlatmaz → Active false kalır
                break;

            case OperationMode.Otonom:
                StartAutonomous();
                Active = AutonomousRunning;
                break;

            case OperationMode.YariOto:
                LogMessage = "Yarı-Oto (Genie) modu yakında eklenecek.";
                break;
        }
    }

    private void StopSelectedMode()
    {
        switch (SelectedMode)
        {
            case OperationMode.Kombo:   _dispatcher.SetActive(false); break;
            case OperationMode.OtoFarm: StopFarm();                   break;
            case OperationMode.Otonom:  StopAutonomous();             break;
        }
        Active = false;
    }

    /// <summary>
    /// Genel başlat/durdur tuşu (varsayılan <see cref="AppState.GlobalStartKey"/>, F12).
    /// Kill-switch: çalışıyorsa motoru hook thread'inde ANINDA durdur (UI donsa bile),
    /// UI bookkeeping'i dispatcher'a bırak.
    /// </summary>
    private void OnModeToggleHotkeyDown(object? sender, HookKeyEventArgs e)
    {
        var key = string.IsNullOrWhiteSpace(_state.GlobalStartKey) ? "F12" : _state.GlobalStartKey;
        if (!e.Key.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return;
        e.Handled = true;

        if (Active) StopActiveEngineImmediate();   // anında kill-switch (hook thread)
        Application.Current.Dispatcher.BeginInvoke(ToggleSelectedMode);
    }

    /// <summary>Çalışan modun motorunu doğrudan (hook thread) durdurur — UI bookkeeping ToggleSelectedMode'da.</summary>
    private void StopActiveEngineImmediate()
    {
        switch (SelectedMode)
        {
            case OperationMode.OtoFarm: _farmEngine.Stop();       break;
            case OperationMode.Otonom:  _autonomousEngine.Stop(); break;
            case OperationMode.Kombo:   _dispatcher.SetActive(false); break;
        }
        // Kill-switch: Oto Pot'u da anında durdur (UI donsa/BeginInvoke gecikse bile pot basmaya devam etmesin).
        _autoPotService.Stop();
    }
}
