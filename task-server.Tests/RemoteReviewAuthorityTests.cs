using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Xunit;

namespace TaskServer.Tests;

public sealed class RemoteReviewAuthorityTests
{
    private const string ResultSha = "589c462f589c462f589c462f589c462f589c462f";
    private const string TreeSha = "0123456789abcdef0123456789abcdef01234567";
    private const string RepositoryId = "repo_0123456789abcdef";
    private const string RepositoryUrl = "https://example.invalid/product.git";
    private const string ReviewHttpToken = "review-http-token-000000000000000000000000000001";

    [Fact]
    public async Task Stored_review_subject_limits_dotnet_test_cpu_before_it_becomes_immutable()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "sh",
                ["-lc", "dotnet test"],
                CompareToBaseline: true)],
            ["build-tests"],
            IntegrationRef: "refs/heads/develop");

        var subject = await SeedReviewSubjectAsync(store, plan: plan);

        Assert.Equal(
            "dotnet test -maxcpucount:2 -p:ParallelizeTestCollections=false",
            Assert.Single(subject.Plan.Commands).Arguments[1]);
    }

    [Fact]
    public async Task Late_ordinary_heartbeat_cannot_shorten_the_final_handoff_lease()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-07T03:10:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "review-instance-a", "review-host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance-a"), "review-a", default);

        var handoff = await store.RenewReviewLeaseAsync(
            claim.Attempt!.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a", "review-instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, "final-handoff-renew", RequestedTtlSeconds: 600),
            "review-a",
            default);
        clock.Advance(TimeSpan.FromSeconds(120));
        var lateHeartbeat = await store.RenewReviewLeaseAsync(
            claim.Attempt.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a", "review-instance-a", claim.Lease.LeaseId,
                claim.Lease.Fence, "late-ordinary-renew", RequestedTtlSeconds: 120),
            "review-a",
            default);

        Assert.Equal(handoff.ExpiresAt, lateHeartbeat.ExpiresAt);
    }

    [Fact]
    public async Task Fenced_review_cleanup_queues_full_envelope_decision_and_reaches_human_review_idempotently()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var source = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "review-instance-a", "review-host-a");

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance-a"), "review-a", default);
        Assert.Equal(source.SubjectId, claim.Subject!.SubjectId);
        Assert.Equal(ResultSha, claim.Subject.ExpectedResultSha);
        Assert.NotEqual(source.SourceRunId, claim.Attempt!.AttemptId);

        var renewed = await store.RenewReviewLeaseAsync(
            claim.Attempt.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a", "review-instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, "renew-1"),
            "review-a",
            default);
        Assert.True(renewed.ExpiresAt >= claim.Lease.ExpiresAt);

        var request = PassingReport(claim);
        var report = await store.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        var replay = await store.ReportReviewAsync(claim.Attempt.AttemptId, request, "review-a", default);
        Assert.Equal(report, replay);
        Assert.False(report.RetryScheduled);
        Assert.Equal("4-auto-review", report.TaskState);

        var cleanupRequest = new ReviewCleanupRequest(
            "review-a", "review-instance-a", claim.Lease.LeaseId,
            claim.Lease.Fence, "cleanup-1", true);
        var cleanup = await store.CleanupReviewAsync(
            claim.Attempt.AttemptId, cleanupRequest, "review-a", default);
        var cleanupReplay = await store.CleanupReviewAsync(
            claim.Attempt.AttemptId, cleanupRequest, "review-a", default);
        Assert.Equal("cleaned", cleanup.Status);
        Assert.Equal("duplicate", cleanupReplay.Status);
        Assert.Equal("4-auto-review", (await TaskAsync(store, source.TaskId)).State);
        var orchestration = Assert.Single(await store.ListOrchestrationRunsAsync(
            null, "pending", default));
        Assert.Equal(source.TaskId, orchestration.TaskId);
        var payload = JsonSerializer.Deserialize<ReviewOrchestrationPayloadDto>(
            orchestration.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(source.SourceRunId, payload.RunAttemptId);
        Assert.Equal(source.SubjectId, payload.ReviewSubjectId);
        Assert.Equal(claim.Attempt.AttemptId, payload.ReviewAttemptId);
        Assert.Equal(ResultSha, payload.ResultSha);
        Assert.Equal(source.ReviewPolicyHash, payload.ReviewPolicyHash);
        Assert.Equal(report.ReportSha256, payload.ReviewReportSha256);
        Assert.Equal("Pass", payload.ReviewOutcome);
        Assert.All(payload.Verdicts, verdict => Assert.Equal("pass", verdict.Status));
        Assert.All(payload.Gates, gate => Assert.Equal("passed", gate.Status));

        await CompletePassingOrchestrationAsync(store, orchestration);

        var task = await TaskAsync(store, source.TaskId);
        Assert.Equal("5-human-review", task.State);
        var audit = await store.ListAuditAsync(0, default);
        Assert.Contains(audit, record => record.Action == "review.claimed");
        Assert.Contains(audit, record => record.Action == "review.reported"
                                         && record.DetailJson.Contains(ResultSha, StringComparison.Ordinal));
        Assert.Contains(audit, record => record.Action == "review.cleaned");
        Assert.Contains(audit, record => record.Action == "orchestration.run-created");
    }

    [Fact]
    public async Task Product_failure_cleanup_preserves_the_full_envelope_and_reissues_server_side()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var source = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "review-instance-a", "review-host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance-a"), "review-a", default);
        var passing = PassingReport(claim);
        var failedStep = passing.Commands[0].StepId;
        var failedAspect = passing.Commands[0].Aspect;
        var productFailure = passing with
        {
            Commands = passing.Commands.Select(command =>
                command.StepId == failedStep
                    ? command with { ExitCode = 1 }
                    : command).ToArray(),
            Verdicts = passing.Verdicts.Select(verdict =>
                verdict.Aspect == failedAspect
                    ? verdict with
                    {
                        Status = "block",
                        Classification = "ReviewCommandFailed",
                        Summary = "The declared command failed.",
                    }
                    : verdict).ToArray(),
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId, productFailure, "review-a", default);
        Assert.Equal("ProductFailure", report.Outcome);
        Assert.Equal("4-auto-review", report.TaskState);
        await store.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new ReviewCleanupRequest(
                "review-a", "review-instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, "cleanup-product-failure", true),
            "review-a",
            default);

        var orchestration = Assert.Single(await store.ListOrchestrationRunsAsync(
            null, "pending", default));
        var payload = JsonSerializer.Deserialize<ReviewOrchestrationPayloadDto>(
            orchestration.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.NotNull(payload);
        Assert.Equal(source.SourceRunId, payload.RunAttemptId);
        Assert.Equal(source.SubjectId, payload.ReviewSubjectId);
        Assert.Equal(claim.Attempt.AttemptId, payload.ReviewAttemptId);
        Assert.Equal(ResultSha, payload.ResultSha);
        Assert.Equal(report.ReportSha256, payload.ReviewReportSha256);
        Assert.Equal("ProductFailure", payload.ReviewOutcome);
        Assert.Equal("failed", Assert.Single(
            payload.Gates,
            gate => gate.StepId == failedStep).Status);

        var engineClaim = await store.ClaimOrchestrationAsync(
            new OrchestrationClaimRequest(
                "engine-a", "engine-instance-a", [OrchestrationStage.ReviewDecision]),
            "engine-a",
            default);
        var settled = await store.CompleteOrchestrationStageAsync(
            orchestration.RunId,
            new CompleteOrchestrationStageRequest(
                "engine-a",
                "engine-instance-a",
                engineClaim.Lease!.LeaseId,
                engineClaim.Lease.Fence,
                OrchestrationStage.ReviewDecision,
                OrchestrationAction.Reissue,
                "{}",
                "settle-product-failure"),
            "engine-a",
            default);

        Assert.Equal("reissued", settled.Status);
        Assert.Equal(1, settled.ReissueAttempts);
        Assert.Equal("2-ready", (await TaskAsync(store, source.TaskId)).State);
    }

    [Fact]
    public async Task Cleanup_does_not_reopen_an_operator_lane_move_or_leave_review_authority_active()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var source = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "review-instance-a", "review-host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "review-instance-a"), "review-a", default);
        await store.ReportReviewAsync(
            claim.Attempt!.AttemptId, PassingReport(claim), "review-a", default);
        var beforeMove = await TaskAsync(store, source.TaskId);
        var project = Assert.Single(await store.ListProjectsAsync(null, default));
        await store.UpdateTaskAsync(
            project.ProjectId,
            source.TaskId,
            new UpdateTaskRequest(null, null, "5-human-review", beforeMove.Version),
            "operator",
            default);

        var cleanup = await store.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new ReviewCleanupRequest(
                "review-a", "review-instance-a", claim.Lease!.LeaseId,
                claim.Lease.Fence, "cleanup-after-operator-move", true),
            "review-a",
            default);

        Assert.Equal("cleaned", cleanup.Status);
        Assert.Equal("5-human-review", (await TaskAsync(store, source.TaskId)).State);
        Assert.Empty(await store.ListOrchestrationRunsAsync(null, null, default));
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "review.orchestration-superseded");
    }

    [Fact]
    public async Task Failed_workspace_cleanup_is_audited_and_retries_the_same_subject()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await store.ReportReviewAsync(
            first.Attempt!.AttemptId, PassingReport(first), "review-a", default);

        var cleanupRequest = new ReviewCleanupRequest(
            "review-a", "instance-a", first.Lease!.LeaseId, first.Lease.Fence,
            "cleanup-failed-1", false, "WorkspaceCleanupFailed");
        var cleanup = await store.CleanupReviewAsync(
            first.Attempt.AttemptId, cleanupRequest, "review-a", default);
        var replay = await store.CleanupReviewAsync(
            first.Attempt.AttemptId, cleanupRequest, "review-a", default);

        Assert.Equal("cleanup-failed", cleanup.Status);
        Assert.True(cleanup.RetryScheduled);
        Assert.Equal("duplicate", replay.Status);
        Assert.True(replay.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "review.cleanup-failed"
                      && record.DetailJson.Contains("WorkspaceCleanupFailed", StringComparison.Ordinal));

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.Equal(subject.SubjectId, retry.Subject!.SubjectId);
        Assert.NotEqual(first.Attempt.AttemptId, retry.Attempt!.AttemptId);
        Assert.Equal(first.Attempt.AttemptNumber + 1, retry.Attempt.AttemptNumber);
    }

    [Theory]
    [InlineData("RepositoryMismatch")]
    [InlineData("ShaMismatch")]
    [InlineData("DirtyBefore")]
    [InlineData("MutatedAfter")]
    [InlineData("SnapshotUnavailable")]
    public async Task Typed_infrastructure_outcomes_stay_in_auto_review_and_retry_the_same_subject(
        string classification)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(first) with
        {
            Outcome = classification == "SnapshotUnavailable" ? "ReviewInfra" : "Pass",
            FailureClassification = classification == "SnapshotUnavailable" ? classification : null,
            Workspace = PassingReport(first).Workspace with
            {
                RepositoryId = classification == "RepositoryMismatch" ? "repo_wrong" : RepositoryId,
                ActualHead = classification == "ShaMismatch"
                    ? "6130634361306343613063436130634361306343"
                    : ResultSha,
                DirtyBefore = classification == "DirtyBefore",
                DirtyAfter = classification == "MutatedAfter",
            },
        };

        var report = await store.ReportReviewAsync(
            first.Attempt!.AttemptId, request, "review-a", default);

        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal(classification, report.FailureClassification);
        Assert.True(report.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.Equal("claimed", retry.Status);
        Assert.Equal(subject.SubjectId, retry.Subject!.SubjectId);
        Assert.NotEqual(first.Attempt.AttemptId, retry.Attempt!.AttemptId);
        Assert.Equal(first.Attempt.AttemptNumber + 1, retry.Attempt.AttemptNumber);
    }

    [Fact]
    public async Task Restart_takeover_raises_review_fence_and_rejects_stale_report()
    {
        using var temp = new TempDirectory();
        var firstStore = Store(temp.Path);
        await firstStore.InitializeAsync();
        await SeedReviewSubjectAsync(firstStore);
        await RegisterReviewerAsync(firstStore, "review-a", "instance-a", "host-a");
        var first = await firstStore.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await RegisterReviewerAsync(restarted, "review-b", "instance-b", "host-b");
        var takeover = await restarted.ClaimReviewAsync(
            new ReviewClaimRequest("review-b", "instance-b"), "review-b", default);

        Assert.Equal(first.Attempt!.AttemptId, takeover.Attempt!.AttemptId);
        Assert.True(takeover.Lease!.Fence > first.Lease!.Fence);
        Assert.NotEqual(first.Lease.ResourceNamespace, takeover.Lease.ResourceNamespace);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.ReportReviewAsync(first.Attempt.AttemptId, PassingReport(first), "review-a", default));
        Assert.Equal("stale-review-fence", stale.Code);

        var accepted = await restarted.ReportReviewAsync(
            takeover.Attempt.AttemptId, PassingReport(takeover), "review-b", default);
        Assert.Equal("Pass", accepted.Outcome);
    }

    [Fact]
    public async Task Restart_registration_re_adopts_active_review_and_accepts_report()
    {
        using var temp = new TempDirectory();
        var firstStore = Store(temp.Path);
        await firstStore.InitializeAsync();
        await SeedReviewSubjectAsync(firstStore);
        await RegisterReviewerAsync(firstStore, "review-a", "instance-a", "host-a");
        var claim = await firstStore.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var registered = await restarted.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "replacement-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                    ReviewCapabilities.VisionReview,
                    ReviewCapabilities.BaselineComparison,
                    ReviewCapabilities.DependencyPreparation,
                ],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        claim.Attempt!.AttemptId,
                        claim.Attempt.TaskId,
                        claim.Lease!.LeaseId,
                        claim.Lease.Fence,
                        LeaseInstanceId: claim.Lease.InstanceId),
                ]),
            "review-a",
            default);

        Assert.Equal("adopted", Assert.Single(registered.AttemptAdoptions!).Status);
        Assert.Equal(1, Assert.Single(
            await restarted.ListRunnerCapabilitySnapshotsAsync(default),
            snapshot => snapshot.RunnerId == "review-a").Telemetry!.ActiveSlots);
        var renewed = await restarted.RenewReviewLeaseAsync(
            claim.Attempt!.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a",
                claim.Lease!.InstanceId,
                claim.Lease.LeaseId,
                claim.Lease.Fence,
                "renew-after-handoff"),
            "review-a",
            default);
        Assert.Equal(claim.Lease.LeaseId, renewed.LeaseId);
        Assert.Equal(claim.Lease.ResourceNamespace, renewed.ResourceNamespace);
        Assert.Equal(claim.Lease.PortBase, renewed.PortBase);
        var accepted = await restarted.ReportReviewAsync(
            claim.Attempt.AttemptId,
            PassingReport(claim),
            "review-a",
            default);
        Assert.Equal("Pass", accepted.Outcome);
    }

    [Fact]
    public async Task Restart_registration_adoption_does_not_shorten_a_longer_handoff_lease()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-07T03:10:00Z"));
        using var temp = new TempDirectory();
        var firstStore = Store(temp.Path, clock);
        await firstStore.InitializeAsync();
        await SeedReviewSubjectAsync(firstStore);
        await RegisterReviewerAsync(firstStore, "review-a", "instance-a", "host-a");
        var claim = await firstStore.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var handoff = await firstStore.RenewReviewLeaseAsync(
            claim.Attempt!.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a",
                "instance-a",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                "handoff-renew",
                RequestedTtlSeconds: 900),
            "review-a",
            default);

        clock.Advance(TimeSpan.FromSeconds(30));
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var registered = await restarted.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "replacement-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        claim.Attempt.AttemptId,
                        claim.Attempt.TaskId,
                        claim.Lease.LeaseId,
                        claim.Lease.Fence,
                        LeaseInstanceId: claim.Lease.InstanceId),
                ],
                AttemptLeaseTtlSeconds: 120),
            "review-a",
            default);

        var adoption = Assert.Single(registered.AttemptAdoptions!);
        Assert.Equal("adopted", adoption.Status);
        Assert.Equal(handoff.ExpiresAt, adoption.ExpiresAt);
    }

    [Fact]
    public async Task Second_restart_counts_a_missing_worker_from_the_original_lease_generation()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var task = await TaskAsync(store, subject.TaskId);

        var firstRestart = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "replacement-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        claim.Attempt!.AttemptId,
                        claim.Attempt.TaskId,
                        claim.Lease!.LeaseId,
                        claim.Lease.Fence,
                        LeaseInstanceId: claim.Lease.InstanceId),
                ]),
            "review-a",
            default);
        Assert.Equal("adopted", Assert.Single(firstRestart.AttemptAdoptions!).Status);
        Assert.Equal(
            0,
            Assert.Single(
                await store.ListRunnerCapabilitySnapshotsAsync(default),
                item => item.RunnerId == "review-a").ReviewsLost);

        var registered = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "successor-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                ActiveAttempts: []),
            "review-a",
            default);

        Assert.Empty(registered.AttemptAdoptions!);
        var snapshot = Assert.Single(
            await store.ListRunnerCapabilitySnapshotsAsync(default),
            item => item.RunnerId == "review-a");
        Assert.Equal(1, snapshot.ReviewsLost);

        var loss = Assert.Single(
            await store.ListAuditAsync(0, default),
            item => item.Action == "review-attempt.lost-on-restart");
        Assert.Equal(claim.Attempt!.AttemptId, loss.TargetId);
        var detail = JsonDocument.Parse(loss.DetailJson).RootElement;
        Assert.Equal(task.TaskKey, detail.GetProperty("taskKey").GetString());
        Assert.Equal("worker-not-reported", detail.GetProperty("status").GetString());
        Assert.Contains("replacement ActiveAttempts", detail.GetProperty("cause").GetString());
        Assert.Contains(claim.Attempt.AttemptId, detail.GetProperty("message").GetString());
        Assert.Contains(task.TaskKey, detail.GetProperty("message").GetString());

        var lossEvent = Assert.Single(
            await store.ListStudioStreamEventsSinceAsync(0, default),
            item => item.Kind == "review-attempt.lost-on-restart");
        Assert.Equal(subject.TaskId, lossEvent.TaskId);
    }

    [Fact]
    public async Task Restart_reclaim_preserves_physical_isolation_replays_and_accepts_the_report()
    {
        using var temp = new TempDirectory();
        var firstStore = Store(temp.Path);
        await firstStore.InitializeAsync();
        await SeedReviewSubjectAsync(firstStore);
        await RegisterReviewerAsync(firstStore, "review-a", "instance-a", "host-a");
        var first = await firstStore.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        await RegisterReviewerAsync(restarted, "review-a", "replacement-instance", "host-a");
        var request = new ReviewReClaimRequest(
            "review-a",
            "replacement-instance",
            first.Lease!.LeaseId,
            first.Lease.Fence,
            "reclaim-after-restart");

        var reclaimed = await restarted.ReClaimReviewAsync(
            first.Attempt!.AttemptId, request, "review-a", default);
        var replay = await restarted.ReClaimReviewAsync(
            first.Attempt.AttemptId, request, "review-a", default);

        Assert.Equal("claimed", reclaimed.Status);
        Assert.Equal(reclaimed, replay);
        Assert.True(reclaimed.Lease!.Fence > first.Lease.Fence);
        Assert.NotEqual(first.Lease.LeaseId, reclaimed.Lease.LeaseId);
        Assert.Equal("replacement-instance", reclaimed.Lease.InstanceId);
        Assert.Equal(first.Lease.ResourceNamespace, reclaimed.Lease.ResourceNamespace);
        Assert.Equal(first.Lease.PortBase, reclaimed.Lease.PortBase);
        var mismatch = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.ReClaimReviewAsync(
                first.Attempt.AttemptId,
                request with { RequestedTtlSeconds = request.RequestedTtlSeconds + 1 },
                "review-a",
                default));
        Assert.Equal("idempotency-conflict", mismatch.Code);

        // A second server restart proves that the preserved physical facts are
        // durable, rather than being reconstructed from the new fence.
        var secondRestart = Store(temp.Path);
        await secondRestart.InitializeAsync();
        var registered = await secondRestart.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "replacement-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                    ReviewCapabilities.VisionReview,
                    ReviewCapabilities.BaselineComparison,
                    ReviewCapabilities.DependencyPreparation,
                ],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        reclaimed.Attempt!.AttemptId,
                        reclaimed.Attempt.TaskId,
                        reclaimed.Lease.LeaseId,
                        reclaimed.Lease.Fence,
                        LeaseInstanceId: reclaimed.Lease.InstanceId),
                ]),
            "review-a",
            default);
        Assert.Equal("adopted", Assert.Single(registered.AttemptAdoptions!).Status);

        var report = await secondRestart.ReportReviewAsync(
            reclaimed.Attempt.AttemptId,
            PassingReport(reclaimed with { Subject = first.Subject }),
            "review-a",
            default);
        Assert.Equal("Pass", report.Outcome);
        Assert.Single(
            await secondRestart.ListAuditAsync(0, default),
            record => record.Action == "review.re-claimed");
    }

    [Fact]
    public async Task Ambiguous_reclaim_recovery_reports_success_without_a_false_restart_loss()
    {
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-07T03:10:00Z"));
        using var temp = new TempDirectory();
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var original = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        var firstRestart = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "replacement-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        original.Attempt!.AttemptId,
                        original.Attempt.TaskId,
                        original.Lease!.LeaseId,
                        original.Lease.Fence,
                        LeaseInstanceId: original.Lease.InstanceId),
                ]),
            "review-a",
            default);
        Assert.Equal("adopted", Assert.Single(firstRestart.AttemptAdoptions!).Status);

        clock.Advance(TimeSpan.FromMinutes(11));
        var request = new ReviewReClaimRequest(
            "review-a",
            "replacement-instance",
            original.Lease!.LeaseId,
            original.Lease.Fence,
            "ambiguous-reclaim");
        var committed = await store.ReClaimReviewAsync(
            original.Attempt!.AttemptId,
            request,
            "review-a",
            default);

        // The response is lost. The next daemon still reports the old durable
        // slot while registration observes the already-advanced server fence.
        var secondRestart = await store.RegisterRunnerAsync(
            "review-a",
            new RegisterRunnerRequest(
                "review-a",
                "host-a",
                "successor-instance",
                "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.ReviewExecutor],
                ActiveAttempts:
                [
                    new RunnerActiveAttempt(
                        RunnerAttemptKinds.Review,
                        original.Attempt.AttemptId,
                        original.Attempt.TaskId,
                        original.Lease.LeaseId,
                        original.Lease.Fence,
                        LeaseInstanceId: original.Lease.InstanceId),
                ]),
            "review-a",
            default);
        Assert.Equal("stale-authority", Assert.Single(secondRestart.AttemptAdoptions!).Status);

        var replay = await store.ReClaimReviewAsync(
            original.Attempt.AttemptId,
            request,
            "review-a",
            default);
        Assert.Equal(committed, replay);
        var report = await store.ReportReviewAsync(
            replay.Attempt!.AttemptId,
            PassingReport(replay with { Subject = original.Subject }),
            "review-a",
            default);
        Assert.Equal("Pass", report.Outcome);

        var snapshot = Assert.Single(
            await store.ListRunnerCapabilitySnapshotsAsync(default),
            item => item.RunnerId == "review-a");
        Assert.Equal(0, snapshot.ReviewsLost);
        var audits = await store.ListAuditAsync(0, default);
        Assert.DoesNotContain(
            audits,
            item => item.Action == "review-attempt.lost-on-restart");
        var latestRestart = audits
            .Where(item => item.Action == "review-daemon.restarted")
            .OrderByDescending(item => item.OccurredAt)
            .First();
        var detail = JsonDocument.Parse(latestRestart.DetailJson).RootElement;
        Assert.Equal(0, detail.GetProperty("reviewsLost").GetInt32());
        Assert.Equal(
            "stale-authority",
            detail.GetProperty("attempts")[0].GetProperty("status").GetString());
    }

    [Fact]
    public async Task Reclaim_never_steals_an_unexpired_live_review_lease()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await RegisterReviewerAsync(store, "review-a", "replacement-instance", "host-a");

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReClaimReviewAsync(
                claim.Attempt!.AttemptId,
                new ReviewReClaimRequest(
                    "review-a",
                    "replacement-instance",
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    "must-not-steal"),
                "review-a",
                default));

        Assert.Equal("review-lease-active", conflict.Code);
        var current = await store.GetReviewAttemptAsync(claim.Attempt!.AttemptId, default);
        Assert.Equal(claim.Lease!.Fence, current!.Fence);
    }

    [Fact]
    public async Task Reclaim_repairs_an_expired_matching_lease()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await SetReviewLeaseExpiryAsync(store, claim.Attempt!.AttemptId, DateTime.UtcNow.AddMinutes(-1));
        await RegisterReviewerAsync(store, "review-a", "replacement-instance", "host-a");

        var reclaimed = await store.ReClaimReviewAsync(
            claim.Attempt.AttemptId,
            new ReviewReClaimRequest(
                "review-a",
                "replacement-instance",
                claim.Lease!.LeaseId,
                claim.Lease.Fence,
                "reclaim-expired"),
            "review-a",
            default);

        Assert.True(reclaimed.Lease!.Fence > claim.Lease.Fence);
        Assert.Equal(claim.Lease.ResourceNamespace, reclaimed.Lease.ResourceNamespace);
        Assert.Equal(claim.Lease.PortBase, reclaimed.Lease.PortBase);
    }

    [Fact]
    public async Task Uncommitted_stale_generation_reclaim_can_rotate_to_the_current_instance()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await SetReviewLeaseExpiryAsync(
            store,
            claim.Attempt!.AttemptId,
            DateTime.UtcNow.AddMinutes(-1));
        await RegisterReviewerAsync(store, "review-a", "replacement-instance", "host-a");
        await RegisterReviewerAsync(store, "review-a", "successor-instance", "host-a");

        var staleRequest = new ReviewReClaimRequest(
            "review-a",
            "replacement-instance",
            claim.Lease!.LeaseId,
            claim.Lease.Fence,
            "uncommitted-replacement-request");
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReClaimReviewAsync(
                claim.Attempt.AttemptId,
                staleRequest,
                "review-a",
                default));
        Assert.Equal("runner-instance-stale", stale.Code);
        Assert.Equal(
            claim.Lease.Fence,
            (await store.GetReviewAttemptAsync(claim.Attempt.AttemptId, default))!.Fence);

        var reclaimed = await store.ReClaimReviewAsync(
            claim.Attempt.AttemptId,
            staleRequest with
            {
                InstanceId = "successor-instance",
                IdempotencyKey = "current-successor-request",
            },
            "review-a",
            default);
        Assert.True(reclaimed.Lease!.Fence > claim.Lease.Fence);
        Assert.Equal("successor-instance", reclaimed.Lease.InstanceId);
        Assert.Equal(claim.Lease.ResourceNamespace, reclaimed.Lease.ResourceNamespace);
        Assert.Equal(claim.Lease.PortBase, reclaimed.Lease.PortBase);
    }

    [Fact]
    public async Task Schema_upgrade_backfills_and_persists_review_physical_isolation()
    {
        using var temp = new TempDirectory();
        var first = Store(temp.Path);
        await first.InitializeAsync();
        await SeedReviewSubjectAsync(first);
        await RegisterReviewerAsync(first, "review-a", "instance-a", "host-a");
        var claim = await first.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        await using (var connection = new SqliteConnection(
                         $"Data Source={first.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                ALTER TABLE review_attempts DROP COLUMN resource_namespace;
                DELETE FROM schema_migrations WHERE version > 15;
                UPDATE meta SET value = '15' WHERE key = 'schema_version';
                """;
            await command.ExecuteNonQueryAsync();
        }

        var upgraded = Store(temp.Path);
        await upgraded.InitializeAsync();
        await using var upgradedConnection = new SqliteConnection(
            $"Data Source={upgraded.DatabasePath};Pooling=False");
        await upgradedConnection.OpenAsync();
        await using var read = upgradedConnection.CreateCommand();
        read.CommandText = """
            SELECT resource_namespace, port_base
              FROM review_attempts
             WHERE id = $attempt;
            """;
        read.Parameters.AddWithValue("$attempt", claim.Attempt!.AttemptId);
        await using var reader = await read.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        Assert.Equal(claim.Lease!.ResourceNamespace, reader.GetString(0));
        Assert.Equal(claim.Lease.PortBase, reader.GetInt32(1));
    }

    [Fact]
    public async Task Reclaim_http_endpoint_requires_a_key_and_replays_the_durable_response()
    {
        using var temp = new TempDirectory();
        ReviewClaimResponse claim;
        await using (var firstFactory = new ReviewApiFactory(temp.Path))
        {
            var firstStore = firstFactory.Services.GetRequiredService<TaskServerStore>();
            await SeedReviewSubjectAsync(firstStore);
            await RegisterReviewerAsync(firstStore, "review-http", "instance-a", "host-a");
            claim = await firstStore.ClaimReviewAsync(
                new ReviewClaimRequest("review-http", "instance-a"), "review-http", default);
        }

        await using var factory = new ReviewApiFactory(temp.Path);
        var store = factory.Services.GetRequiredService<TaskServerStore>();
        await RegisterReviewerAsync(store, "review-http", "replacement-instance", "host-a");
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", ReviewHttpToken);
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName, TaskServerProtocol.Current.ToString());
        client.DefaultRequestHeaders.Add("X-Client-Id", "review-http");
        var path = $"/api/v1/reviews/attempts/{claim.Attempt!.AttemptId}/reclaim";
        var request = new ReviewReClaimRequest(
            "review-http",
            "replacement-instance",
            claim.Lease!.LeaseId,
            claim.Lease.Fence,
            "http-reclaim");

        var invalid = await client.PostAsJsonAsync(path, request with { IdempotencyKey = "" });
        Assert.Equal(HttpStatusCode.BadRequest, invalid.StatusCode);
        Assert.Equal("invalid-request", (await invalid.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var response = await client.PostAsJsonAsync(path, request);
        response.EnsureSuccessStatusCode();
        var reclaimed = (await response.Content.ReadFromJsonAsync<ReviewClaimResponse>())!;
        await RegisterReviewerAsync(store, "review-http", "successor-instance", "host-a");

        // The response was committed by replacement-instance. Once a newer
        // daemon generation is current, only the authenticated executor's
        // exact delivery may still replay it.
        var replayResponse = await client.PostAsJsonAsync(path, request);
        replayResponse.EnsureSuccessStatusCode();
        var replay = (await replayResponse.Content.ReadFromJsonAsync<ReviewClaimResponse>())!;
        Assert.Equal(reclaimed, replay);
        Assert.Equal(claim.Lease.ResourceNamespace, reclaimed.Lease!.ResourceNamespace);
        Assert.Equal(claim.Lease.PortBase, reclaimed.Lease.PortBase);

        var mismatch = await client.PostAsJsonAsync(
            path,
            request with { RequestedTtlSeconds = request.RequestedTtlSeconds + 1 });
        Assert.Equal(HttpStatusCode.Conflict, mismatch.StatusCode);
        Assert.Equal("idempotency-conflict", (await mismatch.Content.ReadFromJsonAsync<ApiError>())!.Code);

        var staleMutation = await client.PostAsJsonAsync(
            path,
            request with { IdempotencyKey = "http-reclaim-new-mutation" });
        Assert.Equal(HttpStatusCode.Conflict, staleMutation.StatusCode);
        Assert.Equal(
            "runner-instance-stale",
            (await staleMutation.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    [Fact]
    public async Task Coding_and_review_capabilities_require_separate_registered_identities()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RegisterRunnerAsync(
                "mixed-executor",
                new RegisterRunnerRequest(
                    "mixed-executor", "host-a", "instance-a", "1.0.0",
                    TaskServerProtocol.Current,
                    [ReviewCapabilities.CodingExecutor, ReviewCapabilities.ReviewExecutor]),
                "test",
                default));

        Assert.Equal("runner-role-conflict", conflict.Code);

        await store.RegisterRunnerAsync(
            "coding-only",
            new RegisterRunnerRequest(
                "coding-only", "host-a", "coding-instance", "1.0.0",
                TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test",
            default);

        var capabilityReset = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RegisterRunnerAsync(
                "coding-only",
                new RegisterRunnerRequest(
                    "coding-only", "host-a", "reset-instance", "1.0.0",
                    TaskServerProtocol.Current,
                    []),
                "test",
                default));
        Assert.Equal("runner-role-conflict", capabilityReset.Code);

        var roleSwap = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RegisterRunnerAsync(
                "coding-only",
                new RegisterRunnerRequest(
                    "coding-only", "host-a", "review-instance", "1.0.0",
                    TaskServerProtocol.Current,
                    [ReviewCapabilities.ReviewExecutor]),
                "test",
                default));
        Assert.Equal("runner-role-conflict", roleSwap.Code);

        await store.RegisterRunnerAsync(
            "capability-less",
            new RegisterRunnerRequest(
                "capability-less", "host-a", "capability-less-instance", "1.0.0",
                TaskServerProtocol.Current,
                []),
            "test",
            default);
        var codingClaim = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ClaimAsync(
                new ClaimRequest("capability-less", "capability-less-instance"),
                "test",
                default));
        Assert.Equal("coding-capability-required", codingClaim.Code);
    }

    [Fact]
    public async Task Tool_unavailable_is_retained_as_a_typed_infrastructure_outcome()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var unavailable = PassingReport(claim) with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "ToolUnavailable",
            Summary = "The declared review tool could not be started.",
            Commands = [],
            Artifacts = [],
            Verdicts = [],
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId, unavailable, "review-a", default);

        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("ToolUnavailable", report.FailureClassification);
        Assert.True(report.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Theory]
    [InlineData(127, false)]
    [InlineData(1, true)]
    public async Task Missing_verification_toolchain_cannot_be_accepted_as_a_product_failure(
        int exitCode,
        bool angularModuleEvidence)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(claim);
        var toolchainFailure = Encoding.UTF8.GetBytes(
            "Cannot find module '/review/frontend/node_modules/@angular/cli/bin/ng.js'\n");
        var toolchainFailureDigest = Convert.ToHexString(SHA256.HashData(toolchainFailure))
            .ToLowerInvariant();
        request = request with
        {
            Outcome = "ProductFailure",
            FailureClassification = "ReviewFinding",
            Commands = request.Commands
                .Select((command, index) => index == 0
                    ? command with
                    {
                        ExitCode = exitCode,
                        StderrSha256 = angularModuleEvidence
                            ? toolchainFailureDigest
                            : command.StderrSha256,
                    }
                    : command)
                .ToArray(),
            Artifacts = angularModuleEvidence
                ? request.Artifacts.Append(new ReviewArtifactEvidenceDto(
                    "candidate.verify.stderr.log",
                    "text/plain; charset=utf-8",
                    toolchainFailureDigest,
                    toolchainFailure.LongLength,
                    Convert.ToBase64String(toolchainFailure))).ToArray()
                : request.Artifacts,
            Verdicts = request.Verdicts
                .Select((verdict, index) => index == 0
                    ? verdict with { Status = "block", Classification = "CommandFailed" }
                    : verdict)
                .ToArray(),
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId, request, "review-a", default);

        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("ToolUnavailable", report.FailureClassification);
        Assert.True(report.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Fact]
    public async Task Preparation_failure_preserves_full_command_evidence_and_retries_as_infrastructure()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var preparation = new ReviewPreparationCommandDto(
            "prepare-dependencies",
            "/bin/bash",
            ["-lc", "npm --prefix frontend ci"],
            TimeoutSeconds: 120,
            DependencyScopes:
            [
                new ReviewDependencyScopeDto("frontend", ["package-lock.json"]),
            ]);
        var subject = await SeedReviewSubjectAsync(
            store,
            plan: Plan() with
            {
                Preparation = [preparation],
                PreserveGlobs = ["frontend/node_modules", "frontend/.angular"],
            });
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var template = PassingReport(claim);
        var stdout = Encoding.UTF8.GetBytes("complete preparation stdout\n");
        var stderr = Encoding.UTF8.GetBytes("npm: dependency unavailable\n");
        var stdoutDigest = Convert.ToHexString(SHA256.HashData(stdout)).ToLowerInvariant();
        var stderrDigest = Convert.ToHexString(SHA256.HashData(stderr)).ToLowerInvariant();
        var failed = new ReviewCommandEvidenceDto(
            preparation.StepId,
            "preparation",
            preparation.FileName,
            preparation.Arguments,
            ResultSha,
            ResultSha,
            TreeSha,
            DateTime.UtcNow.AddSeconds(-2),
            DateTime.UtcNow,
            9,
            null,
            stdoutDigest,
            stderrDigest,
            Phase: "preparation",
            WorkspaceRole: "candidate",
            Budget: new ReviewCommandBudgetEvidenceDto("review-command", 120_000, 2_000, false));
        var unavailable = template with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "PreparationFailed",
            Summary = "Preparation command 'prepare-dependencies' exited 9; budget=120000ms.",
            Commands = [failed],
            Artifacts =
            [
                new ReviewArtifactEvidenceDto(
                    "candidate.prepare-dependencies.stdout.log",
                    "text/plain; charset=utf-8",
                    stdoutDigest,
                    stdout.LongLength,
                    Convert.ToBase64String(stdout)),
                new ReviewArtifactEvidenceDto(
                    "candidate.prepare-dependencies.stderr.log",
                    "text/plain; charset=utf-8",
                    stderrDigest,
                    stderr.LongLength,
                    Convert.ToBase64String(stderr)),
            ],
            Verdicts = [],
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId, unavailable, "review-a", default);

        Assert.Equal("ReviewInfra", report.Outcome);
        Assert.Equal("PreparationFailed", report.FailureClassification);
        Assert.True(report.RetryScheduled);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Fact]
    public async Task Report_with_shared_cache_or_unbound_command_tree_is_rejected_as_review_infrastructure()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var sharedCache = PassingReport(first);
        sharedCache = sharedCache with
        {
            Environment = sharedCache.Environment with
            {
                Isolation = new Dictionary<string, string>(sharedCache.Environment.Isolation)
                {
                    ["cache"] = $"{sharedCache.Environment.Isolation["workspace"]}/../shared-cache",
                },
            },
        };

        var containment = await store.ReportReviewAsync(
            first.Attempt!.AttemptId, sharedCache, "review-a", default);
        Assert.Equal("ReviewInfra", containment.Outcome);
        Assert.Equal("ContainmentMismatch", containment.FailureClassification);

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var wrongTree = PassingReport(retry);
        wrongTree = wrongTree with
        {
            Commands = wrongTree.Commands
                .Select(command => command with { TreeBefore = new string('d', 40) })
                .ToArray(),
        };

        var subjectMismatch = await store.ReportReviewAsync(
            retry.Attempt!.AttemptId, wrongTree, "review-a", default);
        Assert.Equal("ReviewInfra", subjectMismatch.Outcome);
        Assert.Equal("CommandSubjectMismatch", subjectMismatch.FailureClassification);
    }

    [Theory]
    [InlineData(false, "Pass")]
    [InlineData(true, "ProductFailure")]
    public async Task Baseline_evidence_allows_only_pre_existing_nonzero_test_commands(
        bool hasNewFailure,
        string expectedOutcome)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "dotnet",
                ["test"],
                CompareToBaseline: true)],
            ["build-tests"],
            IntegrationRef: "refs/heads/develop");
        await SeedReviewSubjectAsync(store, plan: plan);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(claim);
        request = request with
        {
            Commands = request.Commands.Select(command => command with
            {
                ExitCode = 1,
                BaselineSha = new string('c', 40),
                NewFailures = hasNewFailure ? ["Product.NewFailure"] : [],
                PreExistingFailures = ["Product.ExistingFailure"],
                RetryPerformed = hasNewFailure,
            }).ToArray(),
            Verdicts =
            [
                new ReviewVerdictDto(
                    "build-tests",
                    hasNewFailure ? "block" : "pass",
                    hasNewFailure ? "NewTestFailures" : "BaselineCompared",
                    hasNewFailure
                        ? "1 new failure: Product.NewFailure; 1 pre-existing failure."
                        : "0 new failures; 1 pre-existing failure.")
            ],
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            request,
            "review-a",
            default);

        Assert.Equal(expectedOutcome, report.Outcome);
    }

    [Fact]
    public async Task Baseline_evidence_treats_a_nonreproduced_review_flaky_failure_as_quarantine_not_product_failure()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "verify-2",
                "build-tests",
                "dotnet",
                ["test"],
                CompareToBaseline: true)],
            ["build-tests"],
            IntegrationRef: "refs/heads/develop");
        await SeedReviewSubjectAsync(store, plan: plan);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(claim);
        request = request with
        {
            Commands = request.Commands.Select(command => command with
            {
                ExitCode = 0,
                BaselineSha = new string('c', 40),
                NewFailures = [],
                PreExistingFailures = [],
                RetryPerformed = true,
                FlakyQuarantinedFailures = ["Product.ProcessTiming"],
            }).ToArray(),
            Verdicts =
            [
                new ReviewVerdictDto(
                    "build-tests",
                    "pass",
                    "FlakyQuarantine",
                    "1 flaky quarantined failure: Product.ProcessTiming.")
            ],
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            request,
            "review-a",
            default);

        Assert.Equal("Pass", report.Outcome);
        Assert.False(report.RetryScheduled);
    }

    [Fact]
    public async Task Semantic_block_without_citation_is_admitted_as_non_escalating_concern()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var plan = new ReviewPlanDto(
            [new ReviewCommandDto(
                "aspect-documentation-impact",
                "documentation-impact",
                "codex",
                [],
                ExecutionKind: ReviewCommandKinds.AgentAspect,
                Prompt: "Review the delivery documentation.",
                CliType: "codex",
                Model: "gpt-5.4-mini")],
            ["documentation-impact"],
            IntegrationRef: "refs/heads/develop");
        await SeedReviewSubjectAsync(store, plan: plan);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var request = PassingReport(claim) with
        {
            Outcome = "ProductFailure",
            FailureClassification = "ReviewFinding",
            Verdicts =
            [
                new ReviewVerdictDto(
                    "documentation-impact",
                    "block",
                    "RemoteAspectVerdict",
                    "Required documentation was not evidenced.")
            ],
        };

        var report = await store.ReportReviewAsync(
            claim.Attempt!.AttemptId,
            request,
            "review-a",
            default);

        Assert.Equal("Pass", report.Outcome);
        Assert.Equal(ReviewVerdictCitationPolicy.BlockWithoutCitation, report.FailureClassification);
        Assert.False(report.RetryScheduled);
    }

    [Fact]
    public async Task Stale_review_subject_cannot_overwrite_a_newer_task_lifecycle()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var task = await TaskAsync(store, subject.TaskId);
        await store.UpdateTaskAsync(
            task.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, "2-ready", task.Version),
            "operator",
            default);

        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportReviewAsync(
                claim.Attempt!.AttemptId, PassingReport(claim), "review-a", default));

        Assert.Equal("review-subject-not-current", stale.Code);
        Assert.Equal("2-ready", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Theory]
    [InlineData("6-completed")]
    [InlineData("7-archive")]
    public async Task Terminal_task_transition_supersedes_open_review_authority(string terminalLane)
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var task = await TaskAsync(store, subject.TaskId);

        await store.UpdateTaskAsync(
            task.ProjectId,
            task.TaskId,
            new UpdateTaskRequest(null, null, terminalLane, task.Version),
            "operator",
            default);

        var attempt = await store.GetReviewAttemptAsync(claim.Attempt!.AttemptId, default);
        Assert.Equal("superseded", attempt!.Status);
        Assert.Equal("Superseded", attempt.Outcome);
        var next = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        Assert.Equal("empty", next.Status);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "review.superseded"
                      && record.TargetId == claim.Attempt.AttemptId
                      && record.DetailJson.Contains("\"authority\":\"Superseded\"", StringComparison.Ordinal)
                      && record.DetailJson.Contains("\"source\":\"lane-transition\"", StringComparison.Ordinal)
                      && record.DetailJson.Contains(terminalLane, StringComparison.Ordinal));
    }

    [Fact]
    public async Task Claim_guard_supersedes_a_stale_terminal_attempt_instead_of_returning_it()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        var attemptId = await ReviewAttemptIdAsync(store, subject.TaskId);
        await SetTaskStateOutsideAuthorityAsync(store, subject.TaskId, "6-completed");
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        Assert.Equal("empty", claim.Status);
        var attempt = await store.GetReviewAttemptAsync(attemptId, default);
        Assert.Equal("superseded", attempt!.Status);
        Assert.Equal("Superseded", attempt.Outcome);
        Assert.Contains(
            await store.ListAuditAsync(0, default),
            record => record.Action == "review.superseded"
                      && record.TargetId == attemptId
                      && record.DetailJson.Contains("\"source\":\"claim-guard\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Boot_sweep_supersedes_terminal_attempts_left_by_an_older_server()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        var attemptId = await ReviewAttemptIdAsync(store, subject.TaskId);
        await SetTaskStateOutsideAuthorityAsync(store, subject.TaskId, "7-archive");

        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();

        var attempt = await restarted.GetReviewAttemptAsync(attemptId, default);
        Assert.Equal("superseded", attempt!.Status);
        Assert.Equal("Superseded", attempt.Outcome);
        Assert.Contains(
            await restarted.ListAuditAsync(0, default),
            record => record.Action == "review.superseded"
                      && record.TargetId == attemptId
                      && record.DetailJson.Contains("\"source\":\"boot-sweep\"", StringComparison.Ordinal)
                      && record.DetailJson.Contains("\"lane\":\"7-archive\"", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Source_bundle_subject_requires_its_content_digest()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);

        var invalid = new CreateReviewSubjectRequest(
            subject.TaskId,
            subject.SourceRunId,
            subject.RepositoryId,
            null,
            subject.ExpectedResultSha,
            null,
            "artifact-without-digest",
            null,
            subject.CodingHostId,
            subject.ReviewPolicyHash,
            subject.Plan,
            "bundle-without-digest");

        await Assert.ThrowsAsync<ArgumentException>(
            () => store.CreateReviewSubjectAsync(invalid, "test", default));
    }

    [Fact]
    public async Task Three_parallel_reviews_of_one_repository_have_independent_subjects_fences_and_namespaces()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        for (var i = 0; i < 3; i++)
            await SeedReviewSubjectAsync(store, $"Task {i}");
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var claims = new List<ReviewClaimResponse>();
        for (var i = 0; i < 3; i++)
            claims.Add(await store.ClaimReviewAsync(
                new ReviewClaimRequest("review-a", "instance-a", AvailableSlots: 3 - i),
                "review-a",
                default));

        Assert.Equal(3, claims.Select(claim => claim.Subject!.SubjectId).Distinct().Count());
        Assert.Equal(3, claims.Select(claim => claim.Attempt!.AttemptId).Distinct().Count());
        Assert.Equal(3, claims.Select(claim => claim.Lease!.ResourceNamespace).Distinct().Count());
        Assert.All(claims, claim => Assert.Equal(RepositoryId, claim.Subject!.RepositoryId));
    }

    [Fact]
    public async Task Different_host_policy_refuses_same_failure_domain_but_allows_another_review_host()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store, policyDifferentHost: true, codingHost: "host-a");
        await RegisterReviewerAsync(store, "same-host-review", "instance-a", "host-a");
        await RegisterReviewerAsync(store, "other-host-review", "instance-b", "host-b");

        var refused = await store.ClaimReviewAsync(
            new ReviewClaimRequest("same-host-review", "instance-a"), "same-host-review", default);
        var accepted = await store.ClaimReviewAsync(
            new ReviewClaimRequest("other-host-review", "instance-b"), "other-host-review", default);

        Assert.Equal("empty", refused.Status);
        Assert.Equal("claimed", accepted.Status);
        Assert.Equal("host-b", accepted.Lease!.HostId);
    }

    [Fact]
    public async Task Review_infrastructure_failure_never_creates_a_coding_attempt_or_returns_to_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var infra = PassingReport(claim) with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "SnapshotUnavailable",
        };

        await store.ReportReviewAsync(claim.Attempt!.AttemptId, infra, "review-a", default);

        await using var connection = new SqliteConnection($"Data Source={store.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT count(*) FROM runs WHERE task_id = $task;";
        command.Parameters.AddWithValue("$task", subject.TaskId);
        Assert.Equal(1L, (long)(await command.ExecuteScalarAsync())!);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
    }

    [Fact]
    public async Task Remote_task_done_review_infra_retry_then_all_aspects_pass_reaches_human_review_without_ready()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");

        var first = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var unavailable = PassingReport(first) with
        {
            Outcome = "ReviewInfra",
            FailureClassification = "SnapshotUnavailable",
        };
        var infra = await store.ReportReviewAsync(
            first.Attempt!.AttemptId, unavailable, "review-a", default);
        Assert.Equal("4-auto-review", infra.TaskState);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);

        var retry = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        var passed = await store.ReportReviewAsync(
            retry.Attempt!.AttemptId, PassingReport(retry), "review-a", default);

        Assert.Equal("4-auto-review", passed.TaskState);
        var cleanup = await store.CleanupReviewAsync(
            retry.Attempt.AttemptId,
            new ReviewCleanupRequest(
                "review-a", "instance-a", retry.Lease!.LeaseId, retry.Lease.Fence,
                "cleanup-passing-retry", true),
            "review-a",
            default);
        Assert.Equal("cleaned", cleanup.Status);
        Assert.Equal("4-auto-review", (await TaskAsync(store, subject.TaskId)).State);
        var orchestration = Assert.Single(await store.ListOrchestrationRunsAsync(
            null, "pending", default));
        await CompletePassingOrchestrationAsync(store, orchestration);
        Assert.Equal("5-human-review", (await TaskAsync(store, subject.TaskId)).State);
        Assert.Equal(8, PassingReport(retry).Verdicts.Count);
        Assert.Equal(
            new[]
            {
                "artifacts",
                "build-tests",
                "code-quality",
                "completion",
                "documentation",
                "evidence",
                "requirements",
                "visual",
            },
            PassingReport(retry).Verdicts.Select(verdict => verdict.Aspect).Order().ToArray());
        Assert.DoesNotContain(
            await store.ListAuditAsync(0, default),
            record => record.Action == "task.updated"
                      && record.DetailJson.Contains("2-ready", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Draining_allows_review_renew_report_and_cleanup_but_blocks_shutdown_while_authority_is_active()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var claim = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        await store.ChangeModeAsync(
            new ChangeModeRequest(TaskServerMode.Draining, "review drain test"),
            "operator",
            default);

        var renewed = await store.RenewReviewLeaseAsync(
            claim.Attempt!.AttemptId,
            new ReviewLeaseRenewRequest(
                "review-a", "instance-a", claim.Lease!.LeaseId, claim.Lease.Fence,
                "renew-during-drain"),
            "review-a",
            default);
        Assert.True(renewed.ExpiresAt >= claim.Lease.ExpiresAt);

        var admission = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ClaimReviewAsync(
                new ReviewClaimRequest("review-a", "instance-a"), "review-a", default));
        Assert.Equal("admission-closed", admission.Code);

        var deferred = await store.PrepareShutdownAsync(
            new PrepareShutdownRequest("review drain test"), "operator", default);
        Assert.False(deferred.SafeToStop);
        Assert.Equal(1, deferred.UnresolvedAttempts);

        await store.ReportReviewAsync(
            claim.Attempt.AttemptId, PassingReport(claim), "review-a", default);
        await store.CleanupReviewAsync(
            claim.Attempt.AttemptId,
            new ReviewCleanupRequest(
                "review-a", "instance-a", claim.Lease.LeaseId, claim.Lease.Fence,
                "cleanup-during-drain", true),
            "review-a",
            default);

        var prepared = await store.PrepareShutdownAsync(
            new PrepareShutdownRequest("review drain test"), "operator", default);
        Assert.True(prepared.SafeToStop);
        Assert.Equal(TaskServerMode.Maintenance, prepared.Mode);
    }

    [Fact]
    public async Task Restore_is_blocked_while_review_attempt_authority_is_active()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var backup = await store.CreateBackupAsync(new BackupRequest("before-review-claim"), "operator", default);
        _ = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);
        await store.ChangeModeAsync(
            new ChangeModeRequest(TaskServerMode.Maintenance, "restore review authority test"),
            "operator",
            default);

        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.RestoreBackupAsync(new RestoreRequest(backup.BackupId), "operator", default));

        Assert.Equal("attempt-authority-unresolved", conflict.Code);
    }

    [Fact]
    public async Task Integrity_digest_includes_review_attempt_and_fence_state()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        await SeedReviewSubjectAsync(store);
        await RegisterReviewerAsync(store, "review-a", "instance-a", "host-a");
        var beforeClaim = await store.ComputeIntegrityDigestAsync(default);

        _ = await store.ClaimReviewAsync(
            new ReviewClaimRequest("review-a", "instance-a"), "review-a", default);

        Assert.NotEqual(beforeClaim, await store.ComputeIntegrityDigestAsync(default));
    }

    private static TaskServerStore Store(string dataDirectory, TimeProvider? timeProvider = null)
        => new(
            Options.Create(new TaskServerOptions { DataDirectory = dataDirectory }),
            timeProvider ?? TimeProvider.System);

    private static async Task<ReviewSubjectDto> SeedReviewSubjectAsync(
        TaskServerStore store,
        string title = "Task",
        bool policyDifferentHost = false,
        string codingHost = "coding-host",
        ReviewPlanDto? plan = null)
    {
        var workspaces = await store.ListWorkspacesAsync(default);
        var workspace = workspaces.FirstOrDefault()
            ?? await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var projects = await store.ListProjectsAsync(workspace.WorkspaceId, default);
        var project = projects.FirstOrDefault()
            ?? await store.CreateProjectAsync(
                new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(
            project.ProjectId, new CreateTaskRequest(title, "Do the work", "2-ready"), "test", default);
        var codingId = "coding-" + task.TaskId;
        var codingInstance = "instance-" + task.TaskId;
        await store.RegisterRunnerAsync(
            codingId,
            new RegisterRunnerRequest(
                codingId, codingHost, codingInstance, "1.0.0", TaskServerProtocol.Current,
                [ReviewCapabilities.CodingExecutor]),
            "test",
            default);
        var coding = await store.ClaimAsync(new ClaimRequest(codingId, codingInstance), codingId, default);
        var resultRef = FencedGitRefs.ImmutableResult(
            coding.Run!.RunId,
            coding.Lease!.Fence,
            ResultSha);
        var envelope = new ImmutableResultEnvelope(
            RepositoryId,
            coding.Run.RunId,
            new string('1', 40),
            ResultSha,
            resultRef,
            null,
            new string('2', 64),
            RepositoryUrl: RepositoryUrl);
        var envelopeDigest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(
            coding.Run.RunId,
            new ResultHandoffRequest(
                codingId,
                codingInstance,
                coding.Lease!.LeaseId,
                coding.Lease.Fence,
                1,
                $"handoff:{coding.Run.RunId}:{envelopeDigest}",
                envelopeDigest,
                envelope),
            codingId,
            default);
        await store.CompleteRunAsync(
            coding.Run.RunId,
            new CompleteRunRequest(
                codingId,
                codingInstance,
                coding.Lease.LeaseId,
                coding.Lease.Fence,
                "success",
                "done",
                envelopeDigest,
                $"completion:{coding.Run.RunId}:{envelopeDigest}",
                2),
            codingId,
            default);
        return await store.CreateReviewSubjectAsync(
            new CreateReviewSubjectRequest(
                task.TaskId, coding.Run.RunId, RepositoryId, RepositoryUrl, ResultSha,
                resultRef, null, null, codingHost,
                "policy-v1", plan ?? Plan(policyDifferentHost), $"subject-{task.TaskId}"),
            "orchestrator",
            default);
    }

    private static ReviewPlanDto Plan(bool differentHost = false)
    {
        string[] aspects =
        [
            "completion",
            "build-tests",
            "requirements",
            "code-quality",
            "documentation",
            "evidence",
            "artifacts",
            "visual",
        ];
        return new ReviewPlanDto(
            aspects.Select(aspect => new ReviewCommandDto(
                $"step-{aspect}", aspect, "review-tool", [aspect])).ToArray(),
            aspects,
            RequiresVisualReview: true,
            RequireDifferentHostFailureDomain: differentHost);
    }

    private static async Task RegisterReviewerAsync(
        TaskServerStore store,
        string id,
        string instance,
        string host)
        => await store.RegisterRunnerAsync(
            id,
            new RegisterRunnerRequest(
                id, host, instance, "1.0.0", TaskServerProtocol.Current,
                [
                    ReviewCapabilities.ReviewExecutor,
                    ReviewCapabilities.GitMaterialization,
                    ReviewCapabilities.SemanticReview,
                    ReviewCapabilities.VisionReview,
                    ReviewCapabilities.BaselineComparison,
                    ReviewCapabilities.DependencyPreparation,
                    CapabilityProtocol.CliExecution("codex"),
                    CapabilityProtocol.ProviderAuthentication("codex"),
                ]),
            id,
            default);

    private static ReviewReportRequest PassingReport(ReviewClaimResponse claim)
    {
        var subject = claim.Subject!;
        var lease = claim.Lease!;
        var workspacePath = $"/review/{lease.ResourceNamespace}";
        var commands = subject.Plan.Commands.Select(command => new ReviewCommandEvidenceDto(
            command.StepId, command.Aspect, command.FileName, command.Arguments,
            ResultSha, ResultSha, TreeSha,
            DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 0, null,
            new string('a', 64), new string('b', 64),
            ExecutionKind: command.ExecutionKind,
            ExecutionLocation: "remote",
            ExecutorId: lease.ExecutorId,
            HostId: lease.HostId,
            AttemptId: claim.Attempt!.AttemptId,
            Model: command.Model,
            ThinkingLevel: command.ThinkingLevel))
            .Concat((subject.Plan.Preparation ?? []).Select(command => new ReviewCommandEvidenceDto(
                command.StepId, "preparation", command.FileName, command.Arguments,
                ResultSha, ResultSha, TreeSha,
                DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow, 0, null,
                new string('a', 64), new string('b', 64),
                Phase: "preparation",
                WorkspaceRole: "candidate",
                Budget: new ReviewCommandBudgetEvidenceDto(
                    "review-command", command.TimeoutSeconds * 1000L, 1_000, false),
                ExecutionLocation: "remote",
                ExecutorId: lease.ExecutorId,
                HostId: lease.HostId,
                AttemptId: claim.Attempt!.AttemptId)))
            .ToArray();
        var verdicts = subject.Plan.RequiredAspects.Select(aspect =>
            new ReviewVerdictDto(aspect, "pass", "Verified", $"{aspect} passed")).ToArray();
        var toolchain = new Dictionary<string, string>
        {
            ["runtime"] = ".NET 10",
            ["git"] = "git;sha256=" + new string('d', 64),
        };
        foreach (var command in subject.Plan.Commands)
            toolchain[$"command:{command.StepId}"] = command.FileName + ";sha256=" + new string('e', 64);
        foreach (var command in subject.Plan.Preparation ?? [])
            toolchain[$"command:{command.StepId}"] = command.FileName + ";sha256=" + new string('e', 64);
        var artifacts = commands.SelectMany(command => new[]
        {
            new ReviewArtifactEvidenceDto(
                $"{command.StepId}.stdout.log", "text/plain", command.StdoutSha256, 1),
            new ReviewArtifactEvidenceDto(
                $"{command.StepId}.stderr.log", "text/plain", command.StderrSha256, 1),
        }).ToArray();
        return new ReviewReportRequest(
            lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence,
            $"report-{claim.Attempt!.AttemptId}", "Pass", null, "all review aspects passed",
            new ReviewWorkspaceProofDto(
                RepositoryId, ResultSha, ResultSha, TreeSha, false, false,
                Hash(workspacePath), lease.ResourceNamespace),
            new ReviewEnvironmentDto(
                lease.HostId, lease.ExecutorId, lease.InstanceId, "linux", "x64", "10.0",
                toolchain,
                new Dictionary<string, string>
                {
                    ["workspace"] = workspacePath,
                    ["cache"] = $"{workspacePath}/cache",
                    ["temp"] = $"{workspacePath}/tmp",
                    ["ports"] = $"{lease.PortBase}-{lease.PortBase + 7}",
                    ["containers"] = lease.ResourceNamespace,
                    ["databases"] = lease.ResourceNamespace,
                    ["credentials"] = "review-read-only",
                }),
            commands,
            artifacts,
            verdicts);
    }

    private static string Hash(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();

    private static async Task<string> ReviewAttemptIdAsync(TaskServerStore store, string taskId)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT id FROM review_attempts WHERE task_id = $task ORDER BY attempt_number LIMIT 1;";
        command.Parameters.AddWithValue("$task", taskId);
        return Assert.IsType<string>(await command.ExecuteScalarAsync());
    }

    private static async Task SetTaskStateOutsideAuthorityAsync(
        TaskServerStore store,
        string taskId,
        string state)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE tasks SET state = $state WHERE id = $task;";
        command.Parameters.AddWithValue("$state", state);
        command.Parameters.AddWithValue("$task", taskId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task SetReviewLeaseExpiryAsync(
        TaskServerStore store,
        string attemptId,
        DateTime expiresAt)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = store.DatabasePath,
                Mode = SqliteOpenMode.ReadWrite,
                Pooling = false,
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE review_attempts SET expires_at = $expires WHERE id = $attempt;";
        command.Parameters.AddWithValue("$expires", expiresAt.ToUniversalTime().ToString("O"));
        command.Parameters.AddWithValue("$attempt", attemptId);
        Assert.Equal(1, await command.ExecuteNonQueryAsync());
    }

    private static async Task<TaskDto> TaskAsync(TaskServerStore store, string taskId)
    {
        var project = Assert.Single(await store.ListProjectsAsync(null, default));
        return (await store.GetTaskAsync(project.ProjectId, taskId, default))!;
    }

    private static async Task CompletePassingOrchestrationAsync(
        TaskServerStore store,
        OrchestrationRunDto run)
    {
        while (run.Status == "pending")
        {
            var claim = await store.ClaimOrchestrationAsync(
                new OrchestrationClaimRequest(
                    "engine-a", "engine-instance-a", [run.CurrentStage]),
                "engine-a",
                default);
            var action = run.CurrentStage == OrchestrationStage.CompletionJudge
                ? OrchestrationAction.Complete
                : OrchestrationAction.Continue;
            run = await store.CompleteOrchestrationStageAsync(
                run.RunId,
                new CompleteOrchestrationStageRequest(
                    "engine-a",
                    "engine-instance-a",
                    claim.Lease!.LeaseId,
                    claim.Lease.Fence,
                    run.CurrentStage,
                    action,
                    "{}",
                    $"settle:{run.RunId}:{run.CurrentStage}"),
                "engine-a",
                default);
        }

        Assert.Equal("completed", run.Status);
    }

    private sealed class ReviewApiFactory(string dataDirectory) : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            builder.UseEnvironment("Testing");
            builder.ConfigureAppConfiguration((_, configuration) =>
                configuration.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:DataDirectory"] = dataDirectory,
                    ["TaskServer:ListenUrl"] = string.Empty,
                    ["TaskServer:RetentionSchedulerEnabled"] = "false",
                    ["AUTH"] = "bearer",
                    ["BOOTSTRAP_RUNNER_ID"] = "review-http",
                    ["BOOTSTRAP_RUNNER_AUTH_TOKEN"] = ReviewHttpToken,
                }));
        }
    }
}
