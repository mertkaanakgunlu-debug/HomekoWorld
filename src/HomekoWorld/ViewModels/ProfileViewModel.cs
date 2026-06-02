using CommunityToolkit.Mvvm.ComponentModel;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

public partial class ProfileViewModel : ObservableObject
{
    public string Id   { get; init; } = "";
    public string Name { get; init; } = "";
    [ObservableProperty] private bool _isActive;

    public ProfileViewModel() { }
    public ProfileViewModel(Profile p) { Id = p.Id; Name = p.Name; }
}
