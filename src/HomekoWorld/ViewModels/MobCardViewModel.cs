using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;
using HomekoWorld.Models.Farm;

namespace HomekoWorld.ViewModels;

public partial class MobCardViewModel : ObservableObject
{
    private static readonly string[] Palette =
    [
        "#FF1A3A2A", "#FF1A1A4A", "#FF3A1A1A", "#FF1A3A3A",
        "#FF3A2A1A", "#FF2A1A3A", "#FF1A2A3A", "#FF3A3A1A",
    ];

    [ObservableProperty] private bool _isSelected;

    public string NameTr { get; }
    public string NameEn { get; }
    public bool   HasImage    { get; }
    public string? ImagePath  { get; }
    public string PlaceholderColor { get; }

    public MobCardViewModel(MobInfo mob)
    {
        NameTr = mob.NameTr;
        NameEn = mob.NameEn;

        var baseDir = AppContext.BaseDirectory;
        foreach (var ext in new[] { ".png", ".gif", ".jpg" })
        {
            var path = Path.Combine(baseDir, "Assets", "Mobs", mob.NameEn + ext);
            if (File.Exists(path)) { ImagePath = path; break; }
        }
        HasImage = ImagePath is not null;

        var idx = Math.Abs(mob.NameEn.GetHashCode()) % Palette.Length;
        PlaceholderColor = Palette[idx];
    }
}
