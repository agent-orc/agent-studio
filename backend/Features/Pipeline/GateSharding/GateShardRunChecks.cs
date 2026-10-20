namespace AgentStudio.Pipeline;

/// <summary>Why a shard that did not run to a trustworthy test verdict is an infrastructure failure.</summary>
public static class GateShardDetails
{
    /// <summary>The test run aborted or the test host crashed; the shard's tests did not all run.</summary>
    public const string TestRunAborted = "test-run-aborted";

    /// <summary>The shard process wrote no TRX report, so nothing proves which tests ran.</summary>
    public const string NoTestReport = "no-test-report";

    /// <summary>The TRX report holds classes the shard's filter should not have selected.</summary>
    public const string ShardFilterNotApplied = "shard filter not applied";

    /// <summary>The failures printed on the console differ from the TRX report; no quarantine, no re-run.</summary>
    public const string FailuresUnconfirmed = "failures-unconfirmed";

    /// <summary>The targeted re-run did not prove every targeted test passed.</summary>
    public const string RerunUnconfirmed = "rerun-unconfirmed";
}

/// <summary>
/// AGT-3232: pure checks that keep a sharded suite from passing on evidence it
/// does not have. A shard is green only when its run completed, its TRX report
/// shows it ran exactly the classes its filter selects, and every red test it
/// had was either quarantined or passed one confirmed targeted re-run.
/// </summary>
public static class GateShardRunChecks
{
    /// <summary>Console lines VSTest prints when the test run aborts or the test host dies.</summary>
    private static readonly string[] AbortSignatures =
    [
        "Test Run Aborted",
        "The active test run was aborted",
        "Test host process crashed",
        "The test host process exited unexpectedly",
    ];

    /// <summary>The abort a shard's report or console output shows, or null for a completed test run.</summary>
    public static string? Abort(GateTestReport? report, string output)
    {
        if (report?.AbortedOutcome is { } outcome) return $"TRX run outcome {outcome}";
        var signature = AbortSignatures.FirstOrDefault(item => output.Contains(item, StringComparison.OrdinalIgnoreCase));
        return signature is null ? null : $"console reports \"{signature}\"";
    }

    /// <summary>
    /// Classes in the report that the shard's filter must not have selected:
    /// for a listed shard any class outside its own list, for the complement
    /// shard any class another shard owns. Empty when the filter applied.
    /// </summary>
    public static IReadOnlyList<string> ForeignClasses(GateShardPlan plan, int index, GateTestReport report)
    {
        if (index == plan.ComplementIndex)
        {
            var others = plan.ClassesOutsideComplement.ToHashSet(StringComparer.Ordinal);
            return report.Classes.Where(others.Contains).Order(StringComparer.Ordinal).ToArray();
        }
        var assigned = plan.Shards[index].Classes.ToHashSet(StringComparer.Ordinal);
        return report.Classes.Where(name => !assigned.Contains(name)).Order(StringComparer.Ordinal).ToArray();
    }

    /// <summary>
    /// Quarantine and re-run may only act on failures both the console and
    /// the TRX report name, and name identically.
    /// </summary>
    public static bool FailuresConfirmed(IReadOnlyList<string> parsedFailures, GateTestReport? report)
        => report is not null
           && parsedFailures.Count > 0
           && parsedFailures.ToHashSet(StringComparer.Ordinal).SetEquals(report.FailedTests);

    /// <summary>
    /// A targeted re-run is green only when it completed with exit 0 and its
    /// TRX report shows exactly the targeted tests, all passed. VSTest exits 0
    /// when the filter matches nothing, so the exit code alone proves nothing.
    /// </summary>
    public static bool RerunConfirmed(bool completedWithExitZero, GateTestReport? report, string output, int targeted)
        => completedWithExitZero
           && targeted > 0
           && Abort(report, output) is null
           && report is { Counts.Failed: 0 } confirmed
           && confirmed.Counts.Total == targeted
           && confirmed.Counts.Passed == targeted;
}
