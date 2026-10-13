using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class JobStatsMetadataCacheTests : IDisposable
{
    private readonly string _repo;
    private readonly string _watchPath;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;

    public JobStatsMetadataCacheTests()
    {
        _repo = Path.Combine(Path.GetTempPath(), "atp-job-stats-cache-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_repo, "watched");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _repo,
                ["WatchPaths:0:Name"] = "cache-test",
                ["WatchPaths:0:Path"] = _watchPath,
                ["JobStatsMetadataCache:SafetyTtlSeconds"] = "60",
            })
            .Build();

        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
    }

    [Fact]
    public void JobsById_IncludesArchiveMetadata_ForStatsLookup()
    {
        WriteJob(TaskStates.Ready, "live-1", "Normal task");
        WriteJob(TaskStates.Archive, "support-archived", "Security audit of token flow");
        var cache = BuildCache();

        var byId = cache.JobsById(_watchPath);

        Assert.Equal(2, byId.Count);
        Assert.Equal(TaskStates.Archive, byId["support-archived"].State);
        Assert.Equal("Security audit of token flow", byId["support-archived"].Title);
        Assert.Equal(ProjectTokenCategory.Supporting, ProjectTokenCategory.Of("support-archived", byId));
    }

    [Fact]
    public void Restart_UsesPersistedMetadata_AndParsesOnlyChangedTaskJson()
    {
        var livePath = WriteJob(TaskStates.Ready, "live-1", "Normal task");
        WriteJob(TaskStates.Archive, "archived-1", "Archived task");
        var first = BuildCache();

        Assert.Equal(2, first.JobsById(_watchPath).Count);
        Assert.Equal(2, first.IncrementalParses);

        var restarted = BuildCache();
        Assert.Equal(2, restarted.JobsById(_watchPath).Count);
        Assert.Equal(0, restarted.IncrementalParses);

        var bumped = File.GetLastWriteTimeUtc(livePath).AddSeconds(2);
        File.WriteAllText(livePath,
            JsonSerializer.Serialize(new { id = "live-1", title = "Normal task renamed", state = TaskStates.Ready, order = 1, agent = "claude" }));
        File.SetLastWriteTimeUtc(livePath, bumped);

        restarted.Invalidate();
        var afterChange = restarted.JobsById(_watchPath);

        Assert.Equal("Normal task renamed", afterChange["live-1"].Title);
        Assert.Equal(1, restarted.IncrementalParses);
    }

    private JobStatsMetadataCache BuildCache() =>
        new(_scanner, _config, NullLogger<JobStatsMetadataCache>.Instance);

    private string WriteJob(string state, string slug, string title)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "task.json");
        File.WriteAllText(path,
            JsonSerializer.Serialize(new { id = slug, title, state, order = 1, agent = "claude" }));
        return path;
    }

    public void Dispose()
    {
        try { Directory.Delete(_repo, recursive: true); } catch { }
    }
}
