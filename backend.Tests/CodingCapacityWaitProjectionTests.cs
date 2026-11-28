using AgentStudio.Cli;
using AgentStudio.Clients;
using AgentStudio.Dispatch;
using AgentStudio.Git;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Operator;
using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CodingCapacityWaitProjectionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "capacity-wait-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 11, 18, 0, TimeSpan.Zero));

    [Fact]
    public async Task Capacity_read_explains_only_overlapping_deliveries_and_drops_released_or_replaced_receipts()
    {
        var now = _clock.GetUtcNow();
        var queue = new DeliveryIntegrationQueueBoard(_clock);
        var first = Request("AGT-4099", now.AddMinutes(-10));
        var second = Request("AGT-4173", now.AddMinutes(-9));
        var later = Request("LATER-FIRST", now.AddMinutes(-5));
        var other = Request("LATER-SECOND", now.AddMinutes(-4));
        var both = Request("LATER-BOTH", now.AddMinutes(-3));
        var nextFirst = Request("NEXT-FIRST", now.AddMinutes(-3));
        var nextSecond = Request("NEXT-SECOND", now.AddMinutes(-3));
        var missingPaths = Request("MISSING-PATHS", now.AddMinutes(-3));
        var timeout = Request("ENGINE-TIMEOUT", now.AddMinutes(-2));
        var hidden = Request("HIDDEN", now.AddMinutes(-1), "private");
        var requests = new[] { later, other, both, nextFirst, nextSecond, missingPaths, timeout };
        var cards = requests.Concat([first, second, hidden]).Select(Card).ToList();
        var paths = new Dictionary<string, string[]>
        {
            [later.JobId] = ["a.cs"], [other.JobId] = ["pipeline.md"], [both.JobId] = ["a.cs", "pipeline.md"],
            [nextFirst.JobId] = ["a.cs"], [nextSecond.JobId] = ["pipeline.md"],
            [timeout.JobId] = ["elsewhere.cs"],
        };
        var pathReads = 0;
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false,
            request => { pathReads++; return paths.GetValueOrDefault(request.JobId); }, _ => cards);
        var firstConflict = MergeIntoIntegrationResult.RequiresAgentRound(["a.cs"], "Conflict.");
        var secondConflict = MergeIntoIntegrationResult.RequiresAgentRound(["pipeline.md"], "Conflict.");
        Assert.Null(IntegrationConflictRecovery.PrepareQueued(first, first.ReviewedResultSha!, firstConflict));
        Assert.Null(IntegrationConflictRecovery.PrepareQueued(second, second.ReviewedResultSha!, secondConflict));
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<MergeIntoIntegrationResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var merged = MergeIntoIntegrationResult.Of(MergeIntoIntegrationOutcome.Merged);
        var coordinator = new RemoteDeliveryIntegrationCoordinator(request =>
        {
            if (request.JobId != timeout.JobId) return Task.FromResult(merged);
            entered.SetResult();
            return finish.Task;
        }, NullLogger<RemoteDeliveryIntegrationCoordinator>.Instance, queueBoard: queue, conflictRecovery: recovery);
        var pending = requests.Select(request => coordinator.EnqueueAsync(request)).ToArray();
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(30));
            queue.RecordOutcome("studio", timeout.JobId, "Engine request timed out after 60 seconds.");
            queue.Publish("private", [new(hidden.JobId, DeliveryIntegrationQueueBoard.Waiting, hidden.DeliveredAtUtc)]);
            var gate = new GateSlotUsageTracker(clock: _clock);
            gate.Queued("gate-run", "studio", "active-gate", default);
            gate.Acquired("gate-run", 0, 1);
            var ledger = new GateRunLedger(Path.Combine(_root, "gate-ledger"), _clock);
            ledger.Append(new("finished-gate", "per-task", "studio", [], null, null, now.AddMinutes(-3),
                null, null, null, now.AddMinutes(-1), "pass", 0, 0, [], null, 0, null, null, null));

            var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _root,
                ["WatchPaths:0:Name"] = "studio", ["WatchPaths:0:Path"] = _root,
                ["WatchPaths:1:Name"] = "private", ["WatchPaths:1:Path"] = Path.Combine(_root, "private"),
            }).Build();
            var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
                new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config));
            var index = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config, () => cards, timeProvider: _clock);
            scanner.SetIndexCache(index);
            index.GetSnapshot();
            var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, config);
            settings.SetDispatchMode("studio", "live");
            var store = new DispatchStore(config);
            var planAt = now.AddDays(-2);
            var lease = store.ApplyLeaseDecision("studio", 0, new(new(), "queue-keeper", "window", "fixture"),
                planAt, "platform")!;
            Assert.True(store.WriteLive(new(new("evaluation", "expired-plan", "digest", planAt, "fixture",
                new("studio", "lane-entry", "1", lease.Persona, [], [], [], [], [], []), false), lease.Fence, []), planAt));
            var dispatch = new DispatcherShadowService(store, null!, null!, index, settings, _clock,
                NullLogger<DispatcherShadowService>.Instance);
            Assert.Equal("expired-plan", dispatch.ReadActivePlan("studio", "live", planAt).Plan?.DecisionId);
            var quota = new QuotaService(NullLogger<QuotaService>.Instance, [], config,
                new QuotaCacheStore(config, NullLogger<QuotaCacheStore>.Instance), clock: _clock);
            var clients = new ClientIdentityStore(config, NullLogger<ClientIdentityStore>.Instance);
            clients.EnsureLoaded();
            var service = new CodingCapacityService(scanner, settings, clients, new V1ReviewExecutorRegistry(_clock),
                quota, new NoModelSelection(), new OverlapDispatchGuard(_clock, NullLogger<OverlapDispatchGuard>.Instance,
                    new OverlapPredictionLedger(Path.Combine(_root, "overlap"))),
                new CapacityIdleTracker(), new TaskListGitProjectionCache(),
                waitReader: new(queue, gates: gate, ledger: ledger, dispatch: dispatch));
            var readAt = _clock.GetUtcNow();
            CodingCapacityResponse Read(bool scoped) => service.Read(readAt.UtcDateTime,
                scoped ? project => project == "studio" : null, includeQuota: false)!;

            var before = SnapshotFiles();
            var readsBefore = pathReads;
            Assert.Equal(hidden.JobId, Assert.Single(Assert.Single(Read(false).Projects,
                row => row.Project == "private").Wait!.Deliveries).CardKey);
            var scoped = Read(true);
            Assert.Null(scoped.Fleet.Wait);
            var wait = Assert.Single(scoped.Projects).Wait!;
            Assert.Equal("live", wait.DispatchMode);
            Assert.Null(wait.ActivePlan);
            Assert.Null(wait.DispatchLease);
            Assert.False(wait.RestartAdvised);
            Assert.Equal("active-gate", wait.GateOwner?.Id);
            Assert.Equal(timeout.JobId, wait.IntegrationOwner?.Id);
            Assert.Equal(now.AddMinutes(-1), wait.Sources.GateFinishedAtUtc);
            Assert.Equal(wait.IntegrationOwner?.SinceUtc, wait.LastProgressAtUtc);
            Assert.NotNull(wait.Sources.QueuePublishedAtUtc);
            var rows = wait.Deliveries.ToDictionary(row => row.CardKey);
            Assert.Equal(7, rows.Count);
            var held = rows[later.JobId];
            Assert.Equal(("intentional-wait", "wait-for-holder", TaskStates.AutoReview, "waiting"),
                (held.Status, held.Reason, held.Lane, held.QueuePhase));
            Assert.Equal(later.ReviewedResultSha, held.Generation?.ResultSha);
            Assert.Equal((readAt - later.DeliveredAtUtc).TotalSeconds, held.QueueAgeSeconds);
            Assert.Equal(readAt.AddSeconds(30), held.NextCheckAtUtc);
            var reservation = Assert.Single(held.Reservations);
            Assert.Equal(first.JobId, reservation.CardKey);
            Assert.Equal(["a.cs"], reservation.Files);
            Assert.Equal(first.DeliveredAtUtc, reservation.SinceUtc);
            Assert.Null(reservation.ExpiresAtUtc);
            Assert.Equal(3, reservation.DependentCount);
            Assert.Equal(second.JobId, Assert.Single(rows[other.JobId].Reservations).CardKey);
            Assert.Equal(new[] { first.JobId, second.JobId }, rows[both.JobId].Reservations.Select(hold => hold.CardKey));
            Assert.Equal("unknown", rows[timeout.JobId].Status);
            Assert.Empty(rows[timeout.JobId].Reservations);
            Assert.Equal("unknown", rows[missingPaths.JobId].Status);
            Assert.Empty(rows[missingPaths.JobId].Reservations);
            Assert.Equal(readsBefore, pathReads);
            Assert.Equal(before, SnapshotFiles());

            Assert.Null(recovery.Record(first, merged));
            Assert.Null(IntegrationConflictRecovery.PrepareQueued(second with { DeliveredAtUtc = now },
                "new-delivery", secondConflict));
            var reread = Assert.Single(Read(true).Projects).Wait!;
            Assert.Equal(wait.Sources.QueuePublishedAtUtc, reread.Sources.QueuePublishedAtUtc);
            Assert.All(reread.Deliveries, row => Assert.Empty(row.Reservations));
            Assert.False(reread.RestartAdvised);
        }
        finally
        {
            recovery.Record(first, merged);
            recovery.Record(second, merged);
            finish.TrySetResult(merged);
            await Task.WhenAll(pending).WaitAsync(TimeSpan.FromSeconds(30));
        }
    }

    [Fact]
    public void An_earlier_delivery_or_a_different_target_does_not_observe_a_hold()
    {
        var now = _clock.GetUtcNow();
        var holder = Request("HOLDER", now.AddMinutes(-2));
        var earlier = Request("EARLIER", now.AddMinutes(-3));
        var otherTarget = Request("OTHER-TARGET", now) with { IntegrationBranch = "other" };
        var recovery = new IntegrationConflictRecovery(_ => "tip", (_, _, _) => false, _ => ["shared.cs"], _ => [Card(holder)]);
        Assert.Null(IntegrationConflictRecovery.PrepareQueued(holder, holder.ReviewedResultSha!,
            MergeIntoIntegrationResult.RequiresAgentRound(["shared.cs"], "Conflict.")));

        var eligible = recovery.Eligible([earlier, otherTarget], (_, _) => throw new InvalidOperationException("Unexpected hold."));

        Assert.Equal(new[] { earlier, otherTarget }, eligible);
    }

    [Fact]
    public void A_known_replacement_confirms_loss_and_missing_authority_stays_unknown()
    {
        var now = _clock.GetUtcNow();
        var authority = new AttemptAuthorityService(NullLogger<AttemptAuthorityService>.Instance,
            utcNow: () => _clock.GetUtcNow().UtcDateTime);
        var card = Card(Request("AGT-1", now));
        var run = authority.AcquireRun(card.Id, "studio", null, "runner", "host", 60, "create").RunAttempt!;
        authority.SettleRun(new()
        {
            Write = new(run.AttemptId, run.LastFence, run.AuthorityEpoch, "complete"),
            Outcome = "done", ResultSha = "sha-a",
        });
        var review = authority.CreateReviewAttempt(new(card.Id, "studio", "sha-a", run.AttemptId,
            "requirements", "policy", [], "review")).ReviewAttempt!;
        var queue = new DeliveryIntegrationQueueBoard(_clock);
        var row = new DeliveryIntegrationQueueRow(card.Id, DeliveryIntegrationQueueBoard.Waiting, now)
            { ReviewAttemptId = review.AttemptId, ResultSha = review.Subject.ExpectedResultSha };
        queue.Publish("studio", [row]);
        var reader = new CodingCapacityWaitReader(queue, authority: authority);
        Assert.Equal("unknown", Read().Status);
        Assert.NotNull(authority.AcquireRun(card.Id, "studio", run.AttemptId, "runner", "host", 60, "replace").RunAttempt);
        Assert.Equal("confirmed-lost-ownership", Read().Status);
        queue.Publish("studio", [row with { ReviewAttemptId = "absent" }]);
        Assert.Equal("unknown", Read().Status);

        CapacityDeliveryWait Read()
        {
            var wait = reader.Read("studio", new(), [card], _clock.GetUtcNow());
            Assert.False(wait.RestartAdvised);
            return Assert.Single(wait.Deliveries);
        }
    }

    [Fact]
    public void Large_queues_return_a_bounded_project_view()
    {
        var now = _clock.GetUtcNow();
        var queue = new DeliveryIntegrationQueueBoard(_clock);
        var cards = Enumerable.Range(0, CodingCapacityWaitReader.MaxDeliveries + 1)
            .Select(i => new TaskInfo { Id = $"card-{i}", ProjectName = "studio" }).ToArray();
        queue.Publish("studio", cards.Select(card => new DeliveryIntegrationQueueRow(card.Id, "waiting", now)).ToArray());
        var wait = new CodingCapacityWaitReader(queue).Read("studio", new(), cards, now);
        Assert.True(wait.Truncated);
        Assert.Equal(CodingCapacityWaitReader.MaxDeliveries, wait.Deliveries.Count);
        Assert.False(wait.RestartAdvised);
    }

    private RemoteDeliveryIntegrationRequest Request(string id, DateTimeOffset at, string project = "studio")
    {
        var folder = Directory.CreateDirectory(Path.Combine(_root, project, id)).FullName;
        return new(project, id, folder, _root, "develop", IntegrationStrategies.DirectMerge,
            PipelineTypes.Task, at, ReviewedResultSha: "delivery-" + id);
    }

    private static TaskInfo Card(RemoteDeliveryIntegrationRequest request)
        => new() { Id = request.JobId, Key = request.JobId, ProjectName = request.Project,
            State = TaskStates.AutoReview, FolderPath = request.JobFolderPath, WatchPath = request.WatchPath! };

    private (string Path, string Content, DateTime Written)[] SnapshotFiles()
        => Directory.GetFiles(_root, "*", SearchOption.AllDirectories).Order()
            .Select(path => (path, File.ReadAllText(path), File.GetLastWriteTimeUtc(path))).ToArray();

    private sealed class NoModelSelection : IModelSelectionService
    {
        public ModelSelectionDecision Select(ModelSelectionCall call) => throw new InvalidOperationException("Unexpected model selection.");
        public bool IsOperatorSwitchActive(string? cliType) => throw new InvalidOperationException("Unexpected model selection.");
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
