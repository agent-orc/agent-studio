using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;

using AgentStudio.Tasks;

using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// Opt-in latency benchmark for the board read (AGT-3088). It seeds a
/// production-shaped workspace (about 2,800 task folders over 13 projects, 2,088
/// of them archived, 251 in Human Review) and measures
/// <c>GET /api/tasks/grouped</c> as a 200 and as a conditional 304, once on a
/// quiet index and once with an in-process write invalidating the index before
/// every read, which is the runner and review write rate's worst case.
///
/// Enable with RUN_BOARD_READ_PERF=1. Set BOARD_READ_PERF_OUT to a file path to
/// also append the summary lines there. It asserts nothing about the numbers:
/// it is the before/after instrument for the delivery notes, not a gate.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class BoardReadLatencyBenchmarks : IDisposable
{
    private const int Projects = 13;
    private const int Samples = 40;

    // State -> folder count, summed over all projects (2,800 in total).
    private static readonly (string State, int Count)[] Shape =
    [
        (TaskStates.Archive, 2_088),
        (TaskStates.HumanReview, 251),
        (TaskStates.Completed, 200),
        (TaskStates.Backlog, 150),
        (TaskStates.Ready, 40),
        (TaskStates.AutoReview, 30),
        (TaskStates.Preparation, 26),
        (TaskStates.Escalated, 10),
        (TaskStates.Progress, 5),
    ];

    private readonly string _workspace;
    private readonly ITestOutputHelper _output;

    public BoardReadLatencyBenchmarks(ITestOutputHelper output)
    {
        _output = output;
        _workspace = Path.Combine(Path.GetTempPath(), "atp-board-read-perf-" + Guid.NewGuid().ToString("N"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup.
        }
    }

    [SkippableFact]
    public async Task GroupedRead_OnAProductionShapedWorkspace()
    {
        Skip.IfNot(
            Environment.GetEnvironmentVariable("RUN_BOARD_READ_PERF") == "1",
            "Set RUN_BOARD_READ_PERF=1 to run the board read latency benchmark.");

        var settings = Seed();
        using var factory = new WebApplicationFactory<Program>().WithWebHostBuilder(b =>
        {
            b.UseEnvironment("Test");
            b.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(settings));
        });
        using var client = factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
        var scanner = factory.Services.GetRequiredService<TaskScannerService>();
        var cache = factory.Services.GetRequiredService<TaskIndexCache>();

        // Cold read plus a short warm-up so JIT and the folder memo are settled,
        // then wait out the host's boot-time sweeps, which rewrite workspace
        // files and keep invalidating the index for a while after start-up.
        for (var i = 0; i < 5; i++) await GetAsync(client, null);
        await WaitForQuietIndexAsync(client, cache);
        var touched = Directory.EnumerateDirectories(
            Path.Combine(_workspace, "projects", "proj-01", TaskStates.Backlog)).First();

        var lines = new List<string>
        {
            string.Create(CultureInfo.InvariantCulture,
                $"board-read-perf folders={Shape.Sum(s => s.Count)} projects={Projects} samples={Samples} processors={Environment.ProcessorCount}"),
        };

        if (Environment.GetEnvironmentVariable("BOARD_READ_PERF_DUMP") is { Length: > 0 } dumpPath)
        {
            using var dump = await client.GetAsync("/api/tasks/grouped?includeLegacyReviewLane=false");
            await File.WriteAllBytesAsync(dumpPath, await dump.Content.ReadAsByteArrayAsync());
            return;
        }
        var missesBeforeMeasuring = cache.Misses;
        lines.Add(await MeasureAsync("quiet-200", client, conditional: false, beforeRead: null));
        lines.Add(await MeasureAsync("quiet-304", client, conditional: true, beforeRead: null));
        void Churn()
        {
            // One real folder change plus the in-process invalidation a runner
            // or review write performs after it.
            File.SetLastWriteTimeUtc(Path.Combine(touched, "task.json"),
                File.GetLastWriteTimeUtc(Path.Combine(touched, "task.json")).AddSeconds(1));
            scanner.InvalidateCache();
        }
        lines.Add(await MeasureAsync("churn-200", client, conditional: false, beforeRead: Churn));
        lines.Add(await MeasureAsync("churn-304", client, conditional: true, beforeRead: Churn));
        lines.Add(await MeasureConcurrentAsync("churn-10-concurrent", client, Churn));
        lines.Add(string.Create(CultureInfo.InvariantCulture,
            $"board-read-perf index refreshesWhileMeasuring={cache.Misses - missesBeforeMeasuring} hits={cache.Hits} staleHits={cache.StaleHits}"));

        foreach (var line in lines) _output.WriteLine(line);
        if (Environment.GetEnvironmentVariable("BOARD_READ_PERF_OUT") is { Length: > 0 } outPath)
            File.AppendAllLines(outPath, lines);
    }

    /// <summary>
    /// Returns once three seconds of board reads published no new index
    /// generation, or after five minutes. Measured with a stopwatch, never the
    /// wall clock.
    /// </summary>
    private static async Task WaitForQuietIndexAsync(HttpClient client, TaskIndexCache cache)
    {
        var total = Stopwatch.StartNew();
        var quiet = Stopwatch.StartNew();
        var misses = cache.Misses;
        while (total.Elapsed < TimeSpan.FromMinutes(5))
        {
            await GetAsync(client, null);
            if (cache.Misses != misses)
            {
                misses = cache.Misses;
                quiet.Restart();
            }
            else if (quiet.Elapsed >= TimeSpan.FromSeconds(3)) return;
            await Task.Delay(100);
        }
    }

    private static async Task<string> MeasureAsync(
        string label, HttpClient client, bool conditional, Action? beforeRead)
    {
        var timings = new List<double>(Samples);
        var statuses = new Dictionary<int, int>();
        long bytes = 0;
        string? etag = (await GetAsync(client, null)).ETag;
        for (var i = 0; i < Samples; i++)
        {
            beforeRead?.Invoke();
            var started = Stopwatch.GetTimestamp();
            var response = await GetAsync(client, conditional ? etag : null);
            timings.Add(Stopwatch.GetElapsedTime(started).TotalMilliseconds);
            statuses[(int)response.Status] = statuses.GetValueOrDefault((int)response.Status) + 1;
            bytes = Math.Max(bytes, response.Bytes);
            etag = response.ETag ?? etag;
        }
        return Format(label, timings, statuses, bytes);
    }

    private static async Task<string> MeasureConcurrentAsync(string label, HttpClient client, Action beforeRead)
    {
        var timings = new List<double>();
        var statuses = new Dictionary<int, int>();
        for (var round = 0; round < Samples / 10; round++)
        {
            beforeRead();
            var reads = Enumerable.Range(0, 10).Select(async _ =>
            {
                var started = Stopwatch.GetTimestamp();
                var response = await GetAsync(client, null);
                return (Ms: Stopwatch.GetElapsedTime(started).TotalMilliseconds, response.Status);
            }).ToArray();
            foreach (var (ms, status) in await Task.WhenAll(reads))
            {
                timings.Add(ms);
                statuses[(int)status] = statuses.GetValueOrDefault((int)status) + 1;
            }
        }
        return Format(label, timings, statuses, 0);
    }

    private static string Format(string label, List<double> timings, Dictionary<int, int> statuses, long bytes)
    {
        timings.Sort();
        double Percentile(double p) => timings[Math.Min(timings.Count - 1, (int)Math.Ceiling(p * timings.Count) - 1)];
        return string.Create(CultureInfo.InvariantCulture,
            $"board-read-perf {label} p50={Percentile(0.50):0.0}ms p95={Percentile(0.95):0.0}ms max={timings[^1]:0.0}ms "
            + $"statuses={string.Join(',', statuses.OrderBy(s => s.Key).Select(s => $"{s.Key}x{s.Value}"))} maxBytes={bytes}");
    }

    private static async Task<(HttpStatusCode Status, string? ETag, long Bytes)> GetAsync(HttpClient client, string? ifNoneMatch)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/tasks/grouped?includeLegacyReviewLane=false");
        if (ifNoneMatch is not null) request.Headers.TryAddWithoutValidation("If-None-Match", ifNoneMatch);
        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsByteArrayAsync();
        return (response.StatusCode, response.Headers.ETag?.ToString(), body.Length);
    }

    private Dictionary<string, string?> Seed()
    {
        var settings = new Dictionary<string, string?>
        {
            ["TaskRepository"] = _workspace,
            ["TaskIndexCache:SafetyTtlSeconds"] = "3600",
            ["GitStateIndex:SweepIntervalSeconds"] = "3600",
            ["GitStateIndex:MaxRetries"] = "0",
        };
        var watchPaths = new string[Projects];
        for (var p = 0; p < Projects; p++)
        {
            var name = $"proj-{p + 1:D2}";
            var watch = Path.Combine(_workspace, "projects", name);
            foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, state));
            watchPaths[p] = watch;
            settings[$"WatchPaths:{p}:Name"] = name;
            settings[$"WatchPaths:{p}:Path"] = watch;
            settings[$"WatchPaths:{p}:RootPath"] = watch;
        }

        var serial = 0;
        foreach (var (state, count) in Shape)
        {
            for (var i = 0; i < count; i++)
            {
                serial++;
                var project = serial % Projects;
                var key = $"P{project + 1:D2}-{serial}";
                // Every fifth Ready card waits on a Human Review card, so the
                // waits-on gate and the transitive-waiter projection have work.
                var target = 2_088 + 1 + i;
                var dependsOn = state == TaskStates.Ready && i % 5 == 0 ? $"P{target % Projects + 1:D2}-{target}" : null;
                WriteJob(watchPaths[project], state, $"task-{serial:D5}", key, order: i + 1, dependsOn);
            }
        }
        return settings;
    }

    private static void WriteJob(string watchPath, string state, string slug, string key, int order, string? dependsOn)
    {
        var dir = Path.Combine(watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = new StringBuilder()
            .Append(CultureInfo.InvariantCulture,
                $"{{\"id\":\"{slug}\",\"key\":\"{key}\",\"title\":\"Benchmark card {slug}\",\"state\":\"{state}\",")
            .Append(CultureInfo.InvariantCulture,
                $"\"order\":{order},\"agent\":\"claude\",\"cliType\":\"claude\",\"ownerClientId\":\"local-default\"");
        if (dependsOn is not null) json.Append(CultureInfo.InvariantCulture, $",\"references\":{{\"dependsOn\":[\"{dependsOn}\"]}}");
        File.WriteAllText(Path.Combine(dir, "task.json"), json.Append('}').ToString());
    }
}
