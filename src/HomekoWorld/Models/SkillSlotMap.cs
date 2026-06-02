namespace HomekoWorld.Models;

public class SkillSlotMap
{
    public int BarCount  { get; set; } = 8;
    public int SlotCount { get; set; } = 10;

    // Key: "F{bar}.{slot}" (e.g. "F3.7"), Value: SkillInfo.Id (e.g. "archer.multi_shot")
    public Dictionary<string, string> Slots { get; set; } = new();

    public DateTime? CalibratedAt { get; set; }

    public int FilledSlotCount => Slots.Count(kv => !string.IsNullOrEmpty(kv.Value));
    public int TotalSlotCount  => BarCount * SlotCount;
}
