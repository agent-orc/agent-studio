namespace AgentStudio.Git;

/// <summary>
/// The two per-project sweep modes. <see cref="ReportOnly"/> is the default for
/// every project that has not opted in: the sweep classifies and reports, and
/// never calls a delete primitive.
/// </summary>
public static class BranchSweepModes
{
    public const string ReportOnly = "report-only";
    public const string Reclaim = "reclaim";

    public static string Normalize(string? mode)
        => string.Equals(mode?.Trim(), Reclaim, StringComparison.OrdinalIgnoreCase)
            ? Reclaim
            : ReportOnly;
}

/// <summary>
/// Wire names for the ref classes the sweep reports on. They are the stable
/// grouping key for the report, the Activity line, and the operator UI, so they
/// are spelled out here instead of serializing the internal
/// <see cref="BranchNamespace"/> enum.
/// </summary>
public static class BranchSweepClasses
{
    public const string Task = "task";
    public const string Runner = "runner";
    public const string Delivery = "delivery";
    public const string Results = "results";
    public const string Salvage = "salvage";
    public const string Quarantine = "quarantine";
    public const string Protected = "protected";
    public const string Unmanaged = "unmanaged";

    /// <summary>Report order: managed classes first, then the two never-candidate classes.</summary>
    public static readonly IReadOnlyList<string> Order =
        [Task, Runner, Delivery, Results, Salvage, Quarantine, Protected, Unmanaged];

    public static string Of(BranchNamespace ns) => ns switch
    {
        BranchNamespace.Task => Task,
        BranchNamespace.Runner => Runner,
        BranchNamespace.Delivery => Delivery,
        BranchNamespace.ResultsRef => Results,
        BranchNamespace.SalvageRef => Salvage,
        BranchNamespace.QuarantineRef => Quarantine,
        BranchNamespace.Protected => Protected,
        _ => Unmanaged,
    };
}

/// <summary>
/// Everything the sweep observed about one remote ref, before any decision is
/// made. Collected by <see cref="BranchSweepService"/>; consumed only by the
/// pure classifier so the decision matrix stays testable without git.
/// </summary>
public sealed record BranchSweepObservation(
    string Branch,
    string TipSha,
    string TipShortSha,
    DateTimeOffset? TipCommittedAtUtc,
    bool ContainedInMain,
    bool ContainedInDevelop,
    bool MainAvailable,
    bool DevelopAvailable,
    bool CheckedOut,
    string? TaskKey = null,
    string? TaskState = null,
    bool ReferencedByOpenCard = false);

/// <summary>One classified ref in a sweep report.</summary>
public sealed record BranchSweepCandidate(
    string Ref,
    string Class,
    string? TaskKey,
    string? TaskState,
    string TipSha,
    string TipShortSha,
    DateTimeOffset? TipCommittedAtUtc,
    int? AgeDays,
    bool ContainedInMain,
    bool ContainedInDevelop,
    bool ReferencedByOpenCard,
    string Decision,
    bool Eligible,
    string Reason);

/// <summary>
/// Pure classification for the stale-branch sweep (AGT-2794). It derives the
/// ref class and task key from the ref name, then delegates the keep/delete
/// call to the shared <see cref="BranchRetentionPolicy"/> - there is exactly
/// one retention policy in this repository and this is not a second one.
///
/// <para>
/// Protected refs (<c>main</c>, <c>develop</c>, <c>release/*</c>, <c>v*</c>)
/// and refs outside every managed namespace are reported for completeness but
/// are never candidates: the shared policy answers
/// <see cref="BranchRetentionDecision.UnsupportedNamespace"/> for them, and a
/// checked-out ref answers <see cref="BranchRetentionDecision.CheckedOut"/>.
/// </para>
/// </summary>
public static class BranchSweepPolicy
{
    /// <summary>
    /// Age buckets for the report histogram. A ref lands in the first bucket
    /// whose upper bound it is below; the last bucket is open-ended.
    /// </summary>
    public static readonly IReadOnlyList<(string Label, int? UpperBoundDays)> AgeBuckets =
    [
        ("0-6 days", 7),
        ("7-29 days", 30),
        ("30-89 days", 90),
        ("90-364 days", 365),
        ("365+ days", null),
    ];

