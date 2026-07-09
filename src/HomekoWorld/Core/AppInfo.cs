namespace HomekoWorld.Core;

/// <summary>Sürüm TEK KAYNAK: csproj &lt;Version&gt;. UI buradan okur — elle senkron gerekmez
/// (1.0.0/1.0.1 ayrışması bir daha yaşanmasın; .iss AppVersion release'te elle eşitlenir).</summary>
public static class AppInfo
{
    public static string Version { get; } =
        System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "?";

    public static string WindowTitle { get; } = $"FujiMacro {Version}";

    public static string TitleSuffix { get; } = $" · {Version}";
}
