using AgentStudio.Pipeline;
using Xunit;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-2819: the review report is the measurement of the integration branch,
/// because every deterministic gate runs on the merge base before its failure is
/// attributed. These tests cover what the reporter reads out of one report and
/// that the health file makes a repeated red transition quiet.
/// </summary>
public sealed class IntegrationBranchGateReporterTests : IDisposable
{
    private const string Branch = "refs/heads/develop";
    private const string MergeBase = "4444444444444444444444444444444444444444";
    private static readonly DateTime Observed = new(2026, 9, 14, 9, 0, 0, DateTimeKind.Utc);

    private readonly string _root;

    public IntegrationBranchGateReporterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), "gate-health-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void A_lint_gate_red_on_both_sides_is_observed_red_on_the_branch()
    {
        var observation = Assert.Single(IntegrationBranchGateReporter.Observations(
            Plan(),
            [Evidence(exitCode: 1, baselineSha: MergeBase, baselineExitCode: 1)],
            Branch,
            Observed));

        Assert.True(observation.Red);
        Assert.Equal("verify-5", observation.StepId);
        Assert.Equal(MergeBase, observation.MergeBaseSha);
        Assert.Contains("npm --prefix frontend run lint", observation.Command, StringComparison.Ordinal);
    }

    [Fact]
    public void A_gate_the_card_broke_is_observed_green_on_the_branch()
    {
        var observation = Assert.Single(IntegrationBranchGateReporter.Observations(
            Plan(),
            [Evidence(exitCode: 1, baselineSha: MergeBase, baselineExitCode: 0)],
            Branch,
            Observed));

        Assert.False(observation.Red);
        Assert.Equal(MergeBase, observation.MergeBaseSha);
    }

    [Fact]
    public void A_passing_gate_is_observed_green_without_a_branch_commit()
    {
        var observation = Assert.Single(IntegrationBranchGateReporter.Observations(
            Plan(),
            [Evidence(exitCode: 0, baselineSha: null, baselineExitCode: null)],
            Branch,
            Observed));

        Assert.False(observation.Red);
        Assert.Null(observation.MergeBaseSha);
    }

    [Fact]
    public void Baseline_evidence_and_preparation_phases_are_not_branch_observations()
    {
        var observations = IntegrationBranchGateReporter.Observations(
            Plan(),
            [
                Evidence(1, MergeBase, 1) with { WorkspaceRole = "baseline-0123456789ab" },
                Evidence(1, MergeBase, 1) with { Phase = "preparation" },
            ],
            Branch,
            Observed);

        Assert.Empty(observations);
    }

    [Fact]
    public void The_health_file_reports_one_red_transition_and_then_stays_quiet()
    {
        var observation = new IntegrationBranchGateObservation(
            Branch,
            "verify-5",
            "npm --prefix frontend run lint",
            Red: true,
            MergeBase,
            Observed);

        var first = IntegrationBranchGateHealthStore.Apply(_root, "Agent Studio", observation);
        var second = IntegrationBranchGateHealthStore.Apply(_root, "Agent Studio", observation);

        Assert.True(first.ShouldAlert);
        Assert.False(second.ShouldAlert);
        Assert.Equal(IntegrationBranchGateTransition.StillRed, second.Transition);
        var rows = IntegrationBranchGateHealthStore.Read(_root, "Agent Studio");
        var row = Assert.Single(rows);
        Assert.True(row.Red);
        Assert.Equal(MergeBase, row.RedSinceSha);
    }

    [Fact]
    public void Health_rows_are_kept_per_branch_and_per_step()
    {
        IntegrationBranchGateHealthStore.Apply(_root, "studio", Red("refs/heads/develop", "verify-5"));
        IntegrationBranchGateHealthStore.Apply(_root, "studio", Red("refs/heads/develop", "verify-2"));
        IntegrationBranchGateHealthStore.Apply(_root, "studio", Red("refs/heads/main", "verify-5"));

        Assert.Equal(3, IntegrationBranchGateHealthStore.Read(_root, "studio").Count);
    }

    private static IntegrationBranchGateObservation Red(string branch, string stepId) => new(
        branch,
        stepId,
        "npm --prefix frontend run lint",
        Red: true,
        MergeBase,
        Observed);

    private static Contract.ReviewPlanDto Plan() => new(
        [new Contract.ReviewCommandDto(
            "verify-5",
            "lint",
            "sh",
            ["-lc", "npm --prefix frontend run lint"],
            CompareToBaseline: true,
            BaselineMode: Contract.ReviewBaselineModes.ExitStatus)],
        ["lint"],
        IntegrationRef: Branch);

    private static Contract.ReviewCommandEvidenceDto Evidence(
        int? exitCode,
        string? baselineSha,
        int? baselineExitCode)
        => new(
            "verify-5",
            "lint",
            "sh",
            ["-lc", "npm --prefix frontend run lint"],
            new string('b', 40),
            new string('b', 40),
            new string('t', 40),
            Observed.AddSeconds(-30),
            Observed,
            exitCode,
            null,
            new string('0', 64),
            new string('0', 64),
            BaselineSha: baselineSha,
            NewFailures: [],
            PreExistingFailures: [],
            BaselineExitCode: baselineExitCode);
}
