using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Perf gate for slice 8 of the quality-system mockup
/// (docs/mockups/quality-system/, "Token Usage" surface). The project's
/// Token Usage panel polls the summary + heatmap endpoints when it
/// mounts; the prompt's hard rule names a 200-job board returning both
/// in well under one second.
///
/// <para>
/// What this test guards: the per-project rollup must scale linearly in
/// orchestrator-log entries and at most O(M) over the project's job
/// folders, with no per-job disk re-scan. A future regression that
/// introduced per-job <c>FindJob</c> (one full <c>ScanAllJobs</c> per
/// row) would balloon the heatmap call to multiple seconds on a real
/// board, mirroring the slice-2 grouped-jobs incident this test was
/// modelled on.
/// </para>
/// </summary>
[Trait("Category", "MachineBound")]
public class ProjectTokenUsageEndpointPerfTests : IDisposable
{
    private readonly string _watchPath;

    public ProjectTokenUsageEndpointPerfTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-tu-perf-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void SummaryAndHeatmap_Over200JobsAnd1000LogEntries_FinishWellUnderOneSecond()
    {
        // Arrange — populate a board the size of a real long-running
        // project: 200 jobs spread across all lanes, plus a 1000-entry
        // orchestrator log (a year of moderately busy automation). One
        // entry in five tags a job; the rest are orchestrator-only
        // entries (the realistic mix today, where the runner records
        // many actions without an LLM call).
        const int jobCount = 200;
        const int logEntries = 1000;
        const string projectName = "tu-perf-test";

        for (var i = 0; i < jobCount; i++)
        {
            // Sprinkle some "supporting" job titles so the categorisation
            // path also gets exercised on the hot path.
            var title = i % 17 == 0 ? $"Security audit {i}"
                : i % 23 == 0 ? $"Drift analysis {i}"
                : $"job-{i:D4}";
            WriteJob(TaskStates.Archive, $"job-{i:D4}", title);
        }
        WriteOrchestratorLog(jobCount, logEntries);

        var svc = BuildRuntime(projectName);

        // Warm — read once so the JIT / disk cache are settled. We are
        // measuring the rollup, not the first-touch cost.
        _ = svc.BuildSummary(projectName, _watchPath);
        _ = svc.BuildHeatmap(projectName, _watchPath, ProjectTokenUsageFold.DefaultHeatmapDays);

        // Act — measure the two calls the panel mounts in parallel.
        var sw = Stopwatch.StartNew();
        var summary = svc.BuildSummary(projectName, _watchPath);
        var heatmap = svc.BuildHeatmap(projectName, _watchPath, ProjectTokenUsageFold.DefaultHeatmapDays);
        sw.Stop();

        // Assert — generous ceiling. The fast path is a few hundred ms
        // (one orchestrator-log read + one ScanAllJobs per call); 1000ms
        // catches a regression on any reasonable CI runner without
        // flaking. The data assertions confirm we did real work: the
        // log produced totals and the heatmap has rows.
        Assert.True(summary.HasData, "Summary should report HasData = true after we plant token-using entries.");
        Assert.True(summary.LifetimeTotalTokens > 0, "Lifetime totals should be greater than zero.");
        Assert.NotEmpty(heatmap.Days);
        Assert.NotEmpty(heatmap.Jobs);
        Assert.True(
            sw.ElapsedMilliseconds < 1000,
            $"Summary + heatmap over {jobCount} jobs and {logEntries} log entries took " +
            $"{sw.ElapsedMilliseconds} ms; the prompt's perf bar is well under one second. " +
            "If this assertion fires, look at the job lookup and any " +
            "helper that introduces per-job disk I/O on the rollup path.");
    }

