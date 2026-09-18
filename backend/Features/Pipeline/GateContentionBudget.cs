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
internal sealed class GateContentionBudget(TimeSpan originalLimit)
{
    internal TimeSpan OriginalLimit { get; } = originalLimit;
    internal TimeSpan Limit { get; private set; } = originalLimit;
    internal bool FailedTestsObserved { get; set; }
    internal GateBudgetExtensionEvidence? Extension { get; private set; }

    internal bool TryExtend(GateResourceEvidence resources, bool failedTests)
    {
        if (!ShouldExtend(resources, failedTests || FailedTestsObserved, Extension is not null)) return false;
        Limit += TimeSpan.FromMilliseconds(Math.Min(OriginalLimit.TotalMilliseconds * .5, 900_000));
        Extension = new((long)OriginalLimit.TotalMilliseconds, (long)Limit.TotalMilliseconds,
            "host-cpu>=90%; external-cpu>=25%; sustained>=60s; process-tree-progress", resources);
        return true;
    }

    internal static bool ShouldExtend(GateResourceEvidence resources, bool failedTests, bool alreadyExtended)
        => !failedTests && !alreadyExtended && resources.MeasurementAvailable
           && resources.RecentExternalContentionMs >= 60_000
           && resources.RecentProcessTreeCpuMs >= 100;

    internal static bool IsExternalContention(double hostPercent, double treePercent)
        => hostPercent >= 90 && hostPercent - treePercent >= 25;
}
