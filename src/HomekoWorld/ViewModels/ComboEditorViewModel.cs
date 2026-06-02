using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

public partial class ComboEditorViewModel : ObservableObject
{
    [ObservableProperty] private string _id = Guid.NewGuid().ToString();
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _description = string.Empty;
    [ObservableProperty] private string _classId = "archer";
    [ObservableProperty] private string _profileId = "all";
    [ObservableProperty] private string _bindingDisplay = "—";
    [ObservableProperty] private bool _isCapturing;
    [ObservableProperty] private bool _isCustom;
    [ObservableProperty] private bool _isLoop;

    public KeyBinding? Binding { get; private set; }
    public ObservableCollection<StepViewModel>          Steps             { get; } = [];
    public ObservableCollection<ProfileViewModel>        AvailableProfiles { get; } = [];
    public ObservableCollection<CharacterClassViewModel> AvailableClasses  { get; } = [];

    public event EventHandler? Saved;
    public event EventHandler? Cancelled;

    // Raised when user clicks "+ Skill Ekle"; subscriber should show picker and invoke callback
    public event Func<string?>? RequestSkillPick;

    public void LoadFrom(Combo combo)
    {
        Id          = combo.Id;
        Name        = combo.Name;
        Description = combo.Description;
        ClassId     = combo.ClassId;
        ProfileId   = combo.ProfileId;
        IsCustom    = combo.IsCustom;
        IsLoop      = combo.IsLoop;
        Binding     = combo.Binding;
        BindingDisplay = combo.Binding?.Display() ?? "—";

        Steps.Clear();
        foreach (var s in combo.Steps)
            Steps.Add(new StepViewModel { Kind = s.Kind, SkillId = s.SkillId, Key = s.Key, HoldMs = s.HoldMs, DelayMs = s.DelayMs, IsAdaptive = s.IsAdaptive });
    }

    public void LoadNew(string classId = "archer")
    {
        Id          = Guid.NewGuid().ToString();
        Name        = string.Empty;
        Description = string.Empty;
        ClassId     = classId;
        ProfileId   = "all";
        IsCustom    = true;
        IsLoop      = false;
        Binding     = null;
        BindingDisplay = "—";
        Steps.Clear();
    }

    public void SetBinding(KeyBinding? binding)
    {
        Binding = binding;
        BindingDisplay = binding?.Display() ?? "—";
        IsCapturing = false;
    }

    public void SyncProfiles(IEnumerable<ProfileViewModel> mainProfiles)
    {
        AvailableProfiles.Clear();
        foreach (var p in mainProfiles)
        {
            var vm = new ProfileViewModel { Id = p.Id, Name = p.Name };
            vm.IsActive = p.Id == ProfileId;
            AvailableProfiles.Add(vm);
        }
    }

    public void SyncClasses(IEnumerable<CharacterClassViewModel> mainClasses)
    {
        AvailableClasses.Clear();
        foreach (var c in mainClasses.Where(c => c.Id != "all"))
        {
            var vm = new CharacterClassViewModel { Id = c.Id, Name = c.Name, IconKey = c.IconKey };
            vm.IsActive = c.Id == ClassId;
            AvailableClasses.Add(vm);
        }
    }

    partial void OnProfileIdChanged(string value)
    {
        foreach (var p in AvailableProfiles) p.IsActive = p.Id == value;
    }

    partial void OnClassIdChanged(string value)
    {
        foreach (var c in AvailableClasses) c.IsActive = c.Id == value;
    }

    [RelayCommand]
    private void SelectProfile(string? profile)
    {
        if (profile is not null) ProfileId = profile;
    }

    [RelayCommand]
    private void SelectClass(string? classId)
    {
        if (classId is not null) ClassId = classId;
    }

    [RelayCommand]
    private void AddTapStep() => Steps.Add(new StepViewModel { Kind = StepKind.Tap, Key = "1", HoldMs = 50, DelayMs = 150 });

    [RelayCommand]
    private void AddCancelStep() => Steps.Add(new StepViewModel { Kind = StepKind.Cancel, Key = "W", HoldMs = 20, DelayMs = 0 });

    [RelayCommand]
    private void AddMovementStep() => Steps.Add(new StepViewModel { Kind = StepKind.Movement, Key = "W", HoldMs = 20, DelayMs = 0 });


    [RelayCommand]
    private void AddSkillStep()
    {
        // Raise event so the view can open SkillPickerDialog and return the selected skill id
        var skillId = RequestSkillPick?.Invoke();
        if (skillId is null) return; // user cancelled picker
        Steps.Add(new StepViewModel { Kind = StepKind.Skill, SkillId = skillId, Key = string.Empty, HoldMs = 50, DelayMs = 200 });
    }

    [RelayCommand]
    private void RemoveStep(StepViewModel step) => Steps.Remove(step);

    [RelayCommand]
    private void MoveUp(StepViewModel step)
    {
        var idx = Steps.IndexOf(step);
        if (idx > 0) Steps.Move(idx, idx - 1);
    }

    [RelayCommand]
    private void MoveDown(StepViewModel step)
    {
        var idx = Steps.IndexOf(step);
        if (idx < Steps.Count - 1) Steps.Move(idx, idx + 1);
    }

    [RelayCommand]
    private void Save()
    {
        if (string.IsNullOrWhiteSpace(Name)) return;
        Saved?.Invoke(this, EventArgs.Empty);
    }

    [RelayCommand]
    private void Cancel() => Cancelled?.Invoke(this, EventArgs.Empty);

    public Combo ToCombo() => new()
    {
        Id          = Id,
        Name        = Name,
        Description = Description,
        ClassId     = ClassId,
        ProfileId   = ProfileId,
        IsCustom    = IsCustom,
        IsLoop      = IsLoop,
        Binding     = Binding,
        Steps       = [.. Steps.Select(s => new ComboStep(s.Key, s.DelayMs, s.HoldMs) { Kind = s.Kind, SkillId = s.SkillId, IsAdaptive = s.IsAdaptive })],
    };
}

public partial class StepViewModel : ObservableObject
{
    [ObservableProperty] private StepKind _kind       = StepKind.Tap;
    [ObservableProperty] private string   _key        = string.Empty;
    [ObservableProperty] private string?  _skillId;
    [ObservableProperty] private int      _holdMs     = 50;
    [ObservableProperty] private int      _delayMs    = 150;
    /// <summary>
    /// When true, DelayMs is adjusted at runtime by the global adaptive settings
    /// (Faz 14: ping offset, Faz 15: FPS scale). HoldMs is always fixed.
    /// </summary>
    [ObservableProperty] private bool     _isAdaptive = false;
}
