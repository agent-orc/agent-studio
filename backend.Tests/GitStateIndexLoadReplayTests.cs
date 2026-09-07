using System.Diagnostics;
using System.Net.Http.Json;
using System.Text.Json;

using AgentStudio.Git;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// The AGT-2726 regression bound, replayed against a real backend and a real
/// repository.
///
/// <para>
/// The measurement this pins down was taken on 6 September 2026 between 20:00
/// and 20:55 on the operator laptop: 315 <c>tasks/list-refresh</c> runs, 351
/// <c>tasks/grouped</c> calls, 173 <c>git/inventory</c> calls and 27
/// <c>tasks/list</c> calls produced about 3,980 git processes, roughly 72 a
/// minute, while <c>tasks/grouped</c> spent 2,775 seconds waiting with zero
/// spawns of its own. The defect was structural: git-derived state was computed
/// on request paths under a process-wide gate, and every refresh trigger
/// repeated it.
/// </para>
///
/// <para>
/// The replay issues the same request mix. What it asserts is the shape of the
/// fix rather than a wall-clock number from one laptop: request volume must not
/// drive git spawns at all, and no request path may fork git. The spawn ceiling
/// is deliberately far below the measured 3,980 while leaving room for the
/// index's own startup capture and its safety sweep.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class GitStateIndexLoadReplayTests : IDisposable
{
    /// <summary>The measured 55-minute request mix.</summary>
    private const int GroupedCalls = 351;
    private const int ListCalls = 27;
    private const int InventoryCalls = 173;

    /// <summary>
    /// Spawn ceiling for the whole replay. The index captures each repository
    /// once at startup and again per observed ref move; nothing else in this
    /// window changes state. The measured window produced 3,980.
    /// </summary>
    private const int SpawnCeiling = 250;

    /// <summary>
    /// Spawn ceiling for one endpoint's share of the replay. Nothing changes
    /// repository state inside a phase, so the only spawns a correct
    /// implementation can produce there come from the index's safety sweep.
    /// </summary>
    private const int PhaseSpawnCeiling = 60;

    /// <summary>Board budget from the acceptance criteria.</summary>
    private const long GroupedP95BudgetMs = 300;

    private readonly ITestOutputHelper _output;
    private readonly string _root;
    private readonly string _repo;
    private readonly string _jobs;
    private readonly string _workspace;

    public GitStateIndexLoadReplayTests(ITestOutputHelper output)
    {
        _output = output;
        _root = Path.Combine(Path.GetTempPath(), "git-index-replay-" + Guid.NewGuid().ToString("N"));
        _repo = Path.Combine(_root, "repo");
        _jobs = Path.Combine(_root, "jobs");
        _workspace = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(_repo);
        Directory.CreateDirectory(_jobs);
        Directory.CreateDirectory(_workspace);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatchNote(ex); }
    }

    [Fact]
    public async Task ReplayedTriggerPattern_KeepsRequestPathsGitFreeAndTheSpawnBudgetIntact()
    {
        SeedRepository();
        SeedTask();

        var telemetry = new StructuredTelemetryLoggerProvider();
        using var factory = BuildFactory(telemetry);
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
        var index = factory.Services.GetRequiredService<GitStateIndex>();

        using (var readiness = await client.GetAsync("/healthz", timeout.Token))
            readiness.EnsureSuccessStatusCode();

        // Let the index discover the repository and take its first capture, the
        // same way a freshly started backend would before an operator looks at
        // the board.
        Assert.True(
            await WaitUntilAsync(
                () => index.Snapshot(_repo) is not null,
                TimeSpan.FromSeconds(30),
                timeout.Token),
            "The git index never captured the repository.");

        var warmSpawns = TotalSpawns(telemetry);

        // Replay the measured request mix. No repository state changes during
        // it, so a correct implementation spawns nothing on its account.
        // Each phase is also measured on its own, so a regression in one
        // endpoint is named rather than hidden inside a total.
        var groupedSpawns = await ReplayPhase(
            telemetry, client, "/api/tasks/grouped", GroupedCalls, timeout.Token);
        var listSpawns = await ReplayPhase(
            telemetry, client, "/api/tasks", ListCalls, timeout.Token);
        var inventorySpawns = await ReplayPhase(
            telemetry, client, "/api/git/inventory?project=replay", InventoryCalls, timeout.Token);

        // 1. No request path forks git. Each of these labels is opened by the
        //    endpoint itself with nested aggregation, so its rollup accounts for
        //    every spawn the request caused. Background inventory computation
        //    carries the separate git/inventory-compute label and is excluded by
        //    construction rather than by tolerance.
        foreach (var label in new[] { "tasks/grouped", "tasks/list", "git/inventory" })
        {
            var rollups = telemetry.Rollups(label);
            Assert.NotEmpty(rollups);
            Assert.All(
                rollups,
                rollup => Assert.True(
                    rollup.Spawns == 0,
                    $"{label} forked {rollup.Spawns} git processes on the request path."));
        }

        // 2. Request volume does not drive spawns. Per phase, so a regression in
        //    any one endpoint is named rather than hidden in a total.
        var replaySpawns = groupedSpawns + listSpawns + inventorySpawns;
        Assert.True(
            groupedSpawns <= PhaseSpawnCeiling,
            $"{GroupedCalls} grouped requests produced {groupedSpawns} git spawns; the ceiling is {PhaseSpawnCeiling}.");
        Assert.True(
            listSpawns <= PhaseSpawnCeiling,
            $"{ListCalls} list requests produced {listSpawns} git spawns; the ceiling is {PhaseSpawnCeiling}.");
        Assert.True(
            inventorySpawns <= PhaseSpawnCeiling,
            $"{InventoryCalls} inventory requests produced {inventorySpawns} git spawns; the ceiling is {PhaseSpawnCeiling}.");
        Assert.True(
            replaySpawns <= SpawnCeiling,
            $"{GroupedCalls + ListCalls + InventoryCalls} requests produced {replaySpawns} git spawns; "
            + $"the ceiling is {SpawnCeiling} and the pre-index measurement was about 3,980.");

        // 3. The board budget.
        var groupedP95 = Percentile(telemetry.Rollups("tasks/grouped").Select(r => r.WallMs), 95);
        _output.WriteLine(
            $"replay requests={GroupedCalls + ListCalls + InventoryCalls} "
            + $"spawns={replaySpawns} (ceiling {SpawnCeiling}, pre-index measurement about 3980) "
            + $"grouped p50={Percentile(telemetry.Rollups("tasks/grouped").Select(r => r.WallMs), 50)}ms "
            + $"p95={groupedP95}ms (budget {GroupedP95BudgetMs}ms) "
            + $"warmupSpawns={warmSpawns}");
        Assert.True(
            groupedP95 < GroupedP95BudgetMs,
            $"tasks/grouped p95 was {groupedP95} ms over {GroupedCalls} calls; the budget is {GroupedP95BudgetMs} ms.");

        // 4. Freshness: a ref move is folded into the index promptly, without
        //    the request path waiting for it.
        var before = index.Snapshot(_repo)!.CapturedAtUtc;
        RunGit(_repo, "checkout", "-q", "-b", "task/replay-churn");
        File.WriteAllText(Path.Combine(_repo, "churn.txt"), "moved\n");
        RunGit(_repo, "add", "churn.txt");
        RunGit(_repo, "commit", "-q", "-m", "test: move a ref");
        Assert.True(
            await WaitUntilAsync(
                () => index.Snapshot(_repo)!.CapturedAtUtc > before,
                TimeSpan.FromSeconds(10),
                timeout.Token),
            "The git index did not pick the ref move up inside its ten-second freshness budget.");

        // 5. The stamp reaches the wire, and only as headers. Clients read the
        //    grouped response as a lane map and iterate its values as task
        //    arrays, so a scalar sibling there would be a breaking change.
        using var grouped = await client.GetAsync("/api/tasks/grouped", timeout.Token);
        grouped.EnsureSuccessStatusCode();
        Assert.True(grouped.Headers.TryGetValues(GitStateStampHeader.AtHeader, out _));
        Assert.True(grouped.Headers.TryGetValues(GitStateStampHeader.StaleHeader, out _));
        var payload = await grouped.Content.ReadFromJsonAsync<Dictionary<string, JsonElement>>(
            cancellationToken: timeout.Token);
        Assert.NotNull(payload);
        Assert.All(
            payload!,
            lane => Assert.True(
                lane.Value.ValueKind == JsonValueKind.Array,
                $"grouped.{lane.Key} is {lane.Value.ValueKind}; every value of the grouped "
                + "response must stay a task array or clients that iterate it break."));

        using var inventory = await client.GetAsync("/api/git/inventory?project=replay", timeout.Token);
        inventory.EnsureSuccessStatusCode();
        Assert.True(inventory.Headers.TryGetValues(GitStateStampHeader.StaleHeader, out _));
    }

    [Fact]
    public async Task AdminPerformanceEndpoint_ReportsPerEndpointPercentilesAndIndexAge()
    {
        SeedRepository();
        SeedTask();

        var telemetry = new StructuredTelemetryLoggerProvider();
        using var factory = BuildFactory(telemetry);
        using var client = factory.CreateClient();
        using var timeout = new CancellationTokenSource(TimeSpan.FromMinutes(2));
        var index = factory.Services.GetRequiredService<GitStateIndex>();

        using (var readiness = await client.GetAsync("/healthz", timeout.Token))
            readiness.EnsureSuccessStatusCode();
        Assert.True(
            await WaitUntilAsync(
                () => index.Snapshot(_repo) is not null,
                TimeSpan.FromSeconds(30),
                timeout.Token),
            "The git index never captured the repository.");

        for (var i = 0; i < 5; i++) await GetOk(client, "/api/tasks/grouped", timeout.Token);

        using var response = await client.GetAsync("/api/admin/performance/git-state", timeout.Token);
        response.EnsureSuccessStatusCode();
        var report = await response.Content.ReadFromJsonAsync<JsonElement>(cancellationToken: timeout.Token);

        var endpoints = report.GetProperty("endpoints").EnumerateArray().ToList();
        Assert.Contains(endpoints, endpoint => endpoint.GetProperty("label").GetString() == "tasks/grouped");

        var repositories = report.GetProperty("repositories").EnumerateArray().ToList();
        var repository = Assert.Single(repositories);
        Assert.True(repository.GetProperty("ageSeconds").GetDouble() >= 0);
        Assert.Contains(
            repository.GetProperty("projectNames").EnumerateArray(),
            project => project.GetString() == "replay");
        Assert.True(report.GetProperty("spawnsPerMinute").GetDouble() >= 0);
    }

    private WebApplicationFactory<Program> BuildFactory(StructuredTelemetryLoggerProvider telemetry)
        => new WebApplicationFactory<Program>()
            .WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureLogging(logging => logging.AddProvider(telemetry));
                builder.ConfigureAppConfiguration((_, config) =>
                {
                    config.AddInMemoryCollection(new Dictionary<string, string?>
                    {
                        ["TaskRepository"] = _workspace,
                        ["WatchPaths:0:Name"] = "replay",
                        ["WatchPaths:0:Path"] = _jobs,
                        ["WatchPaths:0:RootPath"] = _repo,
                        ["WatchPaths:0:RepositoryPath"] = _repo,
                    });
                });
            });

    private void SeedRepository()
    {
        RunGit(_repo, "init", "-q", "-b", "main");
        RunGit(_repo, "config", "user.email", "test@example.com");
        RunGit(_repo, "config", "user.name", "Git Index Replay");
        RunGit(_repo, "config", "commit.gpgsign", "false");
        File.WriteAllText(Path.Combine(_repo, "README.md"), "seed\n");
        RunGit(_repo, "add", "README.md");
        RunGit(_repo, "commit", "-q", "-m", "seed");
        RunGit(_repo, "branch", "develop");
    }

    private void SeedTask()
    {
        var anchor = RunGit(_repo, "rev-parse", "HEAD").Trim();
        var folder = Path.Combine(_jobs, TaskStates.Completed, "task-1");
        Directory.CreateDirectory(folder);
        File.WriteAllText(
            Path.Combine(folder, "task.json"),
            JsonSerializer.Serialize(new
            {
                id = "task-1",
                key = "REPLAY-1",
                title = "Background git index replay",
                state = TaskStates.Completed,
                order = 1,
                commits = new[]
                {
                    new
                    {
                        sha = anchor,
                        shortSha = anchor[..7],
                        message = "seed",
                        filesChanged = 1,
                        files = new[] { "README.md" },
                    },
                },
            }));
    }

    private static async Task GetOk(HttpClient client, string route, CancellationToken cancellationToken)
    {
        using var response = await client.GetAsync(route, cancellationToken);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Issues one endpoint's share of the replay and returns its git spawn delta.</summary>
    private static async Task<int> ReplayPhase(
        StructuredTelemetryLoggerProvider telemetry,
        HttpClient client,
        string route,
        int calls,
        CancellationToken cancellationToken)
    {
        var before = TotalSpawns(telemetry);
        for (var i = 0; i < calls; i++) await GetOk(client, route, cancellationToken);
        return TotalSpawns(telemetry) - before;
    }

    private static int TotalSpawns(StructuredTelemetryLoggerProvider telemetry)
        => telemetry.AllRollups().Sum(rollup => rollup.Spawns);

    private static long Percentile(IEnumerable<long> values, double percentile)
    {
        var ascending = values.OrderBy(value => value).ToArray();
        if (ascending.Length == 0) return 0;
        var rank = (int)Math.Ceiling(percentile / 100d * ascending.Length);
        return ascending[Math.Clamp(rank - 1, 0, ascending.Length - 1)];
    }

    private static async Task<bool> WaitUntilAsync(
        Func<bool> condition,
        TimeSpan timeout,
        CancellationToken cancellationToken)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition()) return true;
            await Task.Delay(50, cancellationToken);
        }
        return condition();
    }

    private static string RunGit(string workingDirectory, params string[] arguments)
    {
        var startInfo = new ProcessStartInfo
        {
            FileName = "git",
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var argument in arguments) startInfo.ArgumentList.Add(argument);
        using var process = Process.Start(startInfo)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        Assert.True(process.WaitForExit(30_000), "git did not exit within 30 seconds.");
        Assert.Equal(0, process.ExitCode);
        Assert.True(string.IsNullOrWhiteSpace(error), error);
        return output;
    }

    private static void SilentCatchNote(Exception exception)
        => Console.WriteLine($"GitStateIndexLoadReplayTests cleanup: {exception.Message}");
}
