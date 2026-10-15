using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3055: the batch suite as claimable gate work. A runner host claims the
/// batch subject under lease and fence; a stale fence never settles it; the
/// subject lives at the authority the runner gate claims reach.
/// </summary>
public sealed class ClaimedBatchGateRunnerTests
{
    private const string CandidateSha = "1111111111111111111111111111111111111111";
    private const string BaseSha = "2222222222222222222222222222222222222222";
    private static readonly string MembershipDigest = new('a', 64);
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-15T08:00:00Z"));

    [Fact]
    public async Task Batch_subject_is_claimed_under_lease_and_a_stale_fence_never_settles_it()
    {
        var store = new MonolithGateStore((string?)null, _clock, new GateHosts());
        IBatchGateSubjectAuthority authority = new MonolithBatchGateSubjectAuthority(store);
        var (manifest, run) = Batch();
        var created = await authority.CreateAsync(ClaimedBatchGateRunner.Subject(manifest, run,
            BatchGatePolicy.CandidateRef(manifest, run.CoordinatorFence), "https://git.test/repo.git",
            [new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test")],
            TimeSpan.FromMinutes(30), _clock.GetUtcNow().UtcDateTime), CancellationToken.None);
        Assert.Equal(GateStates.Queued, created.Phase);
        Assert.Equal(manifest.BatchId, created.Subject.BatchId);
        Assert.Equal(["run-1", "run-2"], created.Subject.MemberRunIds);

        var first = store.Claim(new GateClaimRequest("gate-a", "instance-a"));
        Assert.Equal("claimed", first.Status);
        Assert.Equal(1, first.Lease!.Fence);
        Assert.Equal("empty", store.Claim(new GateClaimRequest("gate-b", "instance-b")).Status);

        // The lease lapses; the authority fences the first host out and requeues after containment.
        _clock.Advance(TimeSpan.FromMinutes(3));
        Assert.Equal(GateStates.InfraRetry, (await authority.GetAsync(created.Subject.SubjectId,
            CancellationToken.None))!.Phase);
        var stale = Authority(first.Lease);
        Assert.Equal("stale-gate-fence", Assert.Throws<GateConflictException>(() =>
            store.Advance(first.Attempt!.AttemptId, new GatePhaseRequest(stale, GateStates.Materializing))).Code);
        store.ConfirmContainment(first.Attempt!.AttemptId, new GateContainmentRequest("gate-a", "instance-a",
            first.Lease.HostId, first.Lease.Fence, first.Lease.ResourceNamespace, true, true));

        var second = store.Claim(new GateClaimRequest("gate-b", "instance-b"));
        Assert.Equal("claimed", second.Status);
        Assert.Equal(2, second.Lease!.Fence);
        var current = Authority(second.Lease);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running, GateStates.Reporting, GateStates.Cleaning })
            store.Advance(second.Attempt!.AttemptId, new GatePhaseRequest(current, phase));
        Assert.Equal("stale-gate-fence", Assert.Throws<GateConflictException>(() =>
            store.Report(second.Attempt!.AttemptId, new SubmitGateReportRequest(
                current with { Fence = 1 }, PassingReport(second.Subject!)))).Code);

        var status = store.Report(second.Attempt!.AttemptId,
            new SubmitGateReportRequest(current, PassingReport(second.Subject!)));
        var result = ClaimedBatchGateRunner.Result(run, status, _clock.GetUtcNow(), _clock.GetUtcNow());

