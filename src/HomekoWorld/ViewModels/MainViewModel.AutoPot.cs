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
    partial void OnAutoPotEnabledChanged(bool value)
    {
        _state.AutoPot.Enabled = value;
        _store.Save(_state);
        // Oto Pot "armed" bayrağı; gerçekten YALNIZ ana mod (Active) açıkken pot basar (master gate).
        SyncAutoPotService();
    }

    /// <summary>Ana mod (Active) açılıp kapandıkça Oto Pot'u eşitle — Active master gate'tir.
    /// Active kapalıyken (başlangıç dahil) Oto Pot asla çalışmaz → masaüstünde/odak dışında pot spam'i olmaz.</summary>
    partial void OnActiveChanged(bool value) => SyncAutoPotService();

    /// <summary>Oto Pot servisini master gate'e göre başlat/durdur: yalnız <c>Active &amp;&amp; AutoPot.Enabled</c> iken çalışır.</summary>
    private void SyncAutoPotService()
    {
        if (Active && _state.AutoPot.Enabled) _autoPotService.Start();
        else                                  _autoPotService.Stop();
    }
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
        var key = string.IsNullOrWhiteSpace(_state.AutoPot.StartKey) ? "F11" : _state.AutoPot.StartKey;
        if (!e.Key.ToString().Equals(key, StringComparison.OrdinalIgnoreCase)) return;
        Application.Current.Dispatcher.BeginInvoke(ToggleAutoPot);
    }
}
