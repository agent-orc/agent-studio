using System.Text.Json;
using AgentStudio.Operator;
using AgentStudio.Shared;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public abstract partial class BatchGatePilotServiceTests
{
    // The authority's supported memory store keeps thousands of simulated
    // heartbeats from measuring filesystem throughput. Task and Git stores
    // still use the existing fixture's isolated temporary workspace.
    private protected WebApplicationFactory<Program> BuildScenarioFactory()
        => BuildFactory(testServices: services =>
        {
            services.RemoveAll<AttemptAuthorityService>();
            services.AddSingleton(new AttemptAuthorityService(
                NullLogger<AttemptAuthorityService>.Instance, () => _clock.UtcDateTime));
            services.RemoveAll<PostMergeRunStore>();
            services.AddSingleton(new PostMergeRunStore(Path.Combine(_workspace, "post-merge")));
            services.RemoveAll<AgentStudio.Personas.LineGuardianStore>();
            services.AddSingleton(new AgentStudio.Personas.LineGuardianStore(Path.Combine(_workspace, "line-guardian")));
            services.RemoveAll<OverlapPredictionLedger>();
            services.AddSingleton(new OverlapPredictionLedger(Path.Combine(_workspace, "overlap")));
        });

    // Extend the existing service harness. The fake host owns polling and lease
    // renewal; eligibility, scope admission, fencing and lane writes are real.
    // No CLI, network host, background timer or elapsed-time sleep is needed.
    private protected ScenarioHost CreateScenarioHost(WebApplicationFactory<Program> factory, int slots = 8)
        => new(factory.Services, () => _clock, slots);

    // Generated cards use canonical storage. Move an unqueued delivery out of
    // the older lane-folder fixture before combining it with generated cards.
    private protected void UseCanonicalScenarioStorage(WebApplicationFactory<Program> factory, string key)
    {
        var card = Card(factory, key);
        TaskStorageLayout.TryParseKeyNumber(key, out var number);
        var folder = TaskStorageLayout.JobDir(_watchPath, number, key);
        Directory.CreateDirectory(Path.GetDirectoryName(folder)!);
        Directory.Move(card.FolderPath, folder);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        scanner.PublishCoreFromFolder(folder, _watchPath, ProjectName, card.State);
    }

    private protected void AdvanceScenarioHour(ScenarioHost host)
    {
        for (var tick = 0; tick < 12; tick++)
        {
            _clock = _clock.AddMinutes(5);
            host.Heartbeat();
        }
    }

    private protected void SeedReady(WebApplicationFactory<Program> factory, string key, string brief,
        string[]? dependsOn = null, string state = TaskStates.Ready)
    {
        var folder = Path.Combine(_watchPath, state, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, key, title = key, state, order = 1, agent = "codex", cliType = "codex",
            model = "gpt-6-sol", thinkingLevel = "medium", kind = TaskKinds.Task,
            enteredLaneAt = _clock.UtcDateTime,
            references = new { dependsOn = (dependsOn ?? []).Select(value => new { key = value }).ToArray() },
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), brief);
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
    }

    private protected sealed class ScenarioHost
    {
        private const string Runner = "scenario-runner";
        private readonly IServiceProvider _services;
        private readonly Func<DateTimeOffset> _now;
        private readonly int _slots;
        private readonly RunLeaseService _leases;
        private readonly CapacityIdleTracker _idle = new();
        private readonly Dictionary<string, RunLeaseInfoDto> _active = new();
        private readonly List<string> _started = [];
        public Dictionary<string, string> Steers { get; } = new();
        public IReadOnlyList<string> Started => _started;
        public IReadOnlyCollection<string> Active => _active.Keys;
        private TaskScannerService Scanner => _services.GetRequiredService<TaskScannerService>();
        private ProjectSettingsService Settings => _services.GetRequiredService<ProjectSettingsService>();
        private OverlapDispatchGuard Dispatch => _services.GetRequiredService<OverlapDispatchGuard>();
        private TaskInfo[] Cards => Scanner.ScanAllJobs().ToArray();

        public ScenarioHost(IServiceProvider services, Func<DateTimeOffset> now, int slots)
        {
            _services = services;
            _now = now;
            _slots = slots;
            _leases = new RunLeaseService(NullLogger<RunLeaseService>.Instance,
                services.GetRequiredService<AttemptAuthorityService>(), () => now().UtcDateTime);
            Settings.SetExecutionSettings(ProjectName, PickupModes.Auto, Runner, true);
            Settings.SetMaxParallelism(ProjectName, slots);
            Settings.SetDispatchOverlapWindow(ProjectName, OverlapWindows.InFlight, 30);
        }

        public void Heartbeat()
        {
            foreach (var lease in _active.Values.ToArray())
            {
                var renewed = _leases.Renew(new(lease.TaskKey, lease.LeaseId, lease.FencingToken,
                    Runner, 600, lease.AttemptId, lease.AuthorityEpoch,
                    "heartbeat-" + lease.AttemptId + "-" + _now().Ticks));
                Assert.True(renewed.Granted, renewed.Message);
                _active[lease.TaskKey] = renewed.Lease!;
            }
        }

        public void AdoptRunning(TaskInfo card) => Acquire(card);

        private RunLeaseInfoDto Acquire(TaskInfo card)
        {
            var key = card.Key ?? card.TaskKey;
            var acquired = _leases.TryAcquire(new(key, Runner, Runner, "fake-host", 1,
                "scripted-agent", 600, RepositoryId, IdempotencyKey: "claim-" + key));
            Assert.True(acquired.Granted, acquired.Message);
            _active.Add(key, acquired.Lease!);
            _started.Add(card.Key ?? card.Id);
            Dispatch.Claimed(card, acquired.Lease!.AttemptId);
            return acquired.Lease!;
        }

        public async Task PollAsync()
        {
            var cards = Cards;
            var references = TaskReferenceIndex.Build(cards);
            var integration = _services.GetRequiredService<TaskIntegrationStatusService>().BuildLookup(cards);
            var candidates = cards.Where(card => RemoteDispatchEligibility.IsClaimableReady(card,
                Settings.Get(card.ProjectName), Runner, Runner, false, references, integration)).ToArray();
            var admitted = false;
            var scopeRejected = false;
            foreach (var card in Dispatch.Order(candidates, Settings.Get, path: "remote-claim"))
            {
                if (_active.Count >= _slots) break;
                var set = OverlapComparisonSet.ForProject(Cards, card.ProjectName,
                    OverlapWindows.InFlight, TaskScopePrediction.Predict, integration, "develop");
                var admission = Dispatch.Decide(card, set.Running, set.InFlight,
                    _slots, OverlapWindows.InFlight, "remote-claim", "develop");
                if (!admission.Admitted) { scopeRejected = true; continue; }
                var lease = Acquire(card);
                if (card.PendingIntent is not null)
                {
                    var intent = _services.GetRequiredService<TaskMutationService>()
                        .ReadAndStashPendingIntent(card.FolderPath);
                    Assert.NotNull(intent);
                    Steers[card.Key ?? card.Id] = intent.Prompt;
                }
                var moved = await _services.GetRequiredService<TaskTransitionService>().MoveAsync(
                    card.Id, TaskStates.Progress, card.WatchPath, cause: "runner-claim",
                    authorityWrite: new AttemptWriteReference(lease.AttemptId!, lease.FencingToken,
                        lease.AuthorityEpoch, "lane-claim-" + lease.AttemptId),
                    suppressProductExecution: true, transitionCause: LaneChangeCauses.Claimed);
                Assert.Equal(MoveJobStatus.Success, moved.Status);
                admitted = true;
            }
            Dispatch.FinishSweep(ProjectName, scopeRejected && !admitted && _active.Count < _slots);
        }

        // The fake host supplies slots and quota. Production projections consume
        // these observations just as they consume host telemetry in the live fleet.
        public IReadOnlyList<OperatorAttentionItem> Attention()
        {
            var cards = Cards;
            var references = TaskReferenceIndex.Build(cards);
            var integration = _services.GetRequiredService<TaskIntegrationStatusService>().BuildLookup(cards);
            var fact = CodingCapacityProjection.Project(ProjectName, cards, references, _slots, _active.Count,
                card => Dispatch.Presentation(card) is { } refusal
                    ? [new CapacityBlock("dispatch-rule", refusal.ToString()!)] : [],
                card => references.EvaluateWaitsOn(card, integration, "develop"));
            var idle = _idle.Observe(fact,
                [new("codex", null, "weekly", 10, 70, 80, _now().AddDays(1).UtcDateTime, "ready")],
                _now().UtcDateTime);
            var facts = Dispatch.Facts(cards, Settings.Get, _now().UtcDateTime)
                .Concat(idle is null ? [] : new[] { idle });
            return OperatorAttention.Project(facts, OperatorDecisionReader.Everything).Items;
        }
    }
}
