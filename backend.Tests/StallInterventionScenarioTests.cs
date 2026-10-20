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
        _h.Admission.Hosts["agent-runner-01"] = new HostState(8, Cores: 12, Load1: 46.7, Target: 95);
        _h.Raise(Overload());

        await _h.Service.RunOnceAsync();

        var lowered = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.LowerAdmission, StallInterventionStatuses.Applied, true, 6),
            (lowered.Action, lowered.Status, lowered.EffectObserved, _h.Admission.Hosts["agent-runner-01"].Admission));
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
        _h.Admission.Hosts["agent-runner-01"] = _h.Admission.Hosts["agent-runner-01"] with
        {
            Load1 = 4.0, CalmSince = _h.Now.AddMinutes(-16),
        };
        await _h.Service.RunOnceAsync();

        Assert.Equal(8, _h.Admission.Hosts["agent-runner-01"].Admission);
        var records = _h.Store.All();
        var restore = Assert.Single(records, item => item.Action == StallInterventionActions.RestoreAdmission);
        Assert.Contains("from 6 back to 8", restore.Receipt);
        Assert.Equal(lowered.Id, restore.PreviousInterventionId);
        Assert.Equal(StallInterventionStatuses.Verified, records.Single(item => item.Id == lowered.Id).Status);
    }

    [Fact]
    public async Task Overload_that_stays_after_the_follow_up_escalates_with_the_receipt_chain()
    {
        _h.Admission.Hosts["agent-runner-01"] = new HostState(8, 12, 46.7, 95);
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
        Assert.Equal(6, _h.Admission.Hosts["agent-runner-01"].Admission);
    }

    [Fact]
    public async Task An_action_without_a_measured_effect_is_escalated_at_once()
    {
        _h.Admission.Hosts["agent-runner-01"] = new HostState(8, 12, 46.7, 95);
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
        _h.Gate.Facts = new StallGateFacts("KORI", 19, ["AGT-3097"], [], 79, PlanIdle: true, "KORI:forming", 30);
        _h.Gate.Pending.UnionWith(["AGT-3097", "AGT-3044"]);
        _h.Raise(Snapshot(gates: [new("KORI", "pending", true, 19, Now0.AddHours(-79), ["AGT-3097", "AGT-3044"], ["AGT-3097"])]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal((StallInterventionActions.ReconcileBatchGate, true, true),
            (record.Action, record.EffectObserved, _h.Gate.CloseRequested));
        Assert.DoesNotContain("AGT-3097", _h.Gate.Pending);
        Assert.Contains("AGT-3044", _h.Gate.Pending);
        Assert.Contains("I dropped AGT-3097 from the gate queue", record.Receipt);
        Assert.Contains(StallInterventionReceipts.Headline(record), _h.CardTimeline(TaskStates.Completed, "AGT-3097"));
    }

    [Fact]
    public async Task Rate_limited_run_requeues_from_its_last_delivery_and_undo_clears_the_base()
    {
        _h.Continuations.Candidates.Add(new("AGT-3176", TaskStates.Ready, true, "refs/heads/agent-results/AGT-3176/r2", "0123456789abcdef"));
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
        _h.Raise(Snapshot(deployments: [new("kori-gate", "engine", "0.9.6", "0.9.5", Now0.AddHours(-3), "ENGINE_SERVER_URL missing")]));

        await _h.Service.RunOnceAsync();

        var record = Assert.Single(_h.Store.All());
        Assert.Equal(StallInterventionActions.RetryEngineStart, record.Action);
        Assert.Equal([("kori-gate", "orchestrator-engine")], _h.Restarts.Calls);
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
        _h.Admission.Hosts["agent-runner-01"] = new HostState(8, 12, 46.7, 95);
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        var lowered = Assert.Single(_h.Store.All());

        var undo = _h.Service.Undo(lowered.Id, "robert");

        Assert.True(undo.Undone, undo.Message);
        Assert.Equal(8, _h.Admission.Hosts["agent-runner-01"].Admission);
        var undoEntry = _h.WorkspaceFeed().Last();
        Assert.Equal("Undone by robert: " + lowered.Done, undoEntry.Summary);

        _h.Clock.Advance(TimeSpan.FromMinutes(20));
        _h.Raise(Overload());
        await _h.Service.RunOnceAsync();
        Assert.Equal(8, _h.Admission.Hosts["agent-runner-01"].Admission);
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
        admission.Hosts["agent-runner-01"] = new HostState(8, 12, 46.7, 95);
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
        Assert.Equal(("engine", claim.Run.RunId, 6), (record.DecidedBy, record.OccurrenceRunId, admission.Hosts["agent-runner-01"].Admission));
    }

    public void Dispose() => _h.Dispose();

    private static readonly DateTime Now0 = DateTime.Parse("2026-10-16T19:51:00Z").ToUniversalTime();

    private static OperationalStallSnapshot Overload() => Snapshot(hosts:
        [new("agent-runner-01", Now0.AddMinutes(-20), 46.7 / 12, 95, null, null, 8, "8 builds", 95)]);

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
        public readonly FakeRoutes Routes = new();
        public readonly FakeGate Gate = new();
        public readonly FakeContinuations Continuations = new();
        public readonly WatcherCaseStore Cases = new(NullLogger<WatcherCaseStore>.Instance);
        public readonly OrchestratorLog Log = new(NullLogger<OrchestratorLog>.Instance);
        public readonly StallInterventionStore Store;
        public readonly StallInterventionService Service;
        private readonly WatcherCaseEngine _engine;
        private int _sweep;

        public Harness()
        {
            foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(ProjectPath, state));
            var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = Root,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = ProjectPath,
                ["WatchPaths:0:RootPath"] = ProjectPath,
            }).Build();
            var scanner = new TaskScannerService(configuration, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, configuration));
            var projector = new WatcherActivityProjector(Log, scanner, configuration, NullLogger<WatcherActivityProjector>.Instance);
            var receipts = new StallInterventionReceipts(Log, projector, scanner, new TimelineLog(NullLogger<TimelineLog>.Instance),
                NullLogger<StallInterventionReceipts>.Instance);
            Store = new StallInterventionStore(() => Root);
            var facts = new StallInterventionFactsBuilder(Admission, Restarts, Routes, Gate, Continuations);
            var effect = new StallInterventionEffect(Store, Cases, facts, Admission, Restarts, Routes, Gate, Continuations,
                receipts, configuration, Clock);
            Service = new StallInterventionService(Store, Cases, facts, effect,
                AiOperatorDecisionGateway.ForTestHost(configuration), configuration, Clock,
                NullLogger<StallInterventionService>.Instance);
            _engine = new WatcherCaseEngine(Cases, new WatcherSuppressionStore(NullLogger<WatcherSuppressionStore>.Instance));
        }

        public string ProjectPath => Path.Combine(Root, "projects", Project);
        public DateTime Now => Clock.GetUtcNow().UtcDateTime;

        public void Raise(OperationalStallSnapshot snapshot)
            => _engine.Ingest(Root, OperationalStallDetector.Detect(snapshot, new(), Now), $"sweep-{++_sweep}",
                new WatcherOptions(), Now);

        public void Clear() => _engine.ResolveClearedOperationalCases(Root, [], Now);

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

    private sealed record HostState(int Admission, int Cores, double Load1, double Target, DateTime? CalmSince = null);

    private sealed class FakeAdmission : IHostAdmissionControl
    {
        public readonly Dictionary<string, HostState> Hosts = new(StringComparer.Ordinal);
        public bool IgnoreWrites { get; set; }

        public HostAdmissionReading? Read(string host, double restoreFraction, DateTime nowUtc)
            => Hosts.TryGetValue(host, out var state)
                ? new HostAdmissionReading(host, host, state.Admission, state.Cores, state.Load1, null, null, state.Target,
                    state.Admission, state.Load1 / state.Cores, state.CalmSince)
                : null;

        public int? SetAdmission(string host, int maxParallelism)
        {
            if (!IgnoreWrites) Hosts[host] = Hosts[host] with { Admission = maxParallelism };
            return Hosts[host].Admission;
        }
    }

    private sealed class FakeRestarts : IServiceRestartControl
    {
        public readonly HashSet<(string Host, string Service)> Allowed = [];
        public readonly List<(string Host, string Service)> Calls = [];
        public bool Succeeds { get; set; } = true;

        public bool CanRestart(string host, string service) => Allowed.Contains((host, service));

        public Task<ServiceRestartOutcome> RestartAsync(string host, string service, string idempotencyKey,
            string reason, CancellationToken ct)
        {
            Calls.Add((host, service));
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
        public bool IsRerouted(string host, string provider) => Excluded.Contains((host, provider));
        public void Reroute(string host, string provider, string interventionId) => Excluded.Add((host, provider));
        public void Restore(string host, string provider) => Excluded.Remove((host, provider));
    }

    private sealed class FakeGate : IBatchGateControl
    {
        public StallGateFacts? Facts { get; set; }
        public readonly HashSet<string> Pending = new(StringComparer.Ordinal);
        public bool CloseRequested { get; private set; }

        public StallGateFacts? Read(string project, DateTime nowUtc) => Facts;

        public IReadOnlyList<string> Drop(string project, IReadOnlyList<string> cards, string reason)
            => cards.Where(Pending.Remove).ToArray();

        public bool IsPending(string project, string card) => Pending.Contains(card);

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
            return Pins[card] = new ContinuationPin(card, candidate.LastDeliveryRef!, candidate.LastDeliverySha!, interventionId);
        }

        public ContinuationPin? Read(string card) => Pins.GetValueOrDefault(card);
        public bool LeftReady(string card) => false;
        public bool Clear(string card, string interventionId) => Pins.Remove(card);
    }
}
