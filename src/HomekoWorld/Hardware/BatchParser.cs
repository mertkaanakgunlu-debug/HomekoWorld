using HomekoWorld.Hardware;

namespace HomekoWorld.Hardware;

/// <summary>
/// Parses the EXEC/KEYDOWN:key:ms/KEYUP:key:ms/ENDEXEC batch protocol payload
/// into a sorted list of BatchEvents. Used by both LocalInputTransport and
/// KernelDriverTransport so neither duplicates the parsing logic.
/// </summary>
internal static class BatchParser
{
    internal record struct BatchEvent(int Ms, string Key, bool IsDown);

    internal static List<BatchEvent> Parse(string payload)
    {
        var result = new List<BatchEvent>();
        foreach (var raw in payload.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line == HidBridgeProtocol.ExecBatch ||
                line == HidBridgeProtocol.ExecLoop  ||
                line == HidBridgeProtocol.ExecEnd) continue;

            // Format: KEYDOWN:key:ms  or  KEYUP:key:ms
            var parts = line.Split(':');
            if (parts.Length != 3) continue;
            if (!int.TryParse(parts[2], out int ms)) continue;

            bool isDown;
            if (parts[0].Equals("KEYDOWN", StringComparison.OrdinalIgnoreCase))
                isDown = true;
            else if (parts[0].Equals("KEYUP", StringComparison.OrdinalIgnoreCase))
                isDown = false;
            else continue;

            result.Add(new BatchEvent(ms, parts[1], isDown));
        }
        result.Sort((a, b) => a.Ms.CompareTo(b.Ms));
        return result;
    }
}
