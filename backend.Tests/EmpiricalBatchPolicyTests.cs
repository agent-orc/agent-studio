using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

public sealed class EmpiricalBatchPolicyTests
{
    private static readonly DateTimeOffset Start = new(2026, 11, 3, 0, 0, 0, TimeSpan.Zero);
    private static readonly BatchPlanCard[] Cards =
    [
        new("A", Start, .20, ["backend/a.cs"], []),
        new("B", Start, .25, ["backend/b.cs"], []),
        new("C", Start, .30, ["backend/c.cs"], []),
        new("D", Start, .35, ["backend/d.cs"], []),
        new("E", Start, .40, ["backend/e.cs"], []),
    ];

    private static BatchPlanInput Input(DateTimeOffset now, IReadOnlyList<BatchPlanObservation>? rows = null)
        => new(now, Cards, [], 2, 8, 15, 45, [], "balanced", "adjacent",
            Calibration: "empirical", Observations: rows);

    [Fact]
    public void Starts_from_safe_prior_and_legacy_switch_keeps_current_rule()
    {
        var empirical = EmpiricalBatchPolicy.Resolve(Input(Start), Cards);
        Assert.Equal(4, empirical.TargetSize);
        Assert.Equal(.55, empirical.AdmissionLimit);
        Assert.Equal(4, BatchGateLookAheadPlanner.Plan(Input(Start)).TargetSize);
        var legacy = BatchGateLookAheadPlanner.Plan(Input(Start) with { Calibration = "legacy" });
        Assert.Equal(BatchGateLookAheadPlanner.Plan(Input(Start) with
        { Calibration = "legacy", Observations = null }).TargetSize, legacy.TargetSize);
    }

    [Fact]
    public void Learns_only_from_completed_prior_days_and_moves_one_size_per_day()
    {
        var rows = Enumerable.Range(0, 24).Select(index => new BatchPlanObservation(
            index.ToString(), Start.AddMinutes(index), 5, .48,
            ["backend"], "disjoint", false)).ToArray();
        Assert.Equal(4, EmpiricalBatchPolicy.Resolve(Input(Start.AddHours(20), rows), Cards).TargetSize);
        Assert.Equal(5, EmpiricalBatchPolicy.Resolve(Input(Start.AddDays(1), rows), Cards).TargetSize);
        Assert.Equal(.60, EmpiricalBatchPolicy.Resolve(Input(Start.AddDays(1), rows), Cards).AdmissionLimit);
        Assert.Equal(.60,
            EmpiricalBatchPolicy.Resolve(Input(Start.AddDays(2), rows), Cards).AdmissionLimit);
    }

    [Fact]
    public void Red_cohorts_shrink_target_and_admission_by_bounded_steps()
    {
        var rows = Enumerable.Range(0, 20).Select(index => new BatchPlanObservation(
            index.ToString(), Start.AddMinutes(index), 4, .50,
            ["backend"], "disjoint", true, 4, 60,
            "deterministic-suite-red")).ToArray();
        var limits = EmpiricalBatchPolicy.Resolve(Input(Start.AddDays(1), rows), Cards);
        Assert.Equal(3, limits.TargetSize);
        Assert.Equal(.50, limits.AdmissionLimit);
    }

    [Fact]
    public void Infrastructure_and_base_red_do_not_train_candidate_failure_rate()
    {
        var rows = Enumerable.Range(0, 20).Select(index => new BatchPlanObservation(
            index.ToString(), Start.AddMinutes(index), 4, .50,
            ["backend"], "disjoint", true, Classification: "pre-existing-base-red"))
            .ToArray();
        Assert.Equal(new EmpiricalBatchPolicy.Limits(4, .55),
            EmpiricalBatchPolicy.Resolve(Input(Start.AddDays(1), rows), Cards));
    }

    [Fact]
    public void Unknown_diff_stays_on_per_task_route_when_admission_limit_expands()
    {
        var rows = Enumerable.Range(0, 24).Select(index => new BatchPlanObservation(
            index.ToString(), Start.AddMinutes(index), 5, .48,
            ["backend"], "disjoint", false)).ToArray();
        var unknown = new BatchPlanCard("unknown", Start.AddMinutes(-1), .55, [], []);
        var decision = BatchGateLookAheadPlanner.Plan(Input(Start.AddDays(1), rows)
            with { Waiting = [unknown, ..Cards] });
        Assert.Equal("per-task", decision.Decision);
        Assert.Equal(["unknown"], decision.ProposedMembers);
    }
}
