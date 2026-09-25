using System.Text.Json;

namespace AgentRunner;

/// <summary>Bounded, per-repository evidence of normalized review failures.</summary>
internal static class ReviewFailureFingerprintHistory
{
    private sealed record Occurrence(DateTime AtUtc, string TaskId, string Fingerprint);
    private static readonly object Gate = new();

    private static string PathFor(string root, string repositoryId)
        => Path.Combine(root, "fingerprints", RemoteReviewWorkspace.HashText(repositoryId) + ".jsonl");

    internal static bool SeenOnOtherCard(
        string root, string repositoryId, string fingerprint, string taskId, DateTime nowUtc)
    {
        var path = PathFor(root, repositoryId);
        lock (Gate)
        {
            if (!File.Exists(path)) return false;
            foreach (var line in File.ReadLines(path))
            {
                Occurrence? occurrence;
                try { occurrence = JsonSerializer.Deserialize<Occurrence>(line); }
                catch (JsonException) { continue; }
                if (occurrence is not null && occurrence.AtUtc >= nowUtc.AddHours(-24)
                    && occurrence.AtUtc <= nowUtc && occurrence.Fingerprint == fingerprint
                    && !string.Equals(occurrence.TaskId, taskId, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }

    internal static void Record(
        string root, string repositoryId, string fingerprint, string taskId, DateTime nowUtc)
    {
        var path = PathFor(root, repositoryId);
        lock (Gate)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, JsonSerializer.Serialize(new Occurrence(nowUtc, taskId, fingerprint)) + "\n");
        }
    }
}
