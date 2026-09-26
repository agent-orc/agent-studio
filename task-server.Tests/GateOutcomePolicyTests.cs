using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace TaskServer.Tests;

public sealed class GateOutcomePolicyTests
{
    public static TheoryData<string?, string, int, int, string, string, string?> Cases => new()
    {
        { null, "clean", 1, 2, GateStates.Passed, GateStates.Passed, null },
        { GateFailureClasses.ProductFailure, "clean", 1, 2, GateStates.ProductFailed, GateFailureClasses.ProductFailure, GateFailureClasses.ProductFailure },
        { GateFailureClasses.SnapshotUnavailable, "clean", 1, 2, GateStates.InfraRetry, GateFailureClasses.SnapshotUnavailable, GateFailureClasses.SnapshotUnavailable },
        { GateFailureClasses.ToolFailure, "clean", 2, 2, GateStates.InfraFailed, GateFailureClasses.GateInfra, GateFailureClasses.ToolFailure },
        { GateFailureClasses.ExecutionTimeout, "clean", 2, 2, GateStates.TimedOut, GateFailureClasses.GateInfra, GateFailureClasses.ExecutionTimeout },
        { GateFailureClasses.ProductFailure, "failed", 1, 2, GateStates.InfraFailed, GateFailureClasses.GateInfra, GateFailureClasses.CleanupFailure },
    };

    [Theory]
    [MemberData(nameof(Cases))]
    public void Outcome_is_fail_closed_and_preserves_typed_failure(
        string? failure, string cleanup, int attemptNumber, int maxAttempts,
        string state, string outcome, string? classification)
    {
        var report = new GateReport(
            failure == null ? GateStates.Passed : GateStates.InfraFailed,
            failure, new string('a', 40), new string('b', 40), false, false,
            [], "host", "toolchain", [], [], cleanup);
        var decision = GateOutcomePolicy.Decide(report, attemptNumber, maxAttempts);
        Assert.Equal((state, outcome, classification), decision);
    }
}
