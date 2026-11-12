using System.Text.Json;
using AgentStudio.Git;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.Time.Testing;
using Microsoft.Extensions.Logging;
using Xunit;

namespace AgentStudio.Tests;

public sealed class BatchGateStoreTests : IDisposable
{
    [Fact]
    public void State_keeps_excluded_overdue_member_out_of_proposals_and_uses_each_deadline()
    {
        _store.Enqueue(new BatchGatePendingRecord("review-a", Subject("task-a", 0),
            _root, _root, _root, "direct-merge", "task", Now));
        _store.Enqueue(new BatchGatePendingRecord("review-b", Subject("task-b", 25),
            _root, _root, _root, "direct-merge", "task", Now));
        _store.AppendPlanDecision("project", new BatchPlanDecision("wait", "waiting", Now.AddMinutes(30),
            [], 2, [], ExcludedMembers: [new BatchPlanExcludedMember("task-a", "MissingResultSha")]));
        var settings = new BatchGateFormationOptions(Enabled: true, DeadlineMinutes: 30);

        JsonElement State(DateTimeOffset at) => JsonSerializer.SerializeToElement(
            BatchGateStateSnapshotReader.Read(_store, "project", settings, now: at),
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).GetProperty("plan");

        var early = State(Now.AddMinutes(40));
        Assert.Empty(early.GetProperty("proposedMembers").EnumerateArray());
        Assert.Contains("task-a: excluded by MissingResultSha", early.GetProperty("reason").GetString());
        var later = State(Now.AddMinutes(60));
        Assert.Equal("task-b", Assert.Single(later.GetProperty("proposedMembers").EnumerateArray()).GetString());
    }

    [Fact]
    public void State_skips_a_malformed_manifest_without_losing_the_project()
    {
        var broken = Path.Combine(_root, "malformed-folder");
        Directory.CreateDirectory(broken);
        File.WriteAllText(Path.Combine(broken, "manifest.json"), "{broken");
        var incomplete = Path.Combine(_root, "incomplete-folder");
        Directory.CreateDirectory(incomplete);
        File.WriteAllText(Path.Combine(incomplete, "manifest.json"), "{}");

        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.Empty(state.GetProperty("batches").EnumerateArray());
        Assert.Empty(_store.ListRecoverableManifests());
    }

