using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Text.Json;

using AgentStudio.Tasks;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Restarts remain serial after unrestricted parallel runs timed out on the
/// restored read. The timeout stack showed no product worker waiting on the
/// held scan; test-host endpoint warmup still waits synchronously.
/// </summary>
[CollectionDefinition(PersistedBoardRestartCollection.Name, DisableParallelization = true)]
public sealed class PersistedBoardRestartCollection
{
    public const string Name = "PersistedBoardRestart";
}

/// <summary>
/// Board reads around the task index publication (Git facts S5, AGT-3088):
/// <list type="bullet">
///   <item>after a restart, a board read answers from the persisted board,
///   marked restored, while the first workspace scan is still held open, and
///   the restored cards are the cards the live read served before;</item>
///   <item>without a persisted board, the cold read still waits for the first
///   publication;</item>
///   <item>a dirty board read serves the published generation and refreshes
///   only in the background;</item>
///   <item>the legacy detail read never waits for the board index.</item>
/// </list>
/// Each host runs without hosted services, so the only index scans are the
/// ones these reads start, and each scan can be held open.
/// </summary>
[Collection(PersistedBoardRestartCollection.Name)]
public sealed class PersistedBoardReadTests : IDisposable
{
    private const string ProjectName = "persisted-board";
    private static readonly TimeSpan NoWait = TimeSpan.FromSeconds(15);
    // Bounds waits for an event that a correct host always reaches: a host
    // start, a scan entering, a held publication finishing. Only a wait that
    // never ends reaches it, so it is not a latency budget; a host start
    // alone took three to four minutes at load 80 on 12 cores.
    private static readonly TimeSpan HangLimit = TimeSpan.FromMinutes(5);

    private readonly string _workspace;
    private readonly string _watchPath;
    private readonly ConcurrentBag<ScanGate> _gates = [];

    private sealed class StartupCalls
    {
        private readonly ConcurrentDictionary<string, string> _pending = new();
        private int _started;
        private int _applicationStarted;
        private int _warmupEntered;
        private int _warmupCompleted;
        private readonly TaskCompletionSource<HttpClient> _clientReady =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Registered { get; set; }
        public Task<HttpClient> ClientReady => _clientReady.Task;
        public void ApplicationStarted(IServer server)
        {
            Volatile.Write(ref _applicationStarted, 1);
            try
            {
                var client = ((TestServer)server).CreateClient();
                client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
                _clientReady.TrySetResult(client);
            }
            catch (Exception ex) { _clientReady.TrySetException(ex); }
        }
        public void WarmupEntered() => Volatile.Write(ref _warmupEntered, 1);
        public void WarmupCompleted() => Volatile.Write(ref _warmupCompleted, 1);
        public void Enter(string name, string phase) => _pending[name] = phase;
        public void Leave(string name) => _pending.TryRemove(name, out _);
        public void CompletedStart() => Interlocked.Increment(ref _started);
        public string Describe() => $"started={Volatile.Read(ref _started)}/{Registered}; "
            + $"applicationStarted={Volatile.Read(ref _applicationStarted) == 1}; "
            + $"warmup={Volatile.Read(ref _warmupEntered) == 1}/{Volatile.Read(ref _warmupCompleted) == 1}; pending="
            + string.Join(", ", _pending.OrderBy(pair => pair.Key)
                .Select(pair => $"{pair.Key}: {pair.Value}"));
    }

