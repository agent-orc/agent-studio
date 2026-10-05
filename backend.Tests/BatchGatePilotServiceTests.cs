using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tests;

/// <summary>
/// Drives the documentation-only batch pilot through the real host wiring:
/// attempt authority, task lanes, Git publication and the review-report
/// endpoint. Only the gate runner is scripted, and the pilot's background
/// tick is removed so each test owns the tick order.
/// </summary>
[Collection(WebApplicationFactorySerialCollection.Name)]
public sealed class BatchGatePilotServiceTests : IDisposable
{
    private const string ProjectName = "batch-pilot";
    private const string RepositoryId = "batch-pilot-repo";
    private const string ReviewRunnerId = "batch-review-runner";
    private const string ReviewInstance = "batch-review-host:7001";

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly string _repo;
    private readonly string _origin;
    private readonly ScriptedGate _gate = new();
    private readonly ErrorLog _errors = new();
    private DateTimeOffset _clock = new(2026, 9, 27, 10, 0, 0, TimeSpan.Zero);

    public BatchGatePilotServiceTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-batch-pilot-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        _repo = Path.Combine(_workspace, "repo");
        _origin = Path.Combine(_workspace, "origin.git");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Directory.CreateDirectory(_repo);
        Git(_workspace, "init", "-q", "--bare", "--initial-branch=develop", _origin);
        Git(_repo, "init", "-q", "-b", "develop");
        Git(_repo, "config", "user.email", "batch-test@example.invalid");
        Git(_repo, "config", "user.name", "Batch Test");
        Directory.CreateDirectory(Path.Combine(_repo, ".agent-studio"));
        File.WriteAllText(Path.Combine(_repo, ".agent-studio", "prepare"), "#!/bin/sh\nexit 0\n");
        File.WriteAllText(Path.Combine(_repo, ".agent-studio", "project.yml"),
            "schemaVersion: 1\nstack: [custom]\ntoolVersions:\ncommands:\n"
            + "  prepare: .agent-studio/prepare\n  build:\n  test: [echo test]\n"
            + "  lint:\ntestSuites:\ncachePaths:\ncapabilities: [linux]\nenvironment:\n");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "Batch pilot.\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", "base");
        Git(_repo, "remote", "add", "origin", _origin);
        Git(_repo, "push", "-q", "origin", "develop");
        // Publication fast-forwards the local integration ref; keep it unchecked-out.
        Git(_repo, "checkout", "-q", "--detach");
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task Pending_member_uses_current_gate_profile_when_the_project_profile_changes()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PROFILE", "docs/profile.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var oldDigest = Assert.Single(store.ListPending()).Subject.GateProfileDigest;
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBuildProfile(ProjectName, new BuildProfile { BuildCmds = ["cd ."] });
        _gate.Batch = request => Green(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.NotEqual(oldDigest, manifest.Scope.GateProfileDigest);
        Assert.Equal(manifest.Scope.GateProfileDigest,
            Assert.Single(manifest.Members).GateProfileDigest);
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Pending_member_uses_current_platform_version_after_an_upgrade()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-VERSION", "docs/version.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = Assert.Single(store.ListPending());
        var path = Assert.Single(Directory.GetFiles(Path.Combine(
            Path.GetDirectoryName(store.BatchDirectory("unused"))!, "pending"), "*.json"));
        File.WriteAllText(path, JsonSerializer.Serialize(pending with
        {
            Subject = pending.Subject with { PlatformVersion = "previous-version" },
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        _gate.Batch = request => Green(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.NotEqual("previous-version", manifest.Scope.PlatformVersion);
        Assert.Equal(manifest.Scope.PlatformVersion,
            Assert.Single(manifest.Members).PlatformVersion);
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Superseded_pending_review_does_not_block_its_replacement_from_the_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-REREVIEW", "docs/rereview.md");
        // A newer review of the same delivery passes and is enqueued while the
        // first pending record is still in the queue.
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("aspect-requirement-fit", "requirement-fit", "claude", [],
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect, Prompt: "Review the delivery.")],
            ["requirement-fit"], BuildTestDeferredToBatch: true);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            member.Key, RepositoryId, member.ResultSha, member.RunAttemptId, "requirements",
            "policy", [], "create-again-" + member.Key,
            ResultRef: "refs/heads/agent-studio/results/" + member.Key, Plan: plan));
        Assert.True(created.Accepted, created.Message);
        var reviewId = created.ReviewAttempt!.AttemptId;
        var claimed = authority.ClaimReview(reviewId, "review-executor", "review-host", 600,
            "claim-again-" + member.Key);
        Assert.True(claimed.Accepted, claimed.Message);
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(reviewId, claimed.ReviewAttempt!.LastFence,
                claimed.ReviewAttempt.AuthorityEpoch, "pass-again-" + member.Key),
            member.ResultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(member.Key, _watchPath)!;
        pilot.Enqueue(task, passed.ReviewAttempt!, authority.GetRun(member.RunAttemptId)!, _clock);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        Assert.Equal(2, store.ListPending().Count);
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Paused_batch_returns_its_members_to_the_per_task_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PAUSE", "docs/pause.md");
        // The batch run and its one same-SHA retry are both infrastructure red,
        // which is the visible pause.
        _gate.Batch = _ => Infrastructure();
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.StartsWith("GateInfra",
            AssertPhase(store, manifest.BatchId, BatchPhase.Paused).Reason, StringComparison.Ordinal);
        Assert.Equal(2, _gate.BatchRuns);
        Assert.Equal(manifest.BatchId, Ownership(member.Key)?.BatchId);
        AssertLane(member.Key, TaskStates.AutoReview);

        await pilot.TickAsync(CancellationToken.None);

        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.StartsWith("paused-to-per-task-gate", state.Reason, StringComparison.Ordinal);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(Ownership(member.Key));
        Assert.Empty(store.ListPending());
        Assert.Empty(store.ListPausedManifests());
    }

    [Fact]
    public async Task All_conflict_cascade_returns_deferred_members_to_the_per_task_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 8);
        var members = Enumerable.Range(1, 8)
            .Select(index => SeedMember(factory, $"DOC-CONFLICT-{index}", "README.md"))
            .ToArray();
        var originalBase = RemoteTip();
        Git(_repo, "checkout", "-q", "--detach", originalBase);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "Integration changed this line.\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-qm", "integration conflict");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        _gate.Batch = _ => throw new InvalidOperationException("An all-conflict batch reached the suite.");
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Equal(8, _gate.FallbackRuns);
        Assert.Empty(store.ListPending());
        foreach (var member in members)
        {
            AssertLane(member.Key, TaskStates.Escalated);
            Assert.Null(Ownership(member.Key));
        }
        Assert.Equal(3, members.Count(member =>
            store.ReadReplay(manifest.BatchId, member.Key).Outcome == "conflict"));
        Assert.Equal(5, members.Count(member =>
            store.ReadReplay(manifest.BatchId, member.Key).Outcome == "cascade-deferred"));
    }

    [Fact]
    public async Task Green_batch_publishes_the_tested_candidate_and_releases_every_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var first = SeedMember(factory, "DOC-GREEN-1", "docs/first.md");
        // The second delivery was cut on top of the first, so both replay with
        // identity SHA mappings onto one candidate.
        var second = SeedMember(factory, "DOC-GREEN-2", "docs/second.md",
            parentSha: first.ResultSha);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        var publication = store.ReadPublication(manifest.BatchId)!;
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        Assert.Equal(1, _gate.BatchRuns);
        foreach (var member in new[] { first, second })
        {
            AssertLane(member.Key, TaskStates.HumanReview);
            var record = store.TryReadMember(manifest.BatchId, member.Key, publication.BatchRunId);
            Assert.NotNull(record);
            Assert.Equal(member.RunAttemptId, record!.RunAttempt);
            Assert.Equal(publication.TestedCandidateSha, record.TestedCandidateSha);
        }
        Assert.Empty(store.ListPending());
        Assert.True(pilot.Report(ProjectName).CorrectnessFloorMet);
    }

    [Fact]
    public async Task Publication_record_failure_after_remote_push_recovers_the_tested_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PUBLISH-RECOVER", "docs/publish-recover.md");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        string? blockedPath = null;
        _gate.Batch = request =>
        {
            var manifest = Assert.Single(store.ListManifests());
            blockedPath = Path.Combine(store.BatchDirectory(manifest.BatchId), "publication.json");
            Directory.CreateDirectory(blockedPath);
            return Green(request.ExpectedSha);
        };
        _gate.Fallback = request => Red(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        var closed = Assert.Single(store.ListManifests());
        Assert.NotEqual(closed.BaseSha, RemoteTip());
        Assert.Null(store.ReadPublication(closed.BatchId));
        var publishing = AssertPhase(store, closed.BatchId, BatchPhase.Publishing);
        Assert.NotNull(publishing.BatchRunId);
        Assert.True(publishing.RefMutationFence!.Value > 0);
        AssertLane(member.Key, TaskStates.AutoReview);

        Directory.Delete(blockedPath!);
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 1);
        await restarted.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var publication = store.ReadPublication(closed.BatchId);
        Assert.NotNull(publication);
        Assert.Equal(publishing.RefMutationFence!.Value, publication.RefMutationFence);
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        AssertPhase(store, closed.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.NotNull(store.TryReadMember(closed.BatchId, member.Key, publication.BatchRunId));
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Member_superseded_during_the_gate_discards_the_verdict_and_publishes_nothing()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var superseded = SeedMember(factory, "DOC-RUN-1", "docs/run-one.md");
        var survivor = SeedMember(factory, "DOC-RUN-2", "docs/run-two.md");
        var baseTip = RemoteTip();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        _gate.Batch = request =>
        {
            // A reissue while the suite runs makes the frozen member stale.
            Supersede(authority, superseded.Key);
            return Green(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal("superseded during gate", state.Reason);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(baseTip, RemoteTip());
        Assert.Null(Ownership(superseded.Key));
        Assert.Null(Ownership(survivor.Key)?.BatchId);
        var pending = Assert.Single(store.ListPending());
        Assert.Equal(survivor.Key, pending.Subject.TaskKey);
        AssertLane(survivor.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Member_superseded_after_the_green_verdict_blocks_publication()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-GREEN-STALE", "docs/green-stale.md");
        var baseTip = RemoteTip();
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var refLeases = factory.Services.GetRequiredService<RefMutationLeaseService>();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();

        // Holding the shared ref-mutation lease parks the pilot between its
        // green verdict and the publish decision.
        Task tick;
        using (await refLeases.AcquireAsync(ProjectName, _repo, "develop", CancellationToken.None))
        {
            tick = pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => store.ListManifests() is [var formed]
                && Directory.Exists(Path.Combine(store.BatchDirectory(formed.BatchId), "state"))
                && store.LatestState(formed.BatchId).Phase == BatchPhase.Green);
            Supersede(authority, member.Key);
        }
        await tick;

        var manifest = Assert.Single(store.ListManifests());
        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal(nameof(BatchPublishDecision.Superseded), state.Reason);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(baseTip, RemoteTip());
        Assert.Null(Ownership(member.Key));
        Assert.Empty(store.ListPending());
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Coordinator_lease_lost_during_the_run_forbids_publication()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-LEASE", "docs/lease.md");
        var baseTip = RemoteTip();
        var leases = factory.Services.GetRequiredService<BatchGateLeaseService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        BatchCoordinatorLease? intruder = null;
        _gate.Batch = request =>
        {
            // The grant expires and another coordinator takes the scope.
            _clock = _clock.AddMinutes(3);
            intruder = leases.TryAcquire(Assert.Single(store.ListManifests()).Scope, "intruder");
            return Green(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.NotNull(intruder);
        var manifest = Assert.Single(store.ListManifests());
        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal(nameof(BatchPublishDecision.LeaseLost), state.Reason);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(baseTip, RemoteTip());
        Assert.Null(Ownership(member.Key)?.BatchId);
        Assert.Equal(member.Key, Assert.Single(store.ListPending()).Subject.TaskKey);
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Coordinator_lease_lost_by_the_heartbeat_mid_gate_abandons_the_batch_without_ending_the_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-HEARTBEAT", "docs/heartbeat.md");
        var baseTip = RemoteTip();
        var leases = factory.Services.GetRequiredService<BatchGateLeaseService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.HeartbeatInterval = TimeSpan.FromMilliseconds(50);
        BatchCoordinatorLease? intruder = null;
        _gate.BatchAsync = async (_, ct) =>
        {
            // The grant expires and another coordinator takes the scope while
            // the suite still runs; only the heartbeat can notice.
            _clock = _clock.AddMinutes(3);
            intruder = leases.TryAcquire(Assert.Single(store.ListManifests()).Scope, "intruder");
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException("The lost lease did not stop the gate.");
        };

        await pilot.TickAsync(CancellationToken.None);

        Assert.NotNull(intruder);
        var manifest = Assert.Single(store.ListManifests());
        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal("coordinator lease lost during the gate", state.Reason);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(baseTip, RemoteTip());
        Assert.Null(Ownership(member.Key)?.BatchId);
        Assert.Equal(member.Key, Assert.Single(store.ListPending()).Subject.TaskKey);
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Member_superseded_before_the_suite_starts_is_ejected_and_no_gate_runs()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var superseded = SeedMember(factory, "DOC-EARLY-1", "docs/early-one.md");
        var survivor = SeedMember(factory, "DOC-EARLY-2", "docs/early-two.md");
        var baseTip = RemoteTip();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        _gate.Batch = _ => throw new InvalidOperationException("A stale manifest reached the suite.");
        var (ready, go) = PauseAtFirstCandidateRef();

        var tick = Task.Run(() => pilot.TickAsync(CancellationToken.None));
        await WaitForAsync(() => File.Exists(ready));
        Supersede(authority, superseded.Key);
        File.WriteAllText(go, string.Empty);
        await tick;

        var manifest = Assert.Single(store.ListManifests());
        var state = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal("superseded after replay", state.Reason);
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Empty(store.ListRuns(manifest.BatchId));
        Assert.Equal(baseTip, RemoteTip());
        Assert.Null(Ownership(superseded.Key));
        Assert.Null(Ownership(survivor.Key)?.BatchId);
        Assert.Equal(survivor.Key, Assert.Single(store.ListPending()).Subject.TaskKey);
        AssertLane(survivor.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Member_superseded_after_verified_publication_keeps_history_and_never_transfers_the_verdict()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-AFTER", "docs/after.md");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        await pilot.TickAsync(CancellationToken.None);
        var manifest = Assert.Single(store.ListManifests());
        var publication = store.ReadPublication(manifest.BatchId)!;
        var frozen = Assert.Single(manifest.Members);

        Supersede(authority, member.Key);
        await pilot.TickAsync(CancellationToken.None);

        // The published history stays exactly as verified.
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        Assert.Equal(1, _gate.BatchRuns);
        var record = store.TryReadMember(manifest.BatchId, member.Key, publication.BatchRunId)!;
        Assert.Equal(member.RunAttemptId, record.RunAttempt);
        // The newer attempt cannot reuse the old generation's gate result.
        var newer = authority.GetTaskProjection(member.Key).CurrentRunAttempt!;
        Assert.NotEqual(member.RunAttemptId, newer.AttemptId);
        var release = store.CheckRelease(frozen with
        {
            RunAttempt = newer.AttemptId,
            FencingToken = newer.LastFence,
            DeliveryEpoch = newer.AuthorityEpoch,
            CurrentGeneration = true,
        }, manifest.BatchId, publication.BatchRunId);
        Assert.False(release.Allowed);
        Assert.Equal("batch-gate-evidence-missing", release.FailureCode);
    }

    [Fact]
    public async Task Retried_emergency_fallback_joins_the_running_per_task_gate()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "CODE-RETRY", "backend/Retry.cs", settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claimed = authority.ClaimReview(member.ReviewAttemptId, "review-executor", "review-host",
            600, "claim-" + member.Key);
        Assert.True(claimed.Accepted, claimed.Message);
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(member.ReviewAttemptId, claimed.ReviewAttempt!.LastFence,
                claimed.ReviewAttempt.AuthorityEpoch, "pass-" + member.Key),
            member.ResultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        var source = authority.GetRun(member.RunAttemptId)!;
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.FallbackAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Red(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        // The report request times out on the client while the gate runs and
        // the client retries the same settled report.
        var first = pilot.RunEmergencyFallbackAsync(task, passed.ReviewAttempt!, source,
            _clock, CancellationToken.None);
        await WaitForAsync(() => _gate.FallbackRuns == 1);
        var retry = pilot.RunEmergencyFallbackAsync(task, passed.ReviewAttempt!, source,
            _clock, CancellationToken.None);
        release.SetResult();
        await Task.WhenAll(first, retry);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(Ownership(member.Key));
    }

    [Fact]
    public async Task Per_task_fallback_interrupted_by_a_restart_resumes_on_the_next_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-CRASH", "docs/crash.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = Assert.Single(store.ListPending());
        // The previous process persisted the active marker and stopped before
        // the per-task gate returned.
        BatchGateOwnershipStore.Write(Path.Combine(_watchPath, TaskStates.AutoReview, member.Key),
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                FallbackGateActive: true, FallbackGateStarts: 1));
        _gate.Fallback = request => Red(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Equal(0, _gate.BatchRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(Ownership(member.Key));
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Per_task_fallback_whose_gate_throws_is_retried_on_the_next_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-THROW", "docs/throw.md");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(Enabled: false));
        var calls = 0;
        _gate.Fallback = request => ++calls == 1
            ? throw new IOException("gate host dropped the connection")
            : Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.AutoReview);
        var marker = Assert.IsType<BatchGateOwnership>(Ownership(member.Key));
        Assert.True(marker.FallbackGateActive);
        Assert.Equal(1, marker.FallbackGateStarts);
        // Re-enabled, the batch route would exclude the member as an active
        // per-task gate; only the interrupted-gate recovery can resume it.
        EnableBatchGate(factory, closeSize: 4);

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(2, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(Ownership(member.Key));
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Per_task_fallback_interrupted_past_its_start_budget_escalates_as_gate_infra()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-BUDGET", "docs/budget.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = Assert.Single(store.ListPending());
        BatchGateOwnershipStore.Write(Path.Combine(_watchPath, TaskStates.AutoReview, member.Key),
            new BatchGateOwnership(pending.ReviewAttemptId, pending.Subject,
                FallbackGateActive: true,
                FallbackGateStarts: BatchGatePilotService.FallbackGateStartBudget));
        _gate.Fallback = request => Green(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Null(Ownership(member.Key));
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public async Task Review_settlement_that_cannot_enqueue_runs_the_per_task_gate_before_answering()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", ReviewRunnerId);
        EnableBatchGate(factory, closeSize: 4);
        // The review plan froze a deferred build/test, but the immutable result
        // carries code, so batch admission refuses it at settlement.
        var member = SeedMember(factory, "CODE-SETTLE", "backend/Pilot.cs", settleReview: false);
        _gate.Fallback = request => Red(request.ExpectedSha);
        await RegisterReviewExecutorAsync(http);
        var claimed = await http.PostAsJsonAsync(
            $"/api/v1/runners/{ReviewRunnerId}/review-claims",
            new Contract.ReviewClaimRequest(ReviewRunnerId, ReviewInstance, 300, AvailableSlots: 1));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        Assert.Equal("claimed", claim.Status);
        Assert.Equal(member.ReviewAttemptId, claim.Attempt!.AttemptId);

        var response = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}/report",
            PassingReport(claim, "batch-settle-pass"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var report = JsonSerializer.Deserialize<Contract.ReviewReportDto>(body,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        Assert.Equal(TaskStates.Escalated, report.TaskState);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.Null(Ownership(member.Key));
    }

    [Fact]
    public async Task Batch_deferred_settlement_journals_no_delivery_so_recovery_leaves_the_member_to_the_batch()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", ReviewRunnerId);
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-SETTLE", "docs/pilot.md", settleReview: false);
        await RegisterReviewExecutorAsync(http);
        var claimed = await http.PostAsJsonAsync(
            $"/api/v1/runners/{ReviewRunnerId}/review-claims",
            new Contract.ReviewClaimRequest(ReviewRunnerId, ReviewInstance, 300, AvailableSlots: 1));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        Assert.Equal(member.ReviewAttemptId, claim.Attempt!.AttemptId);

        var response = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}/report",
            PassingReport(claim, "batch-doc-settle-pass"));

        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, body);
        var pending = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.Equal(member.ReviewAttemptId, pending.ReviewAttemptId);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        var entry = RemoteReviewSettlementJournal.Read(folder, member.ReviewAttemptId).Entry;
        Assert.NotNull(entry);
        Assert.Null(entry!.Delivery);
        Assert.True(RemoteReviewSettlementPolicy.IsBatchDeferredPass(entry));

        // A restart replays the journal: neither the reconciler nor Auto Review
        // resume may integrate the member or settle it as a failed delivery gate.
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        scanner.InvalidateCache();
        var task = scanner.FindJob(member.Key, _watchPath)!;
        var resume = factory.Services.GetRequiredService<AutoReviewDeliveryResumeService>();
        var reconciler = new RemoteReviewSettlementReconciler(scanner,
            factory.Services.GetRequiredService<AttemptAuthorityService>(),
            new RemoteReviewEvidenceProjectionQueue(), resume,
            Microsoft.Extensions.Logging.Abstractions.NullLogger<RemoteReviewSettlementReconciler>.Instance);
        Assert.NotEqual(RemoteReviewSettlementReconcileStatus.Repair, reconciler.Reconcile(task));
        Assert.Null(RemoteDeliverySettlementStore.Read(folder));
        var resumed = await resume.ResumeAsync(task, "test");
        Assert.Equal(AutoReviewResumeAction.None, resumed.Action);
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    private sealed record SeededMember(string Key, string RunAttemptId, string ReviewAttemptId, string ResultSha);

    private SeededMember SeedMember(WebApplicationFactory<Program> factory, string key,
        string changedFile, bool settleReview = true, string? parentSha = null)
    {
        var baseSha = Git(_repo, "rev-parse", "origin/develop");
        var resultRef = "refs/heads/agent-studio/results/" + key;
        Git(_repo, "checkout", "-q", "--detach", parentSha ?? baseSha);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, changedFile))!);
        File.WriteAllText(Path.Combine(_repo, changedFile), key + "\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", key);
        var resultSha = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "update-ref", resultRef, resultSha);
        Git(_repo, "checkout", "-q", "--detach", baseSha);

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(key, RepositoryId, null, "coding-runner", "coding-host",
            600, "acquire-" + key).RunAttempt!;
        var settledRun = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "settle-" + key),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(RepositoryId, run.AttemptId,
                baseSha, resultSha, resultRef, null, new string('a', 64)),
        });
        Assert.True(settledRun.Accepted, settledRun.Message);
        // The card carries what a remote completion records: the attributed
        // result commit and the card-local review subject.
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = key, title = key, state = TaskStates.AutoReview, order = 1, agent = "claude",
            kind = TaskKinds.Task, enteredLaneAt = _clock.UtcDateTime,
            commits = new[]
            {
                new
                {
                    sha = resultSha, shortSha = resultSha[..9], message = key,
                    repository = RepositoryId, branch = "agent-studio/results/" + key,
                    runAttemptId = run.AttemptId, resultSha, at = _clock.UtcDateTime,
                },
            },
        }));
        File.WriteAllText(Path.Combine(folder, "prompt.md"), "Document the pilot.\n");
        File.WriteAllText(Path.Combine(folder, "status.md"), "Result: pending.");
        ReviewSubjectStore.Write(folder, new ReviewSubjectRecord
        {
            TaskKey = key, RunAttemptId = run.AttemptId, AttemptChainId = "chain-" + key,
            Project = ProjectName, Repository = RepositoryId, ResultSha = resultSha,
            ImmutableResultRef = resultRef, CompletedAtUtc = _clock,
        });
        var plan = new Contract.ReviewPlanDto(
            [new Contract.ReviewCommandDto("aspect-requirement-fit", "requirement-fit", "claude", [],
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect, Prompt: "Review the delivery.")],
            ["requirement-fit"], BuildTestDeferredToBatch: true);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key, RepositoryId, resultSha, run.AttemptId, "requirements", "policy", [],
            "create-" + key, ResultRef: resultRef, Plan: plan));
        Assert.True(created.Accepted, created.Message);
        var reviewId = created.ReviewAttempt!.AttemptId;
        factory.Services.GetRequiredService<TaskScannerService>().InvalidateCache();
        if (!settleReview) return new SeededMember(key, run.AttemptId, reviewId, resultSha);

        var claimed = authority.ClaimReview(reviewId, "review-executor", "review-host", 600,
            "claim-" + key);
        Assert.True(claimed.Accepted, claimed.Message);
        var lease = claimed.ReviewAttempt!;
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(reviewId, lease.LastFence, lease.AuthorityEpoch, "pass-" + key),
            resultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        var task = factory.Services.GetRequiredService<TaskScannerService>().FindJob(key, _watchPath)!;
        factory.Services.GetRequiredService<BatchGatePilotService>().Enqueue(task,
            passed.ReviewAttempt!, authority.GetRun(run.AttemptId)!, _clock);
        return new SeededMember(key, run.AttemptId, reviewId, resultSha);
    }

    // A reference-transaction hook parks the assembler right after it creates
    // the candidate ref, which is after close and before the suite starts.
    private (string Ready, string Go) PauseAtFirstCandidateRef()
    {
        var ready = Path.Combine(_workspace, "candidate-ref-ready");
        var go = Path.Combine(_workspace, "candidate-ref-go");
        var hooks = Path.Combine(_repo, ".git", "hooks");
        Directory.CreateDirectory(hooks);
        var hook = Path.Combine(hooks, "reference-transaction");
        File.WriteAllText(hook, "#!/bin/sh\n"
            + "[ \"$1\" = committed ] || exit 0\n"
            + "grep -q 'refs/agent-studio/batch-candidates/' || exit 0\n"
            + $"[ -e '{ready}' ] && exit 0\n"
            + $"touch '{ready}'\n"
            + "i=0\n"
            + $"while [ ! -e '{go}' ] && [ $i -lt 600 ]; do sleep 0.05; i=$((i+1)); done\n"
            + "exit 0\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);
        return (ready, go);
    }

    private static void Supersede(AttemptAuthorityService authority, string key)
    {
        var reissued = authority.AcquireRun(key, RepositoryId, null, "coding-runner",
            "coding-host", 600, "reissue-" + key);
        Assert.True(reissued.Accepted, reissued.Message);
    }

    private static void EnableBatchGate(WebApplicationFactory<Program> factory, int closeSize)
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, CloseSize: closeSize, PressureSize: 1));
    }

    private BatchGateOwnership? Ownership(string key)
        => TaskStates.All.Select(state => Path.Combine(_watchPath, state, key))
            .Where(Directory.Exists)
            .Select(BatchGateOwnershipStore.Read)
            .SingleOrDefault();

    private void AssertLane(string key, string expected)
    {
        var lane = Assert.Single(TaskStates.All, state => Directory.Exists(Path.Combine(_watchPath, state, key)));
        Assert.True(lane == expected, $"{key} is in {lane}, expected {expected}. Errors: {_errors}");
    }

    private string RemoteTip() => Git(_origin, "rev-parse", "refs/heads/develop");

    private static BatchGateState AssertPhase(BatchGateStore store, string batchId, BatchPhase expected)
    {
        var state = store.LatestState(batchId);
        Assert.True(state.Phase == expected, $"Expected {expected}, got {state.Phase}: {state.Reason}");
        return state;
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = Stopwatch.StartNew();
        while (!condition())
        {
            Assert.True(elapsed.Elapsed < TimeSpan.FromSeconds(30),
                "The pilot did not reach the awaited phase.");
            await Task.Delay(50);
        }
    }

    private static BuildTestGateResult Green(string? sha)
        => new(BuildTestGateVerdict.Ok, 0, 1, "ok", "ok", false, false) { TestedSha = sha };

    private static BuildTestGateResult Red(string? sha)
        => new(BuildTestGateVerdict.Fail, 1, 1, "failed", "deterministic-suite-red", false, false)
        {
            TestedSha = sha,
            FailureKind = BuildTestGateFailureKind.Code,
        };

    private static BuildTestGateResult Infrastructure()
        => new(BuildTestGateVerdict.Fail, null, 1, "host lost", "GateInfra", false, false)
        {
            FailureKind = BuildTestGateFailureKind.Environment,
        };

    private async Task RegisterReviewExecutorAsync(HttpClient http)
    {
        var registration = await http.PutAsJsonAsync(
            $"/api/v1/runners/{ReviewRunnerId}",
            new Contract.RegisterRunnerRequest(
                ReviewRunnerId, "review-host", ReviewInstance, "1.0.0",
                Contract.TaskServerProtocol.Current,
                [
                    Contract.ReviewCapabilities.ReviewExecutor,
                    Contract.ReviewCapabilities.BaselineComparison,
                    Contract.ReviewCapabilities.DependencyPreparation,
                    Contract.ReviewCapabilities.GitMaterialization,
                    Contract.ReviewCapabilities.SemanticReview,
                ]));
        registration.EnsureSuccessStatusCode();
    }

    private static Contract.ReviewReportRequest PassingReport(
        Contract.ReviewClaimResponse claim, string idempotencyKey)
    {
        var lease = claim.Lease!;
        var subject = claim.Subject!;
        return new Contract.ReviewReportRequest(
            lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, idempotencyKey,
            "Pass", null, "The documentation review passed.",
            new Contract.ReviewWorkspaceProofDto(
                subject.RepositoryId, subject.ExpectedResultSha, subject.ExpectedResultSha,
                "0123456789abcdef0123456789abcdef01234567", false, false,
                new string('c', 64), lease.ResourceNamespace),
            new Contract.ReviewEnvironmentDto(
                lease.HostId, lease.ExecutorId, lease.InstanceId, "linux", "x64", "10.0",
                new Dictionary<string, string>(), new Dictionary<string, string>()),
            [], [],
            [new Contract.ReviewVerdictDto("requirement-fit", "pass", "RemoteAspectVerdict",
                "The documentation matches the card.")],
            lease.AuthorityEpoch);
    }

    private WebApplicationFactory<Program> BuildFactory()
        => new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["TaskRepository"] = _workspace,
                    ["WatchPaths:0:Name"] = ProjectName,
                    ["WatchPaths:0:Path"] = _watchPath,
                    ["WatchPaths:0:RootPath"] = _repo,
                    ["WatchPaths:0:RepositoryPath"] = _repo,
                    ["ReviewDecisionOrchestrator:Enabled"] = "false",
                }));
            builder.ConfigureTestServices(services =>
            {
                services.RemoveAll<BatchGateStore>();
                services.AddSingleton(new BatchGateStore(Path.Combine(_workspace, "batch-gates")));
                services.RemoveAll<BatchGateLeaseService>();
                services.AddSingleton(new BatchGateLeaseService(
                    Path.Combine(_workspace, "batch-gate-leases"), () => _clock));
                services.RemoveAll<RefMutationLeaseService>();
                services.AddSingleton(new RefMutationLeaseService(
                    Path.Combine(_workspace, "ref-mutation-leases")));
                services.RemoveAll<TimeProvider>();
                services.AddSingleton<TimeProvider>(new PilotTimeProvider(() => _clock));
                services.AddSingleton<ILoggerProvider>(_errors);
                services.RemoveAll<IBuildTestGateRunner>();
                services.AddSingleton<IBuildTestGateRunner>(_gate);
                var tick = services.Single(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(BatchGatePilotHostedService));
                services.Remove(tick);
            });
        });

    private sealed class PilotTimeProvider(Func<DateTimeOffset> now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now();
    }

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    private sealed class ErrorLog : ILoggerProvider, ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
        public ILogger CreateLogger(string categoryName) => this;
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => logLevel >= LogLevel.Error;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (IsEnabled(logLevel)) _entries.Enqueue(formatter(state, exception) + " " + exception?.Message);
        }
        public void Dispose() { }
        public override string ToString() => string.Join(" | ", _entries);
    }

    private sealed class ScriptedGate : IBuildTestGateRunner
    {
        public Func<BuildTestGateRequest, BuildTestGateResult> Batch { get; set; }
            = _ => throw new InvalidOperationException("No batch gate result is scripted.");
        public Func<BuildTestGateRequest, BuildTestGateResult> Fallback { get; set; }
            = _ => throw new InvalidOperationException("No per-task gate result is scripted.");
        public Func<BuildTestGateRequest, CancellationToken, Task<BuildTestGateResult>>? BatchAsync { get; set; }
        public Func<BuildTestGateRequest, CancellationToken, Task<BuildTestGateResult>>? FallbackAsync { get; set; }
        private int _batchRuns;
        private int _fallbackRuns;
        public int BatchRuns => Volatile.Read(ref _batchRuns);
        public int FallbackRuns => Volatile.Read(ref _fallbackRuns);

        public Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
        {
            if (request.GateId == "batch-gate")
            {
                Interlocked.Increment(ref _batchRuns);
                return BatchAsync?.Invoke(request, ct) ?? Task.FromResult(Batch(request));
            }
            Interlocked.Increment(ref _fallbackRuns);
            return FallbackAsync?.Invoke(request, ct) ?? Task.FromResult(Fallback(request));
        }
    }
}