    [Fact]
    public void State_history_preserves_recorded_transitions_in_order()
    {
        var manifest = Close();
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Assembling, Next, 1, Now.AddMinutes(-1), "assembling candidate"));
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Assembled, Next, 1, Now, "candidate assembled"));
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Abandoned, Next, 1, Now.AddMinutes(1), "interrupted"));
        var history = _store.StateHistory(manifest.BatchId);
        Assert.Equal(BatchPhase.Assembled, history[^2].Phase);
        Assert.Equal(BatchPhase.Abandoned, history[^1].Phase);
        var snapshot = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var batch = Assert.Single(snapshot.GetProperty("batches").EnumerateArray());
        Assert.Equal("abandoned", batch.GetProperty("phaseHistory").EnumerateArray().Last()
            .GetProperty("phase").GetString());
    }
    [Fact]
    public void State_reports_capacity_and_current_slot_use_together()
    {
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true),
            gateSlots: new GateSlotUsage(2, 4),
            gateCapacity: new GateCapacity(4, 6, 24, false)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(2, state.GetProperty("gateSlots").GetProperty("busy").GetInt32());
        Assert.Equal(4, state.GetProperty("gateCapacity").GetProperty("slots").GetInt32());
        Assert.Equal(6, state.GetProperty("gateCapacity").GetProperty("processorsPerGate").GetInt32());
    }

    [Fact]
    public void State_exposes_batch_phase_time_for_interrupted_run_duration()
    {
        var manifest = Close();
        var phase = _store.LatestState(manifest.BatchId);
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var batch = Assert.Single(state.GetProperty("batches").EnumerateArray());
        Assert.Equal(phase.RecordedAtUtc, batch.GetProperty("phaseAtUtc").GetDateTimeOffset());
    }

    [Fact]
    public void State_names_review_fix_wait_before_batch_admission()
    {
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true),
            reviewFixWaits: [new BatchGateReviewFixWait("card-7", "review-7", 1, 1,
                ["code-quality"])]), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        var wait = Assert.Single(state.GetProperty("reviewFixWaits").EnumerateArray());
        Assert.Equal("card-7", wait.GetProperty("taskKey").GetString());
        var member = state.GetProperty("members").GetProperty("card-7");
        Assert.Equal("review-fix", member.GetProperty("status").GetString());
        Assert.Contains("before batch admission", member.GetProperty("reasonDetail").GetString());
    }

    [Fact]
    public void Paused_batch_handoff_exposes_ordered_per_task_gates_and_running_host()
    {
        var manifest = Close(4);
        _store.AppendState(new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Abandoned, null, 1, Now.AddMinutes(21),
            "paused-to-per-task-gate: flaky quarantine requires a fresh gate verdict"));
        for (var index = 0; index < 4; index++)
        {
            var key = $"task-{index}";
            var folder = Path.Combine(_root, key);
            var subject = Subject(key, index);
            _store.Enqueue(new BatchGatePendingRecord($"review-{index}", subject,
                _root, _root, folder, "direct-merge", "task", Now.AddMinutes(index)));
            _store.RecordFallbackCause(new BatchGateFallbackCause("project", key,
                $"review-{index}", subject.RunAttempt, PerTaskFallbackRules.BatchPaused,
                $"batch {manifest.BatchId} paused: flaky quarantine requires a fresh gate verdict",
                manifest.BatchId, Now.AddMinutes(22)));
            if (index == 1)
                BatchGateOwnershipStore.Write(folder, new BatchGateOwnership($"review-{index}", subject,
                    FallbackGateActive: true, FallbackGateStartedAtUtc: Now.AddMinutes(23),
                    FallbackGateHost: "gate-01"));
        }
        var unrelated = Subject("other-task", 4);
        _store.Enqueue(new BatchGatePendingRecord("other-review", unrelated,
            _root, _root, Path.Combine(_root, "other-task"), "direct-merge", "task", Now.AddMinutes(4)));
        _store.RecordFallbackCause(new BatchGateFallbackCause("project", "other-task",
            "other-review", unrelated.RunAttempt, PerTaskFallbackRules.BatchPaused,
            "another batch paused", "another-batch", Now.AddMinutes(22)));
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true),
            activePerTaskFlights: new HashSet<string> { "review-1" }),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var gates = state.GetProperty("perTaskGates").EnumerateArray().ToArray();
        Assert.Equal(5, gates.Length);
        Assert.Equal(2, gates[1].GetProperty("position").GetInt32());
        Assert.Equal(4, gates[1].GetProperty("total").GetInt32());
        Assert.Equal(1, gates[4].GetProperty("position").GetInt32());
        Assert.Equal(1, gates[4].GetProperty("total").GetInt32());
        Assert.True(gates[1].GetProperty("running").GetBoolean());
        Assert.Equal("gate-01", gates[1].GetProperty("host").GetString());
        Assert.Contains("flaky quarantine", gates[1].GetProperty("reason").GetString());
        Assert.Equal(PerTaskFallbackRules.BatchPaused,
            state.GetProperty("members").GetProperty("task-1").GetProperty("fallbackRule").GetString());
    }

    [Fact]
    public void Base_verdict_is_reused_by_exact_sha_and_profile()
    {
        var verdict = new BatchGateBaseVerdict("project", Base, "profile-a",
            "fail", "fingerprint", "base red", "evidence.json",
            Now);
        _store.RecordBaseVerdict(verdict);

        Assert.Equal(verdict, new BatchGateStore(_root)
            .ReadBaseVerdict("project", Base, "profile-a"));
        Assert.Null(_store.ReadBaseVerdict("project", Base, "profile-b"));
    }

    [Fact]
    public void Idle_base_verdict_replaces_provisional_red_once()
    {
        var provisional = new BatchGateBaseVerdict("project", Base, "profile-a",
            "fail", "old", "contended", "first.json", Now, Provisional: true);
        var final = provisional with { Outcome = "pass", FailureFingerprint = null,
            Reason = "idle", EvidencePath = "second.json", Provisional = false };
        _store.RecordBaseVerdict(provisional);
        _store.RecordBaseVerdict(final);
        _store.RecordBaseVerdict(provisional);
        Assert.Equal(final, new BatchGateStore(_root).ReadBaseVerdict("project", Base, "profile-a"));
    }

    [Fact]
    public void Legacy_contended_base_can_be_marked_for_recheck()
    {
        var legacy = new BatchGateBaseVerdict("project", Base, "profile-a",
            "fail", "old", "red", "evidence.json", Now);
        _store.RecordBaseVerdict(legacy);
        _store.MarkBaseVerdictProvisional(legacy);
        Assert.True(_store.ReadBaseVerdict("project", Base, "profile-a")!.Provisional);
    }

    [Fact]
    public void First_candidate_and_isolation_cost_survive_store_restart()
    {
        var manifest = Close();
        _store.RecordFirstCandidate(new BatchFirstCandidateFact(manifest.BatchId,
            "project", Now, manifest.Members.Count, .42, ["backend"],
            "disjoint", true, "deterministic-suite-red"));
        _store.RecordIsolationCost(new BatchIsolationCostFact(manifest.BatchId, 3, 24.5, Now));

        var row = Assert.Single(new BatchGateStore(_root).ListPlanObservations("project"));
        Assert.Equal(manifest.BatchId, row.BatchId);
        Assert.Equal(4, row.Size);
        Assert.True(row.Red);
        Assert.Equal(3, row.IsolationRuns);
        Assert.Equal(24.5, row.IsolationMinutes);
        Assert.Empty(_store.ListPlanObservations("other"));
    }

    [Fact]
    public void First_candidate_recorder_skips_successor_manifest()
    {
        var original = Close();
        var successor = original with { BatchId = "successor", ParentBatchId = original.BatchId };
        _store.CloseManifest(successor);
        var verdict = new BatchGateRunVerdict("run", successor.BatchId,
            successor.MembershipDigest, Next, "profile", "fail",
            "deterministic-suite-red", "evidence", Now);

        BatchGatePilotService.RecordFirstCandidateOutcome(_store, successor.BatchId,
            verdict, new RecordingLogger());

        Assert.Empty(_store.ListPlanObservations("project"));
        Assert.Throws<InvalidDataException>(() => _store.RecordFirstCandidate(
            new BatchFirstCandidateFact(successor.BatchId, "project", Now,
                successor.Members.Count, .42, ["backend"], "disjoint", true,
                "deterministic-suite-red")));
    }

    [Fact]
    public void Invalid_first_candidate_fact_is_logged_without_throwing()
    {
        var original = Close();
        var malformed = original with
        {
            BatchId = "invalid-risk",
            PlanningCards = [new BatchPlanCard("task-0", Now, 2, ["backend/a.cs"], [])],
        };
        _store.CloseManifest(malformed);
        var verdict = new BatchGateRunVerdict("run", malformed.BatchId,
            malformed.MembershipDigest, Next, "profile", "pass", "pass", "evidence", Now);
        var logger = new RecordingLogger();

        BatchGatePilotService.RecordFirstCandidateOutcome(_store, malformed.BatchId,
            verdict, logger);

        Assert.IsType<InvalidDataException>(logger.Exception);
        Assert.Empty(_store.ListPlanObservations("project"));
    }

    private sealed class RecordingLogger : ILogger
    {
        public Exception? Exception { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state,
            Exception? exception, Func<TState, Exception?, string> formatter)
            => Exception = exception;
    }

    [Fact]
    public async Task Readers_never_observe_a_partially_written_state()
    {
        var manifest = Close();
        var writer = Task.Run(async () =>
        {
            for (var index = 0; index < 80; index++)
            {
                _store.AppendState(new BatchGateState(manifest.BatchId,
                    manifest.MembershipDigest, BatchPhase.Assembling, null,
                    1, Now.AddSeconds(index)));
                await Task.Yield();
            }
        });
        var reader = Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                Assert.Equal(manifest.BatchId, _store.LatestState(manifest.BatchId).BatchId);
                await Task.Yield();
            }
        });

        await Task.WhenAll(writer, reader);
    }
    private const string Base = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";
    private const string Next = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private static readonly DateTimeOffset Now = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);
    private static readonly BatchGateScope Scope = new("project", "repo", "develop", "full", "digest", "v1");
    private readonly string _root = Path.Combine(Path.GetTempPath(), "batch-gate-test-" + Guid.NewGuid().ToString("N"));
    private readonly BatchGateStore _store;

    public BatchGateStoreTests() => _store = new BatchGateStore(_root);
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }

    private static BatchGateSubject Subject(string key, int order) => new(
        key, "project", "repo", "develop", "full", "digest", "v1",
        "refs/heads/agent-studio/results/one", Base, "run-1", 1, 1, Base,
        true, true, true, true, false, null, true, Now.AddMinutes(order), order + 1);

    private BatchGateManifest Close(int count = 4)
    {
        var members = Enumerable.Range(0, count).Select(i => Subject($"task-{i}", i)).ToArray();
        var manifest = BatchGatePolicy.Form(members, Scope, Base,
            new BatchGateFormationOptions(Enabled: true), Now.AddMinutes(20)).Manifest!;
        _store.CloseManifest(manifest);
        return manifest;
    }

    [Fact]
    public void Card_timelines_and_daily_percentiles_replay_from_durable_events_after_restart()
    {
        var manifest = Close(2);
        var member = manifest.Members[0];
        _store.Enqueue(new BatchGatePendingRecord("review-0", member, "/repo", null,
            "/job", "linear", "default", Now.AddMinutes(1)));
        var ledger = new GateRunLedger(Path.Combine(_root, "ledger"), new FakeTimeProvider(Now.AddMinutes(45)));
        ledger.Append(new GateRunLedgerRow("check-0", "batch-pre-check", "project", [member.TaskKey],
            Base, "digest", Now.AddMinutes(2), Now.AddMinutes(3), null,
            Now.AddMinutes(4), Now.AddMinutes(8), "Ok", 240_000, 60_000, [], null, 0,
            null, null, "pending:review-0"));
        _store.RecordPreCheck(new BatchGatePreCheckVerdict("project", member.TaskKey, "review-0",
            Base, Base, "digest", "green", "pass", [], Now.AddMinutes(4), Now.AddMinutes(8)));
        var evidence = Path.Combine(_root, "timeline-evidence.json");
        File.WriteAllText(evidence, "{}");
        var run = new BatchGateRunRecord("timeline-run", manifest.BatchId,
            manifest.MembershipDigest, Base, Next, "full", "digest", "host", 1,
            ["dotnet test"], Now.AddMinutes(30), evidence);
        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Assembled, Next, 1, Now.AddMinutes(29)));
        _store.RecordRun(run);
        _store.RecordVerdict(new BatchGateRunVerdict(run.BatchRunId, manifest.BatchId,
            manifest.MembershipDigest, Next, "digest", "pass", "passed", evidence,
            Now.AddMinutes(40)));
        _store.RecordPublication(new BatchGatePublication(manifest.BatchId,
            manifest.MembershipDigest, run.BatchRunId, Base, Next, Next, 1, 1,
            Now.AddMinutes(45)));
        _store.RecordReplay(new BatchGateReplayRecord(manifest.BatchId,
            manifest.MembershipDigest, member.TaskKey, Base, Base, Next,
            [new RebasedCommitReplacement(Base, Next)], [], "admitted", Now.AddMinutes(25)));
        ledger.Append(new GateRunLedgerRow("diagnostic-0", "clean-diagnostic-rerun", "project",
            [member.TaskKey], Base, "digest", Now.AddMinutes(35), Now.AddMinutes(35), null,
            Now.AddMinutes(35), Now.AddMinutes(37), "Ok", 120_000, 0, [], "red-candidate",
            120_000, null, null, "red-candidate", manifest.BatchId));
        var timeline = BatchMemberTimelineProjection.Build(_store, member, manifest,
            BatchMemberTimelineProjection.Rows(ledger, "project"), _store.ListPreChecks("project"), []);
        _store.RecordMember(new BatchGateMemberRecord(member.TaskKey, member.RunAttempt,
            member.DeliveryEpoch, Base, [Next], manifest.BatchId, manifest.MembershipDigest,
            Base, Next, "digest", run.BatchRunId, "pass", evidence, Now.AddMinutes(45),
            timeline with { Outcome = "published", PublishedAtUtc = Now.AddMinutes(45),
                TerminalAtUtc = Now.AddMinutes(45) }));
        var fallback = manifest.Members[1];
        _store.RecordFallbackCause(new BatchGateFallbackCause("project", fallback.TaskKey,
            "review-1", fallback.RunAttempt, "deadline", "per-task", manifest.BatchId,
            Now.AddMinutes(26)));

        var restarted = new BatchGateStore(_root);
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(restarted,
            "project", new BatchGateFormationOptions(Enabled: true), now: Now.AddMinutes(46),
            ledger: new GateRunLedger(Path.Combine(_root, "ledger"))),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var card = state.GetProperty("members").GetProperty(member.TaskKey).GetProperty("timeline");
        Assert.Equal(Now.AddMinutes(2), card.GetProperty("preCheckQueuedAtUtc").GetDateTimeOffset());
        Assert.Equal(Now.AddMinutes(4), card.GetProperty("preCheckStartedAtUtc").GetDateTimeOffset());
        Assert.Equal(Now.AddMinutes(8), card.GetProperty("preCheckFinishedAtUtc").GetDateTimeOffset());
        Assert.Equal(manifest.ClosedAtUtc, card.GetProperty("joinedBatchAtUtc").GetDateTimeOffset());
        Assert.Equal(Now.AddMinutes(45), card.GetProperty("publishedAtUtc").GetDateTimeOffset());
        Assert.True(card.GetProperty("diagnosticRerun").GetBoolean());
        Assert.Equal("fallback", state.GetProperty("members").GetProperty(fallback.TaskKey)
            .GetProperty("timeline").GetProperty("outcome").GetString());
        Assert.Equal("fallback", state.GetProperty("members").GetProperty(fallback.TaskKey)
            .GetProperty("status").GetString());
        var daily = state.GetProperty("dailySummary");
        Assert.Equal(8, daily.GetProperty("phaseMinutes").GetProperty("reviewToPreCheck").GetProperty("p50").GetDouble());
        Assert.Equal(1, daily.GetProperty("phaseMinutes").GetProperty("reviewToPreCheck").GetProperty("cards").GetInt32());
        Assert.Equal(22, daily.GetProperty("phaseMinutes").GetProperty("formationWait").GetProperty("p90").GetDouble());
        Assert.Equal(10, daily.GetProperty("phaseMinutes").GetProperty("batchRun").GetProperty("p50").GetDouble());
        Assert.Equal(5, daily.GetProperty("phaseMinutes").GetProperty("publish").GetProperty("p90").GetDouble());
        Assert.Equal(1, daily.GetProperty("diagnosticRerunShare").GetDouble());
        Assert.Equal(1, daily.GetProperty("preCheckYield").GetProperty("runs").GetInt32());
        Assert.Equal(0, daily.GetProperty("preCheckYield").GetProperty("cardsKeptOut").GetInt32());
        Assert.Equal(Now.AddMinutes(2), restarted.TryReadMember(manifest.BatchId,
            member.TaskKey, run.BatchRunId)?.Timeline?.PreCheckQueuedAtUtc);
    }

    [Fact]
    public void Red_pre_check_yield_requires_a_recorded_red_handoff()
    {
        var member = Subject("red-card", 0);
        _store.Enqueue(new BatchGatePendingRecord("red-review", member, "/repo", null,
            "/job", "linear", "default", Now));
        _store.RecordPreCheck(new BatchGatePreCheckVerdict("project", member.TaskKey,
            "red-review", Base, Base, "digest", "red", "failed", [], Now,
            Now.AddMinutes(2)));
        var ledger = new GateRunLedger(Path.Combine(_root, "ledger"),
            new FakeTimeProvider(Now.AddMinutes(3)));
        ledger.Append(new GateRunLedgerRow("red-check", "batch-pre-check", "project",
            [member.TaskKey], Base, "digest", Now, Now, null, Now,
            Now.AddMinutes(2), "Fail", 120_000, 0, [], null, 0,
            null, null, "pending:red-review"));
        var day = DateOnly.FromDateTime(Now.UtcDateTime);
        Assert.Equal(0, ledger.Summary(new BatchGateStore(_root), "project", day)
            .PreCheckYield!.CardsKeptOut);
        _store.ResolvePending("red-review", "pre-check-red");
        var summary = new GateRunLedger(Path.Combine(_root, "ledger"))
            .Summary(new BatchGateStore(_root), "project", day);
        Assert.Equal(1, summary.PreCheckYield!.Runs);
        Assert.Equal(1, summary.PreCheckYield.Red);
        Assert.Equal(1, summary.PreCheckYield.CardsKeptOut);
        Assert.Equal(2, summary.PreCheckYield.CostMinutes);
    }

    [Fact]
    public void Delivery_index_isolates_corruption_and_reuses_closed_manifests()
    {
        var manifest = Close();
        var corrupt = _store.BatchDirectory("corrupt");
        Directory.CreateDirectory(corrupt);
        File.WriteAllText(Path.Combine(corrupt, "manifest.json"), "{broken");
        var errors = new List<Exception>();
        var reader = new BatchGateStore(_root);
        Assert.Equal(manifest.BatchId,
            Assert.Single(reader.FindDeliveryManifests("project", "develop", [Base], errors.Add)).BatchId);
        Assert.Single(errors);

        // Closed manifests were indexed once. Later reads only use the index,
        // while run verdicts and replays are deliberately not cached.
        File.WriteAllText(Path.Combine(_store.BatchDirectory(manifest.BatchId), "manifest.json"), "{broken");
        Assert.Single(reader.FindDeliveryManifests("project", "develop", [Base], errors.Add));
        Assert.Single(errors);
        Assert.Empty(reader.FindDeliveryManifests("other-project", "develop", [Base], errors.Add));
        Assert.Empty(reader.FindDeliveryManifests("project", "main", [Base], errors.Add));
        Assert.Empty(reader.FindDeliveryManifests("project", "develop", [Next], errors.Add));
    }

    [Fact]
    public void Closing_a_manifest_updates_an_initialized_delivery_index()
    {
        var errors = new List<Exception>();
        Assert.Empty(_store.FindDeliveryManifests("project", "develop", [Base], errors.Add));
        var manifest = Close();
        Assert.Equal(manifest.BatchId,
            Assert.Single(_store.FindDeliveryManifests("project", "develop", [Base], errors.Add)).BatchId);
        Assert.Empty(errors);
    }

    [Fact]
    public void Delivery_index_retries_a_manifest_that_becomes_readable()
    {
        var manifest = Close();
        var path = Path.Combine(_store.BatchDirectory(manifest.BatchId), "manifest.json");
        var original = File.ReadAllText(path);
        File.WriteAllText(path, "{broken");
        var reader = new BatchGateStore(_root);
        var errors = new List<Exception>();

        Assert.Empty(reader.FindDeliveryManifests("project", "develop", [Base], errors.Add));
        Assert.Single(errors);
        File.WriteAllText(path, original);
        Assert.Equal(manifest.BatchId,
            Assert.Single(reader.FindDeliveryManifests("project", "develop", [Base], errors.Add)).BatchId);
        Assert.Single(errors);
    }

    [Fact]
    public void PendingReviewIsDurableIdempotentAndSequenced()
    {
        var first = _store.Enqueue(new BatchGatePendingRecord(
            "review-a", Subject("task-a", 0) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        var replay = _store.Enqueue(new BatchGatePendingRecord(
            "review-a", Subject("task-a", 0) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        var second = new BatchGateStore(_root).Enqueue(new BatchGatePendingRecord(
            "review-b", Subject("task-b", 1) with { EnqueueSequence = 0 },
            _root, _root, _root, "direct-merge", "task", Now));
        Assert.Equal(first.Subject.EnqueueSequence, replay.Subject.EnqueueSequence);
        Assert.True(second.Subject.EnqueueSequence > first.Subject.EnqueueSequence);
        Assert.Equal(2, new BatchGateStore(_root).ListPending().Count);
        _store.ResolvePending("review-a", "published");
        Assert.Single(_store.ListPending());
    }

    [Fact]
    public void ClosedManifestCannotAbsorbMembersOrChangeDigest()
    {
        var manifest = Close();
        Assert.Equal(4, _store.ReadManifest(manifest.BatchId).Members.Count);
        Assert.Throws<IOException>(() => _store.CloseManifest(manifest with
        {
            Members = manifest.Members.Take(3).ToArray(),
            MembershipDigest = BatchGatePolicy.MembershipDigest(Base, Scope, manifest.Members.Take(3).ToArray()),
        }));
        Assert.Throws<InvalidDataException>(() => _store.AppendState(new BatchGateState(
            manifest.BatchId, "wrong", BatchPhase.Green, Next, 1, Now)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Manifest_without_state_is_unknown_and_not_recoverable(bool keepEmptyStateFolder)
    {
        var manifest = Close();
        var stateFolder = Path.Combine(_store.BatchDirectory(manifest.BatchId), "state");
        Directory.Delete(stateFolder, recursive: true);
        if (keepEmptyStateFolder) Directory.CreateDirectory(stateFolder);
        // AGT-3563: the phase record is the commit point, so "without state" means neither.
        File.Delete(Path.Combine(_store.BatchDirectory(manifest.BatchId), "phase.json"));

        Assert.Equal(BatchPhase.Unknown, _store.LatestState(manifest.BatchId).Phase);
        Assert.DoesNotContain(_store.ListRecoverableManifests(),
            item => item.BatchId == manifest.BatchId);
        Assert.Empty(_store.ListPausedManifests());
    }

    [Fact]
    public void Missing_manifest_and_state_report_unknown()
    {
        var state = _store.LatestState("missing-batch");
        Assert.Equal(BatchPhase.Unknown, state.Phase);
        Assert.Equal(string.Empty, state.MembershipDigest);
        Assert.Empty(_store.ListRecoverableManifests());
    }

    [Fact]
    public void ConflictDoesNotMoveTipAndCascadeDefersTail()
    {
        var manifest = Close(8);
        var current = Base;
        var updates = 0;
        var assembler = new BatchGateAssembler(_store,
            (member, tip) => member.TaskKey is "task-1" or "task-2" or "task-3"
                ? new BatchReplayResult(false, null, [], ["docs/conflict.md"], "conflict")
                : new BatchReplayResult(true, Next, [new RebasedCommitReplacement(Base, Next)], [], null),
            (_, next, old) =>
            {
                Assert.Equal(current, old ?? current);
                current = next;
                updates++;
                return new BatchCandidateRefResult(true, null);
            });
        var result = assembler.Assemble(manifest.BatchId, 1);
        Assert.Equal(["task-0"], result.AdmittedKeys);
        Assert.Equal(["task-1", "task-2", "task-3"], result.EjectedKeys);
        Assert.Equal(["task-4", "task-5", "task-6", "task-7"], result.DeferredKeys);
        Assert.Equal(Next, result.CandidateSha);
        Assert.Equal(2, updates); // Creation and one successful member only.
        Assert.True(result.CascadeStopped);
    }

    [Fact]
    public void UnattributableMemberIsEjectedAloneWithItsReasonAndTheBatchAssembles()
    {
        var manifest = Close();
        var clock = new FakeTimeProvider(Now);
        const string reason = "Immutable result ref does not resolve to the expected SHA.";
        var current = Base;
        var assembler = new BatchGateAssembler(_store,
            (member, tip) => member.TaskKey == "task-1"
                ? BatchReplayResult.Unattributable(reason)
                : new BatchReplayResult(true, Next, [new RebasedCommitReplacement(Base, Next)], [], null),
            (_, next, old) =>
            {
                Assert.Equal(current, old ?? current);
                current = next;
                return new BatchCandidateRefResult(true, null);
            }, clock);

        var result = assembler.Assemble(manifest.BatchId, 1);

        Assert.Equal(["task-0", "task-2", "task-3"], result.AdmittedKeys);
        Assert.Equal(["task-1"], result.EjectedKeys);
        Assert.Empty(result.DeferredKeys);
        Assert.False(result.CascadeStopped);
        var ejected = _store.ReadReplay(manifest.BatchId, "task-1");
        Assert.Equal("unattributable", ejected.Outcome);
        Assert.Equal(reason, ejected.Reason);
        Assert.Null(ejected.TipAfterSha);
        Assert.Equal(Now, ejected.RecordedAtUtc);
        Assert.Equal(BatchPhase.Assembled, _store.LatestState(manifest.BatchId).Phase);

        // The card names the reason; admitted members stay in the batch.
        var state = JsonSerializer.SerializeToElement(BatchGateStateSnapshotReader.Read(
            _store, "project", new BatchGateFormationOptions(Enabled: true)),
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        var card = state.GetProperty("members").GetProperty("task-1");
        Assert.Equal("fallback", card.GetProperty("status").GetString());
        Assert.Equal("unattributable", card.GetProperty("reason").GetString());
        Assert.Equal(reason, card.GetProperty("reasonDetail").GetString());
        var admitted = state.GetProperty("members").GetProperty("task-0");
        Assert.Equal("in-batch", admitted.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, admitted.GetProperty("reason").ValueKind);
    }

    [Fact]
    public void UnattributableMergeKeepsTheMergeStrategyOnItsReplayRecord()
    {
        var manifest = Close();
        var assembler = new BatchGateAssembler(_store,
            (member, _) => member.TaskKey == "task-1"
                ? BatchReplayResult.Unattributable(
                    "Batch merge did not record exactly the candidate tip and the result SHA as parents.",
                    BatchReplayRoute.Merge)
                : new BatchReplayResult(true, Next, [new RebasedCommitReplacement(Base, Next)], [], null),
            (_, _, _) => new BatchCandidateRefResult(true, null), new FakeTimeProvider(Now));

        var result = assembler.Assemble(manifest.BatchId, 1);

        Assert.Equal(["task-1"], result.EjectedKeys);
        var ejected = _store.ReadReplay(manifest.BatchId, "task-1");
        Assert.Equal("unattributable", ejected.Outcome);
        Assert.Equal(BatchReplayStrategies.Merge, ejected.Strategy);
        Assert.Equal(BatchReplayStrategies.Linear,
            _store.ReadReplay(manifest.BatchId, "task-0").Strategy);
    }

    [Fact]
    public void InfrastructureReplayFailureStillStopsAssembly()
    {
        var manifest = Close();
        var assembler = new BatchGateAssembler(_store,
            (member, _) => member.TaskKey == "task-1"
                ? BatchReplayResult.Infrastructure("Batch merge worktree could not be created.")
                : new BatchReplayResult(true, Next, [new RebasedCommitReplacement(Base, Next)], [], null),
            (_, _, _) => new BatchCandidateRefResult(true, null), new FakeTimeProvider(Now));

        var error = Assert.Throws<InvalidDataException>(() => assembler.Assemble(manifest.BatchId, 1));
        Assert.Contains("worktree", error.Message, StringComparison.Ordinal);
        Assert.Null(_store.TryReadReplay(manifest.BatchId, "task-1"));
    }

    [Fact]
    public void MergedMemberRecordsItsStrategyAndMustMapTheResultToItsMergeCommit()
    {
        var manifest = Close();
        var member = manifest.Members[0];
        Assert.Throws<InvalidDataException>(() => _store.RecordReplay(new BatchGateReplayRecord(
            manifest.BatchId, manifest.MembershipDigest, member.TaskKey, Base, Base, Next,
            [new RebasedCommitReplacement(Base, Base), new RebasedCommitReplacement(Base, Next)],
            [], "admitted", Now, BatchReplayStrategies.Merge)));
        var assembler = new BatchGateAssembler(_store,
            (subject, _) => new BatchReplayResult(true, Next,
                [new RebasedCommitReplacement(subject.ResultSha, Next)], [], null, BatchReplayRoute.Merge),
            (_, _, _) => new BatchCandidateRefResult(true, null), new FakeTimeProvider(Now));

        var result = assembler.Assemble(manifest.BatchId, 1);

        Assert.Equal(manifest.Members.Select(x => x.TaskKey), result.AdmittedKeys);
        Assert.Equal(BatchReplayStrategies.Merge, _store.ReadReplay(manifest.BatchId, member.TaskKey).Strategy);
    }

    [Fact]
    public void MissingOrMismatchedEvidenceBlocksLaneRelease()
    {
        var manifest = Close();
        var member = manifest.Members[0];
        var evidence = Path.Combine(_root, "run-evidence.json");
        File.WriteAllText(evidence, "{\"tested\":true}");
        var run = new BatchGateRunRecord("run-1", manifest.BatchId,
            manifest.MembershipDigest, Base, Next, "full", "digest", "host", 1,
            ["dotnet test"], Now, evidence);
        BatchGateStates.Reach(_store, new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Assembled, Next, 1, Now));
        _store.RecordRun(run);
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.Throws<InvalidDataException>(() => _store.RecordVerdict(
            new BatchGateRunVerdict(run.BatchRunId, manifest.BatchId,
                manifest.MembershipDigest, Next, "digest", "pass", "passed",
                "another-evidence-path", Now)));
        _store.RecordVerdict(new BatchGateRunVerdict(run.BatchRunId, manifest.BatchId,
            manifest.MembershipDigest, Next, "digest", "pass", "passed",
            evidence, Now));
        _store.RecordPublication(new BatchGatePublication(manifest.BatchId,
            manifest.MembershipDigest, run.BatchRunId, Base, Next, Next, 1, 1, Now));
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        _store.RecordReplay(new BatchGateReplayRecord(manifest.BatchId,
            manifest.MembershipDigest, member.TaskKey, Base, Base, Next,
            [new RebasedCommitReplacement(Base, Next)], [], "admitted", Now));
        Assert.Throws<InvalidDataException>(() => _store.RecordMember(new BatchGateMemberRecord(
            member.TaskKey, "wrong-attempt", 1, Base, [Next], manifest.BatchId,
            manifest.MembershipDigest, Base, Next, "digest", run.BatchRunId,
            "pass", evidence, Now)));
        _store.RecordMember(new BatchGateMemberRecord(
            member.TaskKey, member.RunAttempt, member.DeliveryEpoch, Base, [Next],
            manifest.BatchId, manifest.MembershipDigest, Base, Next, "digest",
            run.BatchRunId, "pass", evidence, Now));
        Assert.True(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.False(_store.CanRelease(member with { ResultRef = "refs/heads/agent-studio/results/other" },
            manifest.BatchId, run.BatchRunId));
        File.Delete(evidence);
        Assert.False(_store.CanRelease(member, manifest.BatchId, run.BatchRunId));
        Assert.False(_store.CanRelease(member with { RunAttempt = "new" },
            manifest.BatchId, run.BatchRunId));
    }
}
