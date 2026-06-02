namespace HomekoWorld.Models;

public class KeyBinding
{
    public string[] Modifiers { get; set; } = [];
    public string Code { get; set; } = string.Empty;

    public bool IsEmpty => string.IsNullOrEmpty(Code);

    public string Display()
    {
        if (IsEmpty) return "—";
        if (Modifiers.Length == 0) return Code;
        return string.Join("+", Modifiers) + "+" + Code;
    }
}
