using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

public sealed class WorkspaceRepositoryMaintenanceServiceTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(), "workspace-maintenance-" + Guid.NewGuid().ToString("N"));

    public WorkspaceRepositoryMaintenanceServiceTests()
    {
        Directory.CreateDirectory(_root);
        RunGit("init", "-q", "-b", "main");
        RunGit("config", "user.name", "test");
        RunGit("config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(_root, "README.md"), "seed\n");
        RunGit("add", "README.md");
        RunGit("commit", "-q", "-m", "seed");
    }

    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task RunOnce_ConfiguresRepository_UntracksRuntime_AndConsolidatesLooseObjects()
    {
        Directory.CreateDirectory(Path.Combine(_root, "logs", "bus"));
        Directory.CreateDirectory(Path.Combine(_root, ".metadata"));
        File.WriteAllText(Path.Combine(_root, "logs", "bus", "events.jsonl"), "runtime\n");
        File.WriteAllText(Path.Combine(_root, ".metadata", "attempt-authority.json"), "{}\n");
        for (var i = 0; i < 80; i++)
            File.WriteAllText(Path.Combine(_root, $"object-{i:D3}.txt"), new string((char)('a' + i % 26), 2048) + i);
        RunGit("add", ".");
        RunGit("commit", "-q", "-m", "loose objects and runtime state");

        File.AppendAllText(Path.Combine(_root, "README.md"), "drift\n");
        File.AppendAllText(Path.Combine(_root, "logs", "bus", "events.jsonl"), "more runtime\n");
        File.AppendAllText(Path.Combine(_root, ".metadata", "attempt-authority.json"), "{}\n");
        var looseBefore = LooseObjectCount();

        var configuration = Config();
        var commits = new WorkspaceArtifactCommitService(
            configuration, NullLogger<WorkspaceArtifactCommitService>.Instance);
        var service = new WorkspaceRepositoryMaintenanceService(
            configuration,
            commits,
            new ReadyLoadGate(),
            NullLogger<WorkspaceRepositoryMaintenanceService>.Instance);

        var result = await service.RunOnceAsync();

        Assert.True(result.Success, $"{result.Phase}: {result.Error}");
        Assert.Equal("5000", Git("config", "--local", "--get", "gc.auto").Trim());
        Assert.Equal("incremental", Git("config", "--local", "--get", "maintenance.strategy").Trim());
        Assert.Empty(Git("ls-files", "logs/bus", ".metadata/attempt-authority*")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries));
        Assert.Equal("logs/bus/events.jsonl", Git("check-ignore", "logs/bus/events.jsonl").Trim());
        Assert.Equal(".metadata/attempt-authority.json", Git("check-ignore", ".metadata/attempt-authority.json").Trim());
        Assert.Contains("README.md", Git("show", "--name-only", "--format=", "HEAD"));
        Assert.True(LooseObjectCount() < looseBefore,
            $"Expected maintenance to reduce {looseBefore} loose objects, found {LooseObjectCount()}.");
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "WorkspaceRepositoryMaintenanceServiceTests cleanup"); }
    }

    private IConfiguration Config() => new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["TaskRepository"] = _root,
            ["WorkspaceArtifacts:AutoPushEnabled"] = "false",
            ["WorkspaceRepository:MaintenanceTimeoutSeconds"] = "120",
        })
        .Build();

    private int LooseObjectCount()
    {
        var line = Git("count-objects", "-v")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith("count: ", StringComparison.Ordinal));
        return int.Parse(line["count: ".Length..], System.Globalization.CultureInfo.InvariantCulture);
    }

    private string Git(params string[] args)
    {
        var result = RunGitResult(args);
        Assert.Equal(0, result.Code);
        return result.Out;
    }

    private void RunGit(params string[] args)
    {
        var result = RunGitResult(args);
        Assert.True(result.Code == 0, result.Err);
    }

    private (string Out, string Err, int Code) RunGitResult(params string[] args)
    {
        var psi = new ProcessStartInfo("git")
        {
            WorkingDirectory = _root,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);
        using var process = Process.Start(psi)!;
        var stdout = process.StandardOutput.ReadToEnd();
        var stderr = process.StandardError.ReadToEnd();
        process.WaitForExit();
        return (stdout, stderr, process.ExitCode);
    }

    private sealed class ReadyLoadGate : AgentStudio.Runner.ILoadThrottleGate
    {
        public AgentStudio.Runner.LoadThrottleDecision Current => new(false, 0, TimeSpan.Zero);
        public bool WasRecentlyActive => false;
        public Task WaitUntilReadyAsync(string reason, CancellationToken ct) => Task.CompletedTask;
    }
}
