namespace HomekoWorld.Models;

public class Combo
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Name { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string ClassId   { get; set; } = "rogue";
    public string ProfileId { get; set; } = "all";
    public bool IsCustom { get; set; }
    public bool IsLoop   { get; set; }
    public KeyBinding? Binding { get; set; }
    public List<ComboStep> Steps { get; set; } = [];
}
