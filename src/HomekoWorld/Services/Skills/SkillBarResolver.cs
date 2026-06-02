using HomekoWorld.Engine;
using HomekoWorld.Models;

namespace HomekoWorld.Services.Skills;

public class SkillBarResolver : ISkillResolver
{
    private readonly AppState _state;

    public int ActiveBarIndex { get; set; } = 0;

    public SkillBarResolver(AppState state)
    {
        _state = state;
    }

    // Returns (barIndex 0-based, slotKey) or null if skill not found
    public (int barIndex, string slotKey)? Resolve(string skillId)
    {
        foreach (var (key, sid) in _state.SkillBar.Slots)
        {
            if (sid != skillId) continue;
            // key format: "F{bar}.{slot}" e.g. "F3.7"
            var dot = key.IndexOf('.');
            if (dot < 2) continue;
            if (!int.TryParse(key[1..dot], out var barNum)) continue;
            var slotKey = key[(dot + 1)..];
            return (barNum - 1, slotKey); // barIndex is 0-based
        }
        return null;
    }
}
