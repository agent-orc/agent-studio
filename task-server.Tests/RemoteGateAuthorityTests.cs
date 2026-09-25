using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class RemoteGateAuthorityTests
{
    private const string Sha = "589c462f589c462f589c462f589c462f589c462f";
    private const string Tree = "0123456789abcdef0123456789abcdef01234567";
    private const string RepoUrl = "https://example.invalid/gate.git";
    private static readonly string RepoId = RepositoryIdentityContract.FromUrl(RepoUrl)!;

    [Fact]
    public async Task Subject_creation_is_idempotent_and_conflicting_facts_are_rejected()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var request = await SourceRequestAsync(store);
        var first = await store.CreateGateSubjectAsync(request, "engine", default);
        var replay = await store.CreateGateSubjectAsync(request with
        {
            DispatchDeadline = request.DispatchDeadline.AddMinutes(1),
        }, "engine", default);
        Assert.Equal(first.SubjectId, replay.SubjectId);
        Assert.Equal(first.PlanHash, replay.PlanHash);
        Assert.Equal(first.DispatchDeadline, replay.DispatchDeadline);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.CreateGateSubjectAsync(request with { PolicyHash = "changed" }, "engine", default));
        Assert.Equal("gate-subject-conflict", conflict.Code);
    }

    [Fact]
    public async Task Claimed_attempt_survives_server_restart_and_stale_fence_cannot_report()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var request = await SourceRequestAsync(store);
        var subject = await store.CreateGateSubjectAsync(request, "engine", default);
        await GateRunnerAsync(store);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);
        Assert.Equal("claimed", claim.Status);
        var restarted = Store(temp.Path);
        await restarted.InitializeAsync();
        var status = await restarted.GetGateStatusAsync(subject.SubjectId, default);
        Assert.Equal(GateStates.Claimed, status!.Phase);
        Assert.Equal("host-gate", status.ActiveHost);
        Assert.Equal(1, status.AttemptCount);
        var authority = Authority(claim.Lease!) with { Fence = claim.Lease!.Fence - 1 };
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.AdvanceGatePhaseAsync(claim.Attempt!.AttemptId,
                new GatePhaseRequest(authority, GateStates.Materializing), default));
        Assert.Equal("gate-stale-fence", stale.Code);
    }

    [Fact]
    public async Task Infra_retry_preserves_subject_and_raises_fence()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        var first = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);
        await CleaningAsync(store, first);
        var report = await store.ReportGateAsync(first.Attempt!.AttemptId,
            new SubmitGateReportRequest(Authority(first.Lease!),
                Report("infra-failed", GateClassifications.MissingSnapshot, tested: false), "report-1"),
            "gate-1", default);
        Assert.Equal(GateStates.InfraRetry, report.Attempt.State);
        await GateRunnerAsync(store, "gate-2", "instance-2", "host-gate-2");
        var second = await store.ClaimGateAsync(new GateClaimRequest("gate-2", "instance-2"), "gate-2", default);
        Assert.Equal(subject.SubjectId, second.Subject!.SubjectId);
        Assert.True(second.Lease!.Fence > first.Lease!.Fence);
        Assert.Equal(2, second.Attempt!.AttemptNumber);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportGateAsync(second.Attempt.AttemptId,
                new SubmitGateReportRequest(Authority(first.Lease),
                    Report("passed", null), "stale"), "gate-1", default));
        Assert.Equal("gate-stale-fence", stale.Code);
    }

    [Fact]
    public async Task Replacement_host_process_recovers_abandoned_claim_only_with_containment_proof()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        var first = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1", LeaseSeconds: 30), "gate-1", default);
        clock.Advance(TimeSpan.FromSeconds(31));
        await store.ReconcileGateDeadlinesAsync(default);
        Assert.Equal(GateStates.InfraRetry,
            (await store.GetGateStatusAsync(subject.SubjectId, default))!.Phase);
        var restarted = Store(temp.Path, clock);
        await restarted.InitializeAsync();
        await GateRunnerAsync(restarted, instance: "instance-restarted");
        var receipt = new GateContainmentReceipt(Authority(first.Lease!), "instance-restarted",
            first.Lease!.ResourceNamespace, true, true, clock.GetUtcNow().UtcDateTime,
            "contained:" + first.Attempt!.AttemptId);
        await Assert.ThrowsAsync<ArgumentException>(() => restarted.RecoverGateAfterHostLossAsync(
            first.Attempt.AttemptId, receipt with { NoProcesses = false }, "gate-1", default));
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            restarted.RecoverGateAfterHostLossAsync(first.Attempt.AttemptId,
                receipt with { ResourceNamespace = "other" }, "gate-1", default));
        Assert.Equal("gate-stale-fence", stale.Code);

        var recovered = await restarted.RecoverGateAfterHostLossAsync(first.Attempt.AttemptId,
            receipt, "gate-1", default);
        Assert.Equal(subject.SubjectId, recovered.Subject.SubjectId);
        Assert.Equal(GateStates.Queued, recovered.Phase);
        Assert.Equal(GateStates.InfraRetry, recovered.Attempts[0].Attempt.State);
        await GateRunnerAsync(restarted, "gate-2", "instance-2", "host-gate-2");
        var second = await restarted.ClaimGateAsync(new GateClaimRequest("gate-2", "instance-2"),
            "gate-2", default);
        Assert.Equal("claimed", second.Status);
        Assert.True(second.Lease!.Fence > first.Lease.Fence);
        Assert.Equal(2, second.Attempt!.AttemptNumber);
    }

    [Fact]
    public async Task Pass_and_conflicting_duplicate_are_distinct()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);
        await CleaningAsync(store, claim);
        var request = new SubmitGateReportRequest(Authority(claim.Lease!), Report("passed", null), "report-1");
        var first = await store.ReportGateAsync(claim.Attempt!.AttemptId, request, "gate-1", default);
        var replay = await store.ReportGateAsync(claim.Attempt.AttemptId, request, "gate-1", default);
        Assert.Equal(first.Attempt, replay.Attempt);
        Assert.Equal(GateStates.Passed, (await store.GetGateStatusAsync(subject.SubjectId, default))!.Phase);
        var conflict = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.ReportGateAsync(claim.Attempt.AttemptId,
                request with { IdempotencyKey = "other" }, "gate-1", default));
        Assert.Equal("gate-report-conflict", conflict.Code);
    }

    [Theory]
    [InlineData("product-failed", GateClassifications.ProductFailure, GateStates.ProductFailed, "product-failed")]
    [InlineData("infra-failed", GateClassifications.ExecutionTimeout, GateStates.TimedOut, "GateInfra")]
    [InlineData("infra-failed", GateClassifications.ToolFailure, GateStates.InfraFailed, "GateInfra")]
    public void Classification_is_fail_closed(string outcome, string classification, string state, string terminal)
    {
        var report = Report(outcome, classification);
        var decision = GateReportDecision.Decide(report, attemptNumber: 2, retryBudget: 1);
        Assert.Equal(state, decision.State);
        Assert.Equal(terminal, decision.Outcome);
        Assert.False(decision.Retry);
    }

    [Fact]
    public void Cleanup_failure_overrides_product_verdict()
    {
        var decision = GateReportDecision.Decide(
            Report("product-failed", GateClassifications.ProductFailure) with { CleanupStatus = "failed" },
            attemptNumber: 1, retryBudget: 1);
        Assert.Equal(GateStates.InfraFailed, decision.State);
        Assert.Equal(GateClassifications.CleanupFailure, decision.Classification);
        Assert.False(decision.Retry);
    }

    [Fact]
    public async Task Queue_deadline_and_lost_lease_have_distinct_terminal_classifications()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var first = await store.CreateGateSubjectAsync(await SourceRequestAsync(store,
            clock.GetUtcNow().UtcDateTime.AddSeconds(10)), "engine", default);
        clock.Advance(TimeSpan.FromSeconds(11));
        await store.ReconcileGateDeadlinesAsync(default);
        Assert.Equal(GateClassifications.NoEligibleGateExecutor,
            (await store.GetGateStatusAsync(first.SubjectId, default))!.Attempts[0].Attempt.FailureClassification);

        var secondRequest = await SourceRequestAsync(store, clock.GetUtcNow().UtcDateTime.AddMinutes(15));
        var second = await store.CreateGateSubjectAsync(secondRequest with
        {
            Plan = secondRequest.Plan with { Version = 2 },
            PlanHash = Hash(secondRequest.Plan with { Version = 2 }),
        }, "engine", default);
        await GateRunnerAsync(store);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1", LeaseSeconds: 30),
            "gate-1", default);
        Assert.Equal(second.SubjectId, claim.Subject!.SubjectId);
        clock.Advance(TimeSpan.FromSeconds(31));
        await store.ReconcileGateDeadlinesAsync(default);
        Assert.Equal(GateStates.InfraRetry,
            (await store.GetGateStatusAsync(second.SubjectId, default))!.Phase);
        Assert.Equal(GateClassifications.LostLease,
            (await store.GetGateStatusAsync(second.SubjectId, default))!.Attempts[0].Attempt.FailureClassification);
        clock.Advance(TimeSpan.FromMinutes(5).Add(TimeSpan.FromSeconds(1)));
        await store.ReconcileGateDeadlinesAsync(default);
        Assert.Equal(GateStates.InfraFailed,
            (await store.GetGateStatusAsync(second.SubjectId, default))!.Phase);
    }

    [Fact]
    public async Task Missing_repository_capability_keeps_gate_queued_until_dispatch_deadline()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(
            await SourceRequestAsync(store, clock.GetUtcNow().UtcDateTime.AddSeconds(10)),
            "engine", default);
        await GateRunnerAsync(store, includeRepository: false);
        var claim = await store.ClaimGateAsync(
            new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);
        Assert.Equal("empty", claim.Status);
        Assert.Equal(GateStates.Queued, (await store.GetGateStatusAsync(subject.SubjectId, default))!.Phase);
        clock.Advance(TimeSpan.FromSeconds(11));
        await store.ReconcileGateDeadlinesAsync(default);
        var status = await store.GetGateStatusAsync(subject.SubjectId, default);
        Assert.Equal(GateStates.InfraFailed, status!.Phase);
        Assert.Equal(GateClassifications.NoEligibleGateExecutor,
            status.Attempts[0].Attempt.FailureClassification);
    }

    [Fact]
    public async Task Central_overall_deadline_terminalizes_a_heartbeating_attempt()
    {
        using var temp = new TempDirectory();
        var clock = new ManualTimeProvider(DateTimeOffset.UtcNow);
        var store = Store(temp.Path, clock);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1", LeaseSeconds: 300),
            "gate-1", default);
        clock.Advance(TimeSpan.FromSeconds(181));
        await store.ReconcileGateDeadlinesAsync(default);
        var status = await store.GetGateStatusAsync(subject.SubjectId, default);
        Assert.Equal(GateStates.TimedOut, status!.Phase);
        Assert.Equal(GateClassifications.ExecutionTimeout,
            status.Attempts[0].Attempt.FailureClassification);
    }

    [Fact]
    public async Task Cancellation_revokes_claim_and_rejects_later_phase()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);
        var cancelled = await store.CancelGateSubjectAsync(subject.SubjectId,
            new CancelGateRequest("operator stopped the gate"), "engine", default);
        Assert.Equal(GateStates.Cancelled, cancelled.Phase);
        var stale = await Assert.ThrowsAsync<TaskServerConflictException>(() =>
            store.AdvanceGatePhaseAsync(claim.Attempt!.AttemptId,
                new GatePhaseRequest(Authority(claim.Lease!), GateStates.Materializing), default));
        Assert.Equal("gate-stale-fence", stale.Code);
    }

    [Fact]
    public async Task Studio_gate_read_model_and_host_count_come_from_attempt_rows()
    {
        using var temp = new TempDirectory();
        var store = Store(temp.Path);
        await store.InitializeAsync();
        var subject = await store.CreateGateSubjectAsync(await SourceRequestAsync(store), "engine", default);
        await GateRunnerAsync(store);
        var claim = await store.ClaimGateAsync(new GateClaimRequest("gate-1", "instance-1"), "gate-1", default);

        var listed = await store.ListGateStatusesAsync(100, default);
        var status = Assert.Single(listed, item => item.Subject.SubjectId == subject.SubjectId);
        Assert.Equal("host-gate", status.ActiveHost);
        Assert.Equal(GateStates.Claimed, status.Phase);
        Assert.Equal(1, status.AttemptCount);
        Assert.False(status.EvidenceAvailable);
        Assert.Equal(1, Assert.Single(await store.ListStudioClientsAsync(default),
            host => host.HostId == "host-gate").ActiveGateCount);

        await CleaningAsync(store, claim);
        await store.ReportGateAsync(claim.Attempt!.AttemptId,
            new SubmitGateReportRequest(Authority(claim.Lease!), Report("passed", null), "report-1"),
            "gate-1", default);
        var terminal = await store.GetGateStatusAsync(subject.SubjectId, default);
        Assert.Equal(Sha, terminal!.TestedSha);
        Assert.Equal("passed", terminal.TerminalOutcome);
        Assert.True(terminal.EvidenceAvailable);
        Assert.Equal(0, Assert.Single(await store.ListStudioClientsAsync(default),
            host => host.HostId == "host-gate").ActiveGateCount);
    }

    [Fact]
    public async Task Bound_runner_credential_cannot_write_another_gate_executor_lease()
    {
        using var temp = new TempDirectory();
        await using var factory = new GateApiFactory(temp.Path);
        _ = factory.Services.GetRequiredService<TaskServerStore>();
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Bearer", "gate-test-token-000000000000000000000000000001");
        client.DefaultRequestHeaders.Add(TaskServerProtocol.HeaderName,
            TaskServerProtocol.Current.ToString());
        var request = new GatePhaseRequest(new GateAuthorityRequest(
            "gate-2", "instance-2", "lease-2", 1, 1), GateStates.Materializing);

        var response = await client.PostAsJsonAsync("/api/v1/gates/attempts/gat_other/phase", request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("runner-identity-mismatch",
            (await response.Content.ReadFromJsonAsync<ApiError>())!.Code);
    }

    private static TaskServerStore Store(string path, TimeProvider? clock = null)
        => new(Options.Create(new TaskServerOptions { DataDirectory = path }), clock ?? TimeProvider.System);

    private static async Task<CreateGateSubjectRequest> SourceRequestAsync(TaskServerStore store, DateTime? deadline = null)
    {
        var workspace = (await store.ListWorkspacesAsync(default)).FirstOrDefault()
            ?? await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Workspace"), "test", default);
        var project = (await store.ListProjectsAsync(workspace.WorkspaceId, default)).FirstOrDefault()
            ?? await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Project", "TS"), "test", default);
        var task = await store.CreateTaskAsync(project.ProjectId,
            new CreateTaskRequest("Gate task", "Verify", "2-ready"), "test", default);
        var codingId = "coding-" + task.TaskId;
        var instance = "instance-" + task.TaskId;
        await store.RegisterRunnerAsync(codingId,
            new RegisterRunnerRequest(codingId, "coding-host", instance, "1.0.0",
                TaskServerProtocol.Current, [ReviewCapabilities.CodingExecutor]), "test", default);
        var coding = await store.ClaimAsync(new ClaimRequest(codingId, instance), codingId, default);
        var resultRef = FencedGitRefs.ImmutableResult(coding.Run!.RunId, coding.Lease!.Fence, Sha);
        var envelope = new ImmutableResultEnvelope(RepoId, coding.Run.RunId,
            new string('1', 40), Sha, resultRef, null, new string('2', 64), RepositoryUrl: RepoUrl);
        var digest = ResultEnvelopeDigest.Compute(envelope);
        await store.AcknowledgeResultHandoffAsync(coding.Run.RunId,
            new ResultHandoffRequest(codingId, instance, coding.Lease.LeaseId,
                coding.Lease.Fence, 1, "handoff:" + coding.Run.RunId, digest, envelope), codingId, default);
        await store.CompleteRunAsync(coding.Run.RunId,
            new CompleteRunRequest(codingId, instance, coding.Lease.LeaseId,
                coding.Lease.Fence, "success", "done", digest,
                "completion:" + coding.Run.RunId, 2), codingId, default);
        var plan = new GatePlan("post-build-test-gate", 1,
            [new GateCommand("build", "dotnet", ["test"], "", 60)],
            "", 120, [CapabilityProtocol.DotNet], 100_000, "always");
        return new CreateGateSubjectRequest(task.TaskId, coding.Run.RunId,
            RepoId, RepoUrl, Sha, resultRef, null, null,
            Hash(plan), "policy-1", 1, "audit-1", plan,
            deadline ?? DateTime.UtcNow.AddMinutes(15));
    }

    private static async Task GateRunnerAsync(TaskServerStore store,
        string id = "gate-1", string instance = "instance-1", string host = "host-gate",
        bool includeRepository = true)
    {
        await store.RegisterRunnerAsync(id,
            new RegisterRunnerRequest(id, host, instance, "1.0.0",
                TaskServerProtocol.Current, ["gate-executor"]), "test", default);
        var capabilities = new List<AdvertisedCapabilityDto>
        {
            Capability(CapabilityProtocol.GateExecutor), Capability(CapabilityProtocol.GateGit),
            Capability(CapabilityProtocol.RepositoryAccess),
            Capability(CapabilityProtocol.TaskServerConnectivity),
            Capability(CapabilityProtocol.DotNet),
        };
        if (includeRepository)
            capabilities.Add(Capability(CapabilityProtocol.GateRepository(RepoId)));
        await store.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest(
            id, instance, CapabilityProtocol.CurrentSchemaVersion,
            DateTime.UtcNow, 180, 1, capabilities), id, default);
    }

    private static AdvertisedCapabilityDto Capability(string key)
        => new(key, "gate");

    private static async Task CleaningAsync(TaskServerStore store, GateClaimResponse claim)
    {
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running,
            GateStates.Reporting, GateStates.Cleaning })
            await store.AdvanceGatePhaseAsync(claim.Attempt!.AttemptId,
                new GatePhaseRequest(Authority(claim.Lease!), phase), default);
    }

    private static GateAuthorityRequest Authority(GateLease lease)
        => new(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);

    private static GateReport Report(string outcome, string? classification, bool tested = true)
        => new(outcome, classification, tested ? Sha : null, tested ? Tree : null,
            false, false, tested
                ? [new GateCommandEvidence("build", classification == GateClassifications.ProductFailure ? 1 : 0,
                    classification == GateClassifications.ExecutionTimeout, new string('a', 64), 10,
                    DateTime.UtcNow.AddSeconds(-1), DateTime.UtcNow)]
                : [],
            "linux/x64", "dotnet-test", new Dictionary<string, string>(),
            new Dictionary<string, string>(), "complete");

    private static string Hash(GatePlan plan)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web))))).ToLowerInvariant();

    private sealed class GateApiFactory(string dataDirectory) : WebApplicationFactory<Program>
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
                    ["BOOTSTRAP_RUNNER_ID"] = "gate-1",
                    ["BOOTSTRAP_RUNNER_AUTH_TOKEN"] = "gate-test-token-000000000000000000000000000001",
                }));
        }
    }
}
