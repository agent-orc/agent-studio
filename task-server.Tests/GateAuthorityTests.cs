using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class GateAuthorityTests
{
    private const string Sha = "589c462f589c462f589c462f589c462f589c462f";
    private const string RepositoryId = "repo_0123456789abcdef";
    private const string RepositoryUrl = "https://example.invalid/product.git";

    [Fact]
    public async Task Immutable_subject_and_queue_survive_restart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var request = await SeedRequestAsync(first, clock);
        var subject = await first.CreateGateSubjectAsync(request, "engine", default);
        var replay = await first.CreateGateSubjectAsync(request, "engine", default);
        Assert.Equal(subject.Subject.SubjectId, replay.Subject.SubjectId);
        Assert.Single(replay.Attempts);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        var restored = await restarted.GetGateStatusAsync(subject.Subject.SubjectId, default);
        Assert.Equal(GateStates.Queued, restored!.Phase);
        Assert.Equal(subject.Subject.ExpectedSha, restored.Subject.ExpectedSha);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.CreateGateSubjectAsync(
            request with { PolicyHash = "changed" }, "engine", default));
    }

    [Fact]
    public async Task Claim_report_and_retry_are_fenced_across_restart()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var first = Store(temp.Path, clock);
        await first.InitializeAsync();
        var request = await SeedRequestAsync(first, clock);
        var subject = await first.CreateGateSubjectAsync(request, "engine", default);
        await RegisterGateAsync(first, clock, "gate-a", "instance-a", "host-a");
        var claimed = await first.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal("claimed", claimed.Status);
        Assert.InRange(claimed.Lease!.PortBase, 24000, 60000);
        Assert.Equal(1, (await first.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "gate-a").ActiveGateCount);
        var lease = claimed.Lease!;
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId,
            lease.Fence, lease.AuthorityEpoch);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running,
                     GateStates.Reporting, GateStates.Cleaning })
            await first.AdvanceGateAsync(claimed.Attempt!.AttemptId,
                new GatePhaseRequest(authority, phase), default);
        var report = Report(GateFailureClasses.SnapshotUnavailable);
        var retried = await first.ReportGateAsync(claimed.Attempt!.AttemptId,
            new SubmitGateReportRequest(authority, report), "gate-a", default);
        Assert.Equal(GateStates.Queued, retried.Phase);
        Assert.Equal(GateStates.InfraRetry, retried.Attempts[0].State);
        Assert.Equal(subject.Subject.SubjectId, retried.Subject.SubjectId);
        Assert.Equal(0, (await first.ListRunnerCapabilitySnapshotsAsync(default))
            .Single(item => item.RunnerId == "gate-a").ActiveGateCount);

        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        await RegisterGateAsync(restarted, clock, "gate-b", "instance-b", "host-b");
        var second = await restarted.ClaimGateAsync(new GateClaimRequest("gate-b", "instance-b"), "gate-b", default);
        Assert.Equal("claimed", second.Status);
        Assert.True(second.Lease!.Fence > lease.Fence);
        Assert.Equal(subject.Subject.SubjectId, second.Subject!.SubjectId);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.ReportGateAsync(
            second.Attempt!.AttemptId,
            new SubmitGateReportRequest(authority, Report(null)), "gate-a", default));

        var secondAuthority = new GateAuthority(second.Lease.ExecutorId, second.Lease.InstanceId,
            second.Lease.LeaseId, second.Lease.Fence, second.Lease.AuthorityEpoch);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running,
                     GateStates.Reporting, GateStates.Cleaning })
            await restarted.AdvanceGateAsync(second.Attempt!.AttemptId,
                new GatePhaseRequest(secondAuthority, phase), default);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.ReportGateAsync(
            second.Attempt!.AttemptId,
            new SubmitGateReportRequest(secondAuthority, Report(null) with { TestedSha = new string('d', 40) }),
            "gate-b", default));
        var acceptedReport = Report(null);
        var passed = await restarted.ReportGateAsync(second.Attempt!.AttemptId,
            new SubmitGateReportRequest(secondAuthority, acceptedReport), "gate-b", default);
        Assert.Equal(GateStates.Passed, passed.Phase);
        Assert.True(passed.EvidenceAvailable);
        Assert.Equal(Sha, passed.TestedSha);
        var replay = await restarted.ReportGateAsync(second.Attempt!.AttemptId,
            new SubmitGateReportRequest(secondAuthority, acceptedReport), "gate-b", default);
        Assert.Equal(GateStates.Passed, replay.Phase);
        await Assert.ThrowsAsync<TaskServerConflictException>(() => restarted.ReportGateAsync(
            second.Attempt!.AttemptId,
            new SubmitGateReportRequest(secondAuthority, Report(GateFailureClasses.ProductFailure)), "gate-b", default));
    }

    [Fact]
    public async Task Queue_timeout_and_lost_lease_have_distinct_durable_classifications()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var queueRequest = await SeedRequestAsync(store, clock);
        var queue = await store.CreateGateSubjectAsync(queueRequest, "engine", default);
        clock.Advance(TimeSpan.FromMinutes(16));
        var queuedTimeout = await store.GetGateStatusAsync(queue.Subject.SubjectId, default);
        Assert.Equal(GateStates.InfraFailed, queuedTimeout!.Phase);
        Assert.Equal(GateFailureClasses.NoEligibleGateExecutor,
            queuedTimeout.Attempts[0].FailureClassification);

        var leaseRequest = await SeedRequestAsync(store, clock);
        var active = await store.CreateGateSubjectAsync(leaseRequest, "engine", default);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        Assert.Equal(active.Subject.SubjectId, claim.Subject!.SubjectId);
        clock.Advance(TimeSpan.FromMinutes(3));
        var lost = await store.GetGateStatusAsync(active.Subject.SubjectId, default);
        Assert.Equal(GateStates.InfraRetry, lost!.Phase);
        Assert.Equal(GateFailureClasses.LostLease, lost.Attempts[0].FailureClassification);
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        Assert.Equal(GateFailureClasses.LostLease,
            (await restarted.GetGateStatusAsync(active.Subject.SubjectId, default))!.Attempts[0].FailureClassification);
        var contained = await restarted.ConfirmGateContainmentAsync(
            claim.Attempt!.AttemptId,
            new GateContainmentRequest("gate-a", "instance-a", "host-a",
                claim.Lease!.Fence, claim.Lease.ResourceNamespace, true, true),
            "gate-a", default);
        Assert.Equal(GateStates.Queued, contained.Phase);
        var containmentReplay = await restarted.ConfirmGateContainmentAsync(
            claim.Attempt.AttemptId,
            new GateContainmentRequest("gate-a", "instance-a", "host-a",
                claim.Lease.Fence, claim.Lease.ResourceNamespace, true, true),
            "gate-a", default);
        Assert.Equal(contained.AttemptCount, containmentReplay.AttemptCount);
        await RegisterGateAsync(restarted, clock, "gate-b", "instance-b", "host-b");
        var second = await restarted.ClaimGateAsync(
            new GateClaimRequest("gate-b", "instance-b"), "gate-b", default);
        Assert.Equal("claimed", second.Status);
        Assert.True(second.Lease!.Fence > claim.Lease.Fence);
    }

    [Fact]
    public async Task Product_failure_is_terminal_without_host_retry()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SeedRequestAsync(store, clock), "engine", default);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claimed = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        var lease = claimed.Lease!;
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId,
            lease.Fence, lease.AuthorityEpoch);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running,
                     GateStates.Reporting, GateStates.Cleaning })
            await store.AdvanceGateAsync(claimed.Attempt!.AttemptId,
                new GatePhaseRequest(authority, phase), default);
        var result = await store.ReportGateAsync(claimed.Attempt!.AttemptId,
            new SubmitGateReportRequest(authority, Report(GateFailureClasses.ProductFailure)), "gate-a", default);
        Assert.Equal(GateStates.ProductFailed, result.Phase);
        Assert.Single(result.Attempts);
        Assert.Equal(GateFailureClasses.ProductFailure, result.TerminalOutcome);
        Assert.Equal(subject.Subject.SubjectId, result.Subject.SubjectId);
    }

    [Fact]
    public async Task Materialization_failure_can_report_without_entering_running()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        await store.CreateGateSubjectAsync(await SeedRequestAsync(store, clock), "engine", default);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claimed = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        var lease = claimed.Lease!;
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId,
            lease.Fence, lease.AuthorityEpoch);
        await store.AdvanceGateAsync(claimed.Attempt!.AttemptId,
            new GatePhaseRequest(authority, GateStates.Materializing), default);
        var reporting = await store.AdvanceGateAsync(claimed.Attempt.AttemptId,
            new GatePhaseRequest(authority, GateStates.Reporting), default);
        Assert.Equal(GateStates.Reporting, reporting.State);
        await store.AdvanceGateAsync(claimed.Attempt.AttemptId,
            new GatePhaseRequest(authority, GateStates.Cleaning), default);
        var retry = await store.ReportGateAsync(claimed.Attempt.AttemptId,
            new SubmitGateReportRequest(authority,
                Report(GateFailureClasses.SnapshotUnavailable) with { Commands = [] }), "gate-a", default);
        Assert.Equal(GateStates.InfraRetry, retry.Attempts[0].State);
        Assert.Equal(GateStates.Queued, retry.Phase);
    }

    [Fact]
    public async Task Cancelled_gate_is_durable_and_rejects_its_old_fence()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SeedRequestAsync(store, clock), "engine", default);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a"), "gate-a", default);
        var cancelled = await store.CancelGateSubjectAsync(subject.Subject.SubjectId, "engine", default);
        Assert.Equal(GateStates.Cancelled, cancelled.Phase);
        var replay = await store.CancelGateSubjectAsync(subject.Subject.SubjectId, "engine", default);
        Assert.Equal(GateStates.Cancelled, replay.Phase);
        var lease = claim.Lease!;
        await Assert.ThrowsAsync<TaskServerConflictException>(() => store.RenewGateAsync(
            claim.Attempt!.AttemptId, new GateRenewRequest(new GateAuthority(lease.ExecutorId,
                lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch)), default));
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        Assert.Equal(GateStates.Cancelled,
            (await restarted.GetGateStatusAsync(subject.Subject.SubjectId, default))!.Phase);
    }

    [Fact]
    public async Task Overall_deadline_terminalizes_as_timed_out_even_with_a_live_lease()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.Parse("2026-09-25T12:00:00Z"));
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SeedRequestAsync(store, clock), "engine", default);
        await RegisterGateAsync(store, clock, "gate-a", "instance-a", "host-a");
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-a", "instance-a", RequestedTtlSeconds: 900),
            "gate-a", default);
        clock.Advance(TimeSpan.FromMinutes(6));
        var grace = await store.GetGateStatusAsync(subject.Subject.SubjectId, default);
        Assert.Equal(GateStates.Claimed, grace!.Phase);
        var lease = claim.Lease!;
        await store.RenewGateAsync(claim.Attempt!.AttemptId,
            new GateRenewRequest(new GateAuthority(lease.ExecutorId, lease.InstanceId,
                lease.LeaseId, lease.Fence, lease.AuthorityEpoch), 900), default);
        clock.Advance(TimeSpan.FromMinutes(5));
        var status = await store.GetGateStatusAsync(subject.Subject.SubjectId, default);
        Assert.Equal(GateStates.InfraRetry, status!.Phase);
        Assert.Equal(GateFailureClasses.ExecutionTimeout, status.Attempts[0].FailureClassification);
        clock.Advance(TimeSpan.FromMinutes(6));
        var spent = await store.GetGateStatusAsync(subject.Subject.SubjectId, default);
        Assert.Equal(GateStates.InfraFailed, spent!.Phase);
        Assert.Equal(GateFailureClasses.GateInfra, spent.TerminalOutcome);
    }

    private static TaskServerStore Store(string path, TimeProvider clock)
        => new(Options.Create(new TaskServerOptions { DataDirectory = path }), clock);

    private static GateReport Report(string? failure)
        => new(failure is null ? GateStates.Passed
                : failure == GateFailureClasses.ProductFailure ? GateStates.ProductFailed : GateStates.InfraFailed,
            failure, Sha, new string('b', 40), false, false,
            [new GateCommandEvidence("verify-1", failure is null ? 0 : 1, false,
                new string('c', 64), "output", DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow)],
            "host", "toolchain", [new string('c', 64)], [], "clean");

    private static async Task RegisterGateAsync(TaskServerStore store, ManualTimeProvider clock,
        string id, string instance, string host)
    {
        string[] keys = [GateCapabilities.Executor, GateCapabilities.GitMaterialization,
            CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.TaskServerConnectivity, CapabilityProtocol.Disk];
        await store.RegisterRunnerAsync(id, new RegisterRunnerRequest(
            id, host, instance, "1.0", TaskServerProtocol.Current, keys), id, default);
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            id, instance, CapabilityProtocol.CurrentSchemaVersion,
            clock.GetUtcNow().UtcDateTime, 300, 1,
            keys.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()), id, default);
    }

    private static async Task<CreateGateSubjectRequest> SeedRequestAsync(TaskServerStore store, ManualTimeProvider clock)
    {
        var workspaces = await store.ListWorkspacesAsync(default);
        var workspace = workspaces.FirstOrDefault()
            ?? await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var projects = await store.ListProjectsAsync(workspace.WorkspaceId, default);
        var project = projects.FirstOrDefault()
            ?? await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Task", "Do the work", "2-ready"), "test", default);
        var codingId = "coding-" + task.TaskId;
        var instance = "instance-" + task.TaskId;
        await store.RegisterRunnerAsync(codingId, new RegisterRunnerRequest(codingId,
            "coding-host", instance, "1.0", TaskServerProtocol.Current,
            [ReviewCapabilities.CodingExecutor]), codingId, default);
        var claim = await store.ClaimAsync(new ClaimRequest(codingId, instance), codingId, default);
        var resultRef = FencedGitRefs.ImmutableResult(claim.Run!.RunId, claim.Lease!.Fence, Sha);
        var envelope = new ImmutableResultEnvelope(RepositoryId, claim.Run.RunId,
            new string('1', 40), Sha, resultRef, null, new string('2', 64), RepositoryUrl: RepositoryUrl);
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(claim.Run.RunId,
            new ResultHandoffRequest(codingId, instance, claim.Lease.LeaseId, claim.Lease.Fence,
                1, $"handoff:{claim.Run.RunId}:{digest}", digest, envelope), codingId, default);
        await store.CompleteRunAsync(claim.Run.RunId,
            new CompleteRunRequest(codingId, instance, claim.Lease.LeaseId, claim.Lease.Fence,
                "success", "done", digest, $"completion:{claim.Run.RunId}:{digest}", 2), codingId, default);
        var plan = new GatePlan("post-build-test-gate", 1,
            [new GateCommand("verify-1", "sh", ["-lc", "true"], 30)],
            "", 300, [], 4096, "always");
        var planHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();
        return new CreateGateSubjectRequest(task.TaskId, claim.Run.RunId, RepositoryId,
            RepositoryUrl, Sha, resultRef, null, null, planHash, "policy-v1", "1",
            new string('3', 64), plan, clock.GetUtcNow().UtcDateTime.AddMinutes(15));
    }
}
