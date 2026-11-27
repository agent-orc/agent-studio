using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Security;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class CardDeletionSafetyTests : IClassFixture<IamHandlerAccessTests.Host>
{
    private static int _nextCardKey;
    private readonly IamHandlerAccessTests.Host _host;
    private readonly string _cardId = "draft-" + Guid.NewGuid().ToString("N");
    private readonly string _cardKey = "DEL-" + Interlocked.Increment(ref _nextCardKey);

    public CardDeletionSafetyTests(IamHandlerAccessTests.Host host)
    {
        _host = host;
        host.Reset();
    }

    [Theory]
    [InlineData("developer")]
    [InlineData("operator-agent")]
    [InlineData("owner")]
    public async Task Delivered_card_cannot_be_erased_even_with_a_reason(string caller)
    {
        using var client = Client(caller);
        var folder = Path.Combine(_host.ProjectPath("alpha"), TaskStates.Backlog, "alpha-card");
        var evidence = Path.Combine(folder, "results", "delivery.md");
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        File.WriteAllText(evidence, "Delivery evidence must survive.");
        _host.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();

        using var response = await client.DeleteAsync(Route("alpha-card") + "&reason=Discard%20duplicate%20delivery");

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("card-delete-protected", await response.Content.ReadAsStringAsync());
        Assert.Equal("Delivery evidence must survive.", File.ReadAllText(evidence));
        Assert.True(File.Exists(Path.Combine(folder, "task.json")));
    }

    [Theory]
    [InlineData(TaskStates.Backlog)]
    [InlineData(TaskStates.Preparation)]
    [InlineData(TaskStates.Ready)]
    public async Task Developer_can_delete_an_unstarted_draft(string lane)
    {
        var folder = Seed(lane);
        using var client = Client("developer");

        using var response = await client.DeleteAsync(Route("draft"));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(Directory.Exists(folder));
    }

    [Theory]
    [InlineData(TaskStates.Progress)]
    [InlineData(TaskStates.AutoReview)]
    [InlineData(TaskStates.HumanReview)]
    [InlineData(TaskStates.Completed)]
    [InlineData(TaskStates.Archive)]
    public async Task Execution_and_delivery_lanes_are_not_hard_deleted(string lane)
    {
        var folder = Seed(lane);
        var before = File.ReadAllText(Path.Combine(folder, "task.json"));
        using var client = Client("owner");

        using var response = await client.DeleteAsync(Route("draft"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "task.json")));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("task-key")]
    [InlineData("id")]
    public async Task Ready_card_with_a_live_lease_is_unchanged(string identity)
    {
        var folder = Seed(TaskStates.Ready);
        var key = identity switch
        {
            "key" => _cardKey,
            "task-key" => TaskIdentity.CreateKey(_host.ProjectPath("alpha"), _cardId),
            _ => _cardId,
        };
        var leases = _host.Services.GetRequiredService<RunLeaseService>();
        var acquired = leases.TryAcquire(new RunLeaseAcquireRequest(key, "fixture-runner", "fixture", "host",
            Environment.ProcessId, "test", IdempotencyKey: Guid.NewGuid().ToString("N")));
        Assert.True(acquired.Granted);
        var before = File.ReadAllText(Path.Combine(folder, "task.json"));
        using var client = Client("owner");

        using var response = await client.DeleteAsync(Route("draft"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "task.json")));
        Assert.Equal(acquired.Lease!.LeaseId, leases.Peek(key).Lease!.LeaseId);
    }

    [Theory]
    [InlineData("results/report.md")]
    [InlineData("logs/cli-output.log")]
    [InlineData("logs/session-events.jsonl")]
    [InlineData("status.md")]
    public async Task Ready_card_with_execution_evidence_is_not_a_draft(string relativePath)
    {
        var folder = Seed(TaskStates.Ready);
        var evidence = Path.Combine(folder, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
        File.WriteAllText(evidence, "Retained execution evidence.");
        _host.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        using var client = Client("developer");

        using var response = await client.DeleteAsync(Route("draft"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Contains("card-delete-protected", await response.Content.ReadAsStringAsync());
        Assert.Equal("Retained execution evidence.", File.ReadAllText(evidence));
    }

    [Fact]
    public void Internal_delete_cannot_bypass_delivery_protection()
    {
        var states = _host.Services.GetRequiredService<TaskStateMachine>();
        Assert.False(states.DeleteJob("alpha-card", _host.ProjectPath("alpha")));
        Assert.True(Directory.Exists(Path.Combine(_host.ProjectPath("alpha"), TaskStates.Backlog, "alpha-card")));
    }

    [Fact]
    public async Task Operator_session_still_requires_its_existing_delete_approval()
    {
        var store = _host.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var agent = store.CreateOperatorAgent(new CreateOperatorAgentRequest("delete session", null,
            [new IamGrantRequest(IamRoles.OperatorAgent, IamScope.ForProject("alpha"))]), owner);
        var session = store.StartOperatorSession(agent.Id, new StartOperatorSessionRequest(null, null), owner);
        using var client = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", session.Secret);

        using var response = await client.DeleteAsync(Route("alpha-card"));

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Contains("approval-required", await response.Content.ReadAsStringAsync());
        Assert.True(Directory.Exists(Path.Combine(_host.ProjectPath("alpha"), TaskStates.Backlog, "alpha-card")));
    }

    [Theory]
    [InlineData(BatchPhase.Formed, TaskStates.AutoReview)]
    [InlineData(BatchPhase.Running, TaskStates.AutoReview)]
    [InlineData(BatchPhase.Publishing, TaskStates.AutoReview)]
    [InlineData(BatchPhase.Running, TaskStates.Ready)]
    public async Task Active_batch_membership_and_card_are_unchanged(BatchPhase phase, string lane)
    {
        var folder = Seed(lane);
        var store = _host.Services.GetRequiredService<BatchGateStore>();
        // clock-independent: batch membership uses persisted phases; seed timestamps never meet the current time.
        var now = new DateTimeOffset(2026, 11, 27, 0, 0, 0, TimeSpan.Zero);
        var sha = new string('a', 40);
        var subject = new BatchGateSubject(_cardKey, "alpha", "repo", "develop", "full", "digest", "v1",
            "refs/heads/agent-studio/results/deletion", sha, "run-delete", 1, 1, sha,
            true, true, true, true, false, null, false, now, 1);
        var scope = new BatchGateScope("alpha", "repo", "develop", "full", "digest", "v1");
        var batchId = "delete-" + Guid.NewGuid().ToString("N");
        var manifest = new BatchGateManifest(batchId, scope, sha,
            BatchGatePolicy.MembershipDigest(sha, scope, [subject]), now, [subject], [], [subject.TaskKey]);
        store.CloseManifest(manifest);
        store.Enqueue(new BatchGatePendingRecord("review-" + batchId, subject, "repo", null, folder,
            "merge", "default", now));
        BatchGateStates.Reach(store, new BatchGateState(batchId, manifest.MembershipDigest, phase,
            phase == BatchPhase.Formed ? null : sha, 1, now));
        var before = File.ReadAllText(Path.Combine(folder, "task.json"));
        using var client = Client("owner");

        using var response = await client.DeleteAsync(Route("draft"));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "task.json")));
        Assert.Equal(phase, store.LatestState(batchId).Phase);
        Assert.Contains(store.ListPending(), item => item.Subject.TaskKey == _cardKey
            && item.ReviewAttemptId == "review-" + batchId);
    }

    private string Seed(string lane)
    {
        var folder = Path.Combine(_host.ProjectPath("alpha"), lane, _cardId);
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = _cardId, key = _cardKey, title = "Unstarted draft", state = lane, order = 1, agent = "codex",
        }));
        _host.Services.GetRequiredService<TaskIndexCache>().ForceRefresh();
        return folder;
    }

    private string Route(string id) => $"/api/tasks/{(id == "draft" ? _cardId : id)}?watchPath={Uri.EscapeDataString(_host.ProjectPath("alpha"))}";

    private HttpClient Client(string caller)
    {
        var store = _host.Services.GetRequiredService<AccessSecurityStore>();
        var owner = store.ListUsers().Single(user => user.Username == "first.owner").Id;
        var client = _host.Factory.CreateClient(new WebApplicationFactoryClientOptions
        { BaseAddress = new Uri("https://studio.test"), HandleCookies = false });
        if (caller == "operator-agent")
        {
            var account = store.CreateServiceAccount(new CreateServiceAccountRequest("delete " + Guid.NewGuid(), null,
                [new IamGrantRequest(IamRoles.OperatorAgent, IamScope.ForProject("alpha"))]), owner);
            var token = store.CreateServiceAccountToken(account.Id, new CreateServiceAccountTokenRequest("delete", null), owner);
            client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token.Secret);
            return client;
        }

        string sessionToken;
        string csrf;
        if (caller == "owner")
        {
            var login = store.Login("first.owner", "correct horse battery staple!", "delete-owner|127.0.0.1");
            sessionToken = login.SessionToken;
            csrf = login.CsrfToken;
        }
        else
        {
            var invite = store.InviteUser(new InviteUserRequest("delete." + Guid.NewGuid().ToString("N"),
                "Deletion developer", "", null, [new IamGrantRequest(IamRoles.Developer, IamScope.ForProject("alpha"))]), owner);
            var accepted = store.AcceptInvitation(new AcceptInvitationRequest(invite.Token, "accepted pass phrase!"));
            sessionToken = accepted.SessionToken;
            csrf = accepted.CsrfToken;
        }
        client.DefaultRequestHeaders.Add("Cookie", $"{AccessSecurityStore.SessionCookieName}={sessionToken}");
        client.DefaultRequestHeaders.Add("X-CSRF-Token", csrf);
        client.DefaultRequestHeaders.Add("Origin", "https://studio.test");
        return client;
    }
}

