namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3856: a per-card full suite the host did not start because a batch
/// gates the card anyway. <see cref="Rule"/> is the per-task rule that had
/// sent the card, <see cref="Reason"/> the refusal in operator words. The card
/// timeline and the batch gate state show it.
/// </summary>
public sealed record BatchGateSuiteRefusal(
    string Project, string TaskKey, string ReviewAttemptId, string RunAttempt,
    string Rule, string Reason, DateTimeOffset RefusedAtUtc);

public sealed partial class BatchGateStore
{
    /// <summary>Refusal history kept for the state and the measurement after a deploy.</summary>
    internal const int RetainedSuiteRefusals = 200;

    private readonly object _suiteRefusalLock = new();

    /// <summary>
    /// Records the one refusal of a review generation. Returns false when the
    /// generation was refused before; the rule refuses each generation once.
    /// </summary>
    public bool RecordSuiteRefusal(BatchGateSuiteRefusal refusal)
    {
        lock (_suiteRefusalLock)
        {
            var folder = SuiteRefusalFolder(refusal.Project);
            Directory.CreateDirectory(folder);
            var suffix = "-" + SafeName(refusal.ReviewAttemptId) + ".json";
            if (Directory.EnumerateFiles(folder, "*" + suffix).Any()) return false;
            WriteOnce(Path.Combine(folder, $"{refusal.RefusedAtUtc.UtcTicks:D19}{suffix}"), refusal);
            foreach (var stale in Directory.EnumerateFiles(folder, "*.json")
                         .Order(StringComparer.Ordinal).SkipLast(RetainedSuiteRefusals).ToArray())
                File.Delete(stale);
            return true;
        }
    }

    public bool HasSuiteRefusal(string project, string reviewAttemptId)
    {
        var folder = SuiteRefusalFolder(project);
        return Directory.Exists(folder)
            && Directory.EnumerateFiles(folder, "*-" + SafeName(reviewAttemptId) + ".json").Any();
    }

    /// <summary>The project's retained refusals, oldest first.</summary>
    public IReadOnlyList<BatchGateSuiteRefusal> ListSuiteRefusals(string project)
    {
        var folder = SuiteRefusalFolder(project);
        return Directory.Exists(folder)
            ? Directory.EnumerateFiles(folder, "*.json").Order(StringComparer.Ordinal)
                .Select(Read<BatchGateSuiteRefusal>).ToArray()
            : [];
    }

    private string SuiteRefusalFolder(string project)
        => Path.Combine(_root, "suite-refusals", SafeName(project));
}
