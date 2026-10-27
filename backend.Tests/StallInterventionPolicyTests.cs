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
    [InlineData(StallKinds.HostOffline, "review", false, false, true, AiOperatorDisposition.Act, StallInterventionActions.RestartService)]
    [InlineData(StallKinds.IdleCapacity, "review", true, false, true, AiOperatorDisposition.Hold, StallInterventionActions.None)]
    [InlineData(StallKinds.HostOffline, "review", false, true, true, AiOperatorDisposition.Propose, StallInterventionActions.ProposeRestartService)]
    [InlineData(StallKinds.HostOffline, "coding", false, false, false, AiOperatorDisposition.Propose, StallInterventionActions.ProposeMoveWork)]
    public void Stuck_review_daemon_restarts_through_the_operations_server(string kind, string role,
        bool providerStalled, bool stoppedByOperator, bool restartable, AiOperatorDisposition disposition, string effect)
    {
        var decision = Decide(Facts(kind, $"host:kori-review-01:{role}") with
        {
            Service = new StallServiceFacts("kori-review-01", role, role + "-daemon", restartable, stoppedByOperator,
                Now.AddMinutes(-40), WaitingCount: 15, FreeSlots: 3, providerStalled, BusySlots: 0, ClaimableWaiting: 15),
        });

        Assert.Equal((disposition, effect), (decision.Disposition, decision.Effect));
        if (effect == StallInterventionActions.RestartService)
        {
            Assert.Equal(role + "-daemon", decision.Intervention!.Service);
            Assert.Contains("restarted", decision.Intervention.Done);
            Assert.Contains("Nothing to undo", decision.Intervention.Undo);
        }
    }

    /// <summary>
    /// docker compose up --force-recreate ends the container's work: a worker
    /// that still runs work, runs a gate, has unknown load or nothing it can
    /// claim is proposed for a restart, never restarted.
    /// </summary>
    [Theory]
    [InlineData("review", 2, false, 15, "running-work")]
    [InlineData("review", null, false, 15, "running-work")]
    [InlineData("gate", 0, true, 0, "running-work")]
    [InlineData("review", 0, false, 0, "nothing-claimable")]
    [InlineData("gate", 0, false, 0, "nothing-claimable")]
    public void A_worker_with_running_or_unclaimable_work_is_proposed_for_a_restart_not_restarted(string role,
        int? busy, bool runningWork, int claimable, string reason)
    {
        var decision = Decide(Facts(StallKinds.IdleCapacity, $"host:kori-gate:{role}") with
        {
            Service = new StallServiceFacts("kori-gate", role, role + "-worker", true, false, Now.AddMinutes(-40),
                WaitingCount: 19, FreeSlots: 1, false, busy, runningWork, claimable),
        });

        Assert.Equal((AiOperatorDisposition.Propose, AutonomyClass.A2, StallInterventionActions.ProposeRestartService, reason),
            (decision.Disposition, decision.Autonomy, decision.Effect, decision.ReasonCode));
        Assert.Contains("nothing was changed", decision.Intervention!.Done);
    }

    [Fact]
    public void Orphaned_remote_gate_workers_holding_every_slot_are_proposed_for_release()
    {
        var orphaned = new StallGateSlotFacts(Total: 2, OwnBusy: 0, RemoteBusy: 2, BatchOrGateRunning: false);
        var service = Decide(Facts(StallKinds.IdleCapacity, "host:kori-gate:gate") with
        {
            Service = new StallServiceFacts("kori-gate", "gate", "gate-worker", true, false, Now.AddMinutes(-40),
                WaitingCount: 19, FreeSlots: 2, false, BusySlots: 0, GateSlots: orphaned),
        });
        var gate = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = new StallGateFacts("KORI", 19, [new("AGT-3200", "review-1", TaskStates.AutoReview)], 3, true, null, null, orphaned),
        });
        var ownGate = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = new StallGateFacts("KORI", 19, [new("AGT-3200", "review-1", TaskStates.AutoReview)], 3, true, null, null,
                orphaned with { OwnBusy = 2 }),
        });

        Assert.Equal((AiOperatorDisposition.Propose, StallInterventionActions.ProposeReleaseGateSlots, "orphaned-gate-workers"),
            (service.Disposition, service.Effect, service.ReasonCode));
        Assert.Contains("2 of 2 gate slots busy (0 free)", service.Intervention!.Observed);
        Assert.Contains("AGT-3237", service.Intervention.Proposal);
        Assert.Equal(StallInterventionActions.ProposeReleaseGateSlots, gate.Effect);
        Assert.Equal(AiOperatorDisposition.Hold, ownGate.Disposition);
    }

    [Fact]
    public void Gate_restart_requires_claimable_queue_and_zero_running_work()
    {
        var baseService = new StallServiceFacts("kori-gate", "gate", "gate-worker", true, false,
            Now.AddMinutes(-40), WaitingCount: 1, FreeSlots: 2, false,
            BusySlots: 0, RunningWork: false, ClaimableWaiting: 1);
        var facts = Facts(StallKinds.IdleCapacity, "host:kori-gate:gate") with { Service = baseService };

        Assert.Equal(StallInterventionActions.RestartService, Decide(facts).Effect);
        Assert.Equal(AiOperatorDisposition.Propose,
            Decide(facts with { Service = baseService with { BusySlots = 1 } }).Disposition);
        Assert.Equal(AiOperatorDisposition.Propose,
            Decide(facts with { Service = baseService with { BusySlots = null } }).Disposition);
        Assert.Equal(AiOperatorDisposition.Propose,
            Decide(facts with { Service = baseService with { ClaimableWaiting = 0 } }).Disposition);
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
    public void Invalid_login_on_a_coding_host_is_a_renewal_proposal_not_a_reroute()
    {
        var facts = Provider(["kori-review-02"], alreadyRerouted: false);
        var decision = Decide(facts with { Provider = facts.Provider! with { Host = "agent-runner-01", ReviewHost = false } });

        Assert.Equal((AiOperatorDisposition.Propose, StallInterventionActions.ProposeProviderLogin, "coding-host-login"),
            (decision.Disposition, decision.Effect, decision.ReasonCode));
        Assert.Contains("Renew the codex login on agent-runner-01", decision.Intervention!.Proposal);
    }

    [Fact]
    public void Rate_limited_runs_requeue_from_their_last_delivery_only_from_an_empty_salvage_in_ready()
    {
        var facts = Facts(StallKinds.ProviderRateLimited, "host:agent-runner-01:codex") with
        {
            Requeue =
            [
                new("AGT-3176", TaskStates.Ready, StallContinuationBases.Missing, true, "refs/heads/agent-results/AGT-3176/r2", "0123456789abcdef"),
                new("AGT-3177", TaskStates.Ready, StallContinuationBases.EmptySalvage, true, "refs/heads/agent-results/AGT-3177/r1", "fedcba9876543210", "base000000001"),
                new("AGT-3178", TaskStates.HumanReview, StallContinuationBases.Missing, true, "refs/heads/agent-results/AGT-3178/r1", "aaaaaaaaaaaaaaaa"),
                new("AGT-3179", TaskStates.Ready, StallContinuationBases.Missing, true, null, null),
                new("AGT-3180", TaskStates.Ready, StallContinuationBases.Missing, false, "refs/heads/agent-results/AGT-3180/r1", "bbbbbbbbbbbbbbbb"),
                new("AGT-3181", TaskStates.Ready, StallContinuationBases.Other, true, "refs/heads/agent-results/AGT-3181/r1", "cccccccccccccccc"),
                new("AGT-3182", TaskStates.Ready, StallContinuationBases.Delivery, true, "refs/heads/agent-results/AGT-3182/r1", "dddddddddddddddd"),
            ],
        };

        var decision = Decide(facts);
        var none = Decide(facts with { Requeue = facts.Requeue.Skip(2).ToArray() });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.RequeueFromDelivery), (decision.Disposition, decision.Effect));
        Assert.Equal(["AGT-3176", "AGT-3177"], decision.Intervention!.Members);
        Assert.Contains("AGT-3176 from refs/heads/agent-results/AGT-3176/r2 at 012345678", decision.Intervention.Done);
        Assert.Contains("AGT-3176 with no continuation base; AGT-3177 with an empty salvage base at base00000", decision.Intervention.Observed);
        Assert.Contains("ended their last run on a provider rate limit", decision.Intervention.Observed);
        Assert.Equal((AiOperatorDisposition.Hold, "wait-for-provider-reset"), (none.Disposition, none.ReasonCode));
    }

    [Fact]
    public void A_card_that_already_had_a_card_intervention_in_the_loop_window_is_not_touched_again()
    {
        var facts = Facts(StallKinds.ProviderRateLimited, "host:agent-runner-01:codex") with
        {
            Requeue = [new("AGT-3176", TaskStates.Ready, StallContinuationBases.Missing, true, "refs/heads/r", "0123456789abcdef")],
            History = [new("si-gate", "gate:KORI:pending", "gate:KORI", StallInterventionActions.ReconcileBatchGate,
                Now.AddHours(-1), StallInterventionStatuses.Verified, ["AGT-3176"])],
        };

        var bound = Decide(facts);
        var observedOnly = Decide(facts with
        {
            History = [facts.History[0] with { Status = StallInterventionStatuses.Observed }],
        });

        Assert.Equal((AiOperatorDisposition.Hold, StallInterventionPolicy.CardBound), (bound.Disposition, bound.ReasonCode));
        Assert.Equal(AiOperatorDisposition.Act, observedOnly.Disposition);
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
        var gate = new StallGateFacts("KORI", 19,
            [
                new("AGT-3097", "review-3097", TaskStates.Completed), new("AGT-3044", "review-3044", TaskStates.Completed),
                new("AGT-3110", "review-3110", TaskStates.Ready), new("AGT-3164", "review-3164", TaskStates.AutoReview),
            ], 79, PlanIdle: true, "KORI:forming", OpenBatchWaitHours: 26);
        var decision = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with { Gate = gate });
        var young = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = gate with { OpenBatchWaitHours = 1 },
        });
        var nothing = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = gate with { Members = [gate.Members[3]], OpenBatchWaitHours = 1 },
        });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.ReconcileBatchGate), (decision.Disposition, decision.Effect));
        Assert.Equal(["AGT-3097", "AGT-3044", "AGT-3110"], decision.Intervention!.Members);
        Assert.Equal(["review-3097", "review-3044", "review-3110"], decision.Intervention.AttemptIds);
        Assert.Equal("KORI:forming", decision.Intervention.BatchId);
        Assert.Contains("19 pending members while its plan is idle", decision.Intervention.Observed);
        Assert.Contains("AGT-3110 in 2-ready", decision.Intervention.Observed);
        Assert.Equal("16 pending, the overdue batch closes now and its members run", decision.Intervention.After);
        Assert.Contains("No automatic undo", decision.Intervention.Undo);
        Assert.DoesNotContain("Undo queues", decision.Intervention.Undo);
        Assert.Null(young.Intervention!.BatchId);
        Assert.Equal(AiOperatorDisposition.Hold, nothing.Disposition);
    }

    [Fact]
    public void Gate_members_with_an_unknown_card_or_with_the_pilot_are_never_dropped()
    {
        var decision = Decide(Facts(StallKinds.BatchGateStuck, "gate:KORI:pending") with
        {
            Gate = new StallGateFacts("KORI", 3,
            [
                new("AGT-9001", "review-a", null), new("AGT-9002", "review-b", TaskStates.HumanReview),
                new("AGT-9003", "review-c", TaskStates.Escalated),
            ], 30, PlanIdle: true, null, null),
        });

        Assert.Equal((AiOperatorDisposition.Hold, StallInterventionPolicy.ObserveOnly), (decision.Disposition, decision.ReasonCode));
        Assert.Contains("2 wait in Human Review or Escalated for the pilot; 1 have no readable card and stay", decision.Detail);
    }

    [Fact]
    public void Failed_engine_start_is_retried_only_through_a_validating_restart()
    {
        var deploy = new StallDeployFacts("kori-gate", "orchestrator-engine", "0.9.6", "0.9.5",
            "engine exited during start: connection refused", [], StartRetryAvailable: true);
        var retried = Decide(Facts(StallKinds.DeployDrift, "container:kori-gate:engine") with { Deploy = deploy });
        var missing = Decide(Facts(StallKinds.DeployDrift, "container:kori-gate:engine") with
        {
            Deploy = deploy with
            {
                StartError = "SERVER_URL: ${ENGINE_SERVER_URL:?set ENGINE_SERVER_URL}", MissingVariables = ["ENGINE_SERVER_URL"],
            },
        });
        var proposed = Decide(Facts(StallKinds.DeployDrift, "container:kori-gate:engine") with
        {
            Deploy = deploy with { StartRetryAvailable = false },
        });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.RetryEngineStart), (retried.Disposition, retried.Effect));
        Assert.Contains("validates the configuration", retried.Intervention!.Done);
        Assert.Equal((AiOperatorDisposition.Propose, StallInterventionActions.ProposeDeployFix, "missing-variables"),
            (missing.Disposition, missing.Effect, missing.ReasonCode));
        Assert.Contains("Set ENGINE_SERVER_URL", missing.Intervention!.Proposal);
        Assert.Contains("a start retry would fail the same way", missing.Intervention.Proposal);
        Assert.Equal((AiOperatorDisposition.Propose, StallInterventionActions.ProposeDeployFix), (proposed.Disposition, proposed.Effect));
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
    public void Observed_and_superseded_receipts_changed_nothing_and_never_count_as_repeats()
    {
        var decision = Decide(Overload(admission: 8) with
        {
            History =
            [
                new("si-1", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-3), StallInterventionStatuses.Observed),
                new("si-2", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddHours(-2), StallInterventionStatuses.Superseded),
            ],
        });

        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.LowerAdmission), (decision.Disposition, decision.Effect));
    }

    [Fact]
    public void An_admission_the_AI_Operator_already_lowered_is_never_lowered_again_without_a_person()
    {
        var facts = Overload(admission: 6);
        var decision = Decide(facts with { Host = facts.Host! with { AiLoweredFrom = 8, AiLoweringId = "si-lowered" } });

        Assert.Equal((AiOperatorDisposition.Propose, StallInterventionActions.ProposeMoveWork, "already-lowered"),
            (decision.Disposition, decision.Effect, decision.ReasonCode));
        Assert.Contains("from 8 to 6 (si-lowered)", decision.Intervention!.Proposal);
    }

    [Fact]
    public void Lowering_carries_the_client_identity_the_admission_is_set_on()
    {
        var facts = Overload(admission: 8);
        var decision = Decide(facts with { Host = facts.Host! with { ClientId = "client-runner-01" } });

        Assert.Equal("client-runner-01", decision.Intervention!.HostId);
    }

    [Fact]
    public void Host_rate_limit_holds_a_fourth_intervention_in_the_same_hour()
    {
        StallInterventionHistoryEntry Entry(string id, string subject, int minutesAgo)
            => new(id, subject, "kori-review-01", StallInterventionActions.RestartService, Now.AddMinutes(-minutesAgo),
                StallInterventionStatuses.Verified);
        var facts = Facts(StallKinds.IdleCapacity, "host:kori-review-01:review") with
        {
            Service = new StallServiceFacts("kori-review-01", "review", "review-daemon", true, false, Now, 4, 2, false,
                BusySlots: 0, ClaimableWaiting: 4),
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

        var applying = Decide(Overload(admission: 6) with
        {
            History = [new("si-0", "host:agent-runner-01", "agent-runner-01", StallInterventionActions.LowerAdmission, Now.AddMinutes(-50), StallInterventionStatuses.Applying)],
        });

        Assert.Equal(StallInterventionPolicy.AwaitingFollowUp, pending.ReasonCode);
        Assert.Equal(StallInterventionPolicy.AwaitingFollowUp, applying.ReasonCode);
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
    public void Persisting_overload_while_more_runs_are_active_than_the_new_admission_drains_instead_of_escalating()
    {
        var draining = FollowUp(StallInterventionActions.LowerAdmission, cleared: false, effectObserved: true, 0, 3.9);
        draining = draining with { FollowUp = draining.FollowUp! with { ActiveSlotsNow = 8 } };
        var drained = draining with { FollowUp = draining.FollowUp! with { ActiveSlotsNow = 6 } };
        var overdue = draining with { ObservedAtUtc = Now.AddHours(5) };

        Assert.Equal((AiOperatorDisposition.Hold, StallInterventionPolicy.Draining), (Decide(draining).Disposition, Decide(draining).ReasonCode));
        Assert.Contains("still runs 8 runs above the lowered admission of 6", Decide(draining).Detail);
        Assert.Equal(StallInterventionPolicy.ConditionPersists, Decide(drained).ReasonCode);
        Assert.Equal(StallInterventionPolicy.ConditionPersists, Decide(overdue).ReasonCode);
    }

    [Theory]
    [InlineData(false, 20, 40.0, 6, AiOperatorDisposition.Act, StallInterventionPolicy.ConditionCleared)]
    [InlineData(true, 20, 40.0, 6, AiOperatorDisposition.Hold, StallInterventionPolicy.WaitingForCalm)]
    [InlineData(false, 5, 40.0, 6, AiOperatorDisposition.Hold, StallInterventionPolicy.WaitingForCalm)]
    [InlineData(false, 20, 40.0, 8, AiOperatorDisposition.Hold, StallInterventionPolicy.NothingToRestore)]
    public void An_escalated_reduction_still_in_force_is_restored_once_calm_and_never_escalated_again(bool overloaded,
        int calmMinutes, double loadPercent, int admissionNow, AiOperatorDisposition disposition, string reason)
    {
        var facts = FollowUp(StallInterventionActions.LowerAdmission, !overloaded, effectObserved: true, calmMinutes,
            loadPercent / 100) with { Trigger = StallInterventionTriggers.Restore, ObservedAtUtc = Now.AddHours(9) };
        facts = facts with
        {
            FollowUp = facts.FollowUp! with { AdmissionNow = admissionNow, CalmSinceUtc = Now.AddHours(9).AddMinutes(-calmMinutes) },
        };

        var decision = Decide(facts);

        Assert.Equal(disposition, decision.Disposition);
        if (disposition == AiOperatorDisposition.Act)
        {
            Assert.Equal((StallInterventionActions.RestoreAdmission, 8), (decision.Effect, decision.Intervention!.TargetAdmission));
            Assert.Contains("from 6 back to 8", decision.Intervention.Done);
        }
        else Assert.Equal(reason, decision.ReasonCode);
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
        Facts(StallKinds.IdleCapacity, "host:h:review") with { Service = new("h", "review", "review-daemon", true, false, Now, 1, 1, false, 0, false, 1) },
        Facts(StallKinds.IdleCapacity, "host:h:gate") with { Service = new("h", "gate", "gate-worker", true, false, Now, 1, 1, false, 0, false, 0, new(2, 0, 2, false)) },
        Facts(StallKinds.HostOffline, "host:h:coding") with { Service = new("h", "coding", "coding", false, true, Now, 1, 0, false) },
        Provider(["b"], false), Provider([], true),
        Facts(StallKinds.ProviderRateLimited, "host:h:codex") with { Requeue = [] },
        Facts(StallKinds.ClaimRefusal, "queue:x") with { ClaimReason = "x" },
        Facts(StallKinds.BatchGateStuck, "gate:p:plan") with { Gate = new("p", 2, [new("a", "r-a", TaskStates.Completed)], 3, true, null, null) },
        Facts(StallKinds.DeployDrift, "container:h:engine") with { Deploy = new("h", "orchestrator-engine", "2", "1", null, [], false) },
        Facts(StallKinds.DeployDrift, "container:h:engine") with { Deploy = new("h", "orchestrator-engine", "2", "1", "X_URL missing", ["X_URL"], true) },
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
            Provider = new StallProviderFacts("kori-review-01", "codex", "auth-invalid", healthy, 15, alreadyRerouted, ReviewHost: true),
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
