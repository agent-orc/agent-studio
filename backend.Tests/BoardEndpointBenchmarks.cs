using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

// clock-independent: fixture dates are payload data; no age or date-dependent verdict is asserted.

/// <summary>
/// Reproducible board/card read benchmark. Run with RUN_BOARD_ENDPOINT_PERF=1
/// and BOARD_ENDPOINT_PERF_OUT pointing at a JSON result file. Timing assertions
/// deliberately stay out of the gate. All writes target a private temp fixture.
/// </summary>
[Trait("Category", "MachineBound")]
public sealed class BoardEndpointBenchmarks(ITestOutputHelper output)
{
    [SkippableTheory]
    [InlineData(1_000)]
    [InlineData(5_000)]
    public async Task Board_and_detail_reads(int count)
    {
        Skip.IfNot(Environment.GetEnvironmentVariable("RUN_BOARD_ENDPOINT_PERF") == "1",
            "Set RUN_BOARD_ENDPOINT_PERF=1 to measure board reads.");
        var root = Path.Combine(Path.GetTempPath(), "board-endpoint-perf-" + Guid.NewGuid().ToString("N"));
        var watch = Path.Combine(root, "projects", "benchmark");
        var logger = new TraceLogger();
        try
        {
            Seed(watch, count);
            using var factory = new TestHostFactory().WithWebHostBuilder(builder =>
            {
                builder.UseEnvironment("Test");
                builder.ConfigureAppConfiguration((_, cfg) => cfg.AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskRepository"] = root,
                    ["WatchPaths:0:Name"] = "benchmark",
                    ["WatchPaths:0:Path"] = watch,
                    ["WatchPaths:0:RootPath"] = watch,
                    ["TaskIndexCache:ReconciliationSeconds"] = "3600",
                    ["GitStateIndex:MaxRetries"] = "0",
                }));
                builder.ConfigureLogging(logging => { logging.ClearProviders(); logging.AddProvider(logger); });
            });
            using var client = factory.CreateClient();
            client.DefaultRequestHeaders.Add("X-Client-Id", "local-default");
            client.DefaultRequestHeaders.Add("X-Task-Switch-Trace", "1");
            var index = factory.Services.GetRequiredService<TaskIndexCache>();
            index.ForceRefresh();
            var grouped = "/api/tasks/grouped?includeLegacyReviewLane=false"
                + (Environment.GetEnvironmentVariable("BOARD_ENDPOINT_VARIANT") ?? "");
            var detail = "/api/tasks/card-00001?watchPath=" + Uri.EscapeDataString(watch);
            for (var i = 0; i < 4; i++) { await Read(grouped); await Read(detail); }
            using var payload = JsonDocument.Parse(await client.GetStringAsync(grouped));
            var inventory = payload.RootElement.EnumerateObject()
                .Where(p => p.Value.ValueKind == JsonValueKind.Array).Select(p => new
                {
                    lane = p.Name, cards = p.Value.GetArrayLength(), bytes = p.Value.GetRawText().Length,
                    fields = p.Value.EnumerateArray().SelectMany(card => card.EnumerateObject())
                        .GroupBy(field => field.Name).ToDictionary(g => g.Key,
                            g => g.Sum(field => field.Name.Length + field.Value.GetRawText().Length + 4)),
                }).ToArray();
            var rows = new List<object>();
            var serial = 0;
            async Task Mutate()
            {
                using var response = await client.PutAsJsonAsync(
                    "/api/tasks/card-00001/title?watchPath=" + Uri.EscapeDataString(watch),
                    new { title = "Updated benchmark card " + ++serial });
                response.EnsureSuccessStatusCode();
            }
            async Task Measure(string name, string route, bool conditional = false, bool mutation = false, int readers = 1)
            {
                var samples = new List<double>();
                var statuses = new ConcurrentDictionary<int, int>();
                var mutationMs = new List<double>();
                long maxBytes = 0;
                var tag = (await Read(route)).Tag;
                logger.Traces.Clear();
                var reloads = index.FolderReloads;
                var probes = index.FolderStampProbes;
                var allocated = GC.GetTotalAllocatedBytes();
                for (var round = 0; round < (readers == 1 ? 12 : 3); round++)
                {
                    if (mutation)
                    {
                        var startMutation = Stopwatch.GetTimestamp();
                        await Mutate();
                        mutationMs.Add(Stopwatch.GetElapsedTime(startMutation).TotalMilliseconds);
                    }
                    var reads = Enumerable.Range(0, readers).Select(async _ =>
                    {
                        var start = Stopwatch.GetTimestamp();
                        var read = await Read(route, conditional ? tag : null);
                        return (Ms: Stopwatch.GetElapsedTime(start).TotalMilliseconds, Read: read);
                    });
                    foreach (var result in await Task.WhenAll(reads))
                    {
                        samples.Add(result.Ms);
                        statuses.AddOrUpdate(result.Read.Status, 1, (_, value) => value + 1);
                        maxBytes = Math.Max(maxBytes, result.Read.Bytes);
                        tag = result.Read.Tag;
                    }
                }
                allocated = GC.GetTotalAllocatedBytes() - allocated;
                samples.Sort();
                var traces = logger.Traces.Select(ParseTrace).ToArray();
                var stages = traces.SelectMany(t => t.RootElement.GetProperty("stages").EnumerateObject())
                    .GroupBy(p => p.Name).ToDictionary(g => g.Key, g => new
                    {
                        meanMs = g.Average(p => p.Value.GetProperty("ms").GetDouble()),
                        files = g.Sum(p => p.Value.GetProperty("files").GetInt32()),
                    });
                rows.Add(new { name, samples = samples.Count, p50 = samples[(samples.Count - 1) / 2],
                    p95 = samples[(int)Math.Ceiling(samples.Count * .95) - 1], max = samples[^1], maxBytes,
                    statuses, mutationMs, allocatedBytes = allocated, folderReloads = index.FolderReloads - reloads, folderStampProbes = index.FolderStampProbes - probes,
                    workspaceScans = traces.Sum(t => t.RootElement.GetProperty("workspaceScans").GetInt32()),
                    gitSpawns = traces.Sum(t => t.RootElement.GetProperty("gitSpawns").ValueKind == JsonValueKind.Number
                        ? t.RootElement.GetProperty("gitSpawns").GetInt32() : 0), stages });
                foreach (var trace in traces) trace.Dispose();
            }
            await Measure("grouped", grouped);
            await Measure("grouped-conditional", grouped, conditional: true);
            await Measure("detail", detail);
            await Measure("grouped-after-mutation", grouped, mutation: true);
            await Measure("detail-after-mutation", detail, mutation: true);
            await Measure("grouped-ten-readers", grouped, readers: 10);
            await Measure("detail-ten-readers", detail, readers: 10);
            await Measure("grouped-after-mutation-ten-readers", grouped, mutation: true, readers: 10);
            await Measure("detail-after-mutation-ten-readers", detail, mutation: true, readers: 10);
            var convergence = new List<double>();
            for (var i = 0; i < 6; i++)
            {
                var start = Stopwatch.GetTimestamp();
                await Mutate();
                factory.Services.GetRequiredService<TaskScannerService>().ScanAllJobs();
                convergence.Add(Stopwatch.GetElapsedTime(start).TotalMilliseconds);
            }
            rows.Add(new { name = "mutation-and-index-convergence", milliseconds = convergence });
            // Explicitly measure rebuilds after convergence too. The immediate
            // reads above may legally serve a previous pinned publication.
            await MeasureConvergedBuilds();
            var result = JsonSerializer.Serialize(new { count, completed = count * 85 / 100,
                machine = Environment.MachineName, os = RuntimeInformation.OSDescription,
                runtime = RuntimeInformation.FrameworkDescription, processors = Environment.ProcessorCount,
                configuration = "Release; in-process TestServer, no socket/transfer/compression cost; 12 samples, 3 rounds of ten",
                inventory, rows }, new JsonSerializerOptions { WriteIndented = true });
            output.WriteLine(result);
            if (Environment.GetEnvironmentVariable("BOARD_ENDPOINT_PERF_OUT") is { Length: > 0 } path)
            {
                Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
                await File.WriteAllTextAsync(Path.ChangeExtension(path, null) + "-" + count + ".json", result);
            }

