namespace HomekoWorld.Engine;

public interface ISkillResolver
{
    int ActiveBarIndex { get; set; }

    // Returns (barIndex 0-based, slotKey "1"–"0") or null if skill not found in slot map
    (int barIndex, string slotKey)? Resolve(string skillId);
}
