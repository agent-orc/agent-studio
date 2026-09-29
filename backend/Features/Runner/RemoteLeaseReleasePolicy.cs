namespace AgentStudio.Runner;

/// <summary>Which server-side handling a remote lease release receives.</summary>
public enum RemoteLeaseReleaseRoute
{
    /// <summary>No agent process started; the per-card failure budget decides Ready or escalation now.</summary>
    PrelaunchInfrastructure,

    /// <summary>A started detached worker disappeared; the AGT-2870 continuation applies.</summary>
    LostWorker,

    /// <summary>Any other release: the lease is freed and nothing else is decided here.</summary>
    Plain,
}

/// <summary>
/// Pure routing for <c>POST /api/runner/lease/release</c> (AGT-2932). The route
/// is decided once, before any side effect, so a run that never started cannot
/// reach the lost-worker continuation built for authority lost mid-run.
/// </summary>
public static class RemoteLeaseReleasePolicy
{
    public const string EnvironmentPreparationFailed = "runner-environment-preparation-failed";
    public const string SalvageFailed = "runner-salvage-failed";
    public const string ResultsHandlingFailed = "runner-results-handling-failed";

    public static readonly IReadOnlyList<string> PrelaunchInfrastructureOutcomes =
        [EnvironmentPreparationFailed, SalvageFailed, ResultsHandlingFailed];

    public static RemoteLeaseReleaseRoute Classify(string? outcome)
    {
        if (outcome is EnvironmentPreparationFailed or SalvageFailed or ResultsHandlingFailed)
            return RemoteLeaseReleaseRoute.PrelaunchInfrastructure;
        return LostWorkerContinuationPolicy.IsLostWorkerRelease(outcome)
            ? RemoteLeaseReleaseRoute.LostWorker
            : RemoteLeaseReleaseRoute.Plain;
    }
}
