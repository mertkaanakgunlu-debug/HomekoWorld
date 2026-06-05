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
    private void StopLoop()
    {
        _engine.CancelAll();
    }

    [RelayCommand]
    private void SelectProfile(string? profile)
    {
        if (profile is null) return;
        CurrentProfile   = profile;
        _state.ProfileId = profile;
        SaveState();
    }

    [RelayCommand]
    private void SelectClass(string? classId)
    {
        if (classId is null) return;
        CurrentClass   = classId;
        _state.ClassId = classId;
        SaveState();
    }

    [RelayCommand]
    private void StartAddProfile()
    {
        NewProfileName  = "";
        IsAddingProfile = true;
    }

    [RelayCommand]
    private void ConfirmNewProfile()
    {
        var name = NewProfileName.Trim();
        if (string.IsNullOrEmpty(name))
        {
            IsAddingProfile = false;
            return;
        }
        var profile = new Models.Profile { Name = name };
        _state.Profiles.Add(profile);
        Profiles.Add(new ProfileViewModel(profile));
        IsAddingProfile  = false;
        NewProfileName   = "";
        CurrentProfile   = profile.Id;
        _state.ProfileId = profile.Id;
        SaveState();
    }

    [RelayCommand]
    private void CancelNewProfile()
    {
        IsAddingProfile = false;
        NewProfileName  = "";
    }

    [RelayCommand]
    private void NewCombo()
    {
        Editor.LoadNew(CurrentClass == "all" ? "archer" : CurrentClass);
        SyncEditorProfiles();
        SyncEditorClasses();
        IsEditing = true;
    }

    [RelayCommand]
    private void EditCombo(ComboViewModel? vm)
    {
        if (vm is null) return;
        var combo = _state.Combos.First(c => c.Id == vm.Id);
        Editor.LoadFrom(combo);
        SyncEditorProfiles();
        SyncEditorClasses();
        IsEditing = true;
    }

    [RelayCommand]
    private void DeleteCombo(string? id)
    {
        if (id is null) return;
        _state.Combos.RemoveAll(c => c.Id == id);
        var vm = Combos.FirstOrDefault(c => c.Id == id);
        if (vm is not null) Combos.Remove(vm);
        ApplyFilter();
        SaveState();
        _dispatcher.SetCombos(_state.Combos);
    }

    [RelayCommand]
    private void DuplicateCombo(string? id)
    {
        if (id is null) return;
        var src = _state.Combos.FirstOrDefault(c => c.Id == id);
        if (src is null) return;
        var clone = new Combo
        {
            Id          = Guid.NewGuid().ToString(),
            Name        = src.Name + " (kopya)",
            Description = src.Description,
            ProfileId   = src.ProfileId,
            ClassId     = src.ClassId,
            IsCustom    = true,
            IsLoop      = src.IsLoop,
            Binding     = null,
            Steps       = [.. src.Steps.Select(s => new ComboStep(s.Key, s.DelayMs, s.HoldMs) { Kind = s.Kind, SkillId = s.SkillId, IsAdaptive = s.IsAdaptive })],
        };
        _state.Combos.Add(clone);
        Combos.Add(new ComboViewModel(clone));
        ApplyFilter();
        SaveState();
        _dispatcher.SetCombos(_state.Combos);
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnCurrentProfileChanged(string value)
    {
        foreach (var p in Profiles) p.IsActive = p.Id == value;
        ApplyFilter();
    }

    partial void OnCurrentClassChanged(string value)
    {
        foreach (var c in Classes) c.IsActive = c.Id == value;
        _state.ClassId = value;
        _store.Save(_state);
        // Hotkey filtresi: aktif sınıfın combo'ları ve global ('all') combo'lar çalışsın
        _dispatcher.SetCombos(_state.Combos.Where(c => c.ClassId == "all" || value == "all" || c.ClassId == value).ToList());
        ApplyFilter();
        RefreshFarmAvailableCombos();
    }

    private void ApplyFilter()
    {
        Filtered.Clear();
        var q = SearchText.Trim().ToLowerInvariant();
        foreach (var vm in Combos)
        {
            // Class filter: aktif sınıfa göre filtrele
            if (CurrentClass != "all" && vm.ClassId != "all" && vm.ClassId != CurrentClass)
                continue;
            // Profile filter
            if (CurrentProfile != "all" && vm.ProfileId != CurrentProfile)
                continue;
            if (string.IsNullOrEmpty(q) ||
                vm.Name.ToLowerInvariant().Contains(q) ||
                vm.Description.ToLowerInvariant().Contains(q))
                Filtered.Add(vm);
        }
    }

    private void LoadProfiles()
    {
        Profiles.Clear();
        foreach (var p in _state.Profiles)
            Profiles.Add(new ProfileViewModel(p) { IsActive = p.Id == CurrentProfile });
    }

    private void SyncEditorProfiles() => Editor.SyncProfiles(Profiles);
    private void SyncEditorClasses()  => Editor.SyncClasses(Classes);

    private void LoadClasses()
    {
        Classes.Clear();
        foreach (var c in _state.Classes)
            Classes.Add(new CharacterClassViewModel(c) { IsActive = c.Id == CurrentClass });
    }

    private void LoadCombos()
    {
        Combos.Clear();
        foreach (var c in _state.Combos)
        {
            _state.Stats.TryGetValue(c.Id, out var stats);
            Combos.Add(new ComboViewModel(c, stats));
        }
        // Hotkey filtresi: sadece aktif sınıfın combo'larını dispatch'e ver
        _dispatcher.SetCombos(_state.Combos.Where(c => c.ClassId == CurrentClass).ToList());
        RefreshFarmAvailableCombos();
    }

    private void RefreshFarmAvailableCombos()
    {
        FarmAvailableCombos.Clear();
        foreach (var vm in Combos.Where(c => c.ClassId == CurrentClass))
            FarmAvailableCombos.Add(vm);
    }

    private void CommitEdit()
    {
        var combo = Editor.ToCombo();
        var idx   = _state.Combos.FindIndex(c => c.Id == combo.Id);
        if (idx >= 0)
        {
            _state.Combos[idx] = combo;
            _state.Stats.TryGetValue(combo.Id, out var existingStats);
            var vmIdx = Combos.ToList().FindIndex(c => c.Id == combo.Id);
            if (vmIdx >= 0) Combos[vmIdx] = new ComboViewModel(combo, existingStats);
        }
        else
        {
            _state.Combos.Add(combo);
            Combos.Add(new ComboViewModel(combo));
        }
        ApplyFilter();
        SaveState();
        _dispatcher.SetCombos(_state.Combos);
        IsEditing = false;
    }

    // ── Adaptive delay (Faz 14/15) ────────────────────────────────────────────

    partial void OnAdaptPingEnabledChanged(bool value) { PushAdaptiveSettings(); SaveAdaptiveState(); }
    partial void OnPingMultiplierChanged(double value)  { PushAdaptiveSettings(); SaveAdaptiveState(); }
    partial void OnAdaptFpsEnabledChanged(bool value)   { PushAdaptiveSettings(); SaveAdaptiveState(); }
    partial void OnCalibrationFpsChanged(int value)     { PushAdaptiveSettings(); SaveAdaptiveState(); }
    partial void OnCurrentFpsInputChanged(string value) { PushAdaptiveSettings(); SaveAdaptiveState(); }

    /// <summary>
    /// Builds an immutable AdaptiveSettings snapshot and pushes it to ComboEngine.
    /// Also refreshes the live preview string shown in the UI.
    /// </summary>
    private void PushAdaptiveSettings()
    {
        var currentFps = DelayCalculator.ParseFpsInput(CurrentFpsInput);
        var ping       = _pingEma >= 0 ? _pingEma : 0;

        var s = new AdaptiveSettings(
            AdaptPingEnabled, PingMultiplier, ping,
            AdaptFpsEnabled,  CalibrationFps, currentFps);

        _engine.AdaptiveSettings = s;

        // Update live preview for 350ms sample (typical skill-wait delay)
        AdaptPreview = DelayCalculator.Preview(350, AdaptPingEnabled || AdaptFpsEnabled, s);
    }

    private void SaveAdaptiveState()
    {
        _state.AdaptPingEnabled = AdaptPingEnabled;
        _state.PingMultiplier   = PingMultiplier;
        _state.AdaptFpsEnabled  = AdaptFpsEnabled;
        _state.CalibrationFps   = CalibrationFps;
        _state.CurrentFpsInput  = CurrentFpsInput;
        _store.Save(_state);
    }

    // ── Walk to Mob (WtM) ────────────────────────────────────────────────────

    partial void OnWtmEnabledChanged(bool value)
    {
        _state.Wtm.Enabled = value;
        _store.Save(_state);

        // WtM yalnızca Active=ON iken uyandırılır
        if (value && _state.Active)
        {
            _mouseHook.Start();
            _wtmEngine.Start();
            WtmStatus = "Pasif — hedef bekliyor";
        }
        else
        {
            _wtmEngine.Stop();
            if (!_isWtmCalibrating) _mouseHook.Stop();
            WtmStatus = value ? "Pasif — Active OFF" : "Pasif";
        }
    }

    partial void OnWtmComboIdChanged(string? value)
    {
        _state.Wtm.ComboId = value;
        _store.Save(_state);
    }

    // Called from OnCalibrationMouseDown when calibration is active.
    private Task<System.Drawing.Point> WaitForCalibClickAsync()
    {
        // RunContinuationsAsynchronously: TrySetResult mouse hook thread'inde çağrılır;
        // kalibrasyon devamı o thread'de senkron koşmasın (hook'u bloke etmesin).
        _calibClickTcs = new TaskCompletionSource<System.Drawing.Point>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        return _calibClickTcs.Task;
    }

    private void OnCalibrationMouseDown(object? sender, System.Drawing.Point p)
    {
        if (_isWtmCalibrating)
            _calibClickTcs?.TrySetResult(p);
        // Farm now uses CalibrationOverlayWindow (ShowDialog) — no click TCS needed
    }

}
