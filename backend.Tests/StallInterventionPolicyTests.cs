using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: pure policy inputs; the observation time is a frozen fact, the policy reads no clock.

/// <summary>
/// AGT-3207: the AI Operator's stall interventions as a pure decision matrix.
/// A0/A1 act, A2 is a proposal, nothing is ever A3; the rate limit per host
/// and hour and the repeat limit per subject stop intervention loops.
/// </summary>
public sealed class StallInterventionPolicyTests
{
    private static readonly DateTime Now = new(2026, 10, 16, 19, 51, 0, DateTimeKind.Utc);

    [Fact]
    public void Overloaded_runner_lowers_admission_by_a_quarter_with_a_plain_receipt()
    {
        var decision = Decide(Overload(admission: 8));

        Assert.Equal((AiOperatorDisposition.Act, AutonomyClass.A1, StallInterventionActions.LowerAdmission),
            (decision.Disposition, decision.Autonomy, decision.Effect));
        var plan = decision.Intervention!;
        Assert.Equal((6, 8), (plan.TargetAdmission, plan.PreviousAdmission));
        Assert.Contains("agent-runner-01 ran with load 46.7 on 12 cores", plan.Observed);
        Assert.Contains("from 8 to 6", plan.Done);
        Assert.Contains("new work waits, running work continues", plan.After);
        Assert.Contains("Settings > Capacity", plan.Undo);
        Assert.Equal(plan.Receipt, decision.Detail);
        Assert.Equal(15, plan.FollowUpMinutes);
    }

    [Fact]
    public void Overload_at_the_admission_floor_or_without_admission_becomes_an_A2_proposal()
    {
        var floor = Decide(Overload(admission: 1));
        var unknown = Decide(Overload(admission: null));

        Assert.Equal((AiOperatorDisposition.Propose, AutonomyClass.A2, StallInterventionActions.ProposeMoveWork),
            (floor.Disposition, floor.Autonomy, floor.Effect));
        Assert.Equal(AiOperatorDisposition.Propose, unknown.Disposition);
        Assert.Contains("move work", floor.Intervention!.Proposal);
    }

    [Theory]
    [InlineData(StallKinds.IdleCapacity, "review", false, false, true, AiOperatorDisposition.Act, StallInterventionActions.RestartService)]
    [InlineData(StallKinds.HostOffline, "gate", false, false, true, AiOperatorDisposition.Act, StallInterventionActions.RestartService)]
    [InlineData(StallKinds.IdleCapacity, "review", true, false, true, AiOperatorDisposition.Hold, StallInterventionActions.None)]
    [InlineData(StallKinds.HostOffline, "review", false, true, true, AiOperatorDisposition.Propose, StallInterventionActions.ProposeMoveWork)]
    [InlineData(StallKinds.HostOffline, "coding", false, false, false, AiOperatorDisposition.Propose, StallInterventionActions.ProposeMoveWork)]
    public void Stuck_review_daemon_or_gate_worker_restarts_through_the_operations_server(string kind, string role,
        bool providerStalled, bool stoppedByOperator, bool restartable, AiOperatorDisposition disposition, string effect)
    {
        var decision = Decide(Facts(kind, $"host:kori-review-01:{role}") with
        {
            Service = new StallServiceFacts("kori-review-01", role, role + "-daemon", restartable, stoppedByOperator,
                Now.AddMinutes(-40), WaitingCount: 15, FreeSlots: 3, providerStalled),
        });

        Assert.Equal((disposition, effect), (decision.Disposition, decision.Effect));
        if (effect == StallInterventionActions.RestartService)
        {
            Assert.Equal(role + "-daemon", decision.Intervention!.Service);
            Assert.Contains("restarted", decision.Intervention.Done);
            Assert.Contains("Nothing to undo", decision.Intervention.Undo);
        }
    }