        Assert.Equal(GateStates.Passed, status.Phase);
        Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        Assert.Equal(CandidateSha, result.TestedSha);
        Assert.Equal($"gate:host-b/{second.Attempt.AttemptId}#2", result.Executor);
    }

    [Fact]
    public void Claimed_execution_is_the_default_and_bridge_is_the_only_rollback()
    {
        static IConfiguration Config(string? value) => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { [ClaimedBatchGateRunner.ExecutionKey] = value })
            .Build();

        Assert.True(ClaimedBatchGateRunner.Enabled(Config(null)));
        Assert.True(ClaimedBatchGateRunner.Enabled(Config("Claimed")));
        Assert.False(ClaimedBatchGateRunner.Enabled(Config("bridge")));
        Assert.Throws<InvalidOperationException>(() => ClaimedBatchGateRunner.Mode(Config("ssh")));
    }

    [Fact]
    public async Task Task_server_authority_creates_and_reads_the_subject_where_runner_claims_land()
    {
        var store = new MonolithGateStore((string?)null, _clock, new GateHosts());
        var (manifest, run) = Batch();
        var request = ClaimedBatchGateRunner.Subject(manifest, run,
            BatchGatePolicy.CandidateRef(manifest, run.CoordinatorFence), null,
            [new VerifyCommand(VerifyEcosystem.DotNet, VerifyCommandKind.Test, "", "dotnet test")],
            TimeSpan.FromMinutes(30), _clock.GetUtcNow().UtcDateTime);
        var handler = new TaskServerStub(store);
        var authority = new TaskServerBatchGateSubjectAuthority(() =>
            new HttpClient(handler) { BaseAddress = new Uri("https://task-server.test/") });

        var created = await authority.CreateAsync(request, CancellationToken.None);
        var read = await authority.GetAsync(created.Subject.SubjectId, CancellationToken.None);
        await authority.CancelAsync(created.Subject.SubjectId, CancellationToken.None);

        Assert.Equal(["POST /api/v1/gates/subjects", $"GET /api/v1/gates/subjects/{created.Subject.SubjectId}",
            $"POST /api/v1/gates/subjects/{created.Subject.SubjectId}/cancel"], handler.Calls);
        Assert.Equal(manifest.MembershipDigest, read!.Subject.MembershipDigest);
        Assert.Null(await authority.GetAsync("gsub_missing", CancellationToken.None));
        Assert.Equal(GateStates.Cancelled, store.GetStatus(created.Subject.SubjectId)!.Phase);
    }

    private (BatchGateManifest Manifest, BatchGateRunRecord Run) Batch()
    {
        var batchId = Guid.NewGuid().ToString("N");
        var scope = new BatchGateScope("PROJ-001", "repo-1", "develop", "default", new string('b', 64), "1.0");
        BatchGateSubject Member(string key, string runId) => new(key, "PROJ-001", "repo-1", "develop",
            "default", new string('b', 64), "1.0", $"refs/agent-studio/results/{key}", CandidateSha, runId,
            1, 1, CandidateSha, true, true, true, true, false, batchId, false, _clock.GetUtcNow(), 1);
        var manifest = new BatchGateManifest(batchId, scope, BaseSha, MembershipDigest, _clock.GetUtcNow(),
            [Member("AGT-1", "run-1"), Member("AGT-2", "run-2")], [], ["AGT-1", "AGT-2"]);
        var run = new BatchGateRunRecord(Guid.NewGuid().ToString("N"), batchId, MembershipDigest, BaseSha,
            CandidateSha, "default", new string('b', 64), "host", 3, ["dotnet test"], _clock.GetUtcNow(),
            "gate-evidence.jsonl");
        return (manifest, run);
    }

    private static GateAuthority Authority(GateLease lease)
        => new(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);

    private GateReport PassingReport(GateSubject subject)
        => new(GateStates.Passed, null, subject.ExpectedSha, new string('c', 40), false, false,
            subject.Plan.Commands.Select(command => new GateCommandEvidence(command.StepId, 0, false,
                new string('d', 64), "ok", _clock.GetUtcNow().UtcDateTime, _clock.GetUtcNow().UtcDateTime)).ToArray(),
            "linux-x64", "dotnet-10", [], [], "clean");

    private sealed class GateHosts : IGateExecutorDirectory
    {
        public bool TryGetGateExecutor(string runnerId, string instanceId,
            out string hostId, out IReadOnlySet<string> capabilities)
        {
            hostId = runnerId.Replace("gate-", "host-", StringComparison.Ordinal);
            capabilities = new HashSet<string>(StringComparer.Ordinal)
            {
                GateCapabilities.Executor, GateCapabilities.GitMaterialization,
                CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess, CapabilityProtocol.DotNet,
            };
            return true;
        }
    }

    /// <summary>The Task Server gate subject routes, served from a gate store.</summary>
    private sealed class TaskServerStub(MonolithGateStore store) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        public List<string> Calls { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            Calls.Add($"{request.Method} {path}");
            const string subjects = "/api/v1/gates/subjects";
            object? body = null;
            if (request.Method == HttpMethod.Post && path == subjects)
                body = store.Create(JsonSerializer.Deserialize<CreateGateSubjectRequest>(
                    await request.Content!.ReadAsStringAsync(cancellationToken), Json)!);
            else if (request.Method == HttpMethod.Post && path.EndsWith("/cancel", StringComparison.Ordinal))
                body = store.Cancel(path[(subjects.Length + 1)..^"/cancel".Length]);
            else if (request.Method == HttpMethod.Get)
                body = store.GetStatus(path[(subjects.Length + 1)..]);
            return body is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(JsonSerializer.Serialize(body, Json), Encoding.UTF8, "application/json"),
                };
        }
    }
}