    private sealed class ApplicationStartedSignal(
        IHostApplicationLifetime lifetime, IServer server, StartupCalls calls) : IHostedService
    {
        public Task StartAsync(CancellationToken ct)
        {
            lifetime.ApplicationStarted.Register(() => calls.ApplicationStarted(server));
            return Task.CompletedTask;
        }

        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class ScanStartBarrier(ScanGate gate) : IHostedService
    {
        public Task StartAsync(CancellationToken ct) => gate.Entered(1).WaitAsync(ct);
        public Task StopAsync(CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class WarmupProbeFilter(StartupCalls calls) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Path.Value != TestHostFactory.EndpointWarmUpPath)
                {
                    await nextMiddleware();
                    return;
                }
                calls.WarmupEntered();
                try { await nextMiddleware(); }
                finally { calls.WarmupCompleted(); }
            });
            next(app);
        };
    }

    private sealed class TracedHostedService(IHostedService inner, StartupCalls calls, string name)
        : BackgroundService, IHostedLifecycleService
    {
        public override Task? ExecuteTask => (inner as BackgroundService)?.ExecuteTask;

        private async Task Trace(string phase, Func<Task> run)
        {
            calls.Enter(name, phase);
            try
            {
                await run();
                if (phase == "StartAsync") calls.CompletedStart();
            }
            finally { calls.Leave(name); }
        }

        public Task StartAsync(CancellationToken ct) => Trace("StartAsync", () => inner.StartAsync(ct));
        public Task StopAsync(CancellationToken ct) => inner.StopAsync(ct);
        public Task StartingAsync(CancellationToken ct) => Trace("StartingAsync", () =>
            inner is IHostedLifecycleService lifecycle ? lifecycle.StartingAsync(ct) : Task.CompletedTask);
        public Task StartedAsync(CancellationToken ct) => Trace("StartedAsync", () =>
            inner is IHostedLifecycleService lifecycle ? lifecycle.StartedAsync(ct) : Task.CompletedTask);
        public Task StoppingAsync(CancellationToken ct) =>
            inner is IHostedLifecycleService lifecycle ? lifecycle.StoppingAsync(ct) : Task.CompletedTask;
        public Task StoppedAsync(CancellationToken ct) =>
            inner is IHostedLifecycleService lifecycle ? lifecycle.StoppedAsync(ct) : Task.CompletedTask;

        protected override Task ExecuteAsync(CancellationToken ct) => Task.CompletedTask;
        public override void Dispose() { GC.SuppressFinalize(this); }
    }

    private static void TraceStarts(IServiceCollection services, StartupCalls calls)
    {
        for (var index = 0; index < services.Count; index++)
        {
            var descriptor = services[index];
            if (descriptor.ServiceType != typeof(IHostedService) || descriptor.IsKeyedService) continue;
            calls.Registered++;
            var key = $"persisted-board-hosted-{index}";
            var name = descriptor.ImplementationType?.Name ?? $"hosted-{index}";
            if (descriptor.ImplementationInstance is { } instance)
                services.Add(new ServiceDescriptor(typeof(IHostedService), key, instance));
            else if (descriptor.ImplementationFactory is { } factory)
                services.Add(new ServiceDescriptor(typeof(IHostedService), key,
                    (provider, _) => factory(provider), descriptor.Lifetime));
            else
                services.Add(new ServiceDescriptor(typeof(IHostedService), key,
                    descriptor.ImplementationType!, descriptor.Lifetime));
            services[index] = new ServiceDescriptor(typeof(IHostedService), provider =>
            {
                calls.Enter(name, "resolve");
                try
                {
                    var inner = provider.GetRequiredKeyedService<IHostedService>(key);
                    return new TracedHostedService(inner, calls, inner.GetType().Name);
                }
                finally { calls.Leave(name); }
            }, descriptor.Lifetime);
        }
    }

    public PersistedBoardReadTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-persisted-board-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", ProjectName);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        Seed("pb-backlog", TaskStates.Backlog, "PB-1");
        Seed("pb-ready", TaskStates.Ready, "PB-2");
        Seed("pb-auto", TaskStates.AutoReview, "PB-3");
        Seed("pb-human", TaskStates.HumanReview, "PB-4");
        Seed("pb-escalated", TaskStates.Escalated, "PB-5");
        Seed("pb-done", TaskStates.Completed, "PB-6");
        Seed("pb-fixture", TaskStates.Ready, "PB-7", fixture: true);
        Seed("pb-archived", TaskStates.Archive, "PB-8");
    }

    public void Dispose()
    {
        // A failed test may end before its own release; no walk outlives it.
        foreach (var gate in _gates) gate.Release();
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Debug.WriteLine($"PersistedBoardReadTests temporary workspace cleanup failed: {ex}");
        }
    }

    private void Seed(string id, string state, string key, bool fixture = false)
    {
        var dir = Path.Combine(_watchPath, state, id);
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "task.json"), JsonSerializer.Serialize(new
        {
            id, key, title = "Card " + id, state, order = 1, agent = "claude", fixture,
        }));
    }

    /// <summary>
    /// The index disk walk of one host, counted and holdable. A held walk
    /// waits until the test releases it, and the class releases every gate
    /// when the test ends. No clock ends the hold: a restart test holds the
    /// first walk across the whole host start, which takes minutes on a loaded
    /// host, so a timed hold gave up before the test read the restored board.
    /// </summary>
    private sealed class ScanGate
    {
        private readonly ManualResetEventSlim _open = new(true);
        private readonly ConcurrentDictionary<int, TaskCompletionSource> _entered = new();
        private int _scans;
        private int _completed;
        private int _failures;

        /// <summary>Walks that have entered, held or not.</summary>
        public int Scans => Volatile.Read(ref _scans);

        /// <summary>Walks that have returned their task list.</summary>
        public int Completed => Volatile.Read(ref _completed);

        public void Hold() => _open.Reset();

        public void Release() => _open.Set();

        /// <summary>Completes once <paramref name="count"/> walks have entered.</summary>
        public Task Entered(int count)
        {
            var entered = _entered.GetOrAdd(count,
                _ => new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously));
            if (Scans >= count) entered.TrySetResult();
            return entered.Task;
        }

        /// <summary>The next <paramref name="count"/> scans fail at once, like an unreadable workspace.</summary>
        public void Fail(int count) => Volatile.Write(ref _failures, count);

        public Func<List<TaskInfo>> Wrap(Func<List<TaskInfo>> scan) => () =>
        {
            var entered = Interlocked.Increment(ref _scans);
            foreach (var (count, signal) in _entered)
                if (count <= entered) signal.TrySetResult();
            if (Interlocked.Decrement(ref _failures) >= 0) throw new IOException("workspace walk failed");
            _open.Wait();
            var tasks = scan();
            Interlocked.Increment(ref _completed);
            return tasks;
        };
    }

    private WebApplicationFactory<Program> Host(ScanGate gate, bool hostedServices = false, StartupCalls? calls = null)
    {
        _gates.Add(gate);
        return new TestHostFactory().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.ConfigureAppConfiguration((_, config) => config.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:Path"] = _watchPath,
                ["WatchPaths:0:RootPath"] = _watchPath,
                ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
                ["GitStateIndex:SweepIntervalSeconds"] = "3600",
                ["GitStateIndex:MaxRetries"] = "0",
            }));
            builder.ConfigureTestServices(services =>
            {
                if (!hostedServices) services.RemoveAll<IHostedService>();
                if (calls is not null)
                {
                    services.Insert(0, ServiceDescriptor.Singleton<IHostedService>(new ScanStartBarrier(gate)));
                    TraceStarts(services, calls);
                    services.AddSingleton<IHostedService>(provider => new ApplicationStartedSignal(
                        provider.GetRequiredService<IHostApplicationLifetime>(),
                        provider.GetRequiredService<IServer>(), calls));
                    services.AddSingleton<IStartupFilter>(new WarmupProbeFilter(calls));
                }
                services.AddSingleton(provider =>
                {
                    var scanner = provider.GetRequiredService<TaskScannerService>();
                    return new TaskIndexCache(
                        scanner,
                        provider.GetRequiredService<ILogger<TaskIndexCache>>(),
                        provider.GetRequiredService<IConfiguration>(),
                        gate.Wrap(scanner.ScanAllJobsRaw));
                });
            });
        });
    }

    private static HttpClient Client(WebApplicationFactory<Program> host)
    {
        var client = host.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        return client;
    }

    private static async Task<(HttpStatusCode Status, byte[] Body, string? ETag, string? State, string? Generation)> Get(
        HttpClient client, string url, string? ifNoneMatch = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        HttpResponseMessage response;
        try { response = await client.SendAsync(request).WaitAsync(NoWait); }
        catch (TimeoutException ex)
        {
            var pool = PoolState();
            throw new TimeoutException($"GET {url} timed out; {pool}", ex);
        }
        using (response)
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Headers.ETag?.ToString(),
            Header(response, RestoredBoard.StateHeader), Header(response, RestoredBoard.GenerationHeader));
    }

    /// <summary>Awaits <paramref name="task"/>, failing with <paramref name="what"/> if it never ends.</summary>
    private static Task Within(Task task, string what) => Within(task, () => what);

    private static async Task Within(Task task, Func<string> what)
    {
        if (await Task.WhenAny(task, Task.Delay(HangLimit)) != task)
            Assert.Fail($"{what()} still pending after {HangLimit.TotalMinutes} min; {PoolState()}");
        await task;
    }

    /// <summary>
    /// Starts <paramref name="host"/> off the test thread. A start that waited
    /// for the held scan would never return, and the test must still reach the
    /// release of that scan.
    /// </summary>
    private static async Task<HttpClient> Start(WebApplicationFactory<Program> host, ScanGate gate)
    {
        var start = Task.Run(() => Client(host));
        await Within(start, () => $"host start (scans={gate.Scans}, completed={gate.Completed})");
        return await start;
    }

    private static string PoolState() =>
        $"threadPoolThreads={ThreadPool.ThreadCount}; pendingWorkItems={ThreadPool.PendingWorkItemCount}; "
        + $"completedWorkItems={ThreadPool.CompletedWorkItemCount}; processId={Environment.ProcessId}";

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static Dictionary<string, string> Lanes(byte[] grouped)
        => JsonDocument.Parse(grouped).RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Array)
            .ToDictionary(property => property.Name, property => property.Value.GetRawText());

    /// <summary>Same lanes, naming the first lane that differs and both of its bodies.</summary>
    private static void AssertSameLanes(byte[] expected, byte[] actual, string what)
    {
        var want = Lanes(expected);
        var got = Lanes(actual);
        var lane = want.Keys.Union(got.Keys).Order(StringComparer.Ordinal)
            .FirstOrDefault(name => want.GetValueOrDefault(name) != got.GetValueOrDefault(name));
        Assert.True(lane is null,
            $"{what}: lane '{lane}' differs.\nexpected: {want.GetValueOrDefault(lane ?? "")}\nactual:   {got.GetValueOrDefault(lane ?? "")}");
    }

    [Fact]
    public async Task After_a_restart_the_board_answers_from_the_persisted_board_marked_restored_while_the_first_scan_runs()
    {
        byte[] liveList, liveGrouped, liveFixtures;
        await using (var first = Host(new ScanGate()))
        {
            using var client = Client(first);
            liveGrouped = (await Get(client, "/api/tasks/grouped")).Body;
            liveList = (await Get(client, "/api/tasks/")).Body;
            liveFixtures = (await Get(client, "/api/tasks/?includeFixtures=true")).Body;
            Assert.Equal(1, first.Services.GetRequiredService<BoardSummaryPersister>().PersistPublished());
        }

        var gate = new ScanGate();
        gate.Hold();
        await using var restarted = Host(gate);
        HttpClient restartedClient;
        Task? firstPublication;
        try
        {
            // The boot starts the first publication, and the host starts while it is held.
            restartedClient = await Start(restarted, gate);
            firstPublication = restarted.Services.GetRequiredService<TaskIndexCache>().RefreshInFlight;
            Assert.NotNull(firstPublication);

            var grouped = await Get(restartedClient, "/api/tasks/grouped");
            Assert.Equal(HttpStatusCode.OK, grouped.Status);
            Assert.Equal(RestoredBoard.Restored, grouped.State);
            AssertSameLanes(liveGrouped, grouped.Body, "restored read");
            Assert.Equal(["pb-auto"], JsonDocument.Parse(grouped.Body).RootElement.GetProperty("review")
                .EnumerateArray().Select(card => card.GetProperty("id").GetString()));
            Assert.True(JsonDocument.Parse(grouped.Body).RootElement.GetProperty("stale").GetBoolean());

            // The list body is the live body, byte for byte, under the same tag rule.
            var list = await Get(restartedClient, "/api/tasks/");
            Assert.Equal(RestoredBoard.Restored, list.State);
            Assert.Equal(liveList, list.Body);
            Assert.Equal(liveFixtures, (await Get(restartedClient, "/api/tasks/?includeFixtures=true")).Body);
            Assert.Equal(HttpStatusCode.NotModified, (await Get(restartedClient, "/api/tasks/grouped", grouped.ETag)).Status);

            // The first publication was started once, off the reads, and is still held.
            await gate.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(1, gate.Scans);
            Assert.Equal(0, gate.Completed);
        }
        finally
        {
            gate.Release();
        }

        using (restartedClient)
        {
            await Within(firstPublication, "the first publication");
            var ready = await Get(restartedClient, "/api/tasks/grouped");
            Assert.Equal(RestoredBoard.Ready, ready.State);
            AssertSameLanes(liveGrouped, ready.Body, "ready read");
        }
    }

    [Fact]
    public async Task A_restarted_host_with_every_hosted_service_starts_and_answers_restored_before_the_first_scan()
    {
        byte[] liveList;
        await using (var first = Host(new ScanGate()))
        {
            using var client = Client(first);
            liveList = (await Get(client, "/api/tasks/")).Body;
            Assert.Equal(1, first.Services.GetRequiredService<BoardSummaryPersister>().PersistPublished());
        }

        var gate = new ScanGate();
        gate.Hold();
        var calls = new StartupCalls();
        var restarted = Host(gate, hostedServices: true, calls);
        Task<HttpClient>? startup = null;
        try
        {
            // The boot no longer publishes before the listener, and no hosted
            // service start waits for the index: the host starts while the
            // first scan is held, and the board answers restored.
            var entered = 0;
            startup = Task.Run(() =>
            {
                Volatile.Write(ref entered, 1);
                return Client(restarted);
            });
            // The barrier starts every hosted service after the held scan has
            // entered. ApplicationStarted proves none waited for publication;
            // the factory's unrelated endpoint warmup may finish later.
            var ready = calls.ClientReady;
            var started = await Task.WhenAny(ready, Task.Delay(HangLimit)) == ready;
            var startPool = started ? null : PoolState();
            Assert.True(ready.IsCompleted,
                $"host start pending; entered={Volatile.Read(ref entered) == 1}; scans={gate.Scans}; hosted calls={calls.Describe()}; {startPool}");
            using var client = await ready;
            // Every request waits for routing, which the factory's warmup
            // builds once after the start; on a loaded host that build alone
            // outlasts the read bound. Wait for it, with the scan still held.
            await Within(startup, () => $"endpoint warmup (scans={gate.Scans}; hosted calls={calls.Describe()})");
            var list = await Get(client, "/api/tasks/");
            Assert.Equal(RestoredBoard.Restored, list.State);
            Assert.Equal(liveList, list.Body);
            Assert.True(gate.Scans >= 1, "the boot must start the first publication");
            Assert.True(gate.Completed == 0, "the restored read must be answered while the first scan is held");
            var index = restarted.Services.GetRequiredService<TaskIndexCache>();
            Assert.True(index.BlockingRefreshWaits == 0,
                $"blocking refresh waits={index.BlockingRefreshWaits}; {PoolState()}; first stack={index.FirstBlockingRefreshWaitStack}");
        }
        finally
        {
            // Hosted services wait for the held scan; release it before the stop.
            gate.Release();
            try
            {
                if (startup is not null)
                {
                    using var factoryClient = await startup.WaitAsync(HangLimit);
                }
            }
            finally { await restarted.DisposeAsync(); }
        }
    }

    [Fact]
    public async Task Without_a_persisted_board_the_cold_read_waits_for_the_first_publication()
    {
        // Without a persisted board the boot keeps its synchronous first scan;
        // here it fails, so the first read meets a cold index.
        var gate = new ScanGate();
        gate.Fail(1);
        await using var host = Host(gate);
        using var client = Client(host);
        Assert.Equal(1, gate.Scans);
        gate.Hold();

        var read = client.GetAsync("/api/tasks/grouped");
        try
        {
            // The read starts the first publication, then waits for it.
            var entered = gate.Entered(2);
            await Within(Task.WhenAny(entered, read), "the cold read");
            Assert.True(entered.IsCompleted, "the cold read must start the first publication");
            Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(300))));
        }
        finally
        {
            gate.Release();
        }
        await Within(read, "the cold read after the release");
        using var response = await read;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal(RestoredBoard.Ready, Header(response, RestoredBoard.StateHeader));
    }

    [Fact]
    public async Task A_dirty_board_read_serves_the_published_generation_and_refreshes_only_in_the_background()
    {
        var gate = new ScanGate();
        await using var host = Host(gate);
        using var client = Client(host);
        var published = await Get(client, "/api/tasks/grouped");
        // The boot published the first generation; the read did not scan.
        Assert.Equal(1, gate.Scans);

        gate.Hold();
        Seed("pb-new", TaskStates.Backlog, "PB-9");
        var index = host.Services.GetRequiredService<TaskIndexCache>();
        index.Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        Task? refresh;
        try
        {
            // Both reads return while the refresh is held: neither ran it.
            var dirty = await Get(client, "/api/tasks/grouped");
            Assert.Equal(published.Body, dirty.Body);
            Assert.Equal(published.Generation, dirty.Generation);
            Assert.Equal(RestoredBoard.Ready, dirty.State);
            Assert.Equal(HttpStatusCode.NotModified, (await Get(client, "/api/tasks/grouped", published.ETag)).Status);
            Assert.Equal(published.Generation, (await Get(client, "/api/tasks/")).Generation);
            // The dirty read started one background refresh, which is held.
            await Within(gate.Entered(2), "the background refresh of the dirty read");
            refresh = index.RefreshInFlight;
            Assert.NotNull(refresh);
            Assert.Equal(2, gate.Scans);
            Assert.Equal(1, gate.Completed);
        }
        finally
        {
            gate.Release();
        }

        await Within(refresh, "the background refresh after the release");
        var fresh = await Get(client, "/api/tasks/grouped");
        Assert.NotEqual(published.Generation, fresh.Generation);
        Assert.Contains("pb-new", Lanes(fresh.Body)["backlog"]);
        Assert.Equal(2, gate.Scans);
    }

    [Fact]
    public async Task The_legacy_detail_read_does_not_wait_for_the_board_index()
    {
        var gate = new ScanGate();
        await using var host = Host(gate);
        using var client = Client(host);
        await Get(client, "/api/tasks/grouped");

        gate.Hold();
        host.Services.GetRequiredService<TaskIndexCache>().Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        try
        {
            var detail = await Get(client, "/api/tasks/pb-human");
            Assert.Equal(HttpStatusCode.OK, detail.Status);
            Assert.Equal("pb-human", JsonDocument.Parse(detail.Body).RootElement.GetProperty("info").GetProperty("id").GetString());
        }
        finally
        {
            gate.Release();
        }
    }
}
