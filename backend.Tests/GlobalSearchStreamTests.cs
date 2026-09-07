using AgentStudio.Registry;
using AgentStudio.Search;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// The palette's contract with the stream: the task domain never waits on git,
/// each repository is delivered as it finishes, and cancelling stops the work.
/// </summary>
public sealed class GlobalSearchStreamTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"global-search-stream-{Guid.NewGuid():N}");

    public GlobalSearchStreamTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "user.name", "Test");
        File.WriteAllText(Path.Combine(_root, "README-search-proof.md"), "proof");
        RunGit("add", "README-search-proof.md");
        RunGit("commit", "-m", "feat: streamed global search");
    }

    [Fact]
    public async Task StreamAsync_DeliversTasksBeforeAnyRepository()
    {
        var frames = new List<GlobalSearchStreamEvent>();

        await foreach (var frame in Build().StreamAsync("search-proof", Domains(), 20, CancellationToken.None))
            frames.Add(frame);

        Assert.Equal("tasks", frames[0].Event);
        Assert.Equal("progress", frames[1].Event);
        Assert.Equal("done", frames[^1].Event);

        var repository = Assert.IsType<GlobalSearchRepositoryFrame>(
            Assert.Single(frames, f => f.Event == "repository").Payload);
        Assert.Equal(1, repository.Completed);
        Assert.Equal(1, repository.Total);
        Assert.Empty(repository.FailedDomains);
        Assert.Contains(repository.Files, file => file.Path == "README-search-proof.md");
    }

    [Fact]
    public async Task StreamAsync_ReportsRepositoryProgressAndCacheState()
    {
        var service = Build();

        await Drain(service.StreamAsync("search-proof", Domains(), 20, CancellationToken.None));
        // A different term against the same HEAD must reuse the corpus rather
        // than re-spawning git; the frame says so.
        var warm = await Drain(service.StreamAsync("streamed", Domains(), 20, CancellationToken.None));

        var repository = Assert.IsType<GlobalSearchRepositoryFrame>(
            Assert.Single(warm, f => f.Event == "repository").Payload);
        Assert.True(repository.FromCache);
        Assert.Contains(repository.Commits, commit => commit.Title == "feat: streamed global search");
    }

    [Fact]
    public async Task StreamAsync_StopsWhenTheCallerCancels()
    {
        using var cancellation = new CancellationTokenSource();
        var frames = new List<GlobalSearchStreamEvent>();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var frame in Build().StreamAsync("search-proof", Domains(), 20, cancellation.Token))
            {
                frames.Add(frame);
                // Cancel the moment the palette has something to show; the
                // repository fan-out must not run to completion afterwards.
                await cancellation.CancelAsync();
            }
        });

        Assert.Equal("tasks", Assert.Single(frames).Event);
    }

    [Fact]
    public async Task Search_AndStream_AgreeOnResults()
    {
        var service = Build();

        var single = service.Search("search-proof", Domains(), 20);
        var streamed = await Drain(service.StreamAsync("search-proof", Domains(), 20, CancellationToken.None));
        var streamedFiles = streamed
            .Where(f => f.Event == "repository")
            .SelectMany(f => ((GlobalSearchRepositoryFrame)f.Payload).Files)
            .Select(file => file.Path)
            .ToList();

        Assert.Equal(single.Files.Select(file => file.Path), streamedFiles);
    }

    private static async Task<List<GlobalSearchStreamEvent>> Drain(IAsyncEnumerable<GlobalSearchStreamEvent> stream)
    {
        var frames = new List<GlobalSearchStreamEvent>();
        await foreach (var frame in stream) frames.Add(frame);
        return frames;
    }

    private static HashSet<string> Domains() =>
        new(StringComparer.OrdinalIgnoreCase) { "tasks", "commits", "files" };

    private GlobalSearchService Build()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "demo",
            ["WatchPaths:0:Path"] = _root,
            ["WatchPaths:0:RootPath"] = _root,
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        var indexes = new GlobalSearchIndexes(config, git, NullLogger<GlobalSearchIndexes>.Instance);
        var registry = new ProjectRegistry(config, NullLogger<ProjectRegistry>.Instance);
        var docs = new ProjectDocsService(scanner, registry, NullLogger<ProjectDocsService>.Instance, git);
        return new GlobalSearchService(scanner, indexes, registry, docs, NullLogger<GlobalSearchService>.Instance);
    }

    private void RunGit(params string[] args)
    {
        using var process = new System.Diagnostics.Process { StartInfo = new("git") {
            WorkingDirectory = _root, UseShellExecute = false, RedirectStandardError = true
        }};
        foreach (var arg in args) process.StartInfo.ArgumentList.Add(arg);
        process.Start();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, stderr);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_root)) return;
        foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(_root, true);
    }
}
