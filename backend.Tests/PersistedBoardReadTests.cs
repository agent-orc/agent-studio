using System.Diagnostics;
using System.Net;
using System.Text.Json;

using AgentStudio.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

using Xunit;

namespace AgentStudio.Tests;

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
public sealed class PersistedBoardReadTests : IDisposable
{
    private const string ProjectName = "persisted-board";
    private static readonly TimeSpan NoWait = TimeSpan.FromSeconds(15);

    private readonly string _workspace;
    private readonly string _watchPath;

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
        Microsoft.Data.Sqlite.SqliteConnection.ClearAllPools();
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
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

    /// <summary>The index disk walk of one host, counted and holdable.</summary>
    private sealed class ScanGate
    {
        private int _scans;
        private int _failures;
        public ManualResetEventSlim Open { get; } = new(true);
        public int Scans => Volatile.Read(ref _scans);

        /// <summary>The next <paramref name="count"/> scans fail at once, like an unreadable workspace.</summary>
        public void Fail(int count) => Volatile.Write(ref _failures, count);

        public Func<List<TaskInfo>> Wrap(Func<List<TaskInfo>> scan) => () =>
        {
            Interlocked.Increment(ref _scans);
            if (Interlocked.Decrement(ref _failures) >= 0) throw new IOException("workspace walk failed");
            Assert.True(Open.Wait(TimeSpan.FromSeconds(60)), "the test never released the index scan");
            return scan();
        };
    }

    private WebApplicationFactory<Program> Host(ScanGate gate, bool hostedServices = false) =>
        new TestHostFactory().WithWebHostBuilder(builder =>
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
        using var response = await client.SendAsync(request).WaitAsync(NoWait);
        return (response.StatusCode, await response.Content.ReadAsByteArrayAsync(), response.Headers.ETag?.ToString(),
            Header(response, RestoredBoard.StateHeader), Header(response, RestoredBoard.GenerationHeader));
    }

    private static string? Header(HttpResponseMessage response, string name)
        => response.Headers.TryGetValues(name, out var values) ? values.FirstOrDefault() : null;

    private static Dictionary<string, string> Lanes(byte[] grouped)
        => JsonDocument.Parse(grouped).RootElement.EnumerateObject()
            .Where(property => property.Value.ValueKind == JsonValueKind.Array)
            .ToDictionary(property => property.Name, property => property.Value.GetRawText());

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
        gate.Open.Reset();
        await using var restarted = Host(gate);
        using var restartedClient = Client(restarted);
        try
        {
            var grouped = await Get(restartedClient, "/api/tasks/grouped");
            Assert.Equal(HttpStatusCode.OK, grouped.Status);
            Assert.Equal(RestoredBoard.Restored, grouped.State);
            Assert.Equal(Lanes(liveGrouped), Lanes(grouped.Body));
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
            Assert.Equal(1, gate.Scans);
        }
        finally
        {
            gate.Open.Set();
        }

        var waited = Stopwatch.StartNew();
        (HttpStatusCode Status, byte[] Body, string? ETag, string? State, string? Generation) ready;
        do ready = await Get(restartedClient, "/api/tasks/grouped");
        while (ready.State != RestoredBoard.Ready && waited.Elapsed < TimeSpan.FromSeconds(30) && await Delay());
        Assert.Equal(RestoredBoard.Ready, ready.State);
        Assert.Equal(Lanes(liveGrouped), Lanes(ready.Body));
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
        gate.Open.Reset();
        var restarted = Host(gate, hostedServices: true);
        try
        {
            // The boot no longer publishes before the listener, and no hosted
            // service start waits for the index: the host starts while the
            // first scan is held, and the board answers restored.
            var startup = Task.Run(() => Client(restarted));
            Assert.Same(startup, await Task.WhenAny(startup, Task.Delay(TimeSpan.FromSeconds(45))));
            using var client = await startup;
            var list = await Get(client, "/api/tasks/");
            Assert.Equal(RestoredBoard.Restored, list.State);
            Assert.Equal(liveList, list.Body);
            Assert.True(gate.Scans >= 1, "the boot must start the first publication");
        }
        finally
        {
            // Hosted services wait for the held scan; release it before the stop.
            gate.Open.Set();
            await restarted.DisposeAsync();
        }
    }

    private static async Task<bool> Delay()
    {
        await Task.Delay(25);
        return true;
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
        gate.Open.Reset();

        var read = client.GetAsync("/api/tasks/grouped");
        try
        {
            Assert.NotSame(read, await Task.WhenAny(read, Task.Delay(TimeSpan.FromMilliseconds(300))));
        }
        finally
        {
            gate.Open.Set();
        }
        using var response = await read.WaitAsync(NoWait);
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

        gate.Open.Reset();
        Seed("pb-new", TaskStates.Backlog, "PB-9");
        host.Services.GetRequiredService<TaskIndexCache>().Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        try
        {
            // Both reads return while the refresh is held: neither ran it.
            var dirty = await Get(client, "/api/tasks/grouped");
            Assert.Equal(published.Body, dirty.Body);
            Assert.Equal(published.Generation, dirty.Generation);
            Assert.Equal(RestoredBoard.Ready, dirty.State);
            Assert.Equal(HttpStatusCode.NotModified, (await Get(client, "/api/tasks/grouped", published.ETag)).Status);
            Assert.Equal(published.Generation, (await Get(client, "/api/tasks/")).Generation);
            Assert.True(SpinWait.SpinUntil(() => gate.Scans == 2, TimeSpan.FromSeconds(10)),
                "a dirty read must start one background refresh");
            Assert.Equal(2, gate.Scans);
        }
        finally
        {
            gate.Open.Set();
        }

        var waited = Stopwatch.StartNew();
        (HttpStatusCode Status, byte[] Body, string? ETag, string? State, string? Generation) fresh;
        do fresh = await Get(client, "/api/tasks/grouped");
        while (fresh.Generation == published.Generation && waited.Elapsed < TimeSpan.FromSeconds(30) && await Delay());
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

        gate.Open.Reset();
        host.Services.GetRequiredService<TaskIndexCache>().Invalidate(TaskIndexCache.InvalidationSource.Mutation);
        try
        {
            var detail = await Get(client, "/api/tasks/pb-human");
            Assert.Equal(HttpStatusCode.OK, detail.Status);
            Assert.Equal("pb-human", JsonDocument.Parse(detail.Body).RootElement.GetProperty("info").GetProperty("id").GetString());
        }
        finally
        {
            gate.Open.Set();
        }
    }
}
