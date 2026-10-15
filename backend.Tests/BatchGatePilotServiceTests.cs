using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts.BatchGate;
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
    public async Task Batch_gate_settings_and_state_api_follow_the_shared_shape()
    {
        using var factory = BuildFactory();
        var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var invalid = await client.PutAsJsonAsync($"/api/projects/{ProjectName}/batch-gate",
            new BatchGateFormationOptions(Planner: "look-ahead", MaximumSize: 9));
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, invalid.StatusCode);
        using (var body = JsonDocument.Parse(await invalid.Content.ReadAsStringAsync()))
            Assert.Equal("maximumSize", body.RootElement.GetProperty("field").GetString());
        using var wrongType = new StringContent("{\"maximumSize\":\"many\"}",
            System.Text.Encoding.UTF8, "application/json");
        var malformed = await client.PutAsync($"/api/projects/{ProjectName}/batch-gate", wrongType);
        Assert.Equal(System.Net.HttpStatusCode.BadRequest, malformed.StatusCode);
        using (var body = JsonDocument.Parse(await malformed.Content.ReadAsStringAsync()))
            Assert.Equal("maximumSize", body.RootElement.GetProperty("field").GetString());

        var valid = await client.PutAsJsonAsync($"/api/projects/{ProjectName}/batch-gate",
            new BatchGateFormationOptions(Enabled: true, Scope: "code",
                Planner: "look-ahead", MinimumSize: 2, MaximumSize: 6,
                DeadlineMinutes: 20, LookAheadMinutes: 45,
                Advisor: new BatchGateAdvisorOptions(Mode: "advise")));
        Assert.Equal(System.Net.HttpStatusCode.OK, valid.StatusCode);
        using (var body = JsonDocument.Parse(await valid.Content.ReadAsStringAsync()))
        {
            Assert.Equal("code", body.RootElement.GetProperty("settings").GetProperty("scope").GetString());
            Assert.Equal(8, body.RootElement.GetProperty("limits").GetProperty("hardMaximumSize").GetInt32());
        }

        using var state = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/state?history=10"));
        Assert.True(state.RootElement.TryGetProperty("plan", out _));
        Assert.True(state.RootElement.TryGetProperty("pending", out _));
        Assert.True(state.RootElement.TryGetProperty("batches", out _));
        Assert.True(state.RootElement.TryGetProperty("members", out _));

        var close = await client.PostAsync($"/api/projects/{ProjectName}/batch-gate/close-now", null);
        Assert.Equal(System.Net.HttpStatusCode.Accepted, close.StatusCode);
        using var accepted = JsonDocument.Parse(await close.Content.ReadAsStringAsync());
        Assert.True(accepted.RootElement.GetProperty("accepted").GetBoolean());

    }

    [Fact]
    public async Task Enabling_the_gate_replans_a_queued_review_from_its_fetched_result_sha()
    {
        // Stable 9e2526be5: the re-plan diffed the result ref, which the Studio
        // checkout holds only under refs/remotes/origin, so no diff was found and
        // every queued review kept its full build/test plan.
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetIntegrationBranch(ProjectName, "develop");
        var queued = SeedMember(factory, "DOC-REPLAN", "docs/replan.md",
            settleReview: false, deferredPlan: false);
        Assert.Empty(Git(_repo, "for-each-ref", "refs/heads/agent-studio/results/"));
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        Assert.False(authority.GetReview(queued.ReviewAttemptId)!.Subject.Plan!.BuildTestDeferredToBatch);

        var response = await http.PutAsJsonAsync($"/api/projects/{ProjectName}/batch-gate",
            new BatchGateFormationOptions(Enabled: true, Scope: "documentation",
                ReviewTestLevel: "build-only"));
        Assert.Equal(System.Net.HttpStatusCode.OK, response.StatusCode);

        // The re-plan runs after the response; its timeline event follows the plan write.
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, queued.Key);
        var timeline = factory.Services.GetRequiredService<TimelineLog>();
        await WaitForAsync(() => timeline.ReadAll(folder).Any(item =>
            item.Kind == TimelineEventKinds.ReviewAttemptReplanned
            && item.RunId == queued.ReviewAttemptId));
        var plan = authority.GetReview(queued.ReviewAttemptId)!.Subject.Plan!;
        Assert.True(plan.BuildTestDeferredToBatch);
        Assert.Equal("build-only", plan.TestLevel);
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
    public void Snapshot_reuses_diff_stats_for_the_same_result_sha()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-SNAPSHOT-CACHE", "docs/cache.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var builder = new BatchGateSnapshotBuilder(store,
            factory.Services.GetRequiredService<AttemptAuthorityService>(),
            factory.Services.GetRequiredService<TaskScannerService>(),
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>());
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .Get(ProjectName).BatchGate;
        var pending = store.ListPending();

        builder.Build(ProjectName, "develop", pending, settings, _clock);
        builder.Build(ProjectName, "develop", pending, settings, _clock);

        Assert.Equal(1, builder.DiffReadCount);
    }

    [Fact]
    public void Look_ahead_snapshot_includes_queued_reviews()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-SNAPSHOT-WAITING", "docs/waiting.md");
        var queued = SeedMember(factory, "DOC-SNAPSHOT-QUEUED", "docs/queued.md",
            settleReview: false);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var builder = new BatchGateSnapshotBuilder(store,
            factory.Services.GetRequiredService<AttemptAuthorityService>(),
            factory.Services.GetRequiredService<TaskScannerService>(),
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>());
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .Get(ProjectName).BatchGate;

        var snapshot = builder.Build(ProjectName, "develop", store.ListPending(),
            settings, _clock);

        Assert.Contains(snapshot.Upcoming,
            item => item.TaskKey == queued.Key && item.Stage == "review"
                && item.EtaUtc is not null);
    }

    [Fact]
    public void Look_ahead_snapshot_ignores_queued_reviews_without_a_deferred_plan()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-FULL-WAITING", "docs/waiting.md");
        var queued = SeedMember(factory, "DOC-FULL-QUEUED", "docs/queued.md",
            settleReview: false, deferredPlan: false);

        var snapshot = BuildSnapshot(factory);

        Assert.DoesNotContain(snapshot.Upcoming, item => item.TaskKey == queued.Key);
    }

    [Fact]
    public void Look_ahead_snapshot_ignores_reviews_of_a_superseded_run()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-STALE-WAITING", "docs/waiting.md");
        var queued = SeedMember(factory, "DOC-STALE-QUEUED", "docs/queued.md",
            settleReview: false);
        Supersede(factory.Services.GetRequiredService<AttemptAuthorityService>(), queued.Key);

        var snapshot = BuildSnapshot(factory);

        Assert.DoesNotContain(snapshot.Upcoming, item => item.TaskKey == queued.Key);
    }

    [Fact]
    public void Look_ahead_snapshot_reads_the_current_run_by_card_key()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-KEYED-WAITING", "docs/waiting.md");
        // Production cards carry a slug id; the authority keys runs by card key.
        var queued = SeedMember(factory, "DOC-KEYED-QUEUED", "docs/queued.md",
            settleReview: false, taskId: "doc-keyed-queued-slug");

        var snapshot = BuildSnapshot(factory);

        Assert.Contains(snapshot.Upcoming,
            item => item.TaskKey == queued.Key && item.Stage == "review");
    }

    [Fact]
    public void Look_ahead_plan_holds_a_lone_card_only_for_a_deferred_review()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-IDLE-WAITING", "docs/waiting.md");
        SeedMember(factory, "DOC-IDLE-FULL", "docs/full.md",
            settleReview: false, deferredPlan: false);

        var alone = BatchGateLookAheadPlanner.Plan(BuildSnapshot(factory, idleGateHost: true));

        Assert.Equal("per-task", alone.Decision);
        Assert.Empty(alone.Upcoming);

        SeedMember(factory, "DOC-IDLE-DEFERRED", "docs/deferred.md", settleReview: false);
        var held = BatchGateLookAheadPlanner.Plan(BuildSnapshot(factory, idleGateHost: true));

        Assert.Equal("wait", held.Decision);
        Assert.Equal("DOC-IDLE-DEFERRED", Assert.Single(held.Upcoming,
            item => item.Stage == "review").TaskKey);
    }

    [Fact]
    public async Task Deferred_review_settlement_records_its_review_verification_minutes_once()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", ReviewRunnerId);
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-VERIFY", "docs/verify.md", settleReview: false,
            verificationCommand: VerificationCommand());
        await RegisterReviewExecutorAsync(http);
        var claimed = await http.PostAsJsonAsync(
            $"/api/v1/runners/{ReviewRunnerId}/review-claims",
            new Contract.ReviewClaimRequest(ReviewRunnerId, ReviewInstance, 300, AvailableSlots: 1));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        Assert.Equal(member.ReviewAttemptId, claim.Attempt!.AttemptId);
        var report = PassingReport(claim, "batch-verify-pass") with
        {
            Commands = [Evidence(member.ResultSha, "candidate", _clock, 12)],
        };

        var response = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}/report", report);
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        var replayed = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt.AttemptId}/report", report);
        Assert.True(replayed.IsSuccessStatusCode, await replayed.Content.ReadAsStringAsync());

        var fact = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListReviewVerifications(ProjectName));
        Assert.Equal(member.Key, fact.TaskKey);
        Assert.Equal(member.ReviewAttemptId, fact.ReviewAttemptId);
        Assert.Equal(12, fact.CandidateMinutes, 6);
        Assert.Equal(0, fact.BaselineMinutes);
        Assert.Equal(1, fact.CountedCommands);
    }

    [Fact]
    public void Deferred_review_with_a_product_failure_records_its_review_verification()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-VERIFY-RED", "docs/red.md", settleReview: false,
            verificationCommand: VerificationCommand());
        var review = Settle(factory, member, ReviewTerminalOutcome.ProductFailure);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        Assert.True(pilot.RecordReviewVerification(ProjectName, review,
            [Evidence(member.ResultSha, "candidate", _clock, 9)]));

        var fact = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListReviewVerifications(ProjectName));
        Assert.Equal("ProductFailure", fact.Outcome);
        Assert.Equal(9, fact.CandidateMinutes, 6);
    }

    [Fact]
    public void Review_without_a_deferred_plan_records_no_review_verification()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-VERIFY-FULL", "docs/full.md", settleReview: false,
            deferredPlan: false, verificationCommand: VerificationCommand());
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        Assert.False(pilot.RecordReviewVerification(ProjectName, review,
            [Evidence(member.ResultSha, "candidate", _clock, 9)]));

        Assert.Empty(factory.Services.GetRequiredService<BatchGateStore>()
            .ListReviewVerifications(ProjectName));
    }

    [Fact]
    public void Cached_baseline_adds_no_minutes_while_its_candidate_still_counts()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-VERIFY-CACHE", "docs/cache.md", settleReview: false,
            verificationCommand: VerificationCommand());
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var cachedAt = _clock.AddMinutes(-90);

        Assert.True(pilot.RecordReviewVerification(ProjectName, review,
        [
            Evidence(member.ResultSha, "candidate", _clock, 10) with
            {
                BaselineCacheHit = true, BaselineReusedFromAttemptId = "review-older",
            },
            // A cache hit is written with the cache creation time; it never ran.
            Evidence(member.ResultSha, "baseline-0123456789ab", cachedAt, 0) with
            {
                BaselineCacheHit = true, BaselineReusedFromAttemptId = "review-older",
            },
            Evidence(member.ResultSha, "baseline-ba9876543210", _clock, 4),
            Evidence(member.ResultSha, "clean-repeat", _clock.AddMinutes(10), 3),
            Evidence(member.ResultSha, "candidate", _clock, 30) with { Aspect = "requirement-fit" },
            Evidence(member.ResultSha, "candidate", _clock, 30) with
            {
                ExecutionKind = Contract.ReviewCommandKinds.AgentAspect,
            },
        ]));

        var fact = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListReviewVerifications(ProjectName));
        Assert.Equal(13, fact.CandidateMinutes, 6);
        Assert.Equal(4, fact.BaselineMinutes, 6);
        Assert.Equal(3, fact.CountedCommands);
    }

    [Fact]
    public void Snapshot_reads_paths_and_lines_from_the_fetched_result_sha()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var member = SeedMember(factory, "CODE-REMOTE-RESULT", "src/Remote.cs",
            content: "one\ntwo\nthree\n");
        Assert.Empty(Git(_repo, "for-each-ref", "refs/heads/agent-studio/results/"));

        var card = Assert.Single(Snapshot(factory).Waiting);

        Assert.Equal(["src/Remote.cs"], card.Paths);
        Assert.Equal(3, DeliveryLines(factory, member));
        Assert.Equal(BatchGateRiskModel.Score(["src/Remote.cs"], 3, false, false, false),
            card.Risk);
        Assert.True(card.Risk > .08);
    }

    [Fact]
    public void Enqueue_records_a_documentation_delivery_from_the_fetched_result_sha()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);

        SeedMember(factory, "DOC-REMOTE-RESULT", "docs/remote.md");

        var pending = Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending());
        Assert.True(pending.Subject.DocumentationOnly);
    }

    [Fact]
    public void Snapshot_ignores_integration_commits_after_the_card_base()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2, scope: "code");
        var member = SeedMember(factory, "CODE-DRIFT", "src/Own.cs", content: "own\nlines\n");
        var before = Assert.Single(Snapshot(factory).Waiting);

        AdvanceOriginDevelop("src/unrelated.cs");

        var after = Assert.Single(Snapshot(factory).Waiting);
        Assert.Equal(["src/Own.cs"], after.Paths);
        Assert.Equal(before.Paths, after.Paths);
        Assert.Equal(before.Risk, after.Risk);
        Assert.Equal(2, DeliveryLines(factory, member));
    }

    [Fact]
    public void Snapshot_and_enqueue_measure_against_origin_when_the_local_branch_is_stale()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        AdvanceOriginDevelop("src/drift.cs");
        Assert.NotEqual(Git(_repo, "rev-parse", "refs/heads/develop"),
            Git(_repo, "rev-parse", "refs/remotes/origin/develop"));

        var member = SeedMember(factory, "DOC-STALE-BASE", "docs/stale-base.md");

        var card = Assert.Single(Snapshot(factory).Waiting);
        Assert.Equal(["docs/stale-base.md"], card.Paths);
        Assert.Equal(1, DeliveryLines(factory, member));
        Assert.Equal(BatchGateRiskModel.Score(["docs/stale-base.md"], 1, false, false, false),
            card.Risk);
        Assert.True(Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListPending()).Subject.DocumentationOnly);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Snapshot_sends_a_card_without_a_computable_diff_to_the_per_task_gate()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        SeedMember(factory, "DOC-MISSING-RESULT", "docs/missing.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var seeded = Assert.Single(store.ListPending());
        var missing = seeded with { RepositoryPath = Path.Combine(_workspace, "unavailable-repository") };
        var log = new CapturingLog();
        var builder = Builder(factory, log);

        var first = builder.Build(ProjectName, "develop", [missing], Settings(factory), _clock);
        builder.Build(ProjectName, "develop", [missing], Settings(factory), _clock);

        var card = Assert.Single(first.Waiting);
        Assert.Empty(card.Paths);
        Assert.True(card.Risk >= .55, $"Risk {card.Risk} is below the balanced limit.");
        var entry = Assert.Single(log.Entries, entry =>
            entry.Contains("batch-gate-diff-unavailable", StringComparison.Ordinal));
        Assert.Contains($"task={seeded.Subject.TaskKey} sha={seeded.Subject.ResultSha}", entry);
        Assert.Equal(1, builder.DiffReadCount);
    }

    [Fact]
    public async Task Operator_close_now_overrides_an_advisor_veto_hold()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(
                Enabled: true, Planner: "look-ahead", MinimumSize: 2,
                MaximumSize: 2, DeadlineMinutes: 20));
        SeedMember(factory, "DOC-VETO-A", "docs/veto-a.md");
        SeedMember(factory, "DOC-VETO-B", "docs/veto-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        store.AppendPlanDecision(ProjectName, new AgentStudio.TaskServer.Contracts.BatchGate.BatchPlanDecision(
            "wait", "advisor hold", _clock.AddMinutes(10), [], 2, [],
            new AgentStudio.TaskServer.Contracts.BatchGate.BatchAdvisorView(
                "enforce", "veto", ["wait"], "model", _clock)));
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.RequestCloseNow(ProjectName);

        await pilot.TickAsync(CancellationToken.None);

        AssertPhase(store, Assert.Single(store.ListManifests()).BatchId, BatchPhase.Published);
        Assert.Equal(1, _gate.BatchRuns);
    }

    // AGT-3055 fix round: behind the Task Server proxy the engine claims
    // occurrences at the Task Server, never in the monolith store. Engine
    // ownership of BatchPlan is refused there and the monolith keeps deciding,
    // so queued batches still close instead of waiting on an unclaimable occurrence.
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Engine_owned_batch_plan_is_refused_behind_the_task_server_proxy(bool proxied)
    {
        var configuration = new Dictionary<string, string?>
        {
            ["Orchestration:Transitions:BatchPlan"] = "Engine",
        };
        if (proxied) configuration["TaskServer:BaseUrl"] = "http://127.0.0.1:5071";
        using var factory = BuildFactory(configuration: configuration);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .SetBatchGate(ProjectName, new BatchGateFormationOptions(
                Enabled: true, Planner: "look-ahead", MinimumSize: 2,
                MaximumSize: 2, DeadlineMinutes: 20));
        SeedMember(factory, "DOC-PROXY-A", "docs/proxy-a.md");
        SeedMember(factory, "DOC-PROXY-B", "docs/proxy-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        _gate.Batch = request => Green(request.ExpectedSha);

        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        var occurrences = factory.Services.GetRequiredService<MonolithOrchestrationStore>()
            .List(ProjectName, null).Where(run => run.CurrentStage == Contract.OrchestrationStage.BatchPlan).ToArray();
        if (proxied)
        {
            Assert.Empty(occurrences);
            AssertPhase(store, Assert.Single(store.ListManifests()).BatchId, BatchPhase.Published);
            Assert.Equal(1, _gate.BatchRuns);
            Assert.Contains("batch-plan-engine-ownership-refused", _errors.ToString(), StringComparison.Ordinal);
        }
        else
        {
            // Without the proxy the engine claims here; the tick only publishes the occurrence.
            Assert.Equal("pending", Assert.Single(occurrences).Status);
            Assert.Empty(store.ListManifests());
            Assert.Equal(0, _gate.BatchRuns);
        }
    }

    [Fact]
    public async Task Batch_gate_emits_machine_slot_waiting_and_acquired_events()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-GATE-LANE", "docs/gate-lane.md");
        var health = factory.Services.GetRequiredService<PipelineHealthDetector>();
        health.GateAcquired(new PipelineGateContext("other-run", "other-project",
            _repo, "other-card", _clock.UtcDateTime));
        _gate.Batch = request =>
        {
            request.OnMachineGateWaiting?.Invoke();
            request.OnMachineGateAcquired?.Invoke();
            return Green(request.ExpectedSha);
        };

        await factory.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var folder = Assert.Single(TaskStates.All.Select(state =>
                Path.Combine(_watchPath, state, member.Key)).Where(Directory.Exists));
        var events = IntegrationLaneEvents.Read(folder);
        var waiting = Assert.Single(events, e => e.Kind == "gate-token-waiting");
        Assert.Equal("other-project", waiting.HoldingProject);
        var acquired = Assert.Single(events, e => e.Kind == "gate-token-acquired");
        Assert.Equal(waiting.Sequence, acquired.Sequence);
        Assert.Equal(member.Key, acquired.JobId);
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
    public async Task Running_batch_does_not_block_next_formation_tick()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.BatchAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Green(request.ExpectedSha);
        };
        try
        {
            SeedMember(factory, "DOC-ASYNC-1", "docs/async-1.md");
            SeedMember(factory, "DOC-ASYNC-2", "docs/async-2.md");
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => _gate.BatchRuns == 1);

            SeedMember(factory, "DOC-ASYNC-3", "docs/async-3.md");
            SeedMember(factory, "DOC-ASYNC-4", "docs/async-4.md");
            await pilot.TickAsync(CancellationToken.None);

            Assert.Equal(2, store.ListManifests().Count);
            Assert.Equal(1, _gate.BatchRuns);
        }
        finally { release.TrySetResult(); }
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
    public async Task Ejected_member_can_enter_the_integration_fallback_after_batch_reservation_releases()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-EJECT-INTEGRATE", "README.md");
        var originalBase = RemoteTip();
        Git(_repo, "checkout", "-q", "--detach", originalBase);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "Integration changed this line.\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-qm", "integration conflict");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        _gate.Batch = _ => throw new InvalidOperationException("Ejected member reached the batch suite.");
        _gate.Fallback = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        Assert.Single(store.ListPending());
        Assert.Equal(member.ResultSha, Ownership(member.Key)?.FallbackTestedSha);
        AssertLane(member.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Member_already_on_the_integration_branch_leaves_as_integrated_without_a_gate()
    {
        // AGT-3103: an operator pushed the delivery before its review settled.
        // The member needs neither a candidate nor a per-task gate.
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-CONTAINED", "docs/contained.md");
        Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
        var containedIn = RemoteTip();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(0, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Empty(store.ListManifests());
        Assert.Empty(store.ListPending());
        AssertLane(member.Key, TaskStates.HumanReview);
        var ownership = Ownership(member.Key);
        Assert.Equal(containedIn, ownership?.ContainedInSha);
        Assert.True(ownership!.FallbackIntegrated);
        Assert.True(File.Exists(Path.Combine(TaskPaths.LogsDir(
            Path.Combine(_watchPath, TaskStates.HumanReview, member.Key)), ownership.FallbackEvidencePath!)));
        Assert.Equal(containedIn, RemoteTip());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Acceptance_repairs_an_unintegrated_fallback_marker_when_delivery_is_published(
        bool patchEquivalent)
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-FALLBACK-CONTAINED", "docs/fallback-contained.md",
            initialState: TaskStates.HumanReview);
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, member.Key);
        var marker = BatchGateOwnershipStore.Read(folder)!;
        BatchGateOwnershipStore.Write(folder, marker with
        {
            FallbackTestedSha = member.ResultSha,
            FallbackIntegrated = false,
        });
        if (patchEquivalent)
        {
            Git(_repo, "checkout", "-q", "--detach", "origin/develop");
            File.WriteAllText(Path.Combine(_repo, "unrelated.txt"), "other published work\n");
            Git(_repo, "add", "unrelated.txt");
            Git(_repo, "commit", "-qm", "unrelated publication");
            Git(_repo, "cherry-pick", member.ResultSha);
            Git(_repo, "push", "-q", "origin", "HEAD:develop");
            Assert.NotEqual(member.ResultSha, RemoteTip());
        }
        else Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
        var containedIn = RemoteTip();

        using var response = await http.PostAsJsonAsync(
            $"/api/tasks/{member.Key}/move?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { targetState = TaskStates.Completed, operatorOverride = true,
                reason = "Published delivery verified by operator." });

        Assert.True(response.StatusCode == System.Net.HttpStatusCode.OK,
            await response.Content.ReadAsStringAsync());
        AssertLane(member.Key, TaskStates.Completed);
        var ownership = Ownership(member.Key)!;
        Assert.Equal(containedIn, ownership.ContainedInSha);
        Assert.True(ownership.FallbackIntegrated);
        Assert.True(File.Exists(Path.Combine(TaskPaths.LogsDir(
            Path.Combine(_watchPath, TaskStates.Completed, member.Key)),
            ownership.FallbackEvidencePath!)));
        Assert.Equal(0, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
    }

    [Fact]
    public async Task Acceptance_with_uncontained_fallback_marker_returns_typed_conflict()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        EnableBatchGate(factory, closeSize: 4);
        var member = SeedMember(factory, "DOC-FALLBACK-MISSING", "docs/fallback-missing.md",
            initialState: TaskStates.HumanReview);
        var folder = Path.Combine(_watchPath, TaskStates.HumanReview, member.Key);
        BatchGateOwnershipStore.Write(folder, BatchGateOwnershipStore.Read(folder)! with
        {
            FallbackTestedSha = string.Empty,
            FallbackIntegrated = false,
        });

        using var response = await http.PostAsJsonAsync(
            $"/api/tasks/{member.Key}/move?watchPath={Uri.EscapeDataString(_watchPath)}",
            new { targetState = TaskStates.Completed, operatorOverride = true,
                reason = "Operator reviewed the delivery." });

        Assert.Equal(System.Net.HttpStatusCode.Conflict, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal("batch-gate-evidence-missing", body.RootElement.GetProperty("code").GetString());
        Assert.Equal("batch-gate-evidence-missing", body.RootElement.GetProperty("error").GetString());
        Assert.False(string.IsNullOrWhiteSpace(body.RootElement.GetProperty("hint").GetString()));
        Assert.Null(Ownership(member.Key)?.ContainedInSha);
        AssertLane(member.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Published_recovery_does_not_wait_for_an_ejected_members_running_fallback()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var admitted = SeedMember(factory, "DOC-RECOVER-ADMITTED", "docs/admitted.md");
        var ejected = SeedMember(factory, "DOC-RECOVER-EJECTED", "README.md");
        var originalBase = RemoteTip();
        Git(_repo, "checkout", "-q", "--detach", originalBase);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "Integration changed this line.\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-qm", "integration conflict");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _gate.Batch = request => Green(request.ExpectedSha);
        _gate.FallbackAsync = async (request, ct) =>
        {
            await release.Task.WaitAsync(ct);
            return Red(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.WaitForFlightsInTick = false;
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        try
        {
            await pilot.TickAsync(CancellationToken.None);
            await WaitForAsync(() => store.ListManifests().Any(manifest =>
                store.LatestState(manifest.BatchId).Phase == BatchPhase.Published));
            await WaitForAsync(() => _gate.FallbackRuns == 1);
            await WaitForAsync(() => Directory.Exists(Path.Combine(_watchPath,
                TaskStates.HumanReview, admitted.Key)));
            await pilot.TickAsync(CancellationToken.None);
            AssertLane(admitted.Key, TaskStates.HumanReview);
            AssertLane(ejected.Key, TaskStates.AutoReview);
            var fact = Assert.Single(store.ListExecutions(ProjectName).Where(f => f.BatchRunId is not null));
            Assert.Equal([admitted.Key], fact.MemberKeys);
            Assert.Equal(2, store.ReadManifest(fact.BatchId!).Members.Count);
            var row = GateEconomicsEndpoints.ProjectRow(ProjectName, [],
                since: fact.StartedAtUtc.AddDays(-1), now: fact.CompletedAtUtc,
                retest: null, batchMetrics:
                [new BatchGateMetric(ProjectName, fact.MemberKeys!.Count, fact, Published: true)]);
            Assert.Equal(1, row.GatedMerges);
        }
        finally { release.TrySetResult(); }
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
    public async Task Fix_round_member_with_a_develop_merge_joins_the_batch_by_a_candidate_merge()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var oldBase = RemoteTip();
        AdvanceOriginDevelop("docs/drift.md");
        var linear = SeedMember(factory, "DOC-LINEAR", "docs/linear.md");
        var fixRound = SeedMember(factory, "DOC-FIX-ROUND", "docs/fix-round.md",
            parentSha: oldBase, mergeFrom: "refs/remotes/origin/develop");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        var publication = store.ReadPublication(manifest.BatchId)!;
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Equal("linear", store.ReadReplay(manifest.BatchId, linear.Key).Strategy);
        AssertMergedMember(store, manifest.BatchId, publication.BatchRunId, fixRound);
        AssertLane(linear.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Red_isolation_ejects_a_linear_offender_and_publishes_a_merged_fix_round_survivor()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var oldBase = RemoteTip();
        AdvanceOriginDevelop("docs/drift.md");
        var offender = SeedMember(factory, "DOC-OFFENDER", "docs/offender.md");
        var fixRound = SeedMember(factory, "DOC-FIX-SURVIVOR", "docs/fix-survivor.md",
            parentSha: oldBase, mergeFrom: "refs/remotes/origin/develop");
        // Only a candidate that carries the offender's file is red.
        _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/offender.md")
            ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var primary = Assert.Single(store.ListManifests(), item => item.ParentBatchId is null);
        AssertPhase(store, primary.BatchId, BatchPhase.Red);
        var survivor = Assert.Single(store.ListManifests(), item =>
            store.LatestState(item.BatchId).Phase == BatchPhase.Published);
        Assert.Equal([fixRound.Key], survivor.Members.Select(member => member.TaskKey));
        var publication = store.ReadPublication(survivor.BatchId)!;
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        Assert.False(TreeHas(RemoteTip(), "docs/offender.md"));
        AssertMergedMember(store, survivor.BatchId, publication.BatchRunId, fixRound);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(offender.Key, TaskStates.Escalated);
    }

    // A merged member keeps every delivery SHA: the candidate merge has the
    // tip before it and the untouched result SHA as its exact parents, so the
    // result maps to that merge and no commit of the card is rewritten.
    private void AssertMergedMember(BatchGateStore store, string batchId, string runId,
        SeededMember member)
    {
        var replay = store.ReadReplay(batchId, member.Key);
        Assert.Equal("admitted", replay.Outcome);
        Assert.Equal("merge", replay.Strategy);
        var replacement = Assert.Single(replay.Replacements);
        Assert.Equal(member.ResultSha, replacement.OriginalSha);
        Assert.Equal(replay.TipAfterSha, replacement.RebasedSha);
        Assert.Equal($"{replay.TipAfterSha} {replay.TipBeforeSha} {member.ResultSha}",
            Git(_repo, "rev-list", "--parents", "-n", "1", replay.TipAfterSha!));
        Git(_origin, "merge-base", "--is-ancestor", member.ResultSha, "refs/heads/develop");
        var record = store.TryReadMember(batchId, member.Key, runId);
        Assert.NotNull(record);
        Assert.Equal([replay.TipAfterSha!], record!.ReplacementShas);
        AssertLane(member.Key, TaskStates.HumanReview);
        var task = JsonDocument.Parse(File.ReadAllText(Path.Combine(
            _watchPath, TaskStates.HumanReview, member.Key, "task.json"))).RootElement;
        Assert.All(task.GetProperty("commits").EnumerateArray(), commit =>
            Assert.False(commit.TryGetProperty("supersededBySha", out var by)
                && by.ValueKind == JsonValueKind.String));
        Assert.Contains(task.GetProperty("integrationRecords").EnumerateArray(), item =>
            item.GetProperty("classification").GetString() == "curated-mapping"
            && item.GetProperty("sourceSha").GetString() == member.ResultSha
            && item.GetProperty("integrationSha").GetString() == replay.TipAfterSha);
    }

    private bool TreeHas(string sha, string path)
        => Git(_repo, "ls-tree", "--name-only", sha, path).Length > 0;

    [Fact]
    public async Task Broken_published_manifest_does_not_block_the_next_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        SeedMember(factory, "DOC-BROKEN-RECOVERY", "docs/broken.md");
        await pilot.TickAsync(CancellationToken.None);
        var first = Assert.Single(store.ListManifests());
        AssertPhase(store, first.BatchId, BatchPhase.Published);
        var publication = store.ReadPublication(first.BatchId)!;
        var verdictPath = Path.Combine(store.BatchDirectory(first.BatchId), "runs",
            publication.BatchRunId + ".verdict.json");
        File.WriteAllText(verdictPath, "{}");

        var next = SeedMember(factory, "DOC-AFTER-BROKEN-RECOVERY", "docs/next.md");
        await pilot.TickAsync(CancellationToken.None);

        var second = Assert.Single(store.ListManifests(), manifest => manifest.BatchId != first.BatchId);
        AssertPhase(store, second.BatchId, BatchPhase.Published);
        AssertLane(next.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Verified_remote_publication_releases_member_when_local_checkout_is_dirty()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-LOCAL-SYNC", "docs/local-sync.md");
        var localBase = Git(_repo, "rev-parse", "develop").Trim();
        Git(_repo, "checkout", "-q", "develop");
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "local-sync.md"), "local untracked edit\n");
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
        Assert.Equal(store.ReadPublication(manifest.BatchId)!.TestedCandidateSha, RemoteTip());
        Assert.Equal(localBase, Git(_repo, "rev-parse", "develop").Trim());
        Assert.Equal("local untracked edit\n",
            File.ReadAllText(Path.Combine(_repo, "docs", "local-sync.md")));
        var lag = factory.Services.GetRequiredService<AgentStudio.Git.GitService>()
            .GetDeveloperCheckoutStatus(_repo, "develop");
        Assert.Equal("behind", lag.Status);
        Assert.Equal(RemoteTip(), lag.PublishedSha);
        Assert.Equal(1, lag.Behind);
        Assert.Contains("local-sync.md", lag.Reason);
    }

    [Fact]
    public async Task Resumed_publication_releases_member_when_local_checkout_refuses_the_fast_forward()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-RESUME-DIRTY", "docs/resume-dirty.md");
        var localBase = Git(_repo, "rev-parse", "develop").Trim();
        Git(_repo, "checkout", "-q", "develop");
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
        AssertPhase(store, closed.BatchId, BatchPhase.Publishing);
        Directory.Delete(blockedPath!);
        Directory.CreateDirectory(Path.Combine(_repo, "docs"));
        File.WriteAllText(Path.Combine(_repo, "docs", "resume-dirty.md"), "local untracked edit\n");
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 1);
        await restarted.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        var publication = store.ReadPublication(closed.BatchId);
        Assert.NotNull(publication);
        Assert.Equal(publication.TestedCandidateSha, RemoteTip());
        AssertPhase(store, closed.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
        Assert.Equal(localBase, Git(_repo, "rev-parse", "develop").Trim());
        Assert.Equal("local untracked edit\n",
            File.ReadAllText(Path.Combine(_repo, "docs", "resume-dirty.md")));
        var lag = restarted.Services.GetRequiredService<AgentStudio.Git.GitService>()
            .GetDeveloperCheckoutStatus(_repo, "develop");
        Assert.Equal("behind", lag.Status);
        Assert.Contains("resume-dirty.md", lag.Reason);
    }

    [Fact]
    public async Task Base_red_with_matching_failure_pauses_without_publication_or_validation()
    {
        using var factory = BuildFactory();
        using var client = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetBuildProfile(ProjectName, new BuildProfile { BuildCmds = ["cd ."] });
        var member = SeedMember(factory, "DOC-BASE-RED", "docs/base-red.md");
        _gate.Batch = request =>
        {
            Assert.Null(request.JobId);
            return Red(request.ExpectedSha);
        };
        _gate.Base = request =>
        {
            Assert.Null(request.JobId);
            return Red(request.ExpectedSha);
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var validatedBefore = settings.Get(ProjectName).BuildProfile!.LastValidatedAt;

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal("integration-branch-red",
            AssertPhase(store, manifest.BatchId, BatchPhase.Paused).Reason);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(1, _gate.BaseRuns);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        var run = Assert.Single(store.ListRuns(manifest.BatchId));
        Assert.Equal("pre-existing-base-red",
            store.ReadVerdict(manifest.BatchId, run.BatchRunId)!.Classification);
        Assert.Equal("fail", store.ReadVerdict(manifest.BatchId, run.BatchRunId)!.Outcome);
        Assert.Equal(validatedBefore, settings.Get(ProjectName).BuildProfile!.LastValidatedAt);
        Assert.NotNull(store.ReadBaseVerdict(ProjectName, manifest.BaseSha,
            manifest.Scope.GateProfileDigest));

        await pilot.TickAsync(CancellationToken.None);
        AssertPhase(store, manifest.BatchId, BatchPhase.Paused);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Single(store.ListPending());
        Assert.Single(store.ListManifests());
        Assert.Contains("integration-branch-red", JsonSerializer.Serialize(pilot.State(ProjectName)));
        Assert.Equal(1, pilot.Report(ProjectName).IntegrationBranchRedBatches);
        using var report = JsonDocument.Parse(await client.GetStringAsync(
            $"/api/projects/{ProjectName}/batch-gate/report"));
        Assert.Equal(1, report.RootElement.GetProperty("integrationBranchRedBatches").GetInt32());

        var previousBase = RemoteTip();
        Git(_repo, "checkout", "-q", "--detach", previousBase);
        File.WriteAllText(Path.Combine(_repo, "README.md"), "Base moved while red.\n");
        Git(_repo, "add", "README.md");
        Git(_repo, "commit", "-qm", "base move");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        var retry = Assert.Single(store.ListManifests(), item => item.BatchId != manifest.BatchId);
        AssertPhase(store, retry.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
    }

    [Fact]
    public async Task Base_red_with_different_failure_does_not_halve_members()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var first = SeedMember(factory, "DOC-BASE-RED-A", "docs/base-a.md");
        var second = SeedMember(factory, "DOC-BASE-RED-B", "docs/base-b.md");
        _gate.Batch = request => Red(request.ExpectedSha) with
        {
            Reason = "candidate failed at an earlier step",
        };
        _gate.Base = request => Red(request.ExpectedSha) with
        {
            Reason = "base failed at another step",
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();

        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        Assert.Equal("integration-branch-red",
            AssertPhase(store, manifest.BatchId, BatchPhase.Paused).Reason);
        Assert.Null(store.ReadPublication(manifest.BatchId));
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(1, _gate.BaseRuns);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.Equal(2, store.ListPending().Count);
        AssertLane(first.Key, TaskStates.AutoReview);
        AssertLane(second.Key, TaskStates.AutoReview);
    }

    [Fact]
    public async Task Eject_from_red_base_hold_releases_only_requested_member()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var ejected = SeedMember(factory, "DOC-HELD-EJECT", "docs/held-eject.md");
        var retained = SeedMember(factory, "DOC-HELD-KEEP", "docs/held-keep.md");
        _gate.Batch = request => Red(request.ExpectedSha);
        _gate.Base = request => Red(request.ExpectedSha);
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        await pilot.TickAsync(CancellationToken.None);
        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Paused);

        Assert.True(pilot.RequestEject(ProjectName, ejected.Key));
        await pilot.TickAsync(CancellationToken.None);

        AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(retained.Key, TaskStates.AutoReview);
        Assert.Equal(1, _gate.BatchRuns);
    }

    [Fact]
    public async Task Superseded_push_marker_uses_current_card_folder_and_does_not_hold_batch()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PUSH-SUPERSEDED", "docs/push-superseded.md");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        var pipeline = factory.Services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureRun(folder, PipelineCatalogue.Standard, ProjectName, member.Key);
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            ApprovedIntegrationSha = RemoteTip(),
            StartedAt = _clock.UtcDateTime,
            CompletedAt = _clock.UtcDateTime,
        });
        var reservations = factory.Services
            .GetRequiredService<BatchIntegrationReservationService>();
        reservations.MarkPushPending(ProjectName, member.Key, member.ResultSha,
            Path.Combine(_workspace, "former-card-folder"), _repo, "develop", _watchPath);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Null(reservations.GetWait(ProjectName));
        AssertPhase(factory.Services.GetRequiredService<BatchGateStore>(),
            Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
                .ListManifests()).BatchId, BatchPhase.Published);
    }

    [Fact]
    public async Task Terminal_non_fast_forward_push_does_not_hold_other_batches()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PUSH-BLOCKED", "docs/push-blocked.md");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        var pipeline = factory.Services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureRun(folder, PipelineCatalogue.Standard, ProjectName, member.Key);
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            ApprovedIntegrationSha = member.ResultSha,
            StartedAt = _clock.UtcDateTime,
            CompletedAt = _clock.UtcDateTime,
        });
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "push-blocked",
            StartedAt = _clock.UtcDateTime,
            CompletedAt = _clock.UtcDateTime,
        });
        var reservations = factory.Services
            .GetRequiredService<BatchIntegrationReservationService>();
        reservations.MarkPushPending(ProjectName, member.Key, member.ResultSha,
            folder, _repo, "develop", _watchPath);
        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);

        Assert.Null(reservations.GetWait(ProjectName));
        AssertPhase(factory.Services.GetRequiredService<BatchGateStore>(),
            Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
                .ListManifests()).BatchId, BatchPhase.Published);
    }

    [Fact]
    public async Task Disabled_code_gate_does_not_reconcile_or_fetch_pending_pushes()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var reservations = factory.Services
            .GetRequiredService<BatchIntegrationReservationService>();
        reservations.MarkPushPending(ProjectName, "AGT-MISSING", "unpublished",
            repositoryPath: _repo, integrationBranch: "develop", watchPath: _watchPath);
        var fetchHead = Path.Combine(_repo, ".git", "FETCH_HEAD");
        if (File.Exists(fetchHead)) File.Delete(fetchHead);

        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        await pilot.TickAsync(CancellationToken.None);

        Assert.NotNull(reservations.GetWait(ProjectName));
        Assert.False(File.Exists(fetchHead));
        Assert.DoesNotContain("waiting on push", JsonSerializer.Serialize(pilot.State(ProjectName)));
    }

    [Fact]
    public void Old_blocked_push_row_does_not_clear_a_new_merge_marker()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-PUSH-REINTEGRATED", "docs/reintegrated.md");
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        var pipeline = factory.Services.GetRequiredService<PipelineExecutionLog>();
        pipeline.EnsureRun(folder, PipelineCatalogue.Standard, ProjectName, member.Key);
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopPushStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Failed,
            Verdict = "push-blocked",
            StartedAt = _clock.UtcDateTime,
            CompletedAt = _clock.UtcDateTime,
        });
        pipeline.RecordStep(folder, new PipelineStepExecution
        {
            StepId = PipelineCatalogue.MergeIntoDevelopStepId,
            Kind = StepKind.Tool,
            Status = PipelineStepStatus.Passed,
            ApprovedIntegrationSha = member.ResultSha,
            StartedAt = _clock.AddMinutes(1).UtcDateTime,
            CompletedAt = _clock.AddMinutes(1).UtcDateTime,
        });
        var marker = new PendingIntegrationPush(member.Key, member.ResultSha,
            folder, _repo, "develop", _watchPath, _clock);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        Assert.False(pilot.IsStalePush(ProjectName, marker,
            new Dictionary<(string Repo, string Branch), string?>(), CancellationToken.None));
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
    public async Task Unattributable_member_of_a_publication_interrupted_by_a_restart_still_runs_its_per_task_gate()
    {
        var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var contained = SeedMember(factory, "DOC-RESTART-CONTAINED", "docs/restart-contained.md",
            settleReview: false);
        var admitted = SeedMember(factory, "DOC-RESTART-ADMITTED", "docs/restart-admitted.md",
            parentSha: contained.ResultSha);
        // Both deliveries are absent from develop at formation. The first
        // replay advances the candidate past the second member's result SHA,
        // so assembly must eject that member as unattributable.
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var settled = Settle(factory, contained, ReviewTerminalOutcome.Pass);
        var task = factory.Services.GetRequiredService<TaskScannerService>()
            .FindJob(contained.Key, _watchPath)!;
        factory.Services.GetRequiredService<BatchGatePilotService>().Enqueue(task,
            settled, authority.GetRun(contained.RunAttemptId)!, _clock);
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
        _gate.Fallback = request => Green(request.ExpectedSha);

        // The process stops after the remote push and before it schedules
        // the ejected member's per-task gate.
        await pilot.TickAsync(CancellationToken.None);

        var closed = Assert.Single(store.ListManifests());
        Assert.Equal(2, closed.Members.Count);
        Assert.Equal("unattributable", store.ReadReplay(closed.BatchId, contained.Key).Outcome);
        AssertPhase(store, closed.BatchId, BatchPhase.Publishing);
        Assert.Equal(0, _gate.FallbackRuns);
        Assert.False(Ownership(contained.Key)?.FallbackGateActive ?? false);
        AssertLane(contained.Key, TaskStates.AutoReview);

        Directory.Delete(blockedPath!);
        factory.Dispose();
        using var restarted = BuildFactory();
        _ = restarted.CreateClient();
        EnableBatchGate(restarted, closeSize: 2);
        await restarted.Services.GetRequiredService<BatchGatePilotService>()
            .TickAsync(CancellationToken.None);

        AssertPhase(store, closed.BatchId, BatchPhase.Published);
        AssertLane(admitted.Key, TaskStates.HumanReview);
        Assert.Equal(1, _gate.BatchRuns);
        Assert.Equal(1, _gate.FallbackRuns);
        // The green per-task gate completes the card the batch could not attribute.
        var completed = Path.Combine(_watchPath, TaskStates.HumanReview, contained.Key);
        Assert.Equal(contained.ResultSha, BatchGateOwnershipStore.Read(completed)?.FallbackTestedSha);
        Assert.True(File.Exists(Path.Combine(TaskPaths.LogsDir(completed),
            $"batch-fallback-{contained.ReviewAttemptId}.json")));
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
            var scope = Assert.Single(store.ListManifests()).Scope;
            // The 50 ms heartbeat briefly holds the scope's lock file, and
            // contention throws IOException; callers retry. The pause ignores
            // ct because the heartbeat cancels it once it sees the lost lease.
            for (var attempt = 0; intruder is null && attempt < 200; attempt++)
            {
                try { intruder = leases.TryAcquire(scope, "intruder"); }
                catch (IOException) { await Task.Delay(10, CancellationToken.None); }
            }
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
        Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListExecutions(ProjectName));
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
        // The emergency per-task gate ran, so the pilot report counts it.
        Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListExecutions(ProjectName));
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

    [Fact]
    public async Task Deferred_review_with_a_base_branch_defect_still_enters_the_batch()
    {
        using var factory = BuildFactory();
        using var http = factory.CreateClient();
        http.DefaultRequestHeaders.Add("X-Client-Id", ReviewRunnerId);
        EnableBatchGate(factory, closeSize: 1);
        var member = SeedMember(factory, "DOC-BASE-DEFECT", "docs/base-defect.md",
            settleReview: false);
        await RegisterReviewExecutorAsync(http);
        var claimed = await http.PostAsJsonAsync(
            $"/api/v1/runners/{ReviewRunnerId}/review-claims",
            new Contract.ReviewClaimRequest(ReviewRunnerId, ReviewInstance, 300, AvailableSlots: 1));
        claimed.EnsureSuccessStatusCode();
        var claim = (await claimed.Content.ReadFromJsonAsync<Contract.ReviewClaimResponse>())!;
        var report = PassingReport(claim, "batch-base-defect") with
        {
            Outcome = "IntegrationBranchDefect",
        };

        var response = await http.PostAsJsonAsync(
            $"/api/v1/reviews/attempts/{claim.Attempt!.AttemptId}/report", report);

        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(member.ReviewAttemptId,
            Assert.Single(factory.Services.GetRequiredService<BatchGateStore>().ListPending()).ReviewAttemptId);
        var folder = Path.Combine(_watchPath, TaskStates.AutoReview, member.Key);
        var entry = RemoteReviewSettlementJournal.Read(folder, member.ReviewAttemptId).Entry;
        Assert.NotNull(entry);
        Assert.Null(entry!.Delivery);
        Assert.True(RemoteReviewSettlementPolicy.IsBatchDeferredPass(entry));
        AssertLane(member.Key, TaskStates.AutoReview);

        _gate.Batch = request => Green(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        await pilot.TickAsync(CancellationToken.None);

        var manifest = Assert.Single(store.ListManifests());
        AssertPhase(store, manifest.BatchId, BatchPhase.Published);
        AssertLane(member.Key, TaskStates.HumanReview);
        Assert.Empty(store.ListPending());
    }

    [Fact]
    public void Enqueue_admits_a_card_whose_slug_id_differs_from_its_reference_key()
    {
        // Production cards live in tasks/<bucket>/<KEY> with a slug id in
        // task.json, while the attempt authority is keyed by the reference key.
        // Looking the card up by its slug found no current attempts, and every
        // passed deferred review failed admission as SupersededGeneration.
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-SLUG", "docs/slug.md", settleReview: false);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claimed = authority.ClaimReview(member.ReviewAttemptId, "review-executor", "review-host", 600,
            "claim-" + member.Key);
        Assert.True(claimed.Accepted, claimed.Message);
        var passed = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(member.ReviewAttemptId, claimed.ReviewAttempt!.LastFence,
                claimed.ReviewAttempt.AuthorityEpoch, "pass-" + member.Key),
            member.ResultSha, ReviewTerminalOutcome.Pass));
        Assert.True(passed.Accepted, passed.Message);
        var seeded = factory.Services.GetRequiredService<TaskScannerService>().FindJob(member.Key, _watchPath)!;
        var flatLayoutCard = seeded with { Id = "doc-slug-documents-the-pilot", Key = member.Key };

        var pending = factory.Services.GetRequiredService<BatchGatePilotService>().Enqueue(flatLayoutCard,
            passed.ReviewAttempt!, authority.GetRun(member.RunAttemptId)!, _clock);

        Assert.Equal(member.Key, pending.Subject.TaskKey);
        Assert.True(pending.Subject.CurrentGeneration);
        Assert.Equal(member.Key, Assert.Single(factory.Services.GetRequiredService<BatchGateStore>()
            .ListPending()).Subject.TaskKey);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Snapshot_excludes_pending_continuations_before_reading_the_delivery_diff()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var waiting = SeedMember(factory, "DOC-INTENT-WAITING", "docs/waiting.md");
        var upcoming = SeedMember(factory, "DOC-INTENT-UPCOMING", "docs/upcoming.md",
            settleReview: false);
        SaveContinuation(factory, waiting);
        SaveContinuation(factory, upcoming);
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var builder = Builder(factory);

        var snapshot = builder.Build(ProjectName, "develop", store.ListPending(),
            new BatchGateFormationOptions(), _clock);

        Assert.Empty(snapshot.Waiting);
        Assert.Empty(snapshot.Upcoming);
        Assert.Equal(0, builder.DiffReadCount);
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData(false)]
    [InlineData(true)]
    public void Snapshot_excludes_a_superseded_review_subject(bool settledReview)
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-SUBJECT", "docs/subject.md",
            settleReview: settledReview);
        ReplaceDelivery(factory, member);
        var builder = Builder(factory);

        var snapshot = builder.Build(ProjectName, "develop",
            factory.Services.GetRequiredService<BatchGateStore>().ListPending(),
            Settings(factory), _clock);

        Assert.Empty(snapshot.Waiting);
        Assert.Empty(snapshot.Upcoming);
        Assert.Equal(0, builder.DiffReadCount);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Snapshot_excludes_the_old_delivery_when_a_continuation_is_queued_in_progress()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-PROGRESS", "docs/progress.md");
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var task = scanner.FindJob(member.Key, _watchPath)!;
        var progressFolder = Path.Combine(_watchPath, TaskStates.Progress, member.Key);
        Directory.Move(task.FolderPath, progressFolder);
        var taskPath = Path.Combine(progressFolder, "task.json");
        var document = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(taskPath))!;
        document["state"] = TaskStates.Progress;
        File.WriteAllText(taskPath, document.ToJsonString());
        scanner.InvalidateCache();
        Assert.Equal(TaskStates.Progress, scanner.FindJob(member.Key, _watchPath)!.State);

        var snapshot = BuildSnapshot(factory);

        Assert.Empty(snapshot.Waiting);
        Assert.DoesNotContain(snapshot.Upcoming, card => card.Stage == "review");
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData(2, false)]
    [InlineData(3, false)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void Formed_batch_removal_replans_survivors_or_dissolves_below_minimum(
        int size, bool supersededReview)
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: size);
        var members = Enumerable.Range(0, size).Select(index =>
            SeedMember(factory, $"DOC-REPLAN-{index}", $"docs/replan-{index}.md")).ToArray();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = ClosePendingBatch(factory);
        if (supersededReview) ReplaceDelivery(factory, members[0]);
        else SaveContinuation(factory, members[0]);

        factory.Services.GetRequiredService<BatchGatePilotService>().ReconcileFormedBatches();

        var retired = AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Equal(_clock, retired.RecordedAtUtc);
        Assert.Contains(members[0].Key + ": " + (supersededReview
            ? "review-subject-mismatch" : "pending-continue-intent"), retired.Reason);
        Assert.Empty(store.ListRecoverableManifests());
        Assert.Empty(store.ListRuns(manifest.BatchId));
        Assert.All(members, member => Assert.Null(Ownership(member.Key)?.BatchId));
        var snapshot = BuildSnapshot(factory) with { CloseNow = true };
        Assert.Equal(size - 1, snapshot.Waiting.Count);
        var plan = BatchGateLookAheadPlanner.Plan(snapshot);
        Assert.Equal(size == 2 ? "per-task" : "close", plan.Decision);
        Assert.Equal(members.Skip(1).Select(member => member.Key), plan.ProposedMembers);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Formed_batch_release_failure_retries_remaining_ownership_after_restart()
    {
        BatchGateManifest manifest;
        SeededMember[] members;
        using (var factory = BuildFactory(authorityOnTestClock: true))
        {
            _ = factory.CreateClient();
            EnableBatchGate(factory, closeSize: 3);
            members = Enumerable.Range(0, 3).Select(index =>
                SeedMember(factory, $"DOC-RELEASE-{index}", $"docs/release-{index}.md"))
                .ToArray();
            var store = factory.Services.GetRequiredService<BatchGateStore>();
            manifest = ClosePendingBatch(factory);
            SaveContinuation(factory, members[0]);
            var survivorFolder = Path.Combine(_watchPath, TaskStates.AutoReview, members[1].Key);
            var lockPath = Path.Combine(TaskPaths.LogsDir(survivorFolder),
                "batch-gate-ownership.lock");
            using (var blockedWrite = new FileStream(lockPath, FileMode.OpenOrCreate,
                       FileAccess.ReadWrite, FileShare.None))
            {
                Assert.Throws<IOException>(() => factory.Services
                    .GetRequiredService<BatchGatePilotService>().ReconcileFormedBatches());
            }

            AssertPhase(store, manifest.BatchId, BatchPhase.Formed);
            Assert.Null(Ownership(members[0].Key)?.BatchId);
            Assert.Equal(manifest.BatchId, Ownership(members[1].Key)?.BatchId);
            Assert.Contains(store.ListRecoverableManifests(), item => item.BatchId == manifest.BatchId);
        }

        using var restarted = BuildFactory(authorityOnTestClock: true);
        _ = restarted.CreateClient();
        restarted.Services.GetRequiredService<BatchGatePilotService>().ReconcileFormedBatches();

        var recoveredStore = restarted.Services.GetRequiredService<BatchGateStore>();
        var retired = AssertPhase(recoveredStore, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Contains(members[0].Key + ": pending-continue-intent", retired.Reason);
        Assert.All(members, member => Assert.Null(Ownership(member.Key)?.BatchId));
        var snapshot = BuildSnapshot(restarted) with { CloseNow = true };
        Assert.Equal(members.Skip(1).Select(member => member.Key),
            BatchGateLookAheadPlanner.Plan(snapshot).ProposedMembers);
    }

    [Theory]
    [Trait("Category", "MachineBound")]
    [InlineData(false)]
    [InlineData(true)]
    public void Running_batch_is_untouched_by_eligibility_reconciliation(bool supersededReview)
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var member = SeedMember(factory, "DOC-RUNNING-A", "docs/running-a.md");
        SeedMember(factory, "DOC-RUNNING-B", "docs/running-b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = ClosePendingBatch(factory);
        var running = new BatchGateState(manifest.BatchId, manifest.MembershipDigest,
            BatchPhase.Running, member.ResultSha, 1, _clock);
        store.AppendState(running);
        if (supersededReview) ReplaceDelivery(factory, member);
        else SaveContinuation(factory, member);

        factory.Services.GetRequiredService<BatchGatePilotService>().ReconcileFormedBatches();

        Assert.Equal(running, store.LatestState(manifest.BatchId));
        Assert.Equal(manifest.BatchId, Ownership(member.Key)?.BatchId);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Tick_replans_a_formed_batch_before_starting_its_gate()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 3);
        var removed = SeedMember(factory, "DOC-TICK-REMOVE", "docs/remove.md");
        SeedMember(factory, "DOC-TICK-A", "docs/a.md");
        SeedMember(factory, "DOC-TICK-B", "docs/b.md");
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var manifest = ClosePendingBatch(factory);
        SaveContinuation(factory, removed);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        pilot.RequestCloseNow(ProjectName);
        _gate.Batch = request => Green(request.ExpectedSha);

        await pilot.TickAsync(CancellationToken.None);

        AssertPhase(store, manifest.BatchId, BatchPhase.Abandoned);
        Assert.Empty(store.ListRuns(manifest.BatchId));
        var successor = Assert.Single(store.ListManifests(), item => item.BatchId != manifest.BatchId);
        Assert.DoesNotContain(successor.Members, member => member.TaskKey == removed.Key);
        Assert.Equal(2, successor.Members.Count);
        AssertPhase(store, successor.BatchId, BatchPhase.Published);
        AssertLane(removed.Key, TaskStates.AutoReview);
    }

    private BatchGateManifest ClosePendingBatch(WebApplicationFactory<Program> factory)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var pending = store.ListPending();
        var first = pending[0].Subject;
        var scope = new BatchGateScope(first.Project, first.Repository, first.IntegrationBranch,
            first.GateProfile, first.GateProfileDigest, first.PlatformVersion);
        var formed = BatchGatePolicy.Close(pending.Select(item => item.Subject), scope,
            RemoteTip(), Settings(factory), pending.Select(item => item.Subject.TaskKey).ToArray(), _clock);
        var manifest = Assert.IsType<BatchGateManifest>(formed.Manifest);
        store.CloseManifest(manifest);
        foreach (var item in pending)
            BatchGateOwnershipStore.Write(item.JobFolderPath,
                new BatchGateOwnership(item.ReviewAttemptId, item.Subject, manifest.BatchId));
        return manifest;
    }

    private static void ReplaceDelivery(WebApplicationFactory<Program> factory, SeededMember member)
    {
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(member.Key, RepositoryId, null, "coding-runner",
            "coding-host", 600, "replacement-" + member.Key).RunAttempt!;
        var sha = new string('b', 40);
        var settled = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "replacement-settle-" + member.Key),
            Outcome = "done", ResultSha = sha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(RepositoryId, run.AttemptId,
                member.ResultSha, sha, "refs/heads/agent-studio/results/replacement-" + member.Key,
                null, new string('a', 64)),
        });
        Assert.True(settled.Accepted, settled.Message);
    }

    private void SaveContinuation(WebApplicationFactory<Program> factory, SeededMember member)
    {
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var task = scanner.FindJob(member.Key, _watchPath)!;
        File.WriteAllText(Path.Combine(task.FolderPath, "pending-intent.json"),
            JsonSerializer.Serialize(new PendingIntent
            {
                Prompt = "Fix the review findings.", SavedAt = _clock.UtcDateTime,
            }));
        scanner.InvalidateCache();
        Assert.NotNull(scanner.FindJob(member.Key, _watchPath)!.PendingIntent);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void New_delivery_returns_to_planning_after_its_review_passes()
    {
        using var factory = BuildFactory(authorityOnTestClock: true);
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 2);
        var old = SeedMember(factory, "DOC-RETURN", "docs/old.md");
        SaveContinuation(factory, old);
        Assert.Empty(BuildSnapshot(factory).Waiting);
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        File.Delete(Path.Combine(scanner.FindJob(old.Key, _watchPath)!.FolderPath, "pending-intent.json"));
        var replacement = SeedMember(factory, old.Key, "docs/new.md",
            settleReview: false, parentSha: old.ResultSha, generation: "-replacement");
        Assert.Empty(BuildSnapshot(factory).Waiting);
        var passed = Settle(factory, replacement, ReviewTerminalOutcome.Pass);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        factory.Services.GetRequiredService<BatchGatePilotService>().Enqueue(
            scanner.FindJob(old.Key, _watchPath)!, passed,
            authority.GetRun(replacement.RunAttemptId)!, _clock);

        Assert.Equal(old.Key, Assert.Single(BuildSnapshot(factory).Waiting).TaskKey);
    }

    private sealed record SeededMember(string Key, string RunAttemptId, string ReviewAttemptId, string ResultSha);

    private SeededMember SeedMember(WebApplicationFactory<Program> factory, string key,
        string changedFile, bool settleReview = true, string? parentSha = null,
        bool deferredPlan = true, string? taskId = null,
        Contract.ReviewCommandDto? verificationCommand = null, string? content = null,
        string? mergeFrom = null, string generation = "",
        string initialState = TaskStates.AutoReview)
    {
        var baseSha = Git(_repo, "rev-parse", "origin/develop");
        var resultRef = "refs/heads/agent-studio/results/" + key;
        Git(_repo, "checkout", "-q", "--detach", parentSha ?? baseSha);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, changedFile))!);
        File.WriteAllText(Path.Combine(_repo, changedFile), content ?? key + "\n");
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", key);
        // A fix round told to "merge current origin/develop first" leaves a
        // merge commit inside its delivery range.
        if (mergeFrom is not null)
            Git(_repo, "merge", "-q", "--no-ff", "--no-edit", mergeFrom);
        var resultSha = Git(_repo, "rev-parse", "HEAD");
        PublishResult(key, resultSha);
        Git(_repo, "checkout", "-q", "--detach", baseSha);

        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var run = authority.AcquireRun(key, RepositoryId, null, "coding-runner", "coding-host",
            600, "acquire-" + key + generation).RunAttempt!;
        var settledRun = authority.SettleRun(new SettleRunAttemptRequest
        {
            Write = new AttemptWriteReference(run.AttemptId, run.LastFence, run.AuthorityEpoch,
                "settle-" + key + generation),
            Outcome = "done",
            ResultSha = resultSha,
            ResultEnvelope = new Contract.ImmutableResultEnvelope(RepositoryId, run.AttemptId,
                baseSha, resultSha, resultRef, null, new string('a', 64)),
        });
        Assert.True(settledRun.Accepted, settledRun.Message);
        // The card carries what a remote completion records: the attributed
        // result commit and the card-local review subject.
        var folder = Path.Combine(_watchPath, initialState, key);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = taskId ?? key, key, title = key, state = initialState, order = 1, agent = "claude",
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
        Contract.ReviewCommandDto[] commands =
        [
            new("aspect-requirement-fit", "requirement-fit", "claude", [],
                ExecutionKind: Contract.ReviewCommandKinds.AgentAspect, Prompt: "Review the delivery."),
            .. verificationCommand is null ? [] : new[] { verificationCommand },
        ];
        var plan = new Contract.ReviewPlanDto(commands, ["requirement-fit"],
            BuildTestDeferredToBatch: deferredPlan);
        var created = authority.CreateReviewAttempt(new CreateReviewAttemptRequest(
            key, RepositoryId, resultSha, run.AttemptId, "requirements", "policy", [],
            "create-" + key + generation, ResultRef: resultRef, Plan: plan));
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

    // The production Studio checkout only holds what the completion fetch
    // stores: the result commit under refs/remotes/origin, never a local
    // refs/heads result ref. Replay fetches that ref itself.
    private void PublishResult(string key, string resultSha)
    {
        Git(_repo, "push", "-q", "origin", $"{resultSha}:refs/heads/agent-studio/results/{key}");
        Git(_repo, "fetch", "-q", "--no-tags", "origin",
            $"+refs/heads/agent-studio/results/{key}:refs/remotes/origin/agent-studio/results/{key}");
        Assert.Empty(Git(_repo, "for-each-ref", $"refs/heads/agent-studio/results/{key}"));
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

    private BatchPlanInput BuildSnapshot(WebApplicationFactory<Program> factory,
        bool idleGateHost = false)
    {
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var builder = new BatchGateSnapshotBuilder(store,
            factory.Services.GetRequiredService<AttemptAuthorityService>(),
            factory.Services.GetRequiredService<TaskScannerService>(),
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>());
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .Get(ProjectName).BatchGate;
        return builder.Build(ProjectName, "develop", store.ListPending(), settings, _clock,
            idleGateHost: idleGateHost);
    }

    private static ReviewAttemptDto Settle(WebApplicationFactory<Program> factory,
        SeededMember member, ReviewTerminalOutcome outcome)
    {
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var claimed = authority.ClaimReview(member.ReviewAttemptId, "review-executor", "review-host",
            600, "claim-" + member.Key);
        Assert.True(claimed.Accepted, claimed.Message);
        var settled = authority.SettleReview(new SettleReviewAttemptRequest(
            new AttemptWriteReference(member.ReviewAttemptId, claimed.ReviewAttempt!.LastFence,
                claimed.ReviewAttempt.AuthorityEpoch, "settle-review-" + member.Key),
            member.ResultSha, outcome));
        Assert.True(settled.Accepted, settled.Message);
        return settled.ReviewAttempt!;
    }

    private static Contract.ReviewCommandDto VerificationCommand()
        => new("verify-build", "review-verification", "dotnet", ["build"]);

    private static Contract.ReviewCommandEvidenceDto Evidence(string sha, string workspaceRole,
        DateTimeOffset startedAt, double minutes)
        => new("verify-build", "review-verification", "dotnet", ["build"], sha, sha,
            new string('c', 64), startedAt.UtcDateTime, startedAt.AddMinutes(minutes).UtcDateTime,
            0, null, new string('d', 64), new string('e', 64), WorkspaceRole: workspaceRole);

    private static void Supersede(AttemptAuthorityService authority, string key)
    {
        var reissued = authority.AcquireRun(key, RepositoryId, null, "coding-runner",
            "coding-host", 600, "reissue-" + key);
        Assert.True(reissued.Accepted, reissued.Message);
    }

    private static void EnableBatchGate(WebApplicationFactory<Program> factory, int closeSize,
        string scope = "documentation")
    {
        var settings = factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>();
        settings.SetIntegrationBranch(ProjectName, "develop");
        settings.SetBatchGate(ProjectName, new BatchGateFormationOptions(
            Enabled: true, Scope: scope, CloseSize: closeSize, PressureSize: 1));
    }

    private static BatchGateSnapshotBuilder Builder(WebApplicationFactory<Program> factory,
        ILogger? logger = null)
        => new(factory.Services.GetRequiredService<BatchGateStore>(),
            factory.Services.GetRequiredService<AttemptAuthorityService>(),
            factory.Services.GetRequiredService<TaskScannerService>(),
            factory.Services.GetRequiredService<AgentStudio.Git.GitService>(), logger);

    private static BatchGateFormationOptions Settings(WebApplicationFactory<Program> factory)
        => factory.Services.GetRequiredService<AgentStudio.Projects.ProjectSettingsService>()
            .Get(ProjectName).BatchGate;

    // A fresh builder per call so a moved integration ref is read again.
    private BatchPlanInput Snapshot(WebApplicationFactory<Program> factory)
        => Builder(factory).Build(ProjectName, "develop",
            factory.Services.GetRequiredService<BatchGateStore>().ListPending(),
            Settings(factory), _clock);

    private int DeliveryLines(WebApplicationFactory<Program> factory, SeededMember member)
        => BatchGateDeliveryDiff.Read(factory.Services.GetRequiredService<AgentStudio.Git.GitService>(),
            _repo, "develop", member.ResultSha)!.Lines;

    // Moves origin's develop past every seeded card while the local develop
    // branch stays where the fixture left it, as on the Studio checkout.
    private void AdvanceOriginDevelop(string changedFile)
    {
        var restore = Git(_repo, "rev-parse", "HEAD");
        Git(_repo, "checkout", "-q", "--detach", "refs/remotes/origin/develop");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.Combine(_repo, changedFile))!);
        File.WriteAllText(Path.Combine(_repo, changedFile),
            string.Concat(Enumerable.Range(0, 40).Select(line => $"drift {line}\n")));
        Git(_repo, "add", ".");
        Git(_repo, "commit", "-qm", "unrelated develop change");
        Git(_repo, "push", "-q", "origin", "HEAD:develop");
        Git(_repo, "fetch", "-q", "origin");
        Git(_repo, "checkout", "-q", "--detach", restore);
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

    // The host registers the attempt authority on the wall clock. A test that
    // follows review CreatedAt into upcoming ETAs needs it on the test clock,
    // or every ETA lies outside the look-ahead window of the fixed clock.
    private WebApplicationFactory<Program> BuildFactory(bool authorityOnTestClock = false,
        IReadOnlyDictionary<string, string?>? configuration = null)
    {
        var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
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
                    // These tests drive the suite through the fake build/test gate runner.
                    [ClaimedBatchGateRunner.ExecutionKey] = ClaimedBatchGateRunner.BridgeMode,
                }).AddInMemoryCollection(configuration ?? new Dictionary<string, string?>()));
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
                if (authorityOnTestClock)
                {
                    services.RemoveAll<AttemptAuthorityService>();
                    services.AddSingleton(sp => new AttemptAuthorityService(
                        sp.GetRequiredService<IConfiguration>(),
                        sp.GetRequiredService<ILogger<AttemptAuthorityService>>(),
                        () => _clock.UtcDateTime));
                }
                var tick = services.Single(descriptor =>
                    descriptor.ServiceType == typeof(IHostedService)
                    && descriptor.ImplementationType == typeof(BatchGatePilotHostedService));
                services.Remove(tick);
            });
        });
        factory.Services.GetRequiredService<BatchGatePilotService>().WaitForFlightsInTick = true;
        return factory;
    }

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
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    private sealed class CapturingLog : ILogger
    {
        private readonly System.Collections.Concurrent.ConcurrentQueue<string> _entries = new();
        public IReadOnlyList<string> Entries => _entries.ToArray();
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
            => _entries.Enqueue(formatter(state, exception));
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
        public Func<BuildTestGateRequest, BuildTestGateResult> Base { get; set; }
            = request => Green(request.ExpectedSha);
        public Func<BuildTestGateRequest, BuildTestGateResult> Fallback { get; set; }
            = _ => throw new InvalidOperationException("No per-task gate result is scripted.");
        public Func<BuildTestGateRequest, CancellationToken, Task<BuildTestGateResult>>? BatchAsync { get; set; }
        public Func<BuildTestGateRequest, CancellationToken, Task<BuildTestGateResult>>? FallbackAsync { get; set; }
        private int _batchRuns;
        private int _baseRuns;
        private int _fallbackRuns;
        public int BatchRuns => Volatile.Read(ref _batchRuns);
        public int BaseRuns => Volatile.Read(ref _baseRuns);
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
            if (request.GateId == "batch-base")
            {
                Interlocked.Increment(ref _baseRuns);
                return Task.FromResult(Base(request));
            }
            Interlocked.Increment(ref _fallbackRuns);
            return FallbackAsync?.Invoke(request, ct) ?? Task.FromResult(Fallback(request));
        }
    }
}