    [Fact]
    public void ExpensiveJobs_ListIsBoundedToLimit_AndOrderedByTotalTokens()
    {
        // Light functional check riding alongside the perf gate: the
        // expensive-jobs endpoint feeds a top-N list and must respect
        // the requested limit.
        const int jobCount = 50;
        const int logEntries = 200;
        const string projectName = "tu-expensive";

        for (var i = 0; i < jobCount; i++)
        {
            WriteJob(TaskStates.Archive, $"job-{i:D4}", $"job-{i:D4}");
        }
        WriteOrchestratorLog(jobCount, logEntries);

        var svc = BuildRuntime(projectName);

        var top5 = svc.BuildExpensiveJobs(_watchPath, 5);

        Assert.True(top5.Count <= 5);
        for (var i = 1; i < top5.Count; i++)
        {
            Assert.True(top5[i - 1].TotalTokens >= top5[i].TotalTokens,
                "Expensive-jobs list must be ordered by TotalTokens descending.");
        }
    }

    private void WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var json = JsonSerializer.Serialize(new
        {
            id = slug,
            title,
            state,
            order = 1,
            agent = "copilot",
        });
        File.WriteAllText(Path.Combine(dir, "task.json"), json);
    }

    private void WriteOrchestratorLog(int jobCount, int entries)
    {
        var dir = Path.Combine(_watchPath, ".orchestrator");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "orchestrator.jsonl");
        var sb = new StringBuilder();
        var now = DateTime.UtcNow;
        for (var i = 0; i < entries; i++)
        {
            // Spread entries across the last 60 days so the heatmap (30 days
            // by default) catches roughly half — a realistic mix of in-window
            // and out-of-window entries.
            var ts = now.AddDays(-(i % 60)).AddMinutes(-(i % 300));
            var entry = new OrchestratorLogEntry
            {
                Ts = ts,
                Kind = OrchestratorLogKinds.Decision,
                Topic = OrchestratorLogTopics.General,
                Summary = $"entry-{i}",
                JobId = i % 5 == 0 ? null : $"job-{(i % jobCount):D4}",
                TokenUsage = new OrchestratorTokenUsage
                {
                    Model = i % 3 == 0 ? "claude-haiku-4-5" : "claude-sonnet-4-6",
                    InputTokens = 500 + (i % 30) * 50,
                    OutputTokens = 100 + (i % 20) * 20,
                    CacheReadTokens = i % 4 == 0 ? 200 : 0,
                    CacheCreationTokens = i % 9 == 0 ? 400 : 0,
                },
            };
            sb.AppendLine(JsonSerializer.Serialize(entry, new JsonSerializerOptions
            {
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
                DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
            }));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private ProjectTokenUsageFromLog BuildRuntime(string projectName)
    {
        var config = BuildConfig(projectName);
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var log = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);
        return new ProjectTokenUsageFromLog(log, scanner);
    }

    /// <summary>
    /// Reads the project's orchestrator log once and the job folders once
    /// per call, then runs the shared pure folds.
    /// </summary>
    private sealed class ProjectTokenUsageFromLog(OrchestratorLog log, TaskScannerService scanner)
    {
        public ProjectTokenUsageSummary BuildSummary(string projectName, string watchPath)
            => ProjectTokenUsageFold.Summary(projectName, log.Read(watchPath), JobsById(watchPath));

        public ProjectTokenHeatmap BuildHeatmap(string projectName, string watchPath, int days)
            => ProjectTokenUsageFold.Heatmap(projectName, log.Read(watchPath), JobsById(watchPath), days);

        public IReadOnlyList<ProjectExpensiveJob> BuildExpensiveJobs(string watchPath, int limit)
            => ProjectTokenJobFold.ExpensiveJobs(log.Read(watchPath), JobsById(watchPath), limit);

        private IReadOnlyDictionary<string, TaskInfo> JobsById(string watchPath)
            => scanner.ScanAllAutomationJobs()
                .Where(job => string.Equals(job.WatchPath, watchPath, StringComparison.OrdinalIgnoreCase))
                .GroupBy(job => job.Id, StringComparer.Ordinal)
                .ToDictionary(group => group.Key, group => group.Last(), StringComparer.Ordinal);
    }

    private IConfiguration BuildConfig(string projectName)
    {
        return new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = projectName,
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
    }
}
