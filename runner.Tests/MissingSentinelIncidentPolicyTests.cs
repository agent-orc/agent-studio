using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2820: AGT-2794, AGT-2817 and AGT-2819 each delivered real work (29, 43
/// and 61 files) and reported "Completed out-of-band ... Remote work completed
/// without a terminal sentinel." All three went straight to Human Review, where
/// the acceptance guard correctly refused them because the delivery is not in
/// the integration branch, so the work was neither reviewed nor integrated.
/// A missing sentinel is an incident about the run, and the delivery it leaves
/// behind is unreviewed.
/// </summary>
public sealed class MissingSentinelIncidentPolicyTests
{
    private const string Ref = "refs/heads/runner/agent-runner-01/AGT-2794";
    private static readonly string Sha = new('a', 40);

    private static ExecutionRawFacts Facts(
        int? exitCode = 0,
        int? signal = null,
        bool oomKilled = false,
        bool timedOut = false,
        bool leaseLost = false,
        ExecutionTransportState transport = ExecutionTransportState.Connected,
        DurableOutputState durableOutput = DurableOutputState.Acknowledged)
        => new(
            "run-1",
            ExecutionAttemptKind.Coding,
            ExitCode: exitCode,
            Signal: signal,
            OomKilled: oomKilled,
            TimedOut: timedOut,
            LeaseLost: leaseLost,
            TransportState: transport,
            DurableOutputState: durableOutput);

    [Fact]
    public void A_verified_delivery_without_a_sentinel_is_named_as_an_incident()
    {
        var incident = MissingSentinelIncidentPolicy.Evaluate(
            ExecutionOutcomeKind.ProtocolInconclusive,
            Facts(),
            deliveryVerified: true,
            Ref,
            Sha,
            "agent-runner-01");

        Assert.NotNull(incident);
        Assert.Contains(MissingSentinelIncidentPolicy.GateKey, incident!.GateItem, StringComparison.Ordinal);
        Assert.Contains($"{Ref}@{Sha}", incident.GateItem, StringComparison.Ordinal);
        Assert.Contains("agent-runner-01", incident.GateItem, StringComparison.Ordinal);
        Assert.Contains("not a completion", incident.GateItem, StringComparison.Ordinal);
        Assert.Contains("unreviewed", incident.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void A_concluded_run_is_not_an_incident()
    {
        Assert.Null(MissingSentinelIncidentPolicy.Evaluate(
            ExecutionOutcomeKind.SuccessfulCompletion,
            Facts(),
            deliveryVerified: true,
            Ref,
            Sha,
            "agent-runner-01"));
    }

    [Fact]
    public void Without_a_proven_delivery_there_is_nothing_to_route_to_review()
    {
        Assert.Null(Evaluate(deliveryVerified: false, Ref, Sha));
        Assert.Null(Evaluate(deliveryVerified: true, resultRef: null, Sha));
        Assert.Null(Evaluate(deliveryVerified: true, Ref, resultSha: ""));

        static MissingSentinelIncident? Evaluate(
            bool deliveryVerified,
            string? resultRef,
            string? resultSha)
            => MissingSentinelIncidentPolicy.Evaluate(
                ExecutionOutcomeKind.ProtocolInconclusive,
                Facts(),
                deliveryVerified,
                resultRef,
                resultSha,
                "agent-runner-01");
    }

    /// <summary>
    /// "Record why the sentinel was missing": the strongest available process
    /// fact wins, so a host kill under load is never reported as a clean exit.
    /// </summary>
    [Theory]
    [InlineData("oom", "memory pressure")]
    [InlineData("signal", "killed by signal 143")]
    [InlineData("lease", "lease was lost")]
    [InlineData("timeout", "exceeded its run budget")]
    [InlineData("transport", "transport was lost")]
    [InlineData("durable", "durable output stream is missing")]
    [InlineData("exit", "exited 1 without emitting a sentinel")]
    [InlineData("clean", "exited cleanly but emitted no sentinel")]
    public void The_cause_names_the_strongest_observed_fact(string shape, string expected)
    {
        var facts = shape switch
        {
            "oom" => Facts(oomKilled: true, signal: 9),
            "signal" => Facts(signal: 143),
            "lease" => Facts(leaseLost: true),
            "timeout" => Facts(timedOut: true),
            "transport" => Facts(transport: ExecutionTransportState.Lost),
            "durable" => Facts(durableOutput: DurableOutputState.Missing),
            "exit" => Facts(exitCode: 1),
            _ => Facts(),
        };

        var incident = MissingSentinelIncidentPolicy.Evaluate(
            ExecutionOutcomeKind.ProtocolInconclusive,
            facts,
            deliveryVerified: true,
            Ref,
            Sha,
            "agent-runner-01");

        Assert.NotNull(incident);
        Assert.Contains(expected, incident!.Cause, StringComparison.Ordinal);
        Assert.Contains(expected, incident.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public void Missing_process_facts_are_reported_rather_than_invented()
    {
        var incident = MissingSentinelIncidentPolicy.Evaluate(
            ExecutionOutcomeKind.ProtocolInconclusive,
            facts: null,
            deliveryVerified: true,
            Ref,
            Sha,
            "agent-runner-01");

        Assert.NotNull(incident);
        Assert.Contains("no process facts", incident!.Cause, StringComparison.Ordinal);
        Assert.Contains("facts=none", incident.GateItem, StringComparison.Ordinal);
    }
}
