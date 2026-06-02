using CommunityToolkit.Mvvm.ComponentModel;
using HomekoWorld.Models;

namespace HomekoWorld.ViewModels;

public partial class CharacterClassViewModel : ObservableObject
{
    public string Id      { get; init; } = "";
    public string Name    { get; init; } = "";
    public string IconKey { get; init; } = "";
    [ObservableProperty] private bool _isActive;

    public CharacterClassViewModel() { }
    public CharacterClassViewModel(CharacterClass c) { Id = c.Id; Name = c.Name; IconKey = c.IconKey; }
}
