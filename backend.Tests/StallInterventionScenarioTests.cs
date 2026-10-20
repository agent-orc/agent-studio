extern alias EngineAssembly;

using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Watcher;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using EngineLoop = EngineAssembly::AgentStudio.OrchestratorEngine.AiOperatorDecisionLoop;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3207: one scenario per intervention, from the AGT-3206 stall event to
/// the plain-language receipt on the event, the cards and the host, with fakes
/// for the Operations Server and the hosts. Each automatic action is verified
/// by its measured effect, then by a follow-up check.
/// </summary>
public sealed class StallInterventionScenarioTests : IDisposable
{
    private readonly Harness _h = new();

    [Fact]
    public async Task Overloaded_runner_gets_lower_admission_then_the_old_value_back_once_calm()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, Cores: 12, Load1: 46.7, Target: 95);
        _h.Raise(Overload());

        await _h.Service.RunOnceAsync();

        var lowered = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.LowerAdmission, StallInterventionStatuses.Applied, true, 6),
            (lowered.Action, lowered.Status, lowered.EffectObserved, _h.Admission.Hosts[ClientId].Admission));
        Assert.Contains("agent-runner-01 ran with load 46.7 on 12 cores", lowered.Receipt);
        Assert.Contains("from 8 to 6", lowered.Receipt);
        Assert.Contains("admission reads 6", lowered.EffectEvidence);
        var entry = Assert.Single(_h.WorkspaceFeed(), item => item.Intervention is not null);
        Assert.Equal((OrchestratorLogKinds.Intervention, lowered.Id), (entry.Kind, entry.Intervention!.Id));
        Assert.Contains("Settings > Capacity", entry.Intervention.Undo);

        // Within the follow-up window nothing else happens on the subject.
        _h.Clock.Advance(TimeSpan.FromMinutes(5));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        Assert.Single(_h.Store.All());

        // The load is back: the stall clears, the host stays calm, the admission is restored.
        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(12));
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with
        {
            Load1 = 4.0, CalmSince = _h.Now.AddMinutes(-16),
        };
        await _h.Service.RunOnceAsync();

        Assert.Equal(8, _h.Admission.Hosts[ClientId].Admission);
        var records = _h.Store.All();
        var restore = Assert.Single(records, item => item.Action == StallInterventionActions.RestoreAdmission);
        Assert.Contains("from 6 back to 8", restore.Receipt);
        Assert.Equal(lowered.Id, restore.PreviousInterventionId);
        Assert.Equal(StallInterventionStatuses.Verified, records.Single(item => item.Id == lowered.Id).Status);
    }

    [Fact]
    public async Task Overload_that_stays_after_the_follow_up_escalates_with_the_receipt_chain()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();

        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();

        var records = _h.Store.All();
        var escalation = Assert.Single(records, item => item.Action == StallInterventionActions.Escalate);
        Assert.Equal((StallInterventionStatuses.Escalated, StallInterventionPolicy.ConditionPersists),
            (escalation.Status, escalation.ReasonCode));
        Assert.Equal([records[0].Id], escalation.Chain);
        Assert.Equal(StallInterventionStatuses.Escalated, records[0].Status);
        var alert = Assert.Single(_h.WorkspaceFeed(), item => item.Kind == OrchestratorLogKinds.Alert);
        Assert.Contains("Receipt chain: " + records[0].Id, alert.Reasoning);

        // The escalated episode waits for a person: further passes change nothing.
        for (var pass = 0; pass < 5; pass++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(5));
            _h.Raise(Overload());
            await _h.Service.RunOnceAsync();
        }
        Assert.Equal(2, _h.Store.All().Count);
        Assert.Equal(6, _h.Admission.Hosts[ClientId].Admission);
    }

    [Fact]
    public async Task An_action_without_a_measured_effect_is_escalated_at_once()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Admission.IgnoreWrites = true;
        _h.Raise(Overload());

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Escalated, false), (record.Status, record.EffectObserved));
        Assert.Contains("admission reads 8, expected 6", record.Outcome);
        Assert.Single(_h.WorkspaceFeed(), item => item.Kind == OrchestratorLogKinds.Alert);
    }

    [Fact]
    public async Task Idle_review_daemon_is_restarted_through_the_operations_server_and_verified()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.WriteCard(TaskStates.AutoReview, "AGT-3194");
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal([("kori-review-01", "review-daemon")], _h.Restarts.Calls);
        Assert.Equal((StallInterventionActions.RestartService, true), (record.Action, record.EffectObserved));
        Assert.Contains("3 idle slots while 15 items wait", record.Observed);
        Assert.Contains(_h.ProjectFeed(), item => item.Intervention?.Id == record.Id);
        Assert.Contains(StallInterventionReceipts.Headline(record), _h.CardTimeline(TaskStates.AutoReview, "AGT-3194"));

        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(11));
        await _h.Service.RunOnceAsync();

        Assert.Equal(StallInterventionStatuses.Verified, _h.Store.Find(record.Id)!.Status);
        Assert.Single(_h.Restarts.Calls);
    }

    [Fact]
    public async Task Restart_without_an_operations_agent_becomes_a_proposal_for_a_person()
    {
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));

        await _h.Service.RunOnceAsync();
        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Proposed, "A2"), (record.Status, record.Autonomy));
        Assert.Empty(_h.Restarts.Calls);
    }

    [Fact]
    public async Task Invalid_provider_login_reroutes_attempts_until_the_login_works_again()
    {
        _h.Routes.Healthy.AddRange(["kori-review-02", "kori-review-03"]);
        _h.Raise(Snapshot(providers: [new("kori-review-01", "codex", "auth-invalid", Now0.AddHours(-2), ["AGT-3194"], "CredentialInvalid")]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Contains(("kori-review-01", "codex"), _h.Routes.Excluded);
        Assert.Contains("Renew the codex login on kori-review-01", record.Proposal);
        Assert.True(record.Undoable);

        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        _h.Raise(Snapshot(providers: [new("kori-review-01", "codex", "auth-invalid", Now0.AddHours(-2), ["AGT-3194"], "CredentialInvalid")]));
        await _h.Service.RunOnceAsync();
        Assert.Equal(StallInterventionStatuses.Applied, _h.Store.Find(record.Id)!.Status);

        _h.Clear();
        await _h.Service.RunOnceAsync();

        Assert.Equal(StallInterventionStatuses.Verified, _h.Store.Find(record.Id)!.Status);
        Assert.DoesNotContain(("kori-review-01", "codex"), _h.Routes.Excluded);
    }

    [Fact]
    public async Task Stuck_batch_gate_drops_completed_members_and_closes_the_overdue_batch()
    {
        _h.WriteCard(TaskStates.Completed, "AGT-3097");
        _h.Gate.Facts = new StallGateFacts("KORI", 19,
            [new("AGT-3097", "review-3097", TaskStates.Completed), new("AGT-3044", "review-3044", TaskStates.AutoReview)],
            79, PlanIdle: true, "KORI:forming", 30);
        _h.Gate.Pending.UnionWith(["review-3097", "review-3044"]);
        _h.Raise(Snapshot(gates: [new("KORI", "pending", true, 19, Now0.AddHours(-79), ["AGT-3097", "AGT-3044"], ["AGT-3097"])]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.ReconcileBatchGate, true, true),
            (record.Action, record.EffectObserved, _h.Gate.CloseRequested));
        Assert.Equal(["review-3097"], record.AttemptIds);
        Assert.DoesNotContain("review-3097", _h.Gate.Pending);
        Assert.Contains("review-3044", _h.Gate.Pending);
        Assert.Contains("I dropped AGT-3097 from the gate queue", record.Receipt);
        Assert.Contains(StallInterventionReceipts.Headline(record), _h.CardTimeline(TaskStates.Completed, "AGT-3097"));
    }

    [Fact]
    public async Task Rate_limited_run_requeues_from_its_last_delivery_and_undo_clears_the_base()
    {
        _h.Continuations.Candidates.Add(new("AGT-3176", TaskStates.Ready, StallContinuationBases.EmptySalvage, true,
            "refs/heads/agent-results/AGT-3176/r2", "0123456789abcdef", "fedcba9876543210"));
        _h.Raise(Snapshot(providers: [new("agent-runner-01", "codex", "rate-limited", Now0.AddHours(-11), ["AGT-3176"], "rate limit 08:12Z")]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.RequeueFromDelivery, true), (record.Action, record.EffectObserved));
        Assert.Equal("0123456789abcdef", _h.Continuations.Pins["AGT-3176"].Sha);

        var undo = _h.Service.Undo(record.Id, "robert");

        Assert.True(undo.Undone, undo.Message);
        Assert.False(_h.Continuations.Pins.ContainsKey("AGT-3176"));
        Assert.Equal(("robert", StallInterventionStatuses.Undone), (undo.Record!.UndoneBy, undo.Record.Status));
        Assert.Equal("intervention-already-undone", _h.Service.Undo(record.Id, "robert").Message);
    }

    [Fact]
    public async Task Failed_engine_start_is_retried_with_a_validating_restart()
    {
        _h.Restarts.Allowed.Add(("kori-gate", "orchestrator-engine"));
        _h.Raise(Snapshot(deployments: [new("kori-gate", "engine", "0.9.6", "0.9.5", Now0.AddHours(-3),
            "engine exited during start: connection refused")]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionActions.RetryEngineStart, record.Action);
        Assert.Equal([("kori-gate", "orchestrator-engine")], _h.Restarts.Calls);
    }

    [Fact]
    public async Task Engine_start_with_a_known_missing_variable_is_proposed_not_retried()
    {
        _h.Restarts.Allowed.Add(("kori-gate", "orchestrator-engine"));
        _h.Raise(Snapshot(deployments: [new("kori-gate", "engine", "0.9.6", "0.9.5", Now0.AddHours(-3), "ENGINE_SERVER_URL missing")]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.ProposeDeployFix, StallInterventionStatuses.Proposed), (record.Action, record.Status));
        Assert.Contains("Set ENGINE_SERVER_URL", record.Proposal);
        Assert.Empty(_h.Restarts.Calls);
    }

    [Fact]
    public async Task Failed_restart_never_loops_and_stays_with_a_person()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.Restarts.Succeeds = false;
        for (var pass = 0; pass < 8; pass++)
        {
            _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));
            await _h.Service.RunOnceAsync();
            _h.Clock.Advance(TimeSpan.FromMinutes(5));
        }

        Assert.Single(_h.Restarts.Calls);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Escalated, false), (record.Status, record.EffectObserved));
        Assert.Contains("Operations Server attempt failed", record.EffectEvidence);
    }

    [Fact]
    public async Task Undo_restores_the_admission_and_the_operator_keeps_control_of_the_episode()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        var lowered = Assert.Single(_h.Store.All());

        var undo = _h.Service.Undo(lowered.Id, "robert");

        Assert.True(undo.Undone, undo.Message);
        Assert.Equal(8, _h.Admission.Hosts[ClientId].Admission);
        var undoEntry = _h.WorkspaceFeed().Last();
        Assert.Equal("Undone by robert: " + lowered.Done, undoEntry.Summary);

        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        Assert.Equal(8, _h.Admission.Hosts[ClientId].Admission);
        Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionPolicy.WaitingForPerson,
            Assert.Single(_h.Service.LatestDecisions()).ReasonCode);
    }

    [Fact]
    public void Restart_and_escalation_cannot_be_undone()
    {
        _h.Store.Save(new StallInterventionRecord
        {
            Id = "si-restart", WriteKey = "w", Subject = "host:h:review", Kind = StallKinds.IdleCapacity, Host = "h",
            Action = StallInterventionActions.RestartService, Autonomy = "A1", Disposition = "Act", ReasonCode = "r",
            Status = StallInterventionStatuses.Verified, DecidedAtUtc = _h.Now,
        });

        Assert.Equal("intervention-not-undoable", _h.Service.Undo("si-restart", "robert").Message);
        Assert.Equal("intervention-not-found", _h.Service.Undo("si-missing", "robert").Message);
    }

    [Fact]
    public async Task Engine_decides_the_stall_and_the_authority_applies_its_receipt()
    {
        var admission = new FakeAdmission();
        admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _h.Root,
                ["WatchPaths:0:Name"] = Harness.Project,
                ["WatchPaths:0:Path"] = _h.ProjectPath,
                ["WatchPaths:0:RootPath"] = _h.ProjectPath,
                ["ReviewDecisionOrchestrator:Enabled"] = "false",
                ["Orchestration:Transitions:OperatorDecision"] = "Engine",
                ["StallIntervention:Mode"] = StallInterventionModes.Act,
            }));
            builder.ConfigureServices(services =>
            {
                services.AddSingleton<IHostAdmissionControl>(admission);
                services.AddSingleton<TimeProvider>(_h.Clock);
            });
        });
        var provider = factory.Services;
        var cases = provider.GetRequiredService<WatcherCaseStore>();
        new WatcherCaseEngine(cases, provider.GetRequiredService<WatcherSuppressionStore>())
            .Ingest(_h.Root, OperationalStallDetector.Detect(Overload(), new(), _h.Now), "engine-sweep", new WatcherOptions(), _h.Now);
        var interventions = provider.GetRequiredService<StallInterventionService>();

        var pass = await interventions.RunOnceAsync();

        Assert.Equal((1, 0), (pass.Submitted, pass.Applied));
        var store = provider.GetRequiredService<StallInterventionStore>();
        Assert.Empty(store.All());
        var occurrences = provider.GetRequiredService<MonolithOrchestrationStore>();
        var claim = occurrences.Claim(new OrchestrationClaimRequest("engine_test", "instance", [OrchestrationStage.OperatorDecision]));
        Assert.Equal("host:agent-runner-01", claim.Run!.TaskId);
        var decision = await new EngineLoop().ExecuteAsync(claim.Run, default);
        var receipt = AiOperatorDecisionGateway.ReadDecision(decision.OutputJson);
        Assert.Equal((AiOperatorDisposition.Act, StallInterventionActions.LowerAdmission), (receipt.Disposition, receipt.Effect));
        var completed = await occurrences.CompleteAsync(claim.Run.RunId, new CompleteOrchestrationStageRequest(
            "engine_test", "instance", claim.Lease!.LeaseId, claim.Lease.Fence, OrchestrationStage.OperatorDecision,
            decision.Action, decision.OutputJson, $"engine:{claim.Run.RunId}:stall"), default);

        Assert.Equal("completed", completed.Status);
        var record = Assert.Single(store.All());
        Assert.Equal(("engine", claim.Run.RunId, 6), (record.DecidedBy, record.OccurrenceRunId, admission.Hosts[ClientId].Admission));

        // A held decision (the follow-up is not due) is decided in process and never sent to the engine.
        var held = await interventions.RunOnceAsync();
        Assert.Equal(0, held.Submitted);
        Assert.Null(occurrences.Claim(new OrchestrationClaimRequest("engine_test", "instance", [OrchestrationStage.OperatorDecision])).Run);
    }

    [Fact]
    public async Task An_exception_after_the_admission_write_never_lowers_it_again()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Admission.FailAfterWrite = new IOException("identity store write timed out");
        _h.Raise(Overload());

        await _h.Service.RunOnceAsync();
        _h.Admission.FailAfterWrite = null;
        for (var pass = 0; pass < 6; pass++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(5));
            _h.Raise(Overload());
            await _h.Service.RunOnceAsync();
        }

        Assert.Equal([(ClientId, 6)], _h.Admission.Writes);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Escalated, false, true), (record.Status, record.EffectObserved, record.Undoable));
        Assert.Contains("identity store write timed out", record.EffectEvidence);
        Assert.True(_h.Service.Undo(record.Id, "robert").Undone);
        Assert.Equal(8, _h.Admission.Hosts[ClientId].Admission);
    }

    [Fact]
    public async Task A_restart_between_action_and_receipt_never_recreates_the_container_twice()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.Restarts.StopAfterRestart = true;
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.RunOnceAsync());
        var applying = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionStatuses.Applying, applying.Status);

        // The process restarts; the stall is still there on every later pass.
        _h.Restart();
        _h.Restarts.StopAfterRestart = false;
        for (var pass = 0; pass < 5; pass++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(3));
            _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));
            await _h.Service.RunOnceAsync();
        }

        Assert.Single(_h.Restarts.Calls);
        var settled = Assert.Single(_h.Store.All());
        Assert.Equal((applying.Id, StallInterventionStatuses.Escalated), (settled.Id, settled.Status));
        Assert.Contains("interrupted", settled.Outcome);
        Assert.Contains("did not repeat it", settled.Outcome);
        Assert.StartsWith("stall-intervention:stall:", applying.WriteKey);
        Assert.Equal(applying.WriteKey, Assert.Single(_h.Restarts.Keys));
    }

    [Fact]
    public async Task An_interrupted_admission_write_with_an_observed_effect_continues_to_its_follow_up()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Admission.FailAfterWrite = new OperationCanceledException("host stopping");
        _h.Raise(Overload());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => _h.Service.RunOnceAsync());
        _h.Admission.FailAfterWrite = null;

        _h.Restart();
        for (var pass = 0; pass < 3; pass++)
        {
            _h.Clock.Advance(TimeSpan.FromMinutes(4));
            _h.Raise(Overload());
            await _h.Service.RunOnceAsync();
        }

        Assert.Equal([(ClientId, 6)], _h.Admission.Writes);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Applied, true), (record.Status, record.EffectObserved));
        Assert.Contains("effect is observed", record.Outcome);
    }

    [Fact]
    public async Task A_review_daemon_with_busy_slots_is_proposed_for_a_restart_and_never_recreated()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.Workload.Reading = new ServiceWorkloadReading(2, false, 15);
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 1, 15, Now0.AddHours(-2), ["AGT-3194"])]));

        await _h.Service.RunOnceAsync();
        await _h.Service.RunOnceAsync();

        Assert.Empty(_h.Restarts.Calls);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.ProposeRestartService, StallInterventionStatuses.Proposed, "running-work"),
            (record.Action, record.Status, record.ReasonCode));
        Assert.Contains("It still runs 2 items", record.Observed);
    }

    [Fact]
    public async Task Work_that_starts_between_decision_and_apply_supersedes_the_restart()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));
        var facts = Facts().Stall(_h.Cases.All(_h.Root).Single(), _h.Cases.All(_h.Root).ToArray(), [],
            StallInterventionOptions.FromConfiguration(_h.Configuration), _h.Now);
        _h.Workload.Reading = new ServiceWorkloadReading(1, false, 14);

        await _h.Effect.ApplyAsync(Application(facts, "engine-run-1"), default);

        Assert.Empty(_h.Restarts.Calls);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionStatuses.Superseded, record.Status);
        Assert.Contains("now runs 1 items", record.Outcome);
    }

    [Fact]
    public async Task Lease_that_starts_while_restart_is_queued_blocks_the_operation()
    {
        _h.Restarts.Allowed.Add(("kori-review-01", "review-daemon"));
        _h.Raise(Snapshot(capacity: [new("KORI", "kori-review-01", "review", 3, 15, Now0.AddHours(-2), ["AGT-3194"])]));
        _h.Workload.ClaimStartsBeforeFence = true;

        await _h.Service.RunOnceAsync();

        Assert.Empty(_h.Restarts.Calls);
        Assert.True(_h.Workload.Reading!.RunningWork);
        Assert.Equal(StallInterventionStatuses.Superseded, Assert.Single(_h.Store.All()).Status);
    }

    [Fact]
    public async Task Escalated_reduction_is_not_lowered_again_by_a_new_episode_and_calm_restores_the_baseline()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        var lowered = _h.Store.All().Single(item => item.Action == StallInterventionActions.LowerAdmission);
        Assert.Equal(StallInterventionStatuses.Escalated, lowered.Status);
        Assert.True(StallInterventionReceipts.View(lowered).Undoable);

        // The overload clears and comes back as a new episode while the reduction is in force.
        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(30));
        _h.Raise(Overload(since: _h.Now.AddMinutes(-12)));
        await _h.Service.RunOnceAsync();

        Assert.Equal([(ClientId, 6)], _h.Admission.Writes);
        var proposal = _h.Store.All().Last();
        Assert.Equal((StallInterventionActions.ProposeMoveWork, "already-lowered"), (proposal.Action, proposal.ReasonCode));

        // Calm: the escalated reduction is restored to the person's baseline.
        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with { Load1 = 3.0, CalmSince = _h.Now.AddMinutes(-18) };
        await _h.Service.RunOnceAsync();
        await _h.Service.RunOnceAsync();

        Assert.Equal([(ClientId, 6), (ClientId, 8)], _h.Admission.Writes);
        var restore = _h.Store.All().Single(item => item.Action == StallInterventionActions.RestoreAdmission);
        Assert.Contains("from 6 back to 8", restore.Done);
        Assert.Equal(restore.Id, _h.Store.Find(lowered.Id)!.RevertedBy);
        Assert.False(_h.Store.Find(lowered.Id)!.AdmissionInForce);
    }

    [Fact]
    public async Task A_lowered_admission_that_a_person_changed_is_never_restored_over()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        _h.Clock.Advance(TimeSpan.FromMinutes(16));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();

        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with { Admission = 4, Load1 = 3.0, CalmSince = _h.Now.AddMinutes(-30) };
        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        await _h.Service.RunOnceAsync();
        await _h.Service.RunOnceAsync();

        Assert.Equal([(ClientId, 6)], _h.Admission.Writes);
        Assert.Equal(4, _h.Admission.Hosts[ClientId].Admission);
        var lowered = _h.Store.All().Single(item => item.Action == StallInterventionActions.LowerAdmission);
        Assert.StartsWith("admission changed:", lowered.RevertedBy);
    }

    [Fact]
    public async Task Old_lane_stalls_never_starve_a_fresh_overload()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        var lanes = Enumerable.Range(1, 25)
            .Select(index => new LaneStallFact(Harness.Project, TaskStates.AutoReview, $"AGT-{1000 + index}", Now0.AddDays(-3)))
            .ToArray();
        _h.Raise(new OperationalStallSnapshot(Overload().Hosts, [], [], [], [], lanes, []));

        var pass = await _h.Service.RunOnceAsync();

        Assert.Equal((1, 1), (pass.Considered, pass.Applied));
        Assert.Equal(6, _h.Admission.Hosts[ClientId].Admission);
        Assert.DoesNotContain(_h.Service.LatestDecisions(), item => item.Kind == StallKinds.LaneStall);
    }

    [Fact]
    public async Task A_duplicate_engine_occurrence_in_the_next_bucket_causes_one_action()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        var facts = Facts().Stall(_h.Cases.All(_h.Root).Single(), _h.Cases.All(_h.Root).ToArray(), [],
            StallInterventionOptions.FromConfiguration(_h.Configuration), _h.Now);

        await _h.Effect.ApplyAsync(Application(facts, "engine-run-1"), default);
        _h.Clock.Advance(StallInterventionService.EnginePeriod);
        await _h.Effect.ApplyAsync(Application(facts, "engine-run-2"), default);

        Assert.Equal([(ClientId, 6)], _h.Admission.Writes);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionEffect.WriteKey(facts, StallInterventionActions.LowerAdmission, observe: false), record.WriteKey);
    }

    [Fact]
    public async Task An_admission_a_person_changed_after_the_decision_is_superseded_without_acting()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        var facts = Facts().Stall(_h.Cases.All(_h.Root).Single(), _h.Cases.All(_h.Root).ToArray(), [],
            StallInterventionOptions.FromConfiguration(_h.Configuration), _h.Now);
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with { Admission = 5 };

        await _h.Effect.ApplyAsync(Application(facts, "engine-run-1"), default);

        Assert.Empty(_h.Admission.Writes);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionStatuses.Superseded, record.Status);
        Assert.Contains("Superseded by a person: the admission of agent-runner-01 reads 5, the plan expected 8", record.Outcome);
    }

    [Fact]
    public async Task Admission_change_between_read_and_atomic_write_preserves_the_persons_value()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        _h.Admission.ChangeBeforeCompare = 5;

        await _h.Service.RunOnceAsync();

        Assert.Equal(5, _h.Admission.Hosts[ClientId].Admission);
        Assert.Empty(_h.Admission.Writes);
        Assert.Equal(StallInterventionStatuses.Superseded, Assert.Single(_h.Store.All()).Status);
    }

    [Fact]
    public async Task Restore_change_between_read_and_atomic_write_preserves_the_persons_value()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        _h.Clear();
        _h.Clock.Advance(TimeSpan.FromMinutes(17));
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with
        { Load1 = 4.0, CalmSince = _h.Now.AddMinutes(-16) };
        _h.Admission.ChangeBeforeCompare = 5;

        await _h.Service.RunOnceAsync();

        Assert.Equal(5, _h.Admission.Hosts[ClientId].Admission);
        Assert.Single(_h.Admission.Writes);
        Assert.Equal(StallInterventionStatuses.Superseded,
            Assert.Single(_h.Store.All(), item => item.Action == StallInterventionActions.RestoreAdmission).Status);
    }

    [Theory]
    [InlineData(11, 46.7)]
    [InlineData(0, 2.0)]
    public async Task Persisted_overload_case_needs_fresh_current_pressure_before_lowering(int sampleAgeMinutes, double load)
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, load, 95,
            SampleUtc: _h.Now.AddMinutes(-sampleAgeMinutes));
        _h.Raise(Overload());

        await _h.Service.RunOnceAsync();

        Assert.Empty(_h.Admission.Writes);
        Assert.DoesNotContain(_h.Store.All(), item => item.Action == StallInterventionActions.LowerAdmission);
    }

    [Fact]
    public async Task Pressure_that_clears_after_decision_supersedes_the_admission_change()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        var stall = Facts().Stall(_h.Cases.All(_h.Root).Single(), _h.Cases.All(_h.Root).ToArray(), [],
            StallInterventionOptions.FromConfiguration(_h.Configuration), _h.Now);
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with
        { SampleUtc = _h.Now.AddMinutes(-11) };

        await _h.Effect.ApplyAsync(Application(stall, "engine-run-stale"), default);

        Assert.Empty(_h.Admission.Writes);
        Assert.Equal(StallInterventionStatuses.Superseded, Assert.Single(_h.Store.All()).Status);
    }

    [Fact]
    public async Task Undo_preserves_a_later_human_capacity_change()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        var lowered = Assert.Single(_h.Store.All());
        _h.Admission.Hosts[ClientId] = _h.Admission.Hosts[ClientId] with { Admission = 5 };

        var undo = _h.Service.Undo(lowered.Id, "robert");

        Assert.False(undo.Undone);
        Assert.Equal(5, _h.Admission.Hosts[ClientId].Admission);
        Assert.Single(_h.Admission.Writes);
        Assert.Null(_h.Store.Find(lowered.Id)!.UndoneAtUtc);
    }

    [Fact]
    public async Task Observe_mode_is_the_default_and_writes_one_would_do_receipt_without_acting()
    {
        using var observe = new Harness(mode: null);
        observe.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        observe.Raise(Overload());

        await observe.Service.RunOnceAsync();
        observe.Clock.Advance(TimeSpan.FromMinutes(5));
        observe.Raise(Overload());
        await observe.Service.RunOnceAsync();

        Assert.Empty(observe.Admission.Writes);
        var record = Assert.Single(observe.Store.All());
        Assert.Equal((StallInterventionStatuses.Observed, StallInterventionActions.LowerAdmission), (record.Status, record.Action));
        Assert.Contains("observe mode", record.Done);
        Assert.Contains("I would have done this: I lowered its admission from 8 to 6", record.Done);
        Assert.False(StallInterventionReceipts.View(record).Undoable);
        Assert.Equal(StallInterventionModes.Observe, StallInterventionOptions.FromConfiguration(observe.Configuration).Mode);
    }

    [Fact]
    public async Task A_switched_off_action_writes_a_would_do_receipt_while_the_others_act()
    {
        _h.Configuration["StallIntervention:Actions:lower-admission"] = "false";
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Routes.Healthy.Add("kori-review-02");
        _h.Raise(new OperationalStallSnapshot(Overload().Hosts, [], [],
            [new("kori-review-01", "codex", "auth-invalid", Now0.AddHours(-2), ["AGT-3194"], "CredentialInvalid")], [], [], []));

        await _h.Service.RunOnceAsync();

        Assert.Empty(_h.Admission.Writes);
        Assert.Contains(("kori-review-01", "codex"), _h.Routes.Excluded);
        var observed = _h.Store.All().Single(item => item.Action == StallInterventionActions.LowerAdmission);
        Assert.Equal(StallInterventionStatuses.Observed, observed.Status);
        Assert.Contains("StallIntervention:Actions:lower-admission", observed.Done);
    }

    [Fact]
    public async Task A_case_waiting_for_a_person_decision_is_left_alone()
    {
        _h.Admission.Hosts[ClientId] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        _h.AskPerson("host:agent-runner-01");

        var pass = await _h.Service.RunOnceAsync();

        Assert.Equal(0, pass.Considered);
        Assert.Empty(_h.Admission.Writes);
        Assert.Empty(_h.Store.All());
    }

    [Fact]
    public async Task Invalid_login_on_a_coding_host_is_proposed_for_renewal_not_rerouted()
    {
        _h.Routes.Healthy.Add("kori-review-02");
        _h.Raise(Snapshot(providers: [new("agent-runner-01", "codex", "auth-invalid", Now0.AddHours(-2), ["AGT-3194"], "CredentialInvalid")]));

        await _h.Service.RunOnceAsync();

        Assert.Empty(_h.Routes.Excluded);
        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.ProposeProviderLogin, StallInterventionStatuses.Proposed), (record.Action, record.Status));
    }

    [Fact]
    public async Task A_reroute_whose_effect_is_not_observed_stays_undoable()
    {
        _h.Routes.Healthy.Add("kori-review-02");
        _h.Raise(Snapshot(providers: [new("kori-review-01", "codex", "auth-invalid", Now0.AddHours(-2), ["AGT-3194"], "CredentialInvalid")]));
        _h.Routes.IgnoreReroute = true;

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionStatuses.Escalated, true), (record.Status, record.Undoable));
        Assert.Contains("Undo reverts whatever part of the change is in force", record.Outcome);
    }

    private StallInterventionFactsBuilder Facts()
        => new(_h.Admission, _h.Restarts, _h.Workload, _h.Routes, _h.Gate, _h.Continuations, _h.Configuration);

    /// <summary>An engine receipt for the frozen facts: the engine runs the same decision library.</summary>
    private static AiOperatorApplication Application(StallInterventionFacts facts, string runId)
    {
        var payload = new AiOperatorDecisionPayload(AiOperatorDeciders.StallIntervention, $"stall:{facts.CaseId}",
            StallIntervention: facts);
        var decision = AiOperatorDecisionPolicy.Decide(payload);
        return new AiOperatorApplication(runId, Harness.Project, facts.Subject, "stall-open",
            $"engine-operator:{runId}:completion", payload, decision, facts.Subject, 0, 0);
    }

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now0 = DateTime.Parse("2026-10-16T19:51:00Z").ToUniversalTime();

    /// <summary>The coding host's client identity id; its display name is agent-runner-01.</summary>
    private const string ClientId = "client-runner-01";

    private static OperationalStallSnapshot Overload(DateTime? since = null) => Snapshot(hosts:
        [new("agent-runner-01", since ?? Now0.AddMinutes(-20), 46.7 / 12, 95, null, null, 8, "8 builds", 95, ClientId)]);

    private static OperationalStallSnapshot Snapshot(
        IReadOnlyList<HostStallFact>? hosts = null, IReadOnlyList<CapacityStallFact>? capacity = null,
        IReadOnlyList<ProviderStallFact>? providers = null, IReadOnlyList<GateStallFact>? gates = null,
        IReadOnlyList<DeployStallFact>? deployments = null)
        => new(hosts ?? [], capacity ?? [], [], providers ?? [], gates ?? [], [], deployments ?? []);

    private sealed class Harness : IDisposable
    {
        public const string Project = "KORI";
        public readonly FakeTimeProvider Clock = new(DateTimeOffset.Parse("2026-10-16T19:51:00Z"));
        public readonly string Root = Path.Combine(Path.GetTempPath(), "stall-interventions-" + Guid.NewGuid().ToString("N"));
        public readonly FakeAdmission Admission = new();
        public readonly FakeRestarts Restarts = new();
        public readonly FakeWorkload Workload = new();
        public readonly FakeRoutes Routes = new();
        public readonly FakeGate Gate = new();
        public readonly FakeContinuations Continuations = new();
        public readonly WatcherCaseStore Cases = new(NullLogger<WatcherCaseStore>.Instance);
        public readonly OrchestratorLog Log = new(NullLogger<OrchestratorLog>.Instance);
        public readonly StallInterventionStore Store;
        public readonly IConfigurationRoot Configuration;
        public StallInterventionService Service { get; private set; }
        public StallInterventionEffect Effect { get; private set; }
        private readonly WatcherCaseEngine _engine;
        private int _sweep;

        public Harness(string? mode = StallInterventionModes.Act)
        {
            foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(ProjectPath, state));
            var settings = new Dictionary<string, string?>
            {
                ["TaskRepository"] = Root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = ProjectPath,
                ["WatchPaths:0:RootPath"] = ProjectPath,
            };
            if (mode is not null) settings["StallIntervention:Mode"] = mode;
            Configuration = new ConfigurationBuilder().AddInMemoryCollection(settings).Build();
            Store = new StallInterventionStore(() => Root);
            (Service, Effect) = Build();
            _engine = new WatcherCaseEngine(Cases, new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance));
        }

        /// <summary>A fresh service and effect over the same files, as after a process restart.</summary>
        public void Restart() => (Service, Effect) = Build();

        private (StallInterventionService, StallInterventionEffect) Build()
        {
            var configuration = Configuration;
            var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var projector = new WatcherActivityProjector(Log, scanner, configuration, NullLogger<WatcherActivityProjector>.Instance);
            var receipts = new StallInterventionReceipts(Log, projector, scanner, new TimelineLog(NullLogger<TimelineLog>.Instance),
                NullLogger<StallInterventionReceipts>.Instance);
            var facts = new StallInterventionFactsBuilder(Admission, Restarts, Workload, Routes, Gate, Continuations, configuration);
            var effect = new StallInterventionEffect(Store, Cases, facts, Admission, Restarts, Workload, Routes, Gate,
                Continuations, receipts, configuration, Clock, NullLogger<StallInterventionEffect>.Instance);
            return (new StallInterventionService(Store, Cases, facts, effect,
                AiOperatorDecisionGateway.ForTestHost(configuration), configuration, Clock,
                NullLogger<StallInterventionService>.Instance), effect);
        }

        public string ProjectPath => Path.Combine(Root, "projects", Project);
        public DateTime Now => Clock.GetUtcNow().UtcDateTime;

        public void Raise(OperationalStallSnapshot snapshot)
            => _engine.Ingest(Root, OperationalStallDetector.Detect(snapshot, new(), Now), $"sweep-{++_sweep}",
                new WatcherOptions(), Now);

        public void Clear() => _engine.ResolveClearedOperationalCases(Root, [], Now);

        /// <summary>A person was asked to decide the case: the AI Operator leaves it alone.</summary>
        public void AskPerson(string subject)
        {
            var item = Cases.All(Root).Single(watcherCase => watcherCase.Subject == subject);
            Cases.Save(Root, item with { State = WatcherCaseStates.DecisionRequired });
        }

        public IReadOnlyList<OrchestratorLogEntry> WorkspaceFeed() => Log.Read(Path.Combine(Root, ".watcher"));

        public IReadOnlyList<OrchestratorLogEntry> ProjectFeed() => Log.Read(ProjectPath);

        public void WriteCard(string state, string id)
        {
            var folder = Path.Combine(ProjectPath, state, id);
            Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
            {
                id, key = id, title = id, state, order = 1, agent = "codex", cliType = "codex",
            }));
        }

        public string CardTimeline(string state, string id)
        {
            var path = TaskPaths.TimelineLog(Path.Combine(ProjectPath, state, id));
            var text = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
            Assert.Contains(TimelineEventKinds.StallInterventionRecorded, text);
            return string.Join("\n", text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                .Select(line => JsonDocument.Parse(line).RootElement.GetProperty("summary").GetString()));
        }

        public void Dispose()
        {
            if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
        }
    }

    private sealed record HostState(int Admission, int Cores, double Load1, double Target, DateTime? CalmSince = null,
        int? ActiveSlots = null, DateTime? SampleUtc = null);

    /// <summary>Hosts keyed by client identity id; the display name is <see cref="HostName"/>.</summary>
    private sealed class FakeAdmission : IHostAdmissionControl
    {
        public const string HostName = "agent-runner-01";
        public readonly Dictionary<string, HostState> Hosts = new(StringComparer.Ordinal);
        public readonly List<(string ClientId, int Value)> Writes = [];
        public bool IgnoreWrites { get; set; }
        public Exception? FailAfterWrite { get; set; }
        public int? ChangeBeforeCompare { get; set; }

        public HostAdmissionReading? Read(string clientId, double restoreFraction, DateTime nowUtc)
            => Hosts.TryGetValue(clientId, out var state)
                ? new HostAdmissionReading(clientId, HostName, state.Admission, state.Cores, state.Load1, null, null, state.Target,
                    state.ActiveSlots ?? state.Admission, state.Load1 / state.Cores, state.CalmSince, state.SampleUtc ?? nowUtc)
                : null;

        public bool TrySetAdmission(string clientId, int expected, int replacement)
        {
            if (ChangeBeforeCompare is { } changed && Hosts.TryGetValue(clientId, out var current))
            {
                Hosts[clientId] = current with { Admission = changed };
                ChangeBeforeCompare = null;
            }
            if (!Hosts.TryGetValue(clientId, out var state) || state.Admission != expected) return false;
            return SetAdmission(clientId, replacement) == replacement;
        }

        public int? SetAdmission(string clientId, int maxParallelism)
        {
            Writes.Add((clientId, maxParallelism));
            if (!IgnoreWrites) Hosts[clientId] = Hosts[clientId] with { Admission = maxParallelism };
            if (FailAfterWrite is { } failure) throw failure;
            return Hosts[clientId].Admission;
        }
    }

    private sealed class FakeWorkload : IServiceWorkload
    {
        public ServiceWorkloadReading? Reading { get; set; } = new(0, false, 15);
        public bool ClaimStartsBeforeFence { get; set; }

        public ServiceWorkloadReading? Read(string host, string role, DateTime nowUtc) => Reading;

        public IDisposable? TryFenceClaims(string host, string role)
        {
            if (ClaimStartsBeforeFence)
            {
                Reading = Reading! with { RunningWork = true };
                return null;
            }
            return new FakeFence();
        }

        private sealed class FakeFence : IDisposable { public void Dispose() { } }
    }

    private sealed class FakeRestarts : IServiceRestartControl
    {
        public readonly HashSet<(string Host, string Service)> Allowed = [];
        public readonly List<(string Host, string Service)> Calls = [];
        public readonly List<string> Keys = [];
        public bool Succeeds { get; set; } = true;
        public bool StopAfterRestart { get; set; }

        public bool CanRestart(string host, string service) => Allowed.Contains((host, service));

        public Task<ServiceRestartOutcome> RestartAsync(string host, string service, string idempotencyKey,
            string reason, CancellationToken ct)
        {
            Calls.Add((host, service));
            Keys.Add(idempotencyKey);
            // The process stops after the container was recreated, before the receipt settles.
            if (StopAfterRestart) throw new OperationCanceledException("host stopping");
            return Task.FromResult(Succeeds
                ? new ServiceRestartOutcome(true, $"Operations Server attempt succeeded for {service}")
                : new ServiceRestartOutcome(false, "Operations Server attempt failed (configuration-invalid)"));
        }
    }

    private sealed class FakeRoutes : IProviderRouteControl
    {
        public readonly List<string> Healthy = [];
        public readonly HashSet<(string Host, string Provider)> Excluded = [];

        public IReadOnlyList<string> HealthyHosts(string provider, string exceptHost) => Healthy.Where(host => host != exceptHost).ToArray();
        public bool IsReviewHost(string host) => host.StartsWith("kori-review", StringComparison.Ordinal);
        public bool IsRerouted(string host, string provider) => Excluded.Contains((host, provider));
        public bool IgnoreReroute { get; set; }

        public void Reroute(string host, string provider, string interventionId)
        {
            if (!IgnoreReroute) Excluded.Add((host, provider));
        }
        public void Restore(string host, string provider) => Excluded.Remove((host, provider));
    }

    /// <summary>Pending members by review attempt id.</summary>
    private sealed class FakeGate : IBatchGateControl
    {
        public StallGateFacts? Facts { get; set; }
        public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        public bool CloseRequested { get; private set; }

        public StallGateFacts? Read(string project, DateTime nowUtc) => Facts;

        public IReadOnlyList<string> Drop(string project, IReadOnlyList<string> reviewAttemptIds, string reason)
            => reviewAttemptIds.Where(Pending.Remove).ToArray();

        public bool IsPending(string project, string reviewAttemptId) => Pending.Contains(reviewAttemptId);

        public bool CloseNow(string project) => CloseRequested = true;
    }

    private sealed class FakeContinuations : IContinuationControl
    {
        public readonly List<StallRequeueCandidate> Candidates = [];
        public readonly Dictionary<string, ContinuationPin> Pins = new(StringComparer.Ordinal);

        public IReadOnlyList<StallRequeueCandidate> RateLimitedRuns(IReadOnlyList<string> cards)
            => Candidates.Where(item => cards.Contains(item.Card)).ToArray();

        public ContinuationPin? PinLastDelivery(string card, string interventionId)
        {
            var candidate = Candidates.Single(item => item.Card == card);
            return Pins[card] = new ContinuationPin(card, candidate.LastDeliveryRef!, candidate.LastDeliverySha!,
                DeliveryContinuationControl.Reason);
        }

        public ContinuationPin? Read(string card) => Pins.GetValueOrDefault(card);
        public bool LeftReady(string card) => false;
        public bool Clear(string card, string interventionId) => Pins.Remove(card);
    }
}
