using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PostMergeGateSchedulerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-placement-" + Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero));

    [Theory]
    [InlineData(true, "gate")]
    [InlineData(false, "run")]
    public async Task PlacementUsesConnectedGateCapacityAndRetainsQueuedIdentity(bool connected, string executor)
    {
        var (scheduler, project, store, authority) = Fixture(connected, true);
        var tip = new string('a', 40);
        var work = new PostMergeGateWork(project.DisplayName, "develop", "window");
        var placement = await scheduler.PlaceAsync(project, tip, work, default);
        Assert.Equal(executor, placement.Executor);
        if (!connected) { Assert.Empty(authority.Created); return; }
        Assert.Equal(placement.SubjectId, store.Read(project.Id).GateSubjectId);
        Assert.Equal(GateSubjectKinds.PostMerge, Assert.Single(authority.Created).Kind);
        Assert.Equal(work, authority.Created[0].Plan.PostMerge);
        Assert.Equal(RepositoryIdentityContract.FromUrl(authority.Created[0].RepositoryUrl), authority.Created[0].RepositoryId);
        GateRules.ValidateSubject(authority.Created[0]);
        await scheduler.PlaceAsync(project, tip, work, default);
        Assert.Single(authority.Created);
        authority.Phase = GateStates.Materializing;
        await scheduler.PlaceAsync(project, new string('b', 40), work, default);
        Assert.Equal(2, authority.Created.Count);
        Assert.Equal(1, authority.Cancelled);
        authority.Phase = GateStates.Running;
        await scheduler.PlaceAsync(project, new string('c', 40), work, default);
        Assert.Equal(2, authority.Created.Count);
        Assert.Equal(1, authority.Cancelled);
    }

    [Fact]
    public async Task NoConnectedHostClosesTheWindowWithAnExplicitReason()
    {
        var (scheduler, project, store, _) = Fixture(false, false);
        store.ObserveWindow(project.Id, new(new string('a', 40), _clock.GetUtcNow().AddDays(-18),
            _clock.GetUtcNow().AddDays(-18), 45, "busy"));
        await scheduler.MaintainAsync(default);
        var state = store.Read(project.Id);
        Assert.Null(state.PendingWindow);
        Assert.Equal("no connected execution host", state.ClosedWindow!.Reason);
        Assert.Empty(state.Runs);
    }

    [Fact]
    public async Task UndiscoverableProjectClosesItsOldWindowEvenWhenOtherCodingHostsAreConnected()
    {
        var (scheduler, project, store, _) = Fixture(false, true, repository: false);
        store.ObserveWindow(project.Id, new(new string('a', 40), _clock.GetUtcNow().AddDays(-18),
            _clock.GetUtcNow(), 45, "old observation"));
        await scheduler.MaintainAsync(default);
        Assert.Null(store.Read(project.Id).PendingWindow);
        Assert.Equal("no repository configured", store.Read(project.Id).ClosedWindow!.Reason);
    }

    [Fact]
    public void MonolithGateQueueKeepsMergePrecedenceAndFencesStartedTests()
    {
        var gates = new MonolithGateStore((string?)null, _clock, new GateHosts());
        var tip = new string('a', 40);
        var plan = new GatePlan("post-build-test-gate", 1, [new("verify", "sh", ["-c", "true"], 60)],
            "", 300, [], 4096, "always", PostMerge: new("fixture", "develop", "window"));
        CreateGateSubjectRequest Request(string kind, GatePlan selected) => new("fixture", kind, "repository",
            "https://example.invalid/product.git", tip, tip, null, null, GateRules.PlanHash(selected), "policy",
            "1", new string('a', 64), selected, _clock.GetUtcNow().UtcDateTime.AddMinutes(15), Kind: kind);
        var postMerge = gates.Create(Request(GateSubjectKinds.PostMerge, plan));
        var merge = gates.Create(Request(GateSubjectKinds.Merge, plan with { PostMerge = null }));
        var first = gates.Claim(new("gate", "instance"));
        Assert.Equal(merge.Subject.SubjectId, first.Subject!.SubjectId);
        Assert.Equal("empty", gates.Claim(new("gate", "instance", ExcludePostMerge: true)).Status);
        var second = gates.Claim(new("gate", "instance"));
        Assert.Equal(postMerge.Subject.SubjectId, second.Subject!.SubjectId);
        var lease = second.Lease!;
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        gates.Advance(second.Attempt!.AttemptId, new(authority, GateStates.Materializing));
        gates.Advance(second.Attempt.AttemptId, new(authority, GateStates.Running));
        Assert.Equal(GateStates.Running, gates.Cancel(postMerge.Subject.SubjectId, onlyBeforeRunning: true).Phase);
        // Existing gate kinds retain their arrival order relative to one another.
        var legacy = new MonolithGateStore((string?)null, _clock, new GateHosts());
        var batch = legacy.Create(Request(GateSubjectKinds.Batch, plan with { PostMerge = null }));
        legacy.Create(Request(GateSubjectKinds.Merge, plan with { PostMerge = null }));
        Assert.Equal(batch.Subject.SubjectId, legacy.Claim(new("gate", "instance")).Subject!.SubjectId);
    }

    private sealed class GateHosts : IGateExecutorDirectory
    {
        public bool TryGetGateExecutor(string runnerId, string instanceId, out string hostId, out IReadOnlySet<string> capabilities)
        {
            hostId = "host";
            capabilities = new HashSet<string> { GateCapabilities.Executor, GateCapabilities.GitMaterialization,
                CapabilityProtocol.RepositoryAccess, CapabilityProtocol.GitFetch };
            return true;
        }
    }

    private (PostMergeGateScheduler, ProjectRecord, PostMergeRunStore, Authority) Fixture(bool gate, bool coding, bool repository = true)
    {
        Directory.CreateDirectory(_root);
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
            { ["TaskRepository"] = _root }).Build();
        var projects = new ProjectRegistry(configuration, NullLogger<ProjectRegistry>.Instance);
        var project = projects.EnsureProjectForStorage(_root, "fixture", DefaultWorkspace.Id, _clock);
        if (repository) project = projects.AddUrl(project.Id, "repo", "https://example.invalid/product.git");
        var settings = new ProjectSettingsService(NullLogger<ProjectSettingsService>.Instance, configuration);
        settings.SetTestingStrategy(project.DisplayName, TestingStrategies.Fast);
        var store = new PostMergeRunStore(Path.Combine(_root, "post-merge"));
        var authority = new Authority { Hosts = new(gate, coding) };
        return (new(authority, store, projects, settings, _clock), project, store, authority);
    }

    private sealed class Authority : IBatchGateSubjectAuthority
    {
        public string Name => "fixture";
        public PostMergeHostAvailability Hosts { get; init; } = new(false, false);
        public List<CreateGateSubjectRequest> Created { get; } = [];
        public string Phase { get; set; } = GateStates.Queued;
        public int Cancelled { get; private set; }
        private GateStatus? _status;
        public Task<PostMergeHostAvailability> PostMergeHostsAsync(CancellationToken ct) => Task.FromResult(Hosts);
        public Task<GateStatus> CreateAsync(CreateGateSubjectRequest request, CancellationToken ct)
        {
            Created.Add(request);
            var subject = new GateSubject("subject-" + Created.Count, request.TaskId, request.SourceRunId,
                request.RepositoryId, request.RepositoryUrl, request.ExpectedSha, request.ResultRef, null, null,
                request.PlanHash, request.PolicyHash, "1", request.TestSelectionAuditDigest, request.Plan,
                DateTime.UnixEpoch, request.DispatchDeadline, 2, Kind: request.Kind);
            _status = new GateStatus(subject, [], null, 0, null, Phase, 0, null, null, null, false);
            return Task.FromResult(_status);
        }
        public Task<GateStatus?> GetAsync(string id, CancellationToken ct)
            => Task.FromResult(_status is null ? null : _status with { Phase = Phase });
        public Task<bool> CancelBeforeTestsAsync(string id, CancellationToken ct) { Cancelled++; return Task.FromResult(true); }
        public Task CancelAsync(string id, CancellationToken ct) => Task.CompletedTask;
        public Task<GateBundleLocator> UploadBundleAsync(string repositoryId, string path, string sha256,
            long sizeBytes, TimeSpan budget, CancellationToken ct) => throw new NotSupportedException();
    }

    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }
}
