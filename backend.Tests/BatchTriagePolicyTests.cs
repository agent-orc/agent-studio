using AgentStudio.Operator;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>OP-4 (AGT-3462): the triage view's open-flight, isolation and next-operation matrix.</summary>
public sealed class BatchTriagePolicyTests
{
    private static readonly BatchTriageProjectFacts Idle = new(GateEnabled: true, PushPending: false, FlightOpen: false, PlanDecision: "wait");

    [Theory]
    [InlineData("running", "stale-running-state")]
    [InlineData("assembling", "stale-running-state")]
    [InlineData("formed", "flight-awaiting-start")]
    public void State_without_a_live_flight_never_claims_the_gate_is_running(string phase, string reason)
    {
        var member = new BatchTriageMemberFacts("in-batch", phase, null, false, false, LiveFlight: false);
        var operation = BatchTriagePolicy.NextOperation(member, Idle);
        Assert.Equal(("wait", reason), (operation.Operation, operation.Reason));
    }

    [Fact]
    public void Stale_isolation_names_the_missing_gate_and_keeps_eject_available()
    {
        var member = new BatchTriageMemberFacts("in-batch", "running", null, true, false, LiveFlight: false);
        var operation = BatchTriagePolicy.NextOperation(member, Idle);
        Assert.Equal(("eject", "stale-running-state"), (operation.Operation, operation.Reason));
    }

    [Fact]
    public void An_unrecognized_phase_without_a_flight_never_claims_a_running_gate()
    {
        var member = new BatchTriageMemberFacts("in-batch", "unknown", null, false, false, LiveFlight: false);
        Assert.Equal("flight-not-live", BatchTriagePolicy.NextOperation(member, Idle).Reason);
    }

    [Theory]
    [InlineData("formed", null, false, true, false)]
    [InlineData("running", null, false, true, false)]
    [InlineData("green", null, false, true, false)]
    [InlineData("publishing", null, false, true, false)]
    [InlineData("paused", null, false, true, false)]
    [InlineData("published", null, false, false, false)]
    [InlineData("abandoned", null, false, false, false)]
    [InlineData("red", null, false, true, true)]
    [InlineData("red", null, true, false, false)]
    [InlineData("running", "parent", false, true, true)]
    [InlineData("green", "parent", false, true, true)]
    [InlineData("red", "parent", false, false, false)]
    [InlineData("published", "parent", false, false, false)]
    public void Flight_openness_and_isolation_follow_the_phase(
        string phase, string? parent, bool childPublished, bool open, bool isolating)
    {
        var flight = new BatchTriageFlightFacts(parent is null ? "parent" : "child", parent, phase);
        var all = new List<BatchTriageFlightFacts> { flight };
        if (childPublished) all.Add(new BatchTriageFlightFacts("child", "parent", "published"));

        Assert.Equal(open, BatchTriagePolicy.IsOpen(flight, all));
        Assert.Equal(isolating, BatchTriagePolicy.IsIsolating(flight, all));
    }

    [Theory]
    // Isolation wins over everything, a red member included.
    [InlineData("in-batch", "red", null, true, false, "eject", "isolation-active")]
    [InlineData("in-batch", "running", null, true, true, "eject", "isolation-active")]
    [InlineData("pending", null, null, false, true, "wait", "red-returns-to-agent")]
    [InlineData("pre-check-red", null, null, false, true, "wait", "red-returns-to-agent")]
    [InlineData("in-batch", "running", null, false, false, "wait", "flight-running")]
    [InlineData("in-batch", "assembling", null, false, false, "wait", "flight-running")]
    [InlineData("in-batch", "green", null, false, false, "wait", "flight-publishing")]
    [InlineData("in-batch", "publishing", null, false, false, "wait", "flight-publishing")]
    [InlineData("in-batch", "paused", "integration-branch-red", false, false, "wait", "integration-branch-red")]
    [InlineData("in-batch", "paused", "flaky quarantine requires a fresh gate verdict", false, false, "wait", "flight-paused")]
    [InlineData("fallback", "running", null, false, false, "wait", "per-task-gate")]
    [InlineData("published", "published", null, false, false, "wait", "unknown-status")]
    public void Member_operation_follows_its_flight(
        string status, string? phase, string? reason, bool isolating, bool red, string operation, string code)
    {
        var decided = BatchTriagePolicy.NextOperation(new BatchTriageMemberFacts(status, phase, reason, isolating, red), Idle);

        Assert.Equal((operation, code), (decided.Operation, decided.Reason));
        Assert.False(string.IsNullOrWhiteSpace(decided.Detail));
    }

    [Theory]
    [InlineData(false, false, false, "wait", "wait", "gate-disabled")]
    [InlineData(true, true, false, "wait", "wait", "pending-push")]
    [InlineData(true, true, true, "wait", "wait", "pending-push")]
    [InlineData(true, false, true, "wait", "wait", "flight-open")]
    [InlineData(true, false, false, "wait", "close-now", "batch-forming")]
    [InlineData(true, false, false, "idle", "wait", "planner")]
    [InlineData(true, false, false, "close", "wait", "planner")]
    [InlineData(true, false, false, "per-task", "wait", "planner")]
    [InlineData(true, false, false, null, "wait", "planner")]
    public void A_pending_member_closes_a_forming_batch_only_when_nothing_holds_the_project(
        bool enabled, bool push, bool flightOpen, string? plan, string operation, string code)
    {
        var decided = BatchTriagePolicy.NextOperation(
            new BatchTriageMemberFacts("pending", null, null, Isolating: false, Red: false),
            new BatchTriageProjectFacts(enabled, push, flightOpen, plan));

        Assert.Equal((operation, code), (decided.Operation, decided.Reason));
    }
}
