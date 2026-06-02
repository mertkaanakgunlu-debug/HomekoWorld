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
    // ── Global AutoPot persist ────────────────────────────────────────────────
    partial void OnGlobalStartKeyChanged(string value)  { _state.GlobalStartKey  = value; _store.Save(_state); }
    partial void OnLanguageChanged(string value)        { _state.Language = value; _store.Save(_state); LogMessage = value == "en" ? "Language change takes effect on restart." : "Dil değişikliği yeniden başlatmada geçerli olur."; }
    partial void OnKeyboardTestKeyChanged(string value) { _state.KeyboardTestKey = value; _store.Save(_state); }
    partial void OnAutoPotEnabledChanged(bool value)    { _state.AutoPot.Enabled      = value; _store.Save(_state); }
    partial void OnAutoPotHpEnabledChanged(bool value)  { _state.AutoPot.HpPotEnabled = value; _store.Save(_state); }
    partial void OnAutoPotHpPercentChanged(int value)   { _state.AutoPot.HpPotPercent = value; _store.Save(_state); }
    partial void OnAutoPotHpKeyChanged(string value)    { _state.AutoPot.HpPotKey     = value; _store.Save(_state); }
    partial void OnAutoPotMpEnabledChanged(bool value)  { _state.AutoPot.MpPotEnabled = value; _store.Save(_state); }
    partial void OnAutoPotMpPercentChanged(int value)   { _state.AutoPot.MpPotPercent = value; _store.Save(_state); }
    partial void OnAutoPotMpKeyChanged(string value)    { _state.AutoPot.MpPotKey     = value; _store.Save(_state); }
    partial void OnAutoPotStartKeyChanged(string value) { _state.AutoPot.StartKey     = value; _store.Save(_state); }

    [RelayCommand]
    private void ToggleAutoPot()
    {
        AutoPotEnabled = !AutoPotEnabled;
    }

    private void OnAutoPotHotkeyDown(object? sender, HookKeyEventArgs e)
    {
        if (!Active) return;
        var key = string.IsNullOrWhiteSpace(_state.AutoPot.StartKey) ? "F11" : _state.AutoPot.StartKey;
        if (!e.Key.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return;
        Application.Current.Dispatcher.BeginInvoke(ToggleAutoPot);
    }
}
