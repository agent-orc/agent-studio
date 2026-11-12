namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3694: one wait of a per-task fallback gate that passed its bound. The
/// kind is one of <see cref="AgentStudio.OrchestrationDecisions.PerTaskFallbackWaitKinds"/>;
/// an engine decision stall names the occurrence, its key and its status.
/// <paramref name="Outcome"/> says what the pilot did: <c>escalated</c>,
/// <c>cancelled</c>, <c>detached</c> or <c>reported</c>.
/// </summary>
public sealed record BatchGateFallbackStall(
    string Project, string TaskKey, string ReviewAttemptId, string Kind,
    DateTimeOffset SinceUtc, DateTimeOffset RaisedAtUtc, string Reason, string Outcome,
    string? EngineRunId = null, string? EngineStatus = null, string? OccurrenceKey = null);

public sealed partial class BatchGateStore
{
    /// <summary>Stall events kept per project; older ones are pruned when a new one is recorded.</summary>
    internal const int RetainedFallbackStalls = 100;

    private readonly object _fallbackStallLock = new();

    /// <summary>
    /// Records a stall event once per review generation, kind, wait start and
    /// outcome, so a replayed tick does not raise the same stall twice.
    /// Returns false when the event was already recorded.
    /// </summary>
    public bool RecordFallbackStall(BatchGateFallbackStall stall)
    {
        lock (_fallbackStallLock)
        {
            var folder = FallbackStallFolder(stall.Project);
            Directory.CreateDirectory(folder);
            var suffix = "-" + SafeName(string.Join("\n", stall.ReviewAttemptId, stall.Kind,
                stall.EngineRunId ?? "", stall.SinceUtc.UtcTicks, stall.Outcome)) + ".json";
            if (Directory.EnumerateFiles(folder, "*" + suffix).Any()) return false;
            WriteOnce(Path.Combine(folder, $"{stall.RaisedAtUtc.UtcTicks:D19}{suffix}"), stall);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                         .SkipLast(RetainedFallbackStalls).ToArray())
                File.Delete(stale);
            return true;
        }
    }

    /// <summary>The project's newest <paramref name="take"/> fallback stall events, oldest first.</summary>
    public IReadOnlyList<BatchGateFallbackStall> ListFallbackStalls(string project,
        int take = RetainedFallbackStalls)
    {
        var folder = FallbackStallFolder(project);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                .TakeLast(Math.Max(0, take)).Select(Read<BatchGateFallbackStall>).ToArray()
            : [];
    }

    private string FallbackStallFolder(string project)
        => Path.Combine(_root, "fallback-stalls", SafeName(project));
}