    private const string UnknownAgeBucket = "unknown age";

    public static BranchSweepCandidate Classify(
        BranchSweepObservation observation,
        DateTimeOffset now,
        BranchRetentionWindows windows)
    {
        var ns = BranchRetentionPolicy.ClassifyNamespace(observation.Branch);
        var taskKey = observation.TaskKey ?? DeriveTaskKey(observation.Branch);
        var facts = new BranchRetentionFacts(
            observation.Branch,
            ns,
            observation.TipCommittedAtUtc,
            observation.CheckedOut,
            observation.DevelopAvailable,
            observation.MainAvailable,
            observation.ContainedInDevelop,
            observation.ContainedInMain,
            IsTaskTerminal: IsTerminal(observation.TaskState),
            // A ref whose name carries no key, or whose key matches no card, is
            // an orphan: the class age rule decides it instead of a task fact.
            TaskResolved: observation.TaskState is not null,
            TaskArchived: string.Equals(observation.TaskState, TaskStates.Archive, StringComparison.Ordinal),
            ReferencedByOpenCard: observation.ReferencedByOpenCard);

        var decision = BranchRetentionPolicy.Evaluate(facts, now, windows);
        var eligible = BranchRetentionPolicy.IsDeletion(decision);
        return new BranchSweepCandidate(
            observation.Branch,
            BranchSweepClasses.Of(ns),
            taskKey,
            observation.TaskState,
            observation.TipSha,
            observation.TipShortSha,
            observation.TipCommittedAtUtc,
            AgeDays(observation.TipCommittedAtUtc, now),
            observation.ContainedInMain,
            observation.ContainedInDevelop,
            observation.ReferencedByOpenCard,
            decision.ToString(),
            eligible,
            BranchRetentionPolicy.ReasonFor(decision));
    }

    /// <summary>
    /// Task key encoded in a managed ref name, or null when the class does not
    /// carry one. Mirrors the layouts written by <c>runner/GitWorkspace.cs</c>:
    /// <c>task/&lt;key&gt;</c>, <c>delivery/&lt;key&gt;</c>,
    /// <c>runner/&lt;runnerId&gt;/&lt;key&gt;</c>, and
    /// <c>agent-studio/{salvage,quarantine}/&lt;runnerId&gt;/&lt;key&gt;/...</c>.
    /// Result refs are keyed by run-attempt id and carry no task key at all.
    /// </summary>
    public static string? DeriveTaskKey(string branch)
    {
        if (string.IsNullOrWhiteSpace(branch)) return null;
        var parts = branch.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return BranchRetentionPolicy.ClassifyNamespace(branch) switch
        {
            BranchNamespace.Task or BranchNamespace.Delivery => At(parts, 1),
            BranchNamespace.Runner => At(parts, 2),
            BranchNamespace.SalvageRef or BranchNamespace.QuarantineRef => At(parts, 3),
            _ => null,
        };
    }

    /// <summary>Whole days between the tip commit and now; null when the tip has no date.</summary>
    public static int? AgeDays(DateTimeOffset? tipCommittedAtUtc, DateTimeOffset now)
        => tipCommittedAtUtc is null
            ? null
            : Math.Max(0, (int)(now - tipCommittedAtUtc.Value).TotalDays);

    public static string AgeBucketOf(int? ageDays)
    {
        if (ageDays is null) return UnknownAgeBucket;
        foreach (var (label, upper) in AgeBuckets)
        {
            if (upper is null || ageDays < upper) return label;
        }
        return AgeBuckets[^1].Label;
    }

    /// <summary>Every bucket label in histogram order, including the unknown-age bucket.</summary>
    public static IReadOnlyList<string> AgeBucketLabels()
        => [.. AgeBuckets.Select(bucket => bucket.Label), UnknownAgeBucket];

    private static bool IsTerminal(string? taskState)
        => taskState is TaskStates.Archive or TaskStates.Completed;

    private static string? At(string[] parts, int index)
    {
        if (index >= parts.Length) return null;
        var value = parts[index].Trim();
        return value.Length == 0 ? null : value;
    }
}
