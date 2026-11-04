namespace AgentStudio.Pipeline;

/// <summary>
/// Why a review generation went to a per-task fallback gate instead of a batch
/// (AGT-3466): the <see cref="PerTaskFallbackRules"/> rule that sent it and the
/// event that triggered the rule. The batch gate state shows it per member.
/// </summary>
public sealed record BatchGateFallbackCause(
    string Project, string TaskKey, string ReviewAttemptId, string RunAttempt,
    string Rule, string Event, string? BatchId, DateTimeOffset RecordedAtUtc);

public sealed partial class BatchGateStore
{
    /// <summary>Causes kept per project; older ones are pruned when a new one is recorded.</summary>
    internal const int RetainedFallbackCauses = 200;

    private readonly object _fallbackCauseLock = new();

    /// <summary>
    /// Records the first cause of a review generation's per-task gate. A later
    /// handoff of the same generation, for example the resume of a gate a
    /// restart interrupted, keeps the cause that sent it there. Returns false
    /// when a cause was already recorded.
    /// </summary>
    public bool RecordFallbackCause(BatchGateFallbackCause cause)
    {
        lock (_fallbackCauseLock)
        {
            var folder = FallbackCauseFolder(cause.Project);
            Directory.CreateDirectory(folder);
            var suffix = "-" + SafeName(cause.ReviewAttemptId) + ".json";
            if (Directory.EnumerateFiles(folder, "*" + suffix).Any()) return false;
            WriteOnce(Path.Combine(folder, $"{cause.RecordedAtUtc.UtcTicks:D19}{suffix}"), cause);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                         .SkipLast(RetainedFallbackCauses).ToArray())
                File.Delete(stale);
            return true;
        }
    }

    /// <summary>
    /// Forgets the cause of a review generation that returned to the batch
    /// queue, so the state no longer shows it as a per-task fallback.
    /// </summary>
    public void ClearFallbackCause(string project, string reviewAttemptId)
    {
        lock (_fallbackCauseLock)
        {
            var folder = FallbackCauseFolder(project);
            if (!Directory.Exists(folder)) return;
            foreach (var path in Directory.EnumerateFiles(folder, "*-" + SafeName(reviewAttemptId) + ".json")
                         .ToArray())
                File.Delete(path);
        }
    }

    /// <summary>The project's retained causes, oldest first.</summary>
    public IReadOnlyList<BatchGateFallbackCause> ListFallbackCauses(string project)
    {
        var folder = FallbackCauseFolder(project);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                .Select(Read<BatchGateFallbackCause>).ToArray()
            : [];
    }

    private string FallbackCauseFolder(string project)
        => Path.Combine(_root, "fallback-causes", SafeName(project));
}
