using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateIsolationTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;
    private static BatchGateSubject Member(int index) => new(
        $"task-{index}", "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Sha, "run-1", 1, 1, Sha,
        true, true, true, true, false, null, true, Now.AddMinutes(index), index);

    [Fact]
    public async Task SingleOffenderUsesAtMostLog2DiagnosticsAndRerunsSurvivors()
    {
        var members = Enumerable.Range(0, 8).Select(Member).ToArray();
        var allRuns = 0;
        var result = await BatchGateIsolation.RunAsync(members,
            (cohort, _) =>
            {
                allRuns++;
                return Task.FromResult(new BatchGateProbeResult(
                    cohort.Any(x => x.TaskKey == "task-5")
                        ? BatchGateFailureClass.DeterministicSuiteRed
                        : BatchGateFailureClass.Pass,
                    Sha, "evidence"));
            }, maximumTotalDiagnosticRuns: 3, CancellationToken.None);
        Assert.Equal(["task-5"], result.EjectedKeys);
        Assert.Empty(result.UnresolvedCohortKeys);
        Assert.Equal(3, result.Diagnostics.Count);
        Assert.Equal(4, allRuns); // Three diagnostics and one survivor rerun.
        Assert.Equal(BatchGateFailureClass.Pass, result.SurvivorVerdict!.Classification);
    }

    [Fact]
    public async Task BudgetExhaustionLeavesVisibleUnresolvedCohort()
    {
        var members = Enumerable.Range(0, 8).Select(Member).ToArray();
        var result = await BatchGateIsolation.RunAsync(members,
            (cohort, _) => Task.FromResult(new BatchGateProbeResult(
                BatchGateFailureClass.DeterministicSuiteRed, Sha, "evidence")),
            maximumTotalDiagnosticRuns: 2, CancellationToken.None);
        Assert.Empty(result.EjectedKeys);
        Assert.Equal(8, result.UnresolvedCohortKeys.Count);
        Assert.Equal(2, result.Diagnostics.Count);
    }

    [Fact]
    public async Task InfrastructureOutcomeDoesNotAttributeMember()
    {
        var members = Enumerable.Range(0, 4).Select(Member).ToArray();
        var result = await BatchGateIsolation.RunAsync(members,
            (cohort, _) => Task.FromResult(new BatchGateProbeResult(
                BatchGateFailureClass.InfrastructureRed, Sha, "evidence")),
            maximumTotalDiagnosticRuns: 2, CancellationToken.None);
        Assert.Empty(result.EjectedKeys);
        Assert.Equal(4, result.UnresolvedCohortKeys.Count);
    }
}