            async Task MeasureConvergedBuilds()
            {
                // A core mutation publishes synchronously. ForceRefresh also
                // drains any broad invalidation before measuring a rebuilt body.
                for (var i = 0; i < 3; i++)
                {
                    await Mutate();
                    index.ForceRefresh();
                    // Record a single cold body through the same trace collector.
                    logger.Traces.Clear();
                    var start = Stopwatch.GetTimestamp();
                    var read = await Read(grouped);
                    rows.Add(new { name = "converged-build-" + i, ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds,
                        read.Bytes, traces = logger.Traces.Select(t => ParseTraceElement(t)).ToArray() });
                }
            }
            async Task<(int Status, string? Tag, long Bytes)> Read(string route, string? tag = null)
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, route);
                if (tag is not null) request.Headers.TryAddWithoutValidation("If-None-Match", tag);
                using var response = await client.SendAsync(request);
                if (!response.IsSuccessStatusCode && (int)response.StatusCode != 304)
                    Assert.Fail(await response.Content.ReadAsStringAsync());
                return ((int)response.StatusCode, response.Headers.ETag?.ToString(),
                    (await response.Content.ReadAsByteArrayAsync()).LongLength);
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static JsonDocument ParseTrace(string text)
    {
        var document = JsonDocument.Parse(text);
        if (document.RootElement.ValueKind != JsonValueKind.String) return document;
        var decoded = document.RootElement.GetString()!;
        document.Dispose();
        return JsonDocument.Parse(decoded);
    }

    private static JsonElement ParseTraceElement(string text)
    {
        using var document = ParseTrace(text);
        return document.RootElement.Clone();
    }

    private static void Seed(string watch, int count)
    {
        foreach (var state in TaskStates.All) Directory.CreateDirectory(Path.Combine(watch, state));
        for (var i = 1; i <= count; i++)
        {
            var state = i <= count * 85 / 100 ? TaskStates.Completed
                : i <= count * 90 / 100 ? TaskStates.Backlog
                : i <= count * 95 / 100 ? TaskStates.Ready
                : i <= count * 98 / 100 ? TaskStates.HumanReview : TaskStates.AutoReview;
            var folder = Path.Combine(watch, state, $"card-{i:D5}");
            Directory.CreateDirectory(Path.Combine(folder, "logs"));
            Directory.CreateDirectory(Path.Combine(folder, "results"));
            File.WriteAllText(Path.Combine(folder, "task.json"), JsonSerializer.Serialize(new
            {
                id = $"card-{i:D5}", key = $"PERF-{i}", title = $"Improve task read projection {i}", state,
                order = i, agent = "codex", cliType = "codex", model = "gpt-5.1-codex", ownerClientId = "local-default",
                createdAt = "2026-11-01T12:00:00Z", tags = new[] { "backend", "performance" },
                commits = Enumerable.Range(1, 4).Select(n => new
                {
                    sha = n.ToString("x").PadLeft(40, '0'), shortSha = n.ToString("x").PadLeft(9, '0'),
                    message = "perf: cache immutable task projection " + n, filesChanged = 3,
                    files = new[] { "backend/Features/Tasks/TaskScannerService.cs" },
                }).ToArray(),
            }));
            File.WriteAllText(Path.Combine(folder, "prompt.md"), string.Concat(Enumerable.Repeat(
                "Measure the board projection. Preserve task identity and published mutation ordering.\n", 40)));
            File.WriteAllText(Path.Combine(folder, "status.md"), "Implemented and verified the scoped task projection.\n");
            File.WriteAllText(Path.Combine(folder, "logs", "cli-output.log"), string.Concat(Enumerable.Repeat(
                "[info] Reading the task projection and validating the changed fields.\n", 50)));
            File.WriteAllText(Path.Combine(folder, "logs", "timeline.jsonl"),
                "{\"kind\":\"task_created\",\"at\":\"2026-11-01T12:00:00Z\"}\n");
            File.WriteAllText(Path.Combine(folder, "results", "report.md"), "Focused checks passed.\n");
        }
    }

    private sealed class TraceLogger : ILoggerProvider, ILogger
    {
        public ConcurrentQueue<string> Traces { get; } = new();
        public ILogger CreateLogger(string categoryName) => this;
        public void Dispose() { }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel level) => level >= LogLevel.Information;
        public void Log<TState>(LogLevel level, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var line = formatter(state, exception);
            if (line.StartsWith("board-read-trace ", StringComparison.Ordinal)
                || line.StartsWith("task-switch-trace ", StringComparison.Ordinal))
                Traces.Enqueue(line[(line.IndexOf(' ') + 1)..]);
        }
    }
}
