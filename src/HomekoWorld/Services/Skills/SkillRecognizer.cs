using System.Drawing;
using HomekoWorld.Models;

namespace HomekoWorld.Services.Skills;

public class SkillRecognizer
{
    private readonly SkillLibrary _library;

    // Slots with distance <= AutoThreshold are auto-assigned without review
    public int AutoThreshold { get; set; } = 15;

    public SkillRecognizer(SkillLibrary library)
    {
        _library = library;
    }

    /// <summary>
    /// Returns the closest library skill and its Hamming distance.
    /// <c>best</c> is never null when the library has entries — caller uses
    /// <c>distance &lt;= AutoThreshold</c> to decide auto-assign vs review.
    /// </summary>
    public (SkillInfo? best, int distance) Recognize(Bitmap cell)
    {
        if (_library.All.Count == 0) return (null, 64);

        var hash    = SkillLibrary.ComputePHashFromBitmap(cell);
        SkillInfo? best    = null;
        int        minDist = 64;

        foreach (var skill in _library.All)
        {
            int d = SkillLibrary.HammingDistance(hash, skill.Hash);
            if (d < minDist) { minDist = d; best = skill; }
        }

        // Always return best guess; caller decides what to do with it
        return (best, minDist);
    }
}
