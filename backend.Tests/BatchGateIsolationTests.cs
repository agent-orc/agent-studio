using AgentStudio.Pipeline;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateIsolationTests
{
    private const string Sha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private static readonly DateTimeOffset Now = DateTimeOffset.UnixEpoch.AddDays(20_000);
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

    [Fact]
    public async Task Superseded_member_leaves_complement_without_discarding_green_child()
    {
        var members = Enumerable.Range(0, 3).Select(Member).ToArray();
        var current = new HashSet<string>(members.Select(member => member.TaskKey));
        var probed = new List<string>();
        var published = new List<string>();
        var result = await BatchGateIsolation.RunAsync(members,
            (subset, _) =>
            {
                var keys = string.Join(',', subset.Select(member => member.TaskKey));
                probed.Add(keys);
                if (keys == "task-1,task-2")
                {
                    current.Remove("task-1");
                    return Task.FromResult(new BatchGateProbeResult(
                        BatchGateFailureClass.Superseded, Sha, "cancelled"));
                }
                return Task.FromResult(new BatchGateProbeResult(
                    BatchGateFailureClass.Pass, Sha, "evidence"));
            }, 4, CancellationToken.None,
            member => current.Contains(member.TaskKey),
            (subset, _, _) =>
            {
                published.AddRange(subset.Select(member => member.TaskKey));
                return Task.FromResult(true);
            });

        Assert.Contains("task-0", published);
        Assert.Contains("task-2", published);
        Assert.DoesNotContain("task-1", published);
        Assert.Contains("task-1,task-2", probed);
        Assert.Contains("task-2", probed);
        Assert.Empty(result.EjectedKeys);
    }

    [Fact]
    public async Task Red_pair_and_green_singleton_prove_the_other_member_red()
    {
        var members = Enumerable.Range(0, 4).Select(Member).ToArray();
        var result = await BatchGateIsolation.RunAsync(members,
            (subset, _) => Task.FromResult(new BatchGateProbeResult(
                subset.Any(member => member.TaskKey == "task-1")
                    ? BatchGateFailureClass.DeterministicSuiteRed
                    : BatchGateFailureClass.Pass, Sha, "evidence")),
            4, CancellationToken.None);

        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.MemberKeys.SequenceEqual(["task-0", "task-1"])
            && diagnostic.Result.Classification == BatchGateFailureClass.DeterministicSuiteRed);
        Assert.Contains(result.Diagnostics, diagnostic =>
            diagnostic.MemberKeys.SequenceEqual(["task-0"])
            && diagnostic.Result.Classification == BatchGateFailureClass.Pass);
        Assert.Contains("task-1", result.EjectedKeys);
    }
}
