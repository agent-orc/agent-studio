using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the ASS-1649 slim-hydration contract for the terminal
/// <c>7-archive</c> lane: an archived folder is scanned from its
/// <c>task.json</c> header only, skipping the three per-folder disk walks
/// (recursive last-activity walk, <c>cli-output.log</c> tail read,
/// <c>session-events.jsonl</c> scan) that feed live-card affordances.
///
/// <para>The guarantees that matter for the rest of the system:</para>
/// <list type="bullet">
///   <item>header fields - Id, Title, State, Commits - survive, so archived
///   cards render and the token-stats drill-down
///   (<c>TokenAggregationService.BuildJobsById</c>) still resolves an
///   archived top-spender's title;</item>
///   <item>the enrichment fields that require a disk walk are defaulted
///   (no outcome chip, no session-log code-activity scan), proving the walk was
///   skipped;</item>
///   <item>the behaviour is unchanged off the archive lane - the same evidence
///   on a non-archive card still derives the outcome chip.</item>
/// </list>
/// </summary>
public class TaskScannerArchiveSlimHydrationTests : IDisposable
{
    private readonly string _watchPath;

    public TaskScannerArchiveSlimHydrationTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "atp-archive-slim-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private string SeedJob(string slug, string state, string taskJson)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(Path.Combine(dir, "logs"));
        File.WriteAllText(Path.Combine(dir, "task.json"), taskJson);
        return dir;
    }

    private static string HeaderJson(string slug, string state, string? title = null) =>
        $"{{\"id\":\"{slug}\",\"title\":\"{title ?? slug + " title"}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\"}}";

    private const string OutcomeMarkerLog =
        "[12:00:05.000] [orchestrator] [classifier-unknown] The run could not be classified after one orchestrator intervention.\n";

    [Fact]
    public void ArchivedCard_KeepsHeaderFields_ForTitleResolution()
    {
        // The §5 coupling: an archived top-spender must still resolve its title
        // through the scanner so the token drill-down can label it. Header fields
        // are cheap (single task.json parse) and must survive slim hydration.
        SeedJob("archived-spender", TaskStates.Archive,
            HeaderJson("archived-spender", TaskStates.Archive, title: "Expensive archived task"));

        var info = BuildScanner().FindJob("archived-spender", _watchPath);

        Assert.NotNull(info);
        Assert.Equal("archived-spender", info!.Id);
        Assert.Equal("Expensive archived task", info.Title);
        Assert.Equal(TaskStates.Archive, info.State);
    }

    [Fact]
    public void ArchivedCard_SkipsOutcomeChip_EvenWhenLogHasAMarker()
    {
        // Proves the cli-output.log tail read is skipped on the archive path:
        // an outcome marker that would surface a chip on a live card produces
        // none here.
        var dir = SeedJob("archived-noisy", TaskStates.Archive,
            HeaderJson("archived-noisy", TaskStates.Archive));
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), OutcomeMarkerLog);

        var info = BuildScanner().FindJob("archived-noisy", _watchPath);

        Assert.NotNull(info);
        Assert.Null(info!.OutcomeIssue);
    }

    [Fact]
    public void NonArchiveCard_StillDerivesOutcomeChip_FromTheSameMarker()
    {
        // Control: the slim path must not change behaviour off the archive lane.
        // The identical marker on a 4-auto-review card still surfaces the chip.
        var dir = SeedJob("live-noisy", TaskStates.AutoReview,
            HeaderJson("live-noisy", TaskStates.AutoReview));
        File.WriteAllText(Path.Combine(dir, "logs", "cli-output.log"), OutcomeMarkerLog);

        var info = BuildScanner().FindJob("live-noisy", _watchPath);

        Assert.NotNull(info);
        Assert.NotNull(info!.OutcomeIssue);
        Assert.Equal("classifier-unknown", info.OutcomeIssue!.Kind);
    }

    [Fact]
    public void ArchivedCard_CodeActivity_FromInlineCommitOnly_NotSessionLog()
    {
        // Slim hydration keeps the O(1) inline-commit check but skips the
        // session-events.jsonl scan. An archived folder whose ONLY evidence of
        // code activity is a HEAD-moving session range (no stamped commit) must
        // therefore report no code activity - the scan that would have found it
        // is the cost we removed.
        var dir = SeedJob("archived-sessiononly", TaskStates.Archive,
            HeaderJson("archived-sessiononly", TaskStates.Archive));
        File.WriteAllText(Path.Combine(dir, "logs", "session-events.jsonl"),
            "{\"headShaBefore\":\"aaaaaaa\",\"headShaAfter\":\"bbbbbbb\"}\n");

        var info = BuildScanner().FindJob("archived-sessiononly", _watchPath);

        Assert.NotNull(info);
        Assert.False(info!.CodeActivityDetected);
    }

    [Fact]
    public void ArchivedCard_CodeActivity_TrueFromInlineCommit()
    {
        // The inline-commit fast path is preserved: an archived task that landed
        // work (stamped commit object) still reports code activity without any
        // disk walk.
        SeedJob("archived-committed", TaskStates.Archive,
            "{\"id\":\"archived-committed\",\"title\":\"t\",\"state\":\"" + TaskStates.Archive +
            "\",\"order\":1,\"agent\":\"claude\",\"commit\":{\"sha\":\"deadbeef\",\"message\":\"done\"}}");

        var info = BuildScanner().FindJob("archived-committed", _watchPath);

        Assert.NotNull(info);
        Assert.True(info!.CodeActivityDetected);
    }
}
