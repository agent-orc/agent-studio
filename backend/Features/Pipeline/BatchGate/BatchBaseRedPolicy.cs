namespace AgentStudio.Pipeline;

/// <summary>Whether a base red can explain and hold a candidate red.</summary>
internal static class BatchBaseRedPolicy
{
    internal static bool Provisional(BuildTestGateResult result)
        => result.GateCollisionDetected || result.Processes.Any(process =>
                process.Resources?.AverageExternalCpuPercent >= 25);

    internal static IReadOnlyList<string> FailedTests(BuildTestGateResult result)
        => result.RecordedFailedTests.Concat(result.Processes.Where(process => process.ExitCode != 0)
            .SelectMany(process => (process.TestReport?.FailedTests ?? [])
                .Concat(GateFlakyRerunPolicy.ParseFailedTests(
                    process.StandardOutput + "\n" + process.StandardError))))
            .Concat(result.TestShards.SelectMany(suite => suite.Shards)
                .SelectMany(shard => shard.FailedTests))
            .Except(result.FlakyQuarantinedFailures, StringComparer.Ordinal)
            .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();

    internal static bool HoldsCandidate(BuildTestGateResult candidate, BatchGateBaseVerdict baseline)
    {
        if (baseline.Outcome != "fail" || baseline.Provisional) return false;
        var candidateTests = FailedTests(candidate);
        var baseTests = baseline.FailedTests ?? [];
        return candidateTests.Count == 0 || baseTests.Count == 0
            || candidateTests.Intersect(baseTests, StringComparer.Ordinal).Any();
    }
}
