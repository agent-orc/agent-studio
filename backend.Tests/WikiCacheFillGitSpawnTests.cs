using System.Diagnostics;
using AgentStudio.Docs;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;
using Xunit.Abstractions;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3793: a Wiki cache fill of an unchanged repository starts no Git. On
/// the live host 133 fills in 30 minutes ran 1,428 Git processes, although
/// the tree had not moved; every value a fill asks Git for is a function of
/// the observed HEAD commit or of the working tree, so a second fill over the
/// same files answers it from the caches keyed by that commit.
/// </summary>
public sealed class WikiCacheFillGitSpawnTests : IDisposable
{
    private const string ProjectName = "WikiSpawns";
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "wiki-fill-spawns-" + Guid.NewGuid().ToString("N"));
    private readonly ITestOutputHelper _output;
    private readonly IConfiguration _config;
    private readonly TaskScannerService _scanner;
    private readonly ProjectRegistry _registry;
    private readonly GitService _git;
    private readonly ProjectDocsService _docs;
    private readonly WorkbenchCatalogueService _workbenches;
    private readonly CapturingLogger<ProjectDocsService> _log = new();

    public WikiCacheFillGitSpawnTests(ITestOutputHelper output)
    {
        _output = output;
        Directory.CreateDirectory(Path.Combine(_root, ".orchestrator", "jobs"));
        Git("init", "-q", "-b", "main");
        Git("config", "user.email", "wiki@example.invalid");
        Git("config", "user.name", "Wiki");
        Write("docs/guide/start.md", "# Start\n\nInitial summary.\n");
        Write("docs/concepts/model.md", "# Model\n\nThe model.\n");
        Write("docs/concepts/deep/inner.md", "# Inner\n\nNested page.\n");
        Write("docs/concepts/dossiers/demo/index.html", "<html><body><h1>Demo</h1></body></html>");
        // clock-independent: descriptor dates are data; no assertion compares them with now.
        Write("docs/concepts/dossiers/demo/workbench.json", """
            {
              "schemaVersion": 1,
              "id": "demo",
              "title": "Demo Dossier",
              "summary": "A Dossier for the spawn test.",
              "entrypoint": "index.html",
              "status": "active",
              "updatedAt": "2026-11-15T10:00:00Z"
            }
            """);
        Write("src/app.cs", "class App {}\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "seed docs");

        _config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = ProjectName,
                ["WatchPaths:0:RootPath"] = _root,
                ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, _config);
        _scanner = new TaskScannerService(_config, NullLogger<TaskScannerService>.Instance, summary);
        _registry = new ProjectRegistry(_config, NullLogger<ProjectRegistry>.Instance);
        _git = new GitService(NullLogger<GitService>.Instance, _scanner, _config, registry: _registry);
        _workbenches = new WorkbenchCatalogueService(_scanner, _registry, _git);
        _docs = new ProjectDocsService(_scanner, _registry, _log, _git, _workbenches);
    }

    [Fact]
    public void Fill_of_an_unchanged_tree_starts_no_git_after_the_first_fill()
    {
        Assert.True(_docs.PreloadWikiContent(ProjectName));
        var first = _docs.GetWikiTreeResult(ProjectName);

        // The live fills are minutes apart: let every short-TTL probe expire.
        _git.ExpireShortLivedProbesForTesting();
        var spawns = SpawnsDuring(() => _docs.InvalidateWikiContent(
            ProjectName, WikiContentCache.InvalidationSource.Watcher));
        var second = _docs.GetWikiTreeResult(ProjectName);

        Assert.True(spawns == 0, $"unchanged fill started Git: {Breakdown()}");
        Assert.Equal(first!.ETag, second!.ETag);
    }

    [Fact]
    public void Fill_after_a_commit_reads_the_new_head()
    {
        Assert.True(_docs.PreloadWikiContent(ProjectName));
        var before = _docs.GetWikiTreeResult(ProjectName)!.Tree.Source?.Commit;

        Write("README.md", "outside docs\n");
        Git("add", "-A");
        Git("commit", "-q", "-m", "change outside docs");
        _docs.InvalidateWikiContent(ProjectName, WikiContentCache.InvalidationSource.Watcher);

        var after = _docs.GetWikiTreeResult(ProjectName)!.Tree.Source?.Commit;
        Assert.NotEqual(before, after);
        Assert.Equal(GitOut("rev-parse", "HEAD"), after);
    }

    private int SpawnsDuring(Action action)
    {
        using var scope = GitProcessTelemetry.BeginRequest("test/wiki-fill", _log, includeNested: true);
        action();
        return GitProcessTelemetry.CurrentTally()!.Value.Spawns;
    }

    private string Breakdown()
    {
        var lines = _log.Entries
            .Select(entry => (Label: Field(entry, "Label")?.ToString(), Breakdown: Field(entry, "Breakdown")?.ToString(),
                Spawns: Field(entry, "Spawns")))
            .Where(entry => entry.Label != null && entry.Breakdown is { Length: > 0 })
            .Select(entry => $"{entry.Label} spawns={entry.Spawns} [{entry.Breakdown}]")
            .ToList();
        foreach (var line in lines) _output.WriteLine(line);
        return string.Join("; ", lines);
    }

    private static object? Field(IReadOnlyList<KeyValuePair<string, object?>> state, string key)
        => state.FirstOrDefault(pair => pair.Key == key).Value;

    private void Write(string rel, string content)
    {
        var path = Path.Combine(_root, rel.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }

    private void Git(params string[] args) => GitOut(args);

    private string GitOut(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(15_000);
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {process.StandardError.ReadToEnd()}");
        return output.Trim();
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
    }
}
