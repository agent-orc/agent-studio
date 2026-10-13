using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace AgentStudio.Projects;

/// <summary>Reads only the bounded header of local build/test gate logs.</summary>
public static class FlowGateLogEvidence
{
    private static readonly Regex Field = new(@"(?<key>\w+)=(?<value>\S+)", RegexOptions.Compiled);

    public static (IReadOnlyList<FlowSegment> Segments, bool Malformed) Read(string folder)
    {
        var path = Path.Combine(folder, "post-steps");
        if (!Directory.Exists(path)) return ([], false);
        var result = new List<FlowSegment>();
        var malformed = false;
        foreach (var file in Directory.EnumerateFiles(path, "build-test-gate-*.log"))
        {
            try
            {
                using var stream = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                var buffer = new byte[Math.Min(4096, (int)Math.Min(int.MaxValue, stream.Length))];
                var count = stream.Read(buffer, 0, buffer.Length);
                // The writer puts duration, start and queue wait on lines 1, 3 and 5.
                // Stop before the evidence body, while keeping the read capped at 4 KB.
                var header = string.Join('\n', Encoding.UTF8.GetString(buffer, 0, count)
                    .Split('\n').Take(5));
                var fields = Field.Matches(header).Cast<Match>()
                    .GroupBy(match => match.Groups["key"].Value, StringComparer.Ordinal)
                    .ToDictionary(g => g.Key, g => g.Last().Groups["value"].Value, StringComparer.Ordinal);
                if (!fields.TryGetValue("startedAtUtc", out var rawStart)
                    || !DateTime.TryParse(rawStart, CultureInfo.InvariantCulture,
                        DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var start)
                    || !fields.TryGetValue("queueWaitMs", out var rawQueue)
                    || !long.TryParse(rawQueue, CultureInfo.InvariantCulture, out var queueMs)
                    || !fields.TryGetValue("durationMs", out var rawDuration)
                    || !long.TryParse(rawDuration, CultureInfo.InvariantCulture, out var durationMs))
                {
                    malformed = true;
                    continue;
                }
                var queueEnd = start.AddMilliseconds(Math.Max(0, queueMs));
                if (queueEnd > start)
                    result.Add(new FlowSegment("resourceWait", null, "gateHost", null,
                        start, queueEnd, (queueEnd - start).TotalSeconds));
                var end = queueEnd.AddMilliseconds(Math.Max(0, durationMs));
                if (end > queueEnd)
                    result.Add(new FlowSegment("active", "reviewGate", null, null,
                        queueEnd, end, (end - queueEnd).TotalSeconds));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                malformed = true;
            }
        }
        return (result, malformed);
    }
}
