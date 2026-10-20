using System.Globalization;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// Converts legacy policy measurements into decision grounds. These methods
/// describe observations only; dispatch, process ceilings and authority floors
/// remain separate mechanical checks.
/// </summary>
public static class PolicySignalProducers
{
    public static GroundsFactV1 GateDuration(int seconds, string derivation,
        int sampleCount, DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.gate-duration", "Measured gate duration warning",
            $"expected={seconds}s; samples={sampleCount}; {derivation}", source, measuredAt);

    public static GroundsFactV1 ReviewAspect(ReviewAspectBudget budget, DateTime measuredAt,
        GroundsSourceRefV1 source)
        => Fact("signal.review-aspect", "Review aspect expected duration",
            $"expected={budget.Seconds}s; cli={budget.CliType}; model={budget.Model}; {budget.Derivation}",
            source, measuredAt);

    public static GroundsFactV1 WatcherContingent(long dayTokens, long weekTokens,
        int dayCalls, int weekCalls, bool exhausted, DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.watcher-contingent", "Watcher contingent consumption",
            $"dayTokens={dayTokens}; weekTokens={weekTokens}; dayCalls={dayCalls}; weekCalls={weekCalls}; exhausted={exhausted}",
            source, measuredAt);

    public static GroundsFactV1 CodingRun(double elapsedSeconds, int emergencySeconds,
        string limitSource, bool stopped, DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.coding-run", "Coding run elapsed exposure",
            $"elapsed={elapsedSeconds.ToString("0", CultureInfo.InvariantCulture)}s; emergency={emergencySeconds}s; source={limitSource}; stopped={stopped}",
            source, measuredAt);

    public static GroundsFactV1 Salvage(int roundsUsed, int reentryLimit, bool hasPair,
        DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.salvage-continuation", "Salvage continuation history",
            $"rounds={roundsUsed}; reentryLimit={reentryLimit}; hasRefAndSha={hasPair}; exhausted={roundsUsed >= reentryLimit}",
            source, measuredAt);

    public static GroundsFactV1 ReviewReissue(int reissuesUsed, int reentryLimit,
        DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.review-reissue", "Review reissue history",
            $"reissues={reissuesUsed}; reentryLimit={reentryLimit}; exhausted={reissuesUsed >= reentryLimit}",
            source, measuredAt);

    public static GroundsFactV1 GitCalls(int calls, int callLimit, TimeSpan elapsed,
        DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.git-calls", "Git subprocess consumption",
            $"calls={calls}; callLimit={callLimit}; elapsed={elapsed.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture)}s",
            source, measuredAt);

    public static GroundsFactV1 GateEnvironmentRetry(string fingerprint, int retriesUsed,
        int reentryLimit, DateTime measuredAt, GroundsSourceRefV1 source)
        => Fact("signal.gate-environment-retry", "Gate environment retry history",
            $"fingerprint={fingerprint}; retries={retriesUsed}; reentryLimit={reentryLimit}; exhausted={retriesUsed >= reentryLimit}",
            source, measuredAt);

    private static GroundsFactV1 Fact(string id, string name, string value,
        GroundsSourceRefV1 source, DateTime measuredAt)
        => new(id, "signal", name, value, null, source, measuredAt, "complete",
            GroundsReliability.Reliable);
}
