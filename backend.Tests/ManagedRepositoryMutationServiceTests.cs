using System.Diagnostics;
using AgentStudio.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pre-existing-change guard and post-write change detection of the managed
/// repository write path against a real temporary repository (AGT-3217).
/// </summary>
public sealed class ManagedRepositoryMutationServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "managed-repository-mutation-" + Guid.NewGuid().ToString("N"));

    public ManagedRepositoryMutationServiceTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "docs", "tracked"));
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "test");
        RunGit("config", "user.email", "test@example.com");
        RunGit("config", "core.autocrlf", "false");
        File.WriteAllText(Path.Combine(_root, "docs", "tracked", "workbench.json"), "{\n  \"id\": \"tracked\"\n}\n");
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "seed");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, true); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Git can still hold a handle on Windows; the per-process temp root sweeps it.
            Trace.TraceWarning("Test repository cleanup deferred: {0}", ex.Message);
        }
    }

    [Fact]
    public void Execute_RefusesATargetInsideAnUntrackedFolder()
    {
        var folder = Path.Combine(_root, "docs", "omgai-dev");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "index.html"), "<h1>Draft</h1>\n");
        var descriptor = Path.Combine(folder, "workbench.json");
        const string draft = "{\n  \"id\": \"omgai-dev\"\n}\n";
        File.WriteAllText(descriptor, draft);
        var head = RunGit("rev-parse", "HEAD");
        var wrote = false;

        var result = Service().Execute(
            "Project", _root, "test-untracked", "test: write", ["docs/omgai-dev/workbench.json"],
            () =>
            {
                wrote = true;
                File.WriteAllText(descriptor, "{}");
            });

        Assert.False(result.Success);
        Assert.False(wrote);
        Assert.Contains("Refusing to overwrite pre-existing changes: docs/omgai-dev/workbench.json", result.Error);
        Assert.Equal(draft, File.ReadAllText(descriptor));
        Assert.Equal(head, RunGit("rev-parse", "HEAD"));
        Assert.Equal("?? docs/omgai-dev/", RunGit("status", "--porcelain=v1").Trim());
    }

    [Fact]
    public void Execute_WritesAndCommitsATargetInATrackedCleanFolder()
    {
        var descriptor = Path.Combine(_root, "docs", "tracked", "workbench.json");
        var head = RunGit("rev-parse", "HEAD");

        var result = Service().Execute(
            "Project", _root, "test-tracked", "test: write tracked", ["docs/tracked/workbench.json"],
            () => File.WriteAllText(descriptor, "{\n  \"id\": \"tracked\",\n  \"key\": \"PRO-W1\"\n}\n"));

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.True(result.DidCommit);
        Assert.NotEqual(head, RunGit("rev-parse", "HEAD"));
        Assert.Equal("test: write tracked", RunGit("log", "-1", "--format=%s").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain=v1"));
    }

    [Fact]
    public void Execute_DetectsAFileTheWriteCreatesInANewFolder()
    {
        var created = Path.Combine(_root, "docs", "fresh", "workbench.json");

        var result = Service().Execute(
            "Project", _root, "test-new-folder", "test: create", ["docs/fresh/workbench.json"],
            () =>
            {
                Directory.CreateDirectory(Path.GetDirectoryName(created)!);
                File.WriteAllText(created, "{}\n");
            });

        Assert.True(result.Success, result.Error);
        Assert.True(result.Changed);
        Assert.True(result.DidCommit);
        Assert.Equal("docs/fresh/workbench.json",
            RunGit("show", "--name-only", "--pretty=format:", "HEAD").Trim());
        Assert.Equal(string.Empty, RunGit("status", "--porcelain=v1"));
    }

    [Theory]
    [InlineData("\n", true)]
    [InlineData("\n", false)]
    [InlineData("\r\n", true)]
    [InlineData("\r\n", false)]
    public void IndentedJsonFor_PreservesLineEndingsAndFinalNewline(string newLine, bool finalNewline)
    {
        var path = Path.Combine(_root, "docs", "tracked", "workbench.json");
        var original = string.Join(newLine, "{", "  \"id\": \"tracked\"", "}") + (finalNewline ? newLine : "");
        File.WriteAllText(path, original);
        var node = System.Text.Json.Nodes.JsonNode.Parse(original)!.AsObject();
        node["key"] = "PRO-W1";

        var written = RepositoryTextLayout.IndentedJsonFor(path, node);

        var expected = string.Join(newLine, "{", "  \"id\": \"tracked\",", "  \"key\": \"PRO-W1\"", "}")
            + (finalNewline ? newLine : "");
        Assert.Equal(expected, written);
    }

    [Fact]
    public void IndentedJsonFor_ANewFileGetsLfAndAFinalNewline()
    {
        var written = RepositoryTextLayout.IndentedJsonFor(
            Path.Combine(_root, "docs", "missing.json"),
            new System.Text.Json.Nodes.JsonObject { ["id"] = "new" });

        Assert.Equal("{\n  \"id\": \"new\"\n}\n", written);
    }

    [Theory]
    [InlineData("a\r\nb\r\nc\n", "\r\n", true)]
    [InlineData("a\nb\nc\r\n", "\n", true)]
    [InlineData("a\r\nb", "\r\n", false)]
    [InlineData("{}", "\n", false)]
    [InlineData("", "\n", true)]
    public void Detect_UsesTheDominantLineEndingAndTheFinalNewline(string text, string newLine, bool finalNewline)
    {
        Assert.Equal(new RepositoryTextLayout(newLine, finalNewline), RepositoryTextLayout.Detect(text));
    }

    private ManagedRepositoryMutationService Service()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["WatchPaths:0:Name"] = "Project",
            ["WatchPaths:0:RootPath"] = _root,
            ["WatchPaths:0:Path"] = Path.Combine(_root, ".orchestrator", "jobs"),
        }).Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        var scanner = new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
        var git = new GitService(NullLogger<GitService>.Instance, scanner, config);
        return new ManagedRepositoryMutationService(
            git, logger: NullLogger<ManagedRepositoryMutationService>.Instance);
    }

    private string RunGit(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {string.Join(' ', args)} failed: {error}");
        return output;
    }
}
