using CommunityToolkit.Mvvm.ComponentModel;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

public partial class ComboViewModel : ObservableObject
{
    public string Id           { get; }
    public string Name         { get; }
    public string Description  { get; }
    public string ClassId      { get; }
    public string ProfileId    { get; }
    public string BindingLabel { get; }
    public int    StepCount    { get; }
    public bool   IsCustom     { get; }
    public bool   IsBound      { get; }
    public bool   IsLoop       { get; }

    [ObservableProperty] private int    _totalCasts;
    [ObservableProperty] private double _avgSpeedMs;
    [ObservableProperty] private bool   _isFiring;

    public string AvgSpeedDisplay =>
        AvgSpeedMs > 0 ? $"{AvgSpeedMs:F0} ms" : "—";

    public ComboViewModel(Combo combo, ComboStats? stats = null)
    {
        Id           = combo.Id;
        Name         = combo.Name;
        Description  = combo.Description;
        ClassId      = combo.ClassId;
        ProfileId    = combo.ProfileId;
        BindingLabel = combo.Binding?.Display() ?? "—";
        StepCount    = combo.Steps.Count;
        IsCustom     = combo.IsCustom;
        IsBound      = combo.Binding?.IsEmpty == false;
        IsLoop       = combo.IsLoop;

        if (stats is not null)
        {
            _totalCasts  = stats.TotalCasts;
            _avgSpeedMs  = stats.AvgSpeedMs;
        }
    }

    public void ApplyStats(ComboStats stats)
    {
        TotalCasts  = stats.TotalCasts;
        AvgSpeedMs  = stats.AvgSpeedMs;
        OnPropertyChanged(nameof(AvgSpeedDisplay));
    }
}
