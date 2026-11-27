extern alias TaskServerAssembly;

using AgentStudio.Pipeline;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Projects;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;
using Microsoft.Extensions.Options;
using TaskGateStore = TaskServerAssembly::AgentStudio.TaskServer.TaskServerStore;
using TaskGateConflict = TaskServerAssembly::AgentStudio.TaskServer.TaskServerConflictException;
using TaskGateOptions = TaskServerAssembly::AgentStudio.TaskServer.TaskServerOptions;

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

    // One matrix exercises both authorities through their real admission paths.
    public static IEnumerable<object[]> QueueScanCases()
    {
        foreach (var authority in new[] { "monolith", "task-server" })
        foreach (var kind in new[] { GateSubjectKinds.Merge, GateSubjectKinds.PostMerge })
        foreach (var prefix in new[] { 0, 31, 32, 64 })
        foreach (var eligible in new[] { true, false })
            yield return [authority, kind, prefix, eligible];
    }

    [Theory]
    [MemberData(nameof(QueueScanCases))]
    public async Task ClaimScansPastIneligiblePages(string authority, string kind, int prefix, bool eligible)
    {
        var (create, claim, status) = await QueueFixture(authority);
        var blocked = new List<string>();
        for (var i = 0; i < prefix; i++)
            blocked.Add((await create(QueueRequest(kind, "blocked-" + i, ["fixture:missing"]))).Subject.SubjectId);
        _clock.Advance(TimeSpan.FromSeconds(1));
        var selected = await create(QueueRequest(kind, "selected", eligible ? [] : ["fixture:missing"]));
        // Equal prefix creation times exercise paging across timestamp ties.
        var response = await claim(new("gate", "instance"));
        Assert.Equal(eligible ? "claimed" : "empty", response.Status);
        if (eligible)
        {
            Assert.Equal(selected.Subject.SubjectId, response.Subject!.SubjectId);
            Assert.Equal(1, response.Lease!.Fence);
            Assert.Equal("empty", (await claim(new("gate", "instance"))).Status);
        }
        foreach (var id in blocked)
            Assert.Equal(GateStates.Queued, (await status(id))!.Phase);
    }

    [Theory]
    [InlineData("monolith")]
    [InlineData("task-server")]
    public async Task PagedClaimRetainsIdentityExclusionAndSubjectConflictChecks(string authority)
    {
        var (create, claim, status) = await QueueFixture(authority);
        var request = QueueRequest(GateSubjectKinds.PostMerge, "selected", []);
        var selected = await create(request);
        var conflict = await Record.ExceptionAsync(() => create(request with { PolicyHash = "changed" }));
        Assert.Equal("gate-subject-conflict", authority == "monolith"
            ? Assert.IsType<GateConflictException>(conflict).Code : Assert.IsType<TaskGateConflict>(conflict).Code);
        var stale = await Record.ExceptionAsync(() => claim(new("gate", "stale")));
        if (authority == "monolith") Assert.Equal("gate-executor-not-registered", Assert.IsType<GateConflictException>(stale).Code);
        else Assert.Equal("runner-instance-stale", Assert.IsType<TaskGateConflict>(stale).Code);
        Assert.Equal("empty", (await claim(new("gate", "instance", AvailableGateSlots: 0))).Status);
        Assert.Equal("empty", (await claim(new("gate", "instance", ExcludePostMerge: true))).Status);
        Assert.Equal(GateStates.Queued, (await status(selected.Subject.SubjectId))!.Phase);
        Assert.Equal(selected.Subject.SubjectId, (await claim(new("gate", "instance"))).Subject!.SubjectId);
    }

    [Theory]
    [InlineData("monolith")]
    [InlineData("task-server")]
    public async Task ClaimScansPastLaneConflictsWithoutClaimingThem(string authority)
    {
        var (create, claim, status) = await QueueFixture(authority);
        await create(QueueRequest(GateSubjectKinds.Merge, "active", []));
        Assert.Equal("claimed", (await claim(new("gate", "instance"))).Status);
        var blocked = new List<string>();
        for (var i = 0; i < 32; i++)
            blocked.Add((await create(QueueRequest(GateSubjectKinds.Merge, "blocked-" + i, []))).Subject.SubjectId);
        var selected = await create(QueueRequest(GateSubjectKinds.PostMerge, "selected", []));
        var response = await claim(new("gate", "instance"));
        Assert.Equal("claimed", response.Status);
        Assert.Equal(selected.Subject.SubjectId, response.Subject!.SubjectId);
        foreach (var id in blocked)
            Assert.Equal(GateStates.Queued, (await status(id))!.Phase);
        Assert.Equal("empty", (await claim(new("gate", "instance"))).Status);
    }

    [Theory]
    [InlineData("monolith")]
    [InlineData("task-server")]
    public async Task ClaimPreservesArrivalOrderWhenCreationTimesTie(string authority)
    {
        var (create, claim, _) = await QueueFixture(authority);
        var first = await create(QueueRequest(GateSubjectKinds.Merge, "first", []));
        await create(QueueRequest(GateSubjectKinds.Merge, "second", []));
        Assert.Equal(first.Subject.SubjectId, (await claim(new("gate", "instance"))).Subject!.SubjectId);
    }

    [Fact]
    public void MonolithClaimStillRequiresCompletePassingEvidence()
    {
        var gates = new MonolithGateStore((string?)null, _clock, new GateHosts());
        gates.Create(QueueRequest(GateSubjectKinds.Merge, "evidence", []));
        var claim = gates.Claim(new("gate", "instance"));
        var lease = claim.Lease!;
        var authority = new GateAuthority(lease.ExecutorId, lease.InstanceId, lease.LeaseId, lease.Fence, lease.AuthorityEpoch);
        foreach (var phase in new[] { GateStates.Materializing, GateStates.Running, GateStates.Reporting, GateStates.Cleaning })
            gates.Advance(claim.Attempt!.AttemptId, new(authority, phase));
        var report = new GateReport(GateStates.Passed, null, claim.Subject!.ExpectedSha, new string('b', 40),
            false, false, [], "host", "toolchain", [], [], "clean",
            new(claim.Subject.PlanHash, claim.Subject.PolicyHash, claim.Subject.ExpectedSha, null, null));
        var error = Assert.Throws<GateConflictException>(() => gates.Report(claim.Attempt!.AttemptId, new(authority, report)));
        Assert.Equal("gate-report-invalid", error.Code);
        Assert.Equal(GateStates.Cleaning, gates.GetStatus(claim.Subject.SubjectId)!.Phase);
    }

    private CreateGateSubjectRequest QueueRequest(string kind, string source, string[] capabilities)
    {
        var plan = new GatePlan("post-build-test-gate", 1, [new("verify", "sh", ["-c", "true"], 60)],
            "", 300, capabilities, 4096, "always",
            PostMerge: kind == GateSubjectKinds.PostMerge ? new("fixture", "develop", "window") : null);
        var tip = new string('a', 40);
        return new("fixture", source, "repository", "https://example.invalid/product.git", tip, tip, null, null,
            GateRules.PlanHash(plan), "policy", "1", new string('a', 64), plan,
            _clock.GetUtcNow().UtcDateTime.AddMinutes(15), Kind: kind);
    }

    private async Task<(Func<CreateGateSubjectRequest, Task<GateStatus>> Create,
        Func<GateClaimRequest, Task<GateClaimResponse>> Claim, Func<string, Task<GateStatus?>> Status)>
        QueueFixture(string authority)
    {
        if (authority == "monolith")
        {
            var store = new MonolithGateStore((string?)null, _clock, new GateHosts());
            return (request => Task.FromResult(store.Create(request)),
                request => Task.FromResult(store.Claim(request)), id => Task.FromResult(store.GetStatus(id)));
        }
        var remote = new TaskGateStore(Options.Create(new TaskGateOptions { DataDirectory = _root }), _clock);
        await remote.InitializeAsync();
        string[] keys = [GateCapabilities.Executor, GateCapabilities.PostMerge, GateCapabilities.GitMaterialization,
            CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.TaskServerConnectivity, CapabilityProtocol.Disk];
        await remote.RegisterRunnerAsync("gate", new RegisterRunnerRequest("gate", "host", "instance", "1",
            TaskServerProtocol.Current, keys), "gate", default);
        await remote.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest("gate", "instance",
            CapabilityProtocol.CurrentSchemaVersion, _clock.GetUtcNow().UtcDateTime, 300, 1,
            keys.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()), "gate", default);
        return (request => remote.CreateGateSubjectAsync(request, "fixture", default),
            request => remote.ClaimGateAsync(request, "gate", default), id => remote.GetGateStatusAsync(id, default));
    }

    private sealed class GateHosts : IGateExecutorDirectory
    {
        public bool TryGetGateExecutor(string runnerId, string instanceId, out string hostId, out IReadOnlySet<string> capabilities)
        {
            hostId = "host";
            capabilities = new HashSet<string> { GateCapabilities.Executor, GateCapabilities.GitMaterialization,
                CapabilityProtocol.RepositoryAccess, CapabilityProtocol.GitFetch };
            return runnerId == "gate" && instanceId == "instance";
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
