using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks the push path for fine-grained task-mutation events:
///   - <see cref="TaskChangeNotifier"/> raises the right typed event with the
///     right payload for every Publish* call, and swallows a throwing
///     subscriber (a bad handler must not poison the mutation path);
///   - <see cref="TaskHubBroadcaster"/> maps each notifier event onto the
///     correct SignalR client method + argument shape, resolves the canonical
///     <see cref="TaskInfo"/> on create/update, and falls back to
///     <c>jobsBulkChanged</c> when the row can no longer be resolved.
///
/// The hub fan-out is captured through a hand-rolled <see cref="IHubContext{THub}"/>
/// fake (the repo bans mocking libraries), so the assertions see exactly the
/// method name and args the broadcaster handed to <c>Clients.All</c>.
/// </summary>
public sealed class JobsHubTests : IDisposable
{
    private readonly string _workspace;
    private readonly string _watchPath;
    private const string Project = "demo";

    public JobsHubTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "rdo-jobshub-tests-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", Project);
        Directory.CreateDirectory(_watchPath);
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    // --- Notifier contract ----------------------------------------------

    [Fact]
    public void Notifier_PublishCreated_RaisesTaskCreatedWithPayload()
    {
        var notifier = NewNotifier();
        TaskChangeEvent? seen = null;
        notifier.TaskCreated += e => seen = e;

        notifier.PublishCreated(Project, "alpha", _watchPath);

        Assert.NotNull(seen);
        Assert.Equal(Project, seen!.Value.ProjectName);
        Assert.Equal("alpha", seen.Value.JobId);
        Assert.Equal(_watchPath, seen.Value.WatchPath);
    }

    [Fact]
    public void Notifier_PublishMoved_RaisesTaskMovedWithLanes()
    {
        var notifier = NewNotifier();
        TaskMoveEvent? seen = null;
        notifier.TaskMoved += e => seen = e;

        notifier.PublishMoved(Project, "beta", _watchPath, "2-ready", "3-progress");

        Assert.NotNull(seen);
        Assert.Equal("beta", seen!.Value.JobId);
        Assert.Equal("2-ready", seen.Value.FromState);
        Assert.Equal("3-progress", seen.Value.ToState);
    }

    [Fact]
    public void Notifier_PublishReordered_CarriesProjectAndLane()
    {
        var notifier = NewNotifier();
        JobsReorderedEvent? seen = null;
        notifier.JobsReordered += e => seen = e;

        notifier.PublishReordered(Project, _watchPath, "0-backlog");

        Assert.NotNull(seen);
        Assert.Equal(Project, seen!.Value.ProjectName);
        Assert.Equal("0-backlog", seen.Value.Lane);
    }

    [Fact]
    public void Notifier_PublishBulkChanged_RaisesParameterlessEvent()
    {
        var notifier = NewNotifier();
        var fired = 0;
        notifier.JobsBulkChanged += () => fired++;

        notifier.PublishBulkChanged();

        Assert.Equal(1, fired);
    }

    [Fact]
    public void Notifier_ThrowingSubscriber_DoesNotPropagate()
    {
        var notifier = NewNotifier();
        notifier.TaskDeleted += _ => throw new InvalidOperationException("boom");

        // A bad handler must be swallowed: the disk write already happened,
        // the push is a courtesy nudge. No exception should escape.
        var ex = Record.Exception(() => notifier.PublishDeleted(Project, "gamma", _watchPath));
        Assert.Null(ex);
    }

    // --- Broadcaster fan-out --------------------------------------------

    [Fact]
    public void Broadcaster_OnDeleted_SendsJobDeletedWithIdAndPath()
    {
        var (notifier, _, _, hub) = BuildBroadcaster();

        notifier.PublishDeleted(Project, "gamma", _watchPath);

        var send = Assert.Single(hub.Sends);
        Assert.Equal("jobDeleted", send.Method);
        var payload = Assert.Single(send.Args)!;
        Assert.Equal("gamma", Prop(payload, "id"));
        Assert.Equal(_watchPath, Prop(payload, "watchPath"));
    }

    [Fact]
    public void Broadcaster_OnReordered_SendsJobsReorderedWithProjectAndLane()
    {
        var (notifier, _, _, hub) = BuildBroadcaster();

        notifier.PublishReordered(Project, _watchPath, "0-backlog");

        var send = Assert.Single(hub.Sends);
        Assert.Equal("jobsReordered", send.Method);
        var payload = Assert.Single(send.Args)!;
        Assert.Equal(Project, Prop(payload, "projectName"));
        Assert.Equal("0-backlog", Prop(payload, "lane"));
    }

    [Fact]
    public void Broadcaster_OnBulkChanged_SendsJobsBulkChangedNoPayload()
    {
        var (notifier, _, _, hub) = BuildBroadcaster();

        notifier.PublishBulkChanged();

        var send = Assert.Single(hub.Sends);
        Assert.Equal("jobsBulkChanged", send.Method);
        Assert.Empty(send.Args);
    }

    [Fact]
    public void Broadcaster_OnCreated_UnresolvableJob_FallsBackToBulkChanged()
    {
        var (notifier, _, _, hub) = BuildBroadcaster();

        // No job folder exists for this id, so the scanner returns null and
        // the broadcaster must not drop the event - it degrades to a re-pull.
        notifier.PublishCreated(Project, "ghost", _watchPath);

        var send = Assert.Single(hub.Sends);
        Assert.Equal("jobsBulkChanged", send.Method);
        Assert.Empty(send.Args);
    }

    [Fact]
    public void Broadcaster_OnCreated_ResolvableJob_SendsJobCreatedWithInfo()
    {
        var (notifier, scanner, machine, hub) = BuildBroadcaster();
        machine.EnsureStateFoldersAndMigrate();

        var mutations = new TaskMutationService(
            scanner,
            new ClientIdentityStore(BuildConfig(), NullLogger<ClientIdentityStore>.Instance),
            new ProjectRegistry(BuildConfig(), NullLogger<ProjectRegistry>.Instance),
            notifier,
            NullLogger<TaskMutationService>.Instance);

        // CreateJob itself fires PublishCreated, so this also covers the
        // publish wiring at the real mutation site, not just the broadcaster.
        var jobId = mutations.CreateJob(new CreateTaskRequest
        {
            Id = "alpha",
            Title = "Alpha task",
            WatchPath = _watchPath,
            Agent = "claude",
        });

        var send = Assert.Single(hub.Sends);
        Assert.Equal("jobCreated", send.Method);
        var info = Assert.IsType<TaskInfo>(Assert.Single(send.Args));
        Assert.Equal(jobId, info.Id);
    }

    [Fact]
    public async Task OrchestratorContextBroadcaster_SendsRefreshHintWithStableContextKey()
    {
        var config = BuildConfig();
        var proxy = new CapturingClientProxy();
        var broadcaster = new OrchestratorContextHubBroadcaster(
            new FakeHubContext(new FakeHubClients(proxy)),
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            NullLogger<OrchestratorContextHubBroadcaster>.Instance);
        var updatedAt = new DateTime(2026, 8, 10, 12, 15, 0, DateTimeKind.Utc);

        await broadcaster.ContextChangedAsync(
            Project,
            $"task:{Project}/AGT-2577",
            updatedAt,
            CancellationToken.None);

        var send = Assert.Single(proxy.Sends);
        Assert.Equal("orchestratorContextChanged", send.Method);
        var payload = Assert.Single(send.Args)!;
        Assert.Equal($"task:{Project}/AGT-2577", Prop(payload, "contextKey"));
        Assert.Equal(Project, Prop(payload, "projectName"));
        Assert.Equal(updatedAt, Prop(payload, "updatedAt"));
    }

    // --- Helpers ---------------------------------------------------------

    private (TaskChangeNotifier notifier, TaskScannerService scanner, TaskStateMachine machine, CapturingClientProxy hub)
        BuildBroadcaster()
    {
        var config = BuildConfig();
        var notifier = NewNotifier();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var machine = new TaskStateMachine(scanner, NullLogger<TaskStateMachine>.Instance);
        var proxy = new CapturingClientProxy();
        _ = new TaskHubBroadcaster(TokenTestServices.Pricing(),
            new FakeHubContext(new FakeHubClients(proxy)),
            scanner,
            new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance),
            notifier,
            NullLogger<TaskHubBroadcaster>.Instance);
        return (notifier, scanner, machine, proxy);
    }

    private static TaskChangeNotifier NewNotifier() =>
        new(NullLogger<TaskChangeNotifier>.Instance);

    private IConfiguration BuildConfig() =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = Project,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
            })
            .Build();

    private static object? Prop(object target, string name) =>
        target.GetType().GetProperty(name)?.GetValue(target);

    // --- Hand-rolled SignalR fakes (no mocking library in this repo) ------

    private sealed class CapturingClientProxy : IClientProxy
    {
        public List<(string Method, object?[] Args)> Sends { get; } = new();

        public Task SendCoreAsync(string method, object?[] args, CancellationToken cancellationToken = default)
        {
            Sends.Add((method, args));
            return Task.CompletedTask;
        }
    }

    private sealed class FakeHubClients : IHubClients
    {
        private readonly IClientProxy _all;
        public FakeHubClients(IClientProxy all) => _all = all;

        public IClientProxy All => _all;
        public IClientProxy AllExcept(IReadOnlyList<string> excludedConnectionIds) => _all;
        public IClientProxy Client(string connectionId) => _all;
        public IClientProxy Clients(IReadOnlyList<string> connectionIds) => _all;
        public IClientProxy Group(string groupName) => _all;
        public IClientProxy Groups(IReadOnlyList<string> groupNames) => _all;
        public IClientProxy GroupExcept(string groupName, IReadOnlyList<string> excludedConnectionIds) => _all;
        public IClientProxy User(string userId) => _all;
        public IClientProxy Users(IReadOnlyList<string> userIds) => _all;
    }

    private sealed class FakeGroupManager : IGroupManager
    {
        public Task AddToGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;

        public Task RemoveFromGroupAsync(string connectionId, string groupName, CancellationToken cancellationToken = default)
            => Task.CompletedTask;
    }

    private sealed class FakeHubContext : IHubContext<TaskHub>
    {
        public FakeHubContext(IHubClients clients) => Clients = clients;
        public IHubClients Clients { get; }
        public IGroupManager Groups { get; } = new FakeGroupManager();
    }
}
