using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WikiAgentReadLogParserTests
{
    [Theory]
    [InlineData("● Read docs/concepts/overview.md", "concepts/overview.md")]
    [InlineData("● Run sed -n '1,80p' docs/operations/setup/README.md", "operations/setup/README.md")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Read","input":{"file_path":"/repo/docs/quality/guide.md"}}]}}""", "quality/guide.md")]
    [InlineData("""{"type":"item.completed","item":{"type":"command_execution","command":"cat docs/a.md","exit_code":0}}""", "a.md")]
    public void ExtractDocsRelativePaths_RecognizesRenderedAndRawReadTools(string line, string expected)
    {
        Assert.Equal(new[] { expected }, WikiAgentReadLogParser.ExtractDocsRelativePaths(line));
    }

    [Fact]
    public void ExtractDocsRelativePaths_ReturnsEveryDistinctPageReadByOneTool()
    {
        var paths = WikiAgentReadLogParser.ExtractDocsRelativePaths(
            "● Run cat docs/a.md docs/folder/b.html docs/a.md");

        Assert.Equal(new[] { "a.md", "folder/b.html" }, paths);
    }

    [Theory]
    [InlineData("The next change is in docs/concepts/overview.md.")]
    [InlineData("""{"type":"item.completed","item":{"type":"agent_message","text":"I inspected docs/concepts/overview.md"}}""")]
    [InlineData("● Write docs/concepts/overview.md")]
    [InlineData("● Edit docs/concepts/overview.md")]
    [InlineData("● Run printf changed > docs/concepts/overview.md")]
    [InlineData("""{"type":"assistant","message":{"content":[{"type":"tool_use","name":"Write","input":{"file_path":"docs/concepts/overview.md"}}]}}""")]
    [InlineData("● Read docs/app/schemas/wiki-document-companion.schema.json")]
    [InlineData("● Read docs/concepts/overview.md.meta.json")]
    public void ExtractDocsRelativePaths_IgnoresMentionsWritesAndNonWikiContracts(string line)
    {
        Assert.Empty(WikiAgentReadLogParser.ExtractDocsRelativePaths(line));
    }
}

public sealed class WikiCompanionAgentReadTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-agent-read-store-" + Guid.NewGuid().ToString("N"));
    private string WikiDir => Path.Combine(_root, "docs");

    [Fact]
    public void Increment_PreservesTrackedCompanionMigratesLegacyBaselineAndCapsNewestHistory()
    {
        Directory.CreateDirectory(WikiDir);
        var page = Path.Combine(WikiDir, "page.md");
        File.WriteAllText(page, "# Page\n");
        File.WriteAllText(page + ".meta.json",
            """
            {
              "schemaVersion": "wiki-document-companion/v1",
              "grading": { "grade": "A" },
              "agentReads": {
                "total": 3,
                "lastReadAt": "2026-07-22T09:59:00Z",
                "recent": [{ "at": "2026-07-22T09:59:00Z", "taskKey": "AGT-legacy" }]
              }
            }
            """);
        var companionBefore = File.ReadAllText(page + ".meta.json");
        var store = new WikiAgentReadStore();
        var start = new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc);

        for (var i = 0; i < 22; i++)
            store.Increment(WikiDir, "page.md", start.AddMinutes(i), $"AGT-{i}");

        Assert.Equal(companionBefore, File.ReadAllText(page + ".meta.json"));
        var reads = Assert.IsType<WikiAgentReads>(store.Read(WikiDir, "page.md"));
        Assert.Equal(25, reads.Total);
        Assert.Equal(20, reads.Recent.Count);
        Assert.Equal("AGT-21", reads.Recent[0].TaskKey);
        Assert.Equal("AGT-2", reads.Recent[19].TaskKey);
        Assert.Equal(start.AddMinutes(21), reads.LastReadAt);
    }

    [Fact]
    public void ApplyBackfill_IsIdempotentAndRetainsSameMillisecondReads()
    {
        Directory.CreateDirectory(WikiDir);
        File.WriteAllText(Path.Combine(WikiDir, "page.md"), "# Page\n");
        var store = new WikiAgentReadStore();
        var at = new DateTime(2026, 7, 22, 10, 0, 0, 123, DateTimeKind.Utc);
        var recent = new[]
        {
            new WikiAgentReadRecent(at, "AGT-1"),
            new WikiAgentReadRecent(at, "AGT-1"),
        };

        store.ApplyBackfill(WikiDir, "page.md", 2, recent);
        store.ApplyBackfill(WikiDir, "page.md", 2, recent);

        var root = JsonNode.Parse(File.ReadAllText(WikiAgentReadStore.StatePathFor(WikiDir, "page.md")))!.AsObject();
        Assert.Equal(2, root["total"]!.GetValue<int>());
        Assert.Equal(2, root["recent"]!.AsArray().Count);
    }

    [Theory]
    [InlineData("../page.md")]
    [InlineData("/absolute/page.md")]
    [InlineData("folder//page.md")]
    public void StatePathFor_RejectsNonRelativeOrEscapingPaths(string relPath)
    {
        Assert.Throws<ArgumentException>(() => WikiAgentReadStore.StatePathFor(WikiDir, relPath));
    }

    [Fact]
    public void CompanionMetadataWrite_MovesLegacyEvidenceIntoRuntimeState()
    {
        Directory.CreateDirectory(WikiDir);
        var page = Path.Combine(WikiDir, "page.md");
        File.WriteAllText(page, "# Page\n");
        File.WriteAllText(page + ".meta.json",
            """{"agentReads":{"total":7,"lastReadAt":"2026-07-22T10:00:00Z","recent":[{"at":"2026-07-22T10:00:00Z","taskKey":"AGT-7"}]}}""");
        var reads = new WikiAgentReadStore();
        var companions = new WikiCompanionStore(reads);

        companions.WriteClassificationStatus(
            WikiDir, "page.md", "Page", "# Page\n", "aktuell", new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc));

        using var companion = JsonDocument.Parse(File.ReadAllText(page + ".meta.json"));
        Assert.False(companion.RootElement.TryGetProperty("agentReads", out _));
        Assert.Equal("aktuell", companion.RootElement.GetProperty("classification").GetProperty("status").GetString());
        var migrated = Assert.IsType<WikiAgentReads>(reads.Read(WikiDir, "page.md"));
        Assert.Equal(7, migrated.Total);
        Assert.Equal("AGT-7", Assert.Single(migrated.Recent).TaskKey);
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public void Increment_LeavesGitStatusClean()
    {
        var repo = Path.Combine(_root, "repo");
        var wiki = Path.Combine(repo, "docs");
        Directory.CreateDirectory(wiki);
        File.WriteAllText(Path.Combine(repo, ".gitignore"), ".orchestrator/\n");
        File.WriteAllText(Path.Combine(wiki, "page.md"), "# Page\n");
        File.WriteAllText(Path.Combine(wiki, "page.md.meta.json"),
            """{"agentReads":{"total":4,"lastReadAt":null,"recent":[]}}""");
        RunGit(repo, "init");
        RunGit(repo, "add", ".");
        RunGit(repo, "-c", "user.name=Test", "-c", "user.email=test@example.invalid", "commit", "-m", "fixture");

        new WikiAgentReadStore().Increment(
            wiki, "page.md", new DateTime(2026, 8, 8, 10, 0, 0, DateTimeKind.Utc), "AGT-2507");

        Assert.Equal(string.Empty, RunGit(repo, "status", "--porcelain"));
    }

    private static string RunGit(string workDir, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = workDir,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)} failed: {stderr}");
        return stdout.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }
}

public sealed class WikiAgentReadBackfillTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "wiki-agent-read-backfill-" + Guid.NewGuid().ToString("N"));
    private readonly string _repo;
    private readonly string _tasks;

    public WikiAgentReadBackfillTests()
    {
        _repo = Path.Combine(_root, "repo");
        _tasks = Path.Combine(_root, "tasks");
        Directory.CreateDirectory(Path.Combine(_repo, "docs", "concepts"));
        Directory.CreateDirectory(_tasks);
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_tasks, state));
        File.WriteAllText(Path.Combine(_repo, "docs", "concepts", "overview.md"), "# Overview\n");
    }

    [Fact]
    public void EnsureBackfilled_FoldsCompleteExistingLogAndMarkerMakesRepeatANoOp()
    {
        // Production's TaskIndexCache keeps archive outside ScanAllJobs().
        // Put the only historical log there so the backfill is forced to use
        // the archive-inclusive inventory rather than passing only in uncached
        // test mode.
        var taskDir = Path.Combine(_tasks, TaskStates.Archive, "wiki-reader");
        Directory.CreateDirectory(Path.Combine(taskDir, "logs"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(new
        {
            id = "wiki-reader",
            key = "AGT-2242",
            title = "Wiki reader",
            state = TaskStates.Archive,
            order = 1,
            agent = "codex",
        }));

        var logPath = Path.Combine(taskDir, "logs", "cli-output.log");
        using (var writer = new StreamWriter(logPath))
        {
            writer.WriteLine("""[10:00:00.000] [stdout] {"type":"item.completed","item":{"type":"command_execution","command":"cat docs/concepts/overview.md","exit_code":0}}""");
            for (var i = 0; i <= CliOutputLogParser.MaxLinesCap; i++)
                writer.WriteLine($"[10:00:01.000] [stdout] noise {i}");
        }

        var service = BuildService(out var scanner);
        var archived = Assert.Single(scanner.ScanAllJobsWithArchive());
        Assert.Equal(taskDir, archived.FolderPath);
        Assert.True(File.Exists(TaskPaths.CliOutputLog(archived.FolderPath)));
        Assert.Empty(scanner.ScanAllJobs());
        Assert.Single(scanner.ScanAllJobsWithArchive());
        var first = service.EnsureBackfilled();
        var second = service.EnsureBackfilled();

        Assert.False(first.AlreadyCompleted);
        Assert.Equal(1, first.LogsScanned);
        Assert.Equal(1, first.ReadsApplied);
        Assert.True(File.Exists(first.MarkerPath));
        Assert.True(second.AlreadyCompleted);

        using var state = JsonDocument.Parse(File.ReadAllText(WikiAgentReadStore.StatePathFor(
            Path.Combine(_repo, "docs"), "concepts/overview.md")));
        var reads = state.RootElement;
        Assert.Equal(1, reads.GetProperty("total").GetInt32());
        Assert.Equal("AGT-2242", reads.GetProperty("recent")[0].GetProperty("taskKey").GetString());
    }

    [Fact]
    public void ProcessOutput_AppliesEachLiveReadEvenWhenTimestampsMatch()
    {
        WriteTask("live-reader", "AGT-2243");
        var service = BuildService(out _);
        var at = new DateTime(2026, 7, 22, 12, 0, 0, DateTimeKind.Utc);

        var applied = service.ProcessOutput("AGT-2243", new[]
        {
            new CliOutputLine { Timestamp = at, Stream = "stdout", Text = "● Read docs/concepts/overview.md" },
            new CliOutputLine { Timestamp = at, Stream = "stdout", Text = "● Read docs/concepts/overview.md" },
        });

        Assert.Equal(2, applied);
        using var state = JsonDocument.Parse(File.ReadAllText(WikiAgentReadStore.StatePathFor(
            Path.Combine(_repo, "docs"), "concepts/overview.md")));
        Assert.Equal(2, state.RootElement.GetProperty("total").GetInt32());
    }

    [Fact]
    public void BackfillThenLiveRead_PreservesTheHistoricalBaselineAndAddsContinuousEvidence()
    {
        WriteTask("reader", "AGT-2244");
        var taskDir = Path.Combine(_tasks, TaskStates.Progress, "reader");
        var logPath = Path.Combine(taskDir, "logs", "cli-output.log");
        File.WriteAllText(
            logPath,
            "[10:00:00.000] [stdout] ● Read docs/concepts/overview.md\n");
        File.SetLastWriteTimeUtc(logPath, new DateTime(2026, 7, 22, 10, 0, 0, DateTimeKind.Utc));
        var service = BuildService(out _);

        var baseline = service.EnsureBackfilled();
        var liveAt = new DateTime(2026, 7, 23, 12, 0, 0, DateTimeKind.Utc);
        var applied = service.ProcessOutput("AGT-2244",
        [
            new CliOutputLine
            {
                Timestamp = liveAt,
                Stream = "stdout",
                Text = "● Read docs/concepts/overview.md",
            },
        ]);

        Assert.Equal(1, baseline.ReadsApplied);
        Assert.Equal(1, applied);
        using var state = JsonDocument.Parse(File.ReadAllText(WikiAgentReadStore.StatePathFor(
            Path.Combine(_repo, "docs"), "concepts/overview.md")));
        var reads = state.RootElement;
        Assert.Equal(2, reads.GetProperty("total").GetInt32());
        Assert.Equal(liveAt, reads.GetProperty("lastReadAt").GetDateTime().ToUniversalTime());
        Assert.Equal(2, reads.GetProperty("recent").GetArrayLength());
        Assert.All(
            reads.GetProperty("recent").EnumerateArray(),
            item => Assert.Equal("AGT-2244", item.GetProperty("taskKey").GetString()));
    }

    [Fact]
    public async Task ProcessOutput_RefreshesOnlyTheReadProjectsWikiOncePerBurst()
    {
        WriteTask("burst-reader", "AGT-3793");
        // AGT-4360: the burst window runs on an injected clock. With a real 50 ms
        // window a busy host spread the five lines over two windows.
        var clock = new FakeTimeProvider();
        var service = BuildService(out _, out var cache, TimeSpan.FromSeconds(2), clock);
        Assert.True(cache.Preload("Demo"));
        Assert.True(cache.Preload("Other"));
        var before = cache.GetStats();

        var at = new DateTime(2026, 11, 15, 21, 0, 0, DateTimeKind.Utc);
        for (var line = 0; line < 5; line++)
        {
            Assert.Equal(1, service.ProcessOutput("AGT-3793", new[]
            {
                new CliOutputLine { Timestamp = at.AddSeconds(line), Stream = "stdout", Text = "● Read docs/concepts/overview.md" },
            }));
            clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        // Inside the window nothing is rebuilt on the CLI output path.
        Assert.Equal(before.Fills, cache.GetStats().Fills);

        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => cache.GetStats().MutationInvalidations > before.MutationInvalidations);
        // The window has closed: a second tick must not find another refresh.
        clock.Advance(TimeSpan.FromSeconds(2));
        await WaitForAsync(() => cache.GetStats().Fills > before.Fills);

        // One rebuild of the read project; before AGT-3793 each line rebuilt every project.
        var after = cache.GetStats();
        Assert.Equal(before.Fills + 1, after.Fills);
        Assert.Equal(before.MutationInvalidations + 1, after.MutationInvalidations);
        var page = cache.GetSnapshot("Demo")!.MetadataByRelPath["concepts/overview.md"];
        Assert.Equal(5, page.AgentReads!.Total);
    }

    /// <summary>Waits for a completion the injected clock released; the bound only breaks a hang.</summary>
    private static async Task WaitForAsync(Func<bool> done)
    {
        var waited = Stopwatch.StartNew();
        while (!done())
        {
            Assert.True(waited.Elapsed < TimeSpan.FromSeconds(60), "The refresh released by the clock did not run.");
            await Task.Delay(10);
        }
    }

    private void WriteTask(string id, string key)
    {
        var taskDir = Path.Combine(_tasks, TaskStates.Progress, id);
        Directory.CreateDirectory(Path.Combine(taskDir, "logs"));
        File.WriteAllText(Path.Combine(taskDir, "task.json"), JsonSerializer.Serialize(new
        {
            id,
            key,
            title = id,
            state = TaskStates.Progress,
            order = 1,
            agent = "codex",
        }));
    }

    private WikiAgentReadService BuildService(out TaskScannerService scanner)
        => BuildService(out scanner, out _, null);

    private WikiAgentReadService BuildService(
        out TaskScannerService scanner, out WikiContentCache cache, TimeSpan? refreshDelay)
        => BuildService(out scanner, out cache, refreshDelay, refreshClock: null);

    private WikiAgentReadService BuildService(
        out TaskScannerService scanner, out WikiContentCache cache, TimeSpan? refreshDelay, TimeProvider? refreshClock)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "Demo",
                ["WatchPaths:0:Path"] = _tasks,
                ["WatchPaths:0:RepositoryPath"] = _repo,
                ["WatchPaths:1:Name"] = "Other",
                ["WatchPaths:1:Path"] = Path.Combine(_root, "other-tasks"),
                ["WatchPaths:1:RepositoryPath"] = _repo,
                ["TaskRepository"] = _tasks,
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var indexCache = new TaskIndexCache(scanner, NullLogger<TaskIndexCache>.Instance, config);
        scanner.SetIndexCache(indexCache);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance);
        cache = new WikiContentCache(docs, NullLogger<WikiContentCache>.Instance);
        docs.SetWikiContentCache(cache);
        var store = new WikiAgentReadStore();
        var logger = NullLogger<WikiAgentReadService>.Instance;
        var delay = refreshDelay ?? TimeSpan.FromSeconds(2);
        // Without an injected clock the service keeps its own default clock.
        return refreshClock is null
            ? new WikiAgentReadService(scanner, registry, store, docs, config, logger) { LiveReadRefreshDelay = delay }
            : new WikiAgentReadService(scanner, registry, store, docs, config, logger)
            {
                LiveReadRefreshDelay = delay,
                RefreshClock = refreshClock,
            };
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best-effort test cleanup */ }
    }
}
