namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3759: the failure class of one isolation probe. A test run that
/// completed on the tested candidate and whose parsed report names failed
/// tests is a code red, even when a later step nulled the exit code (for
/// example a failed workspace cleanup). A run without a parsed report, a hang,
/// a cancellation, a source or transfer failure, and a tested-SHA mismatch stay
/// infrastructure.
/// </summary>
public static class BatchGateProbeClassifier
{
    public static BatchGateFailureClass Classify(BuildTestGateResult gate, string candidateSha)
    {
        var testedCandidate = string.Equals(gate.TestedSha, candidateSha, StringComparison.OrdinalIgnoreCase);
        if (!testedCandidate) return BatchGateFailureClass.InfrastructureRed;
        if (gate.Verdict == BuildTestGateVerdict.Ok) return BatchGateFailureClass.Pass;
        if (gate.Verdict != BuildTestGateVerdict.Fail) return BatchGateFailureClass.InfrastructureRed;
        if (gate.ExitCode is not null && !gate.IsInfrastructureFailure)
            return BatchGateFailureClass.DeterministicSuiteRed;
        return gate.FailureKind == BuildTestGateFailureKind.Code && !Hung(gate) && FailedTests(gate).Count > 0
            ? BatchGateFailureClass.DeterministicSuiteRed
            : BatchGateFailureClass.InfrastructureRed;
    }

    /// <summary>Failed test names from completed runs with a parsed report; empty without one.</summary>
    public static IReadOnlyList<string> FailedTests(BuildTestGateResult gate)
        => gate.Processes
            .Where(process => process.TestReport is { FailedTests.Count: > 0 }
                && process.ExitCode is not null && !process.TimedOut && !process.Cancelled
                && process.LaunchError is null && process.ShardHang is null)
            .SelectMany(process => process.TestReport!.FailedTests)
            .Concat(gate.TestShards.SelectMany(suite => suite.Shards)
                .Where(shard => shard.Outcome == GateShardOutcomes.Failed && shard.Hang is null)
                .SelectMany(shard => shard.FailedTests))
            .Distinct(StringComparer.Ordinal)
            .ToArray();

    private static bool Hung(BuildTestGateResult gate)
        => gate.Processes.Any(process => process.ShardHang is not null)
           || gate.TestShards.SelectMany(suite => suite.Shards).Any(shard => shard.Hang is not null);
}
