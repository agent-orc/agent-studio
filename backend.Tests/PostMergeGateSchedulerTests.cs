extern alias TaskServerAssembly;
extern alias Runner;

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using AgentStudio.Pipeline;
using GateDaemon = Runner::AgentRunner.RemoteGateDaemon;
using GateClient = Runner::AgentRunner.TaskServerClient;
using GateRunnerOptions = Runner::AgentRunner.RunnerOptions;
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
        var (create, claim, status, _) = await QueueFixture(authority);
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
        var (create, claim, status, _) = await QueueFixture(authority);
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
        var (create, claim, status, _) = await QueueFixture(authority);
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
        var (create, claim, _, _) = await QueueFixture(authority);
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

    [Theory]
    [InlineData("monolith", false)]
    [InlineData("task-server", false)]
    [InlineData("task-server-grant", false)]
    [InlineData("task-server-grant-two", false)]
    [InlineData("monolith", true)]
    [InlineData("task-server", true)]
    [InlineData("task-server-grant", true)]
    [InlineData("task-server-grant-two", true)]
    public async Task DaemonAdmitsPostMergeBeyondTwoPagesWhileMergesKeepArriving(string authority, bool terminalBeforeLocalCompletion)
    {
        var (create, claim, _, finish) = await QueueFixture(authority);
        var first = await create(QueueRequest(GateSubjectKinds.Merge, "active", []));
        for (var i = 0; i < 64; i++)
            await create(QueueRequest(GateSubjectKinds.Merge, "queued-" + i, []));
        var post = await create(QueueRequest(GateSubjectKinds.PostMerge, "expired-window", []));
        using var shutdown = new CancellationTokenSource();
        var ordinary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondOrdinary = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var secondMergeStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resumePolling = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var postFlight = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var postStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var polledWithBothFlights = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var mergeExecutions = 0;
        var polls = 0;
        var firstRetired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var server = new AdmissionServer(async request =>
        {
            polls++;
            if (polls == 2 && terminalBeforeLocalCompletion) await firstRetired.Task.WaitAsync(shutdown.Token);
            var response = await claim(request);
            if (polls == 1) Assert.Equal(first.Subject.SubjectId, response.Subject!.SubjectId);
            if (response.Subject?.Kind == GateSubjectKinds.Merge)
                await create(QueueRequest(GateSubjectKinds.Merge, "replenished-" + polls, []));
            return response;
        });
        var options = new GateRunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "gate", RunnerName = "gate", Hostname = "host",
            Role = "gate", BackendName = "test", BaseBranch = "develop",
            WorkDir = Path.Combine(_root, "coding"), ReviewWorkDir = Path.Combine(_root, "gate"),
            StateDir = Path.Combine(_root, "state"),
        };
        using var client = new GateClient(new HttpClient(server) { BaseAddress = new Uri(options.ServerUrl) },
            "gate", options: options, runnerInstanceId: "instance");
        var daemon = new GateDaemon(options, client, message => throw new InvalidOperationException(message))
        {
            OrdinaryExecutor = async (response, _) =>
            {
                mergeExecutions++;
                if (mergeExecutions == 1)
                {
                    if (terminalBeforeLocalCompletion)
                    {
                        await finish(response.Subject!.SubjectId);
                        firstRetired.SetResult();
                    }
                    return await ordinary.Task;
                }
                secondMergeStarted.TrySetResult();
                return await secondOrdinary.Task;
            },
            PostMergeExecutor = (response, _) =>
            {
                Assert.Equal(post.Subject.SubjectId, response.Subject!.SubjectId);
                Assert.False(terminalBeforeLocalCompletion ? secondOrdinary.Task.IsCompleted : ordinary.Task.IsCompleted);
                postStarted.SetResult();
                return postFlight.Task;
            },
            PollDelay = async token =>
            {
                polledWithBothFlights.TrySetResult();
                if (!resumePolling.Task.IsCompleted) await resumePolling.Task.WaitAsync(token);
                else if (!(terminalBeforeLocalCompletion ? postStarted.Task.IsCompleted : secondMergeStarted.Task.IsCompleted))
                    await Task.Yield();
                else await Task.Delay(Timeout.Infinite, token);
            },
        };
        var run = daemon.RunAsync(shutdown.Token);
        try
        {
            // Timeouts bound a broken implementation; assertions use controlled signals.
            if (!terminalBeforeLocalCompletion)
                await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await polledWithBothFlights.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, mergeExecutions);
            Assert.Equal(2, polls);
            if (terminalBeforeLocalCompletion)
            {
                // Both claims survive while the authority has retired the first
                // merge but its local flight has not finished. The successor waits.
                var json = new JsonSerializerOptions(JsonSerializerDefaults.Web);
                var current = JsonSerializer.Deserialize<GateClaimResponse>(
                    File.ReadAllText(Path.Combine(options.StateDir, "gate-claim.json")), json)!;
                var pending = JsonSerializer.Deserialize<GateClaimResponse>(
                    File.ReadAllText(Path.Combine(options.StateDir, "gate-post-merge-claim.json")), json)!;
                Assert.Equal(first.Subject.SubjectId, current.Subject!.SubjectId);
                Assert.Equal(GateSubjectKinds.Merge, pending.Subject!.Kind);
                Assert.NotEqual(current.Lease!.LeaseId, pending.Lease!.LeaseId);
            }
            else await finish(first.Subject.SubjectId);
            ordinary.SetResult(true);
            resumePolling.SetResult();
            await secondMergeStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await postStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.False(postFlight.Task.IsCompleted);
            Assert.Equal(2, mergeExecutions); // Integration continues while the suite is still running.
            Assert.Equal(3, polls);
        }
        finally
        {
            shutdown.Cancel();
            ordinary.TrySetResult(true);
            secondOrdinary.TrySetResult(true);
            postFlight.TrySetResult(true);
            await run;
        }
    }

    [Theory]
    [InlineData("monolith", false)]
    [InlineData("monolith", true)]
    [InlineData("task-server", false)]
    [InlineData("task-server", true)]
    public async Task DaemonRecoversFaultedPredecessorBeforeExecutingClaimedSuccessor(string authority, bool cancelled)
    {
        var (create, claim, status, finish) = await QueueFixture(authority);
        var first = await create(QueueRequest(GateSubjectKinds.Merge, "predecessor", []));
        var second = await create(QueueRequest(GateSubjectKinds.Merge, "successor", []));
        using var shutdown = new CancellationTokenSource();
        var predecessor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var successor = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var retired = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var bothClaimed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var successorStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        GateClaimResponse? executedSuccessor = null;
        var executions = 0;
        var recoveryReads = 0;
        using var server = new AdmissionServer(async request =>
        {
            if (executions > 0) await retired.Task.WaitAsync(shutdown.Token);
            return await claim(request);
        }, async id =>
        {
            Assert.Equal(first.Subject.SubjectId, id);
            recoveryReads++;
            return await status(id);
        });
        var options = new GateRunnerOptions
        {
            ServerUrl = "http://task-server", RunnerId = "gate", RunnerName = "gate", Hostname = "host",
            Role = "gate", BackendName = "test", BaseBranch = "develop",
            WorkDir = Path.Combine(_root, "coding"), ReviewWorkDir = Path.Combine(_root, "gate"),
            StateDir = Path.Combine(_root, "state"),
        };
        var predecessorPath = Path.Combine(options.StateDir, "gate-claim.json");
        var successorPath = Path.Combine(options.StateDir, "gate-post-merge-claim.json");
        using var client = new GateClient(new HttpClient(server) { BaseAddress = new Uri(options.ServerUrl) },
            "gate", options: options, runnerInstanceId: "instance");
        var daemon = new GateDaemon(options, client, _ => { })
        {
            OrdinaryExecutor = async (response, _) =>
            {
                executions++;
                if (response.Subject!.SubjectId == first.Subject.SubjectId)
                {
                    await finish(first.Subject.SubjectId);
                    retired.SetResult();
                    return await predecessor.Task;
                }
                executedSuccessor = response;
                successorStarted.SetResult();
                return await successor.Task;
            },
            // Hold the poll loop so only the waiting successor can recover its predecessor.
            PollDelay = async token =>
            {
                bothClaimed.TrySetResult();
                await Task.Delay(Timeout.Infinite, token);
            },
        };
        var run = daemon.RunAsync(shutdown.Token);
        try
        {
            await bothClaimed.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(1, executions);
            Assert.True(File.Exists(predecessorPath));
            Assert.True(File.Exists(successorPath));
            if (cancelled) predecessor.SetCanceled();
            else predecessor.SetException(new IOException("predecessor failed after authority settlement"));
            await successorStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(2, executions);
            Assert.Equal(second.Subject.SubjectId, executedSuccessor!.Subject!.SubjectId);
            Assert.Equal(1, recoveryReads);
            Assert.False(File.Exists(predecessorPath));
            Assert.True(File.Exists(successorPath));
        }
        finally
        {
            shutdown.Cancel();
            predecessor.TrySetResult(false);
            successor.TrySetResult(true);
            await run;
        }
        Assert.False(File.Exists(successorPath));
    }

    [Theory]
    [InlineData("monolith", GateSubjectKinds.Merge)]
    [InlineData("monolith", GateSubjectKinds.PostMerge)]
    [InlineData("task-server", GateSubjectKinds.Merge)]
    [InlineData("task-server", GateSubjectKinds.PostMerge)]
    public async Task LaterArrivalsCannotOvertakeEitherQueue(string authority, string waitingKind)
    {
        var (create, claim, _, _) = await QueueFixture(authority);
        var waiting = await create(QueueRequest(waitingKind, "waiting", []));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var other = waitingKind == GateSubjectKinds.Merge ? GateSubjectKinds.PostMerge : GateSubjectKinds.Merge;
        for (var i = 0; i < 64; i++) await create(QueueRequest(other, "later-" + i, []));
        Assert.Equal(waiting.Subject.SubjectId, (await claim(new("gate", "instance"))).Subject!.SubjectId);
    }

    [Theory]
    [InlineData("monolith")]
    [InlineData("task-server")]
    public async Task ClaimCursorUsesAgeBeforeKindAcrossPages(string authority)
    {
        var (create, claim, _, _) = await QueueFixture(authority);
        for (var i = 0; i < 64; i++)
            await create(QueueRequest(GateSubjectKinds.PostMerge, "blocked-post-" + i, ["fixture:missing"]));
        _clock.Advance(TimeSpan.FromSeconds(1));
        var merge = await create(QueueRequest(GateSubjectKinds.Merge, "later-merge", []));
        // The later merge has a lower kind priority than the page cursor. A
        // kind-first cursor would lose it after scanning the older post-merge page.
        Assert.Equal(merge.Subject.SubjectId, (await claim(new("gate", "instance"))).Subject!.SubjectId);
    }

    [Theory]
    [InlineData(2, 1, GateSubjectKinds.Merge, true)]
    [InlineData(2, 1, GateSubjectKinds.PostMerge, false)]
    [InlineData(5, 4, GateSubjectKinds.Merge, true)]
    [InlineData(5, 4, GateSubjectKinds.PostMerge, false)]
    [InlineData(5, 3, GateSubjectKinds.PostMerge, true)]
    [InlineData(1, 0, null, true)]
    [InlineData(2, 2, GateSubjectKinds.Merge, false)]
    public void PostMergeGrantProtectsAnIntegrationShare(int capacity, int occupied, string? activeKind, bool expected)
        => Assert.Equal(expected, AgentStudio.OrchestrationDecisions.PostMergeSchedulingPolicy.MayReservePostMergeGrant(
            capacity, occupied, activeKind is null ? [] : [activeKind]));

    private sealed class AdmissionServer(Func<GateClaimRequest, Task<GateClaimResponse>> claim,
        Func<string, Task<GateStatus?>>? status = null) : HttpMessageHandler
    {
        private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            object value = request.RequestUri!.AbsolutePath switch
            {
                "/api/clients/register" => new Runner::AgentRunner.ClientRegisterResponse("gate", "gate", "gate-executor"),
                "/api/v1/gates/claims" => await claim((await JsonSerializer.DeserializeAsync<GateClaimRequest>(
                    await request.Content!.ReadAsStreamAsync(ct), Json, ct))!),
                var path when status is not null && path.StartsWith("/api/v1/gates/subjects/", StringComparison.Ordinal)
                    => (await status(path["/api/v1/gates/subjects/".Length..]))!,
                _ => throw new InvalidOperationException("Unexpected request: " + request.RequestUri),
            };
            return new(HttpStatusCode.OK) { Content = new StringContent(JsonSerializer.Serialize(value, Json), Encoding.UTF8, "application/json") };
        }
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
        Func<GateClaimRequest, Task<GateClaimResponse>> Claim, Func<string, Task<GateStatus?>> Status,
        Func<string, Task> Finish)>
        QueueFixture(string authority)
    {
        if (authority == "monolith")
        {
            var store = new MonolithGateStore((string?)null, _clock, new GateHosts());
            return (request => Task.FromResult(store.Create(request)),
                request => Task.FromResult(store.Claim(request)), id => Task.FromResult(store.GetStatus(id)),
                id => { store.Cancel(id); return Task.CompletedTask; });
        }
        var remote = new TaskGateStore(Options.Create(new TaskGateOptions
        {
            DataDirectory = _root, GateAdmissionMode = authority.StartsWith("task-server-grant", StringComparison.Ordinal) ? "grant" : "slot",
        }), _clock);
        await remote.InitializeAsync();
        string[] keys = [GateCapabilities.Executor, GateCapabilities.PostMerge, GateCapabilities.GitMaterialization,
            CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
            CapabilityProtocol.TaskServerConnectivity, CapabilityProtocol.Disk];
        await remote.RegisterRunnerAsync("gate", new RegisterRunnerRequest("gate", "host", "instance", "1",
            TaskServerProtocol.Current, keys), "gate", default);
        await remote.AdvertiseCapabilitiesAsync(new CapabilityAdvertisementRequest("gate", "instance",
            CapabilityProtocol.CurrentSchemaVersion, _clock.GetUtcNow().UtcDateTime, 300, 1,
            keys.Select(key => new AdvertisedCapabilityDto(key, key.Split(':')[0])).ToArray()), "gate", default);
        if (authority.StartsWith("task-server-grant", StringComparison.Ordinal))
        {
            await using var connection = new SqliteConnection($"Data Source={remote.DatabasePath};Pooling=False");
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO runtime_capacity_settings(host_id, max_parallelism, target_load_percent, ramp_strategy,
                    version, updated_at, allocation_version)
                VALUES ('host', $slots, 80, 'balanced', 1, '2026-11-27T12:00:00.0000000Z', 1);
                UPDATE runners SET runtime_capacity_applied_version = 1 WHERE id = 'gate';
                """;
            command.Parameters.AddWithValue("$slots", authority == "task-server-grant-two" ? 2 : 5);
            await command.ExecuteNonQueryAsync();
        }
        return (request => remote.CreateGateSubjectAsync(request, "fixture", default),
            request => remote.ClaimGateAsync(request, "gate", default), id => remote.GetGateStatusAsync(id, default),
            async id => { await remote.CancelGateSubjectAsync(id, "fixture", default); });
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