    [Fact]
    public void Invalid_provider_login_reroutes_to_healthy_hosts_and_proposes_the_renewal()
    {
        var rerouted = Decide(Provider(["kori-review-02", "kori-review-03"], alreadyRerouted: false));
        var stranded = Decide(Provider([], alreadyRerouted: false));

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.RerouteProvider), (rerouted.Disposition, rerouted.Effect));
        Assert.Contains("kori-review-02, kori-review-03 take them", rerouted.Intervention!.Done);
        Assert.Contains("Renew the codex login on kori-review-01", rerouted.Intervention.Proposal);
        Assert.Equal((AiOperatorDisposition.Propose, AutonomyClass.A2, StallInterventionActions.ProposeProviderLogin),
            (stranded.Disposition, stranded.Autonomy, stranded.Effect));
    }

    [Fact]
    public void Rate_limited_runs_requeue_from_their_last_delivery_only_from_an_empty_salvage_in_ready()
    {
        var facts = Facts(StallKinds.ProviderRateLimited, "host:agent-runner-01:codex") with
        {
            Requeue =
            [
                new("AGT-3176", TaskStates.Ready, SalvageEmpty: true, "refs/heads/agent-results/AGT-3176/r2", "0123456789abcdef"),
                new("AGT-3177", TaskStates.Ready, SalvageEmpty: false, "refs/heads/agent-results/AGT-3177/r1", "fedcba9876543210"),
                new("AGT-3178", TaskStates.HumanReview, SalvageEmpty: true, "refs/heads/agent-results/AGT-3178/r1", "aaaaaaaaaaaaaaaa"),
                new("AGT-3179", TaskStates.Ready, SalvageEmpty: true, null, null),
            ],
        };

        var decision = Decide(facts);
        var none = Decide(facts with { Requeue = [facts.Requeue[1]] });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.RequeueFromDelivery), (decision.Disposition, decision.Effect));
        Assert.Equal(["AGT-3176"], decision.Intervention!.Members);
        Assert.Contains("AGT-3176 from refs/heads/agent-results/AGT-3176/r2 at 012345678", decision.Intervention.Done);
        Assert.Equal((AiOperatorDisposition.Hold, "wait-for-provider-reset"), (none.Disposition, none.ReasonCode));
    }

    [Fact]
    public void Missing_review_toolchain_is_proposed_not_installed()
    {
        var decision = Decide(Facts(StallKinds.ClaimRefusal, "queue:toolchain:compose-render") with
        {
            ClaimReason = "toolchain:compose-render",
        });

        Assert.Equal((AiOperatorDisposition.Propose, AutonomyClass.A2, StallInterventionActions.ProposeReviewToolchain),
            (decision.Disposition, decision.Autonomy, decision.Effect));
        Assert.Contains("add compose-render to the review image", decision.Intervention!.Proposal);
    }

    [Fact]
    public void Stuck_batch_gate_drops_completed_and_stale_members_and_closes_the_overdue_batch()
    {
        var gate = new StallGateFacts("KORI", 19, ["AGT-3097", "AGT-3044"], ["AGT-3110"], 79, PlanIdle: true,
            "KORI:forming", OpenBatchWaitHours: 26);
        var decision = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with { Gate = gate });
        var young = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = gate with { OpenBatchWaitHours = 1 },
        });
        var nothing = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = gate with { CompletedMembers = [], StaleMembers = [], OpenBatchWaitHours = 1 },
        });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.ReconcileBatchGate), (decision.Disposition, decision.Effect));
        Assert.Equal(["AGT-3097", "AGT-3044", "AGT-3110"], decision.Intervention!.Members);
        Assert.Equal("KORI:forming", decision.Intervention.BatchId);
        Assert.Contains("19 pending members while its plan is idle", decision.Intervention.Observed);
        Assert.Equal("16 pending, the overdue batch closed and its members queue again", decision.Intervention.After);
        Assert.Null(young.Intervention!.BatchId);
        Assert.Equal(AiOperatorDisposition.Hold, nothing.Disposition);
    }

    [Fact]
    public void Failed_engine_start_is_retried_only_through_a_validating_restart()
    {
        var deploy = new StallDeployFacts("kori-gate", "orchestrator-engine", "0.9.6", "0.9.5",
            "SERVER_URL: ${ENGINE_SERVER_URL:?set ENGINE_SERVER_URL}", ["ENGINE_SERVER_URL"], StartRetryAvailable: true);
        var retried = Decide(Facts(StallKinds.DeployDrift, "container:kori-gate:engine") with { Deploy = deploy });
        var proposed = Decide(Facts(StallKinds.DeployDrift, "container:kori-gate:engine") with
        {
            Deploy = deploy with { StartRetryAvailable = false },
        });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.RetryEngineStart), (retried.Disposition, retried.Effect));
        Assert.Contains("validates the configuration", retried.Intervention!.Done);
        Assert.Equal(AiOperatorDisposition.Propose, proposed.Disposition);
        Assert.Contains("Set ENGINE_SERVER_URL", proposed.Intervention!.Proposal);
    }

    [Fact]
    public void Lane_stalls_are_observed_only()
    {
        var decision = Decide(Facts(StallKinds.LaneStall, "card:AGT-1:4-auto-review"));

        Assert.Equal((AiOperatorDisposition.Hold, AutonomyClass.A0, StallInterventionPolicy.ObserveOnly),
            (decision.Disposition, decision.Autonomy, decision.ReasonCode));
    }

    [Fact]
    public void Same_intervention_twice_in_the_loop_window_escalates_with_the_chain_instead_of_repeating()
    {
        var decision = Decide(Overload(admission: 6) with
        {
            History =
            [
                new("si-1", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-5), StallInterventionStatuses.Verified),
                new("si-2", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-2), StallInterventionStatuses.Verified),
            ],
        });
        var outsideWindow = Decide(Overload(admission: 6) with
        {
            History =
            [
                new("si-1", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-9), StallInterventionStatuses.Verified),
                new("si-2", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-2), StallInterventionStatuses.Verified),
            ],
        });

        Assert.Equal((AiOperatorDisposition.Ask, StallInterventionPolicy.InterventionLoop),
            (decision.Disposition, decision.ReasonCode));
        Assert.Equal(StallInterventionActions.Escalate, decision.Effect);
        Assert.Contains("si-1, si-2", decision.Intervention!.Done);
        Assert.Equal(StallInterventionActions.LowerAdmission, outsideWindow.Effect);
    }

    [Fact]
    public void Host_rate_limit_holds_a_fourth_intervention_in_the_same_hour()
    {
        StallInterventionHistoryEntry Entry(string id, string subject, int minutesAgo)
            => new(id, subject, "kori-review-01", StallInterventionActions.RestartService, Now.AddMinutes(-minutesAgo),
                StallInterventionStatuses.Verified);
        var facts = Facts(StallKinds.IdleCapacity, "host:kori-review-01:review") with
        {
            Service = new StallServiceFacts("kori-review-01", "review", "review-daemon", true, false, Now, 4, 2, false),
        };

        var limited = Decide(facts with
        {
            History = [Entry("si-a", "host:kori-review-01:gate", 10), Entry("si-b", "host:kori-review-01:coding", 20),
                Entry("si-c", "container:kori-review-01:review", 50)],
        });
        var nextHour = Decide(facts with
        {
            History = [Entry("si-a", "host:kori-review-01:gate", 10), Entry("si-b", "host:kori-review-01:coding", 20),
                Entry("si-c", "container:kori-review-01:review", 70)],
        });

        Assert.Equal((AiOperatorDisposition.Hold, StallInterventionPolicy.HostRateLimit), (limited.Disposition, limited.ReasonCode));
        Assert.Contains("limit 3", limited.Detail);
        Assert.Equal(AiOperatorDisposition.Act, nextHour.Disposition);
    }

    [Fact]
    public void A_pending_follow_up_or_an_open_proposal_holds_the_subject()
    {
        var pending = Decide(Overload(admission: 6) with
        {
            History = [new("si-1", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddMinutes(-5), StallInterventionStatuses.Applied)],
        });
        var proposal = Decide(Overload(admission: 1) with
        {
            History = [new("si-2", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.ProposeMoveWork, Now.AddMinutes(-5), StallInterventionStatuses.Proposed)],
        });
        var undone = Decide(Overload(admission: 8) with
        {
            History = [new("si-3", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddMinutes(-5), StallInterventionStatuses.Undone)],
        });
        var newEpisode = Decide(Overload(admission: 1) with
        {
            SinceUtc = Now.AddMinutes(-2),
            History = [new("si-2", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.ProposeMoveWork, Now.AddMinutes(-5), StallInterventionStatuses.Proposed)],
        });

        Assert.Equal(StallInterventionPolicy.AwaitingFollowUp, pending.ReasonCode);
        Assert.Equal(StallInterventionPolicy.WaitingForPerson, proposal.ReasonCode);
        Assert.Equal(StallInterventionPolicy.WaitingForPerson, undone.ReasonCode);
        Assert.Equal(AiOperatorDisposition.Propose, newEpisode.Disposition);
    }

    [Theory]
    [InlineData(true, true, 20, 40.0, AiOperatorDisposition.Act, StallInterventionActions.RestoreAdmission)]
    [InlineData(true, true, 5, 40.0, AiOperatorDisposition.Hold, StallInterventionActions.None)]
    [InlineData(true, true, 20, 90.0, AiOperatorDisposition.Hold, StallInterventionActions.None)]
    [InlineData(false, true, 0, 300.0, AiOperatorDisposition.Ask, StallInterventionActions.Escalate)]
    [InlineData(true, false, 20, 40.0, AiOperatorDisposition.Ask, StallInterventionActions.Escalate)]
    public void Follow_up_restores_admission_when_calm_and_escalates_when_the_condition_stays(bool cleared,
        bool effectObserved, int calmMinutes, double loadPercent, AiOperatorDisposition disposition, string effect)
    {
        var decision = Decide(FollowUp(StallInterventionActions.LowerAdmission, cleared, effectObserved,
            calmMinutes, loadPercent / 100));

        Assert.Equal((disposition, effect), (decision.Disposition, decision.Effect));
        if (effect == StallInterventionActions.RestoreAdmission)
        {
            Assert.Equal((8, 6), (decision.Intervention!.TargetAdmission, decision.Intervention.PreviousAdmission));
            Assert.Contains("from 6 back to 8", decision.Intervention.Done);
        }
        if (effect == StallInterventionActions.Escalate)
            Assert.Contains("receipt chain (3 receipts)", decision.Intervention!.Done);
    }

    [Fact]
    public void Lowered_admission_without_a_calm_signal_goes_to_a_person_after_the_wait_limit()
    {
        var silent = FollowUp(StallInterventionActions.LowerAdmission, cleared: true, effectObserved: true, 0, 0) with
        {
            ObservedAtUtc = Now.AddHours(5),
        };
        silent = silent with { FollowUp = silent.FollowUp! with { LoadPerCoreNow = null, CalmSinceUtc = null } };

        var decision = Decide(silent);

        Assert.Equal((AiOperatorDisposition.Ask, StallInterventionPolicy.RestoreOverdue), (decision.Disposition, decision.ReasonCode));
        Assert.Contains("still lowered to 6 instead of 8", decision.Intervention!.Observed);
    }

    [Fact]
    public void A_stall_without_current_facts_holds_instead_of_failing()
    {
        var gate = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending"));
        var host = Decide(Facts(StallKinds.HostOverload, "host:agent-runner-01"));

        Assert.Equal((AiOperatorDisposition.Hold, StallInterventionPolicy.FactsUnavailable), (gate.Disposition, gate.ReasonCode));
        Assert.Equal(StallInterventionPolicy.FactsUnavailable, host.ReasonCode);
    }

    [Fact]
    public void Follow_up_waits_until_due_and_confirms_a_cleared_condition()
    {
        var early = Decide(FollowUp(StallInterventionActions.RestartService, cleared: false, effectObserved: true,
            0, 0) with { ObservedAtUtc = Now.AddMinutes(-20) });
        var cleared = Decide(FollowUp(StallInterventionActions.RestartService, cleared: true, effectObserved: true, 0, 0));

        Assert.Equal(StallInterventionPolicy.FollowUpPending, early.ReasonCode);
        Assert.Equal((AiOperatorDisposition.Act, AutonomyClass.A0, StallInterventionActions.ConfirmCleared),
            (cleared.Disposition, cleared.Autonomy, cleared.Effect));
    }

    [Fact]
    public void Rerouted_provider_waits_for_the_login_and_routes_back_once_it_works()
    {
        var waiting = Decide(FollowUp(StallInterventionActions.RerouteProvider, cleared: false, effectObserved: true, 0, 0));
        var renewed = Decide(FollowUp(StallInterventionActions.RerouteProvider, cleared: true, effectObserved: true, 0, 0));
        var lost = Decide(FollowUp(StallInterventionActions.RerouteProvider, cleared: false, effectObserved: false, 0, 0));

        Assert.Equal(StallInterventionPolicy.WaitingForLogin, waiting.ReasonCode);
        Assert.Equal(StallInterventionActions.ConfirmCleared, renewed.Effect);
        Assert.Contains("route attempts to agent-runner-01 again", renewed.Intervention!.Done);
        Assert.Equal((AiOperatorDisposition.Ask, StallInterventionPolicy.EffectMissing), (lost.Disposition, lost.ReasonCode));
    }

    [Theory]
    [MemberData(nameof(EveryKind))]
    public void No_stall_ever_plans_an_A3_action(StallInterventionFacts facts)
    {
        var decision = Decide(facts);

        Assert.NotEqual(AutonomyClass.A3, decision.Autonomy);
        Assert.False(decision.Disposition == AiOperatorDisposition.Act && decision.Autonomy == AutonomyClass.A2,
            "An A2 intervention must be a proposal.");
    }

    public static TheoryData<StallInterventionFacts> EveryKind() =>
    [
        Overload(8), Overload(1), Overload(null),
        Facts(StallKinds.IdleCapacity, "host:h:review") with { Service = new("h", "review", "review-daemon", true, false, Now, 1, 1, false) },
        Facts(StallKinds.HostOffline, "host:h:coding") with { Service = new("h", "coding", "coding", false, true, Now, 1, 0, false) },
        Provider(["b"], false), Provider([], true),
        Facts(StallKinds.ProviderRateLimited, "host:h:codex") with { Requeue = [] },
        Facts(StallKinds.ClaimRefusal, "queue:x") with { ClaimReason = "x" },
        Facts(StallKinds.BatchGateStuck, "gate:p:plan") with { Gate = new("p", 2, ["a"], [], 3, true, null, null) },
        Facts(StallKinds.DeployDrift, "container:h:engine") with { Deploy = new("h", "orchestrator-engine", "2", "1", null, [], false) },
        Facts(StallKinds.LaneStall, "card:a:lane"),
    ];

    private static AiOperatorDecision Decide(StallInterventionFacts facts)
        => AiOperatorDecisionPolicy.Decide(new AiOperatorDecisionPayload(AiOperatorDeciders.StallIntervention,
            "stall:case", StallIntervention: facts));

    private static StallInterventionFacts Facts(string kind, string subject)
        => new(StallInterventionTriggers.Stall, kind, subject, Now, Now.AddMinutes(-30), ["AGT-3194"],
            new StallInterventionLimits(), []);

    private static StallInterventionFacts Overload(int? admission)
        => Facts(StallKinds.HostOverload, "host:agent-runner-01") with
        {
            Host = new StallHostFacts("agent-runner-01", 12, 46.7, null, null, 95, admission, 8),
        };

    private static StallInterventionFacts Provider(IReadOnlyList<string> healthy, bool alreadyRerouted)
        => Facts(StallKinds.ProviderAuthInvalid, "host:kori-review-01:codex") with
        {
            Provider = new StallProviderFacts("kori-review-01", "codex", "auth-invalid", healthy, 15, alreadyRerouted),
        };

    private static StallInterventionFacts FollowUp(string action, bool cleared, bool effectObserved,
        int calmMinutes, double loadPerCore)
        => new(StallInterventionTriggers.FollowUp, StallKinds.HostOverload, "host:agent-runner-01", Now,
            Now.AddMinutes(-60), [], new StallInterventionLimits(), [],
            FollowUp: new StallFollowUpFacts("si-1", action, Now.AddMinutes(-16), Now.AddMinutes(-1),
                ConditionActive: !cleared, effectObserved, effectObserved ? null : "admission reads 8, expected 6",
                ChainLength: 3, AdmissionBefore: 8, AdmissionNow: 6, LoadPerCoreNow: loadPerCore, TargetPercent: 95,
                CalmSinceUtc: Now.AddMinutes(-calmMinutes)));
}
