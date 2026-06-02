namespace HomekoWorld.Models;

public enum StepKind { Tap, Cancel, Skill, Movement }

public class ComboStep
{
    public StepKind Kind    { get; set; } = StepKind.Tap;
    public string   Key     { get; set; } = string.Empty;
    public int      HoldMs  { get; set; } = 50;
    public int      DelayMs { get; set; } = 150;

    // Kind=Skill: resolved via SkillSlotMap at runtime
    public string? SkillId { get; set; }

    // Faz 14/15: when true, DelayMs is scaled by FPS ratio and ping offset at runtime.
    // HoldMs is never adapted — key hold duration is hardware-timing, not server-state timing.
    public bool IsAdaptive { get; set; } = false;

    public ComboStep() { }
    public ComboStep(string key, int delayMs, int holdMs = 50)
    {
        Key     = key;
        DelayMs = delayMs;
        HoldMs  = holdMs;
    }
}
