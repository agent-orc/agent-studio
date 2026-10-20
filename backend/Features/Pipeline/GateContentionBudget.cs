namespace AgentStudio.Pipeline;

public sealed record GateResourceEvidence(
    long WallMs,
    double ProcessTreeCpuMs,
    double? AverageHostCpuPercent,
    double? PeakHostCpuPercent,
    long ExternalContentionMs,
    long RecentExternalContentionMs,
    double RecentProcessTreeCpuMs,
    int Samples,
    bool MeasurementAvailable,
    int HostProcessorCount = 0,
    double? AverageExternalCpuPercent = null);

public sealed record GateBudgetExtensionEvidence(long OriginalLimitMs, long EffectiveLimitMs, string Reason, GateResourceEvidence Resources);

/// <summary>One allowance shared by every verification command in a gate run.</summary>
/// <remarks>Parallel test shards (AGT-3232) read and extend it concurrently.</remarks>
internal sealed class GateContentionBudget(TimeSpan originalLimit)
{
    private readonly object _sync = new();
    private TimeSpan _limit = originalLimit;
    private bool _failedTestsObserved;
    private GateBudgetExtensionEvidence? _extension;

    internal TimeSpan OriginalLimit { get; } = originalLimit;
    internal TimeSpan Limit { get { lock (_sync) return _limit; } }
    internal bool FailedTestsObserved
    {
        get { lock (_sync) return _failedTestsObserved; }
        set { lock (_sync) _failedTestsObserved = value; }
    }
    internal GateBudgetExtensionEvidence? Extension { get { lock (_sync) return _extension; } }

    internal bool TryExtend(GateResourceEvidence resources, bool failedTests)
    {
        lock (_sync)
        {
            if (!ShouldExtend(resources, failedTests || _failedTestsObserved, _extension is not null)) return false;
            _limit += TimeSpan.FromMilliseconds(Math.Min(OriginalLimit.TotalMilliseconds * .5, 900_000));
            _extension = new((long)OriginalLimit.TotalMilliseconds, (long)_limit.TotalMilliseconds,
                "host-cpu>=90%; external-cpu>=25%; sustained>=60s; process-tree-progress", resources);
            return true;
        }
    }

    internal static bool ShouldExtend(GateResourceEvidence resources, bool failedTests, bool alreadyExtended)
        => !failedTests && !alreadyExtended && resources.MeasurementAvailable
           && resources.RecentExternalContentionMs >= 60_000
           && resources.RecentProcessTreeCpuMs >= 100;

    internal static bool IsExternalContention(double hostPercent, double treePercent)
        => hostPercent >= 90 && hostPercent - treePercent >= 25;
}