public sealed class CardDeletionAtomicityTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "card-delete-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData("running")]
    [InlineData("completed")]
    public void Local_execution_without_a_lease_is_preserved(string status)
    {
        var folder = Path.Combine(_root, TaskStates.Ready, "draft");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = "draft", title = "Draft", state = TaskStates.Ready, order = 1,
            ownerClientId = DefaultClientIdentity.Id,
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "deletion", ["WatchPaths:0:Path"] = _root,
        }).Build();
        var scanner = new InterleavingScanner(config);
        var execution = new CliExecution { JobId = "draft", TaskKey = TaskIdentity.CreateKey(_root, "draft"), Status = status };
        var router = new CliRouter(new FakeRunningCliService(execution));
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, cliRouter: router);
        var before = File.ReadAllText(Path.Combine(folder, "task.json"));

        Assert.False(states.DeleteJob("draft", _root));
        Assert.Equal(before, File.ReadAllText(Path.Combine(folder, "task.json")));
        Assert.Same(execution, router.Get(CliTypes.Claude).GetExecution(execution.TaskKey));
    }

    [Fact]
    public void Evidence_written_after_lookup_is_preserved()
    {
        var folder = Path.Combine(_root, TaskStates.Ready, "draft");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = "draft", title = "Draft", state = TaskStates.Ready, order = 1,
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "deletion", ["WatchPaths:0:Path"] = _root,
        }).Build();
        var scanner = new InterleavingScanner(config);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var evidence = Path.Combine(folder, "results", "delivery.md");
        scanner.AfterRead = () =>
        {
            Directory.CreateDirectory(Path.GetDirectoryName(evidence)!);
            File.WriteAllText(evidence, "New delivery.");
        };

        Assert.False(states.DeleteJob("draft", _root));
        Assert.Equal("New delivery.", File.ReadAllText(evidence));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Move_between_lookup_and_delete_preserves_the_new_lane(bool flat)
    {
        foreach (var lane in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_root, lane));
        var folder = flat ? Path.Combine(_root, "tasks", "000", "DEL-1")
            : Path.Combine(_root, TaskStates.Ready, "draft");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
        {
            id = "draft", key = "DEL-1", title = "Draft", state = TaskStates.Ready, order = 1,
        }));
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "deletion", ["WatchPaths:0:Path"] = _root,
        }).Build();
        var scanner = new InterleavingScanner(config);
        var mutex = new LaneMutexRegistry(NullLogger<LaneMutexRegistry>.Instance);
        var states = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance, laneMutex: mutex);
        if (flat) TaskLayoutIndex.Rebuild(_root, NullLogger.Instance);
        var snapshotRead = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scanner.AfterRead = () => snapshotRead.SetResult();
        var held = mutex.Acquire(_root);
        Task<bool> deletion;
        // The competing flow must not inherit the reentrant lane lock.
        using (ExecutionContext.SuppressFlow())
            deletion = Task.Run(() => states.DeleteJob("draft", _root));
        MoveJobOutcome? moved = null;
        var deleted = false;
        try
        {
            // The delete has read its initial snapshot, but cannot take the
            // lane lock until the competing writer has changed the lane.
            await snapshotRead.Task.WaitAsync(TimeSpan.FromSeconds(30));
            moved = states.MoveJob("draft", TaskStates.Preparation, _root);
        }
        finally
        {
            held.Dispose();
            deleted = await deletion.WaitAsync(TimeSpan.FromSeconds(30));
        }
        Assert.Equal(MoveJobStatus.Success, moved?.Status);
        Assert.False(deleted);
        var current = scanner.FindJob("draft", _root);
        Assert.NotNull(current);
        Assert.Equal(TaskStates.Preparation, current.State);
        Assert.True(File.Exists(Path.Combine(current.FolderPath, "task.json")));
        if (flat) Assert.Equal(folder, current.FolderPath);
    }

    public void Dispose() => AgentStudio.TestSupport.TestTempRoot.TryDelete(_root);

    private sealed class InterleavingScanner(IConfiguration config)
        : TaskScannerService(config, NullLogger<TaskScannerService>.Instance,
            new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config))
    {
        public Action? AfterRead;

        public override TaskInfo? ScanJobFolder(string jobDir, WatchPathEntry entry, string state, bool bypassMemo = false)
        {
            var info = base.ScanJobFolder(jobDir, entry, state, bypassMemo);
            if (info is not null) Interlocked.Exchange(ref AfterRead, null)?.Invoke();
            return info;
        }
    }
}
