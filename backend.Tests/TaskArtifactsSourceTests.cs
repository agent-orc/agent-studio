using System.Diagnostics;
using AgentStudio.Tasks;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// <c>TaskArtifacts:Source</c> (pipeline delivery target, section 05, migration
/// M3 and M6, decision D9 A, AGT-3320): the pure setting and mirror rules, and
/// the guard that keeps every monolith commit and push away from the workspace
/// repository once it is the Task Server's export.
/// </summary>
public sealed class TaskArtifactsSourceTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "task-artifacts-source-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_root))
            {
                foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                    File.SetAttributes(file, FileAttributes.Normal);
                Directory.Delete(_root, recursive: true);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Best-effort temp cleanup; a retained temp folder must not fail a run.
            Console.Error.WriteLine($"Task artifacts source test cleanup left {_root}: {exception.Message}");
        }
    }

    [Theory]
    [InlineData(null, TaskArtifactsSourceMode.Workspace, "workspace", null)]
    [InlineData("", TaskArtifactsSourceMode.Workspace, "workspace", null)]
    [InlineData("workspace", TaskArtifactsSourceMode.Workspace, "workspace", null)]
    [InlineData("Task-Server-Shadow", TaskArtifactsSourceMode.TaskServerShadow, "task-server-shadow", null)]
    [InlineData(" task-server ", TaskArtifactsSourceMode.TaskServer, "task-server", null)]
    [InlineData("sqlite", TaskArtifactsSourceMode.Workspace, "workspace", "sqlite")]
    public void Setting_resolves_known_values_and_rolls_unknown_values_back_to_the_workspace(
        string? configured, TaskArtifactsSourceMode mode, string normalized, string? rejected)
    {
        var resolution = TaskArtifactsSourceSetting.Resolve(configured);
        Assert.Equal((mode, normalized, rejected), (resolution.Mode, resolution.Configured, resolution.Rejected));
    }

    [Theory]
    [InlineData("workspace", false, false)]
    [InlineData("task-server-shadow", true, false)]
    [InlineData("task-server", true, true)]
    public void Mirror_runs_in_both_task_server_modes_and_attribution_only_after_the_switch(
        string configured, bool mirrors, bool attributes)
    {
        var settings = new TaskArtifactsSourceSettings(Config(configured, "/workspace"));
        Assert.Equal((mirrors, attributes), (settings.MirrorsToTaskServer, settings.AttributesFromTaskServer));
    }

    [Theory]
    [InlineData("task-server", "", true)]
    [InlineData("task-server", "projects/demo/tasks/000/DEMO-1", true)]
    [InlineData("task-server", "../elsewhere", false)]
    [InlineData("task-server", "-sibling", false)]
    [InlineData("task-server-shadow", "projects/demo", false)]
    [InlineData("workspace", "", false)]
    public void Export_guard_is_decided_from_paths_under_the_task_repository(string source, string relative, bool exported)
    {
        var repository = Path.Combine(_root, "workspace");
        var path = relative.Length == 0 ? null
            : relative.StartsWith('-') ? repository + relative
            : Path.GetFullPath(Path.Combine(repository, relative));
        Assert.Equal(exported, TaskArtifactsSourceSetting.IsExportedWorkspacePath(Config(source, repository), path));
    }

    [Theory]
    [InlineData("task.json", 10, true)]
    [InlineData("prompt.md", 10, true)]
    [InlineData("integration-verification.json", 10, true)]
    [InlineData("results/report.html", 10, true)]
    [InlineData("results/huge.zip", 17L * 1024 * 1024, false)]
    [InlineData("logs/cli-output.log", 10, false)]
    [InlineData("review-attempt-1.log", 10, true)]
    [InlineData("review/stdout.txt", 10, false)]
    [InlineData("attachments/screen.png", 10, false)]
    [InlineData("task.json.tmp", 10, false)]
    [InlineData(".runtime/state.json", 10, false)]
    [InlineData(".git/config", 10, false)]
    public void Mirror_takes_task_state_evidence_and_results_but_no_runtime_or_heavy_logs(string path, long size, bool included)
        => Assert.Equal(included, TaskFileMirrorPolicy.Include(path, size, TaskFileMirrorPolicy.DefaultMaximumFileBytes));

    [Fact]
    public void Mirror_plan_sends_new_and_changed_files_and_removes_paths_that_left_the_folder()
    {
        var accepted = new Dictionary<string, string> { ["task.json"] = "a", ["prompt.md"] = "b", ["gone.md"] = "c" };
        TaskFolderFile File(string path, string sha) => new(path, sha, 1, "/x/" + path);

        var delta = TaskFileMirrorPolicy.Plan("demo", "DEMO-1", accepted,
            [File("task.json", "a"), File("prompt.md", "B2"), File("results/report.html", "d")]);
        var first = TaskFileMirrorPolicy.Plan("demo", "DEMO-1", null, [File("task.json", "a")]);

        Assert.Equal(["prompt.md", "results/report.html"], delta.Changed.Select(file => file.Path));
        Assert.Equal(["gone.md"], delta.Removed);
        Assert.Equal(["task.json"], first.Changed.Select(file => file.Path));
        Assert.Empty(first.Removed);
    }

    [Fact]
    public void Export_mode_stops_run_boundary_evidence_and_sweep_commits_into_the_workspace_repository()
    {
        var repository = InitRepository();
        var job = Path.Combine(repository, "projects", "demo", "tasks", "000", "DEMO-1");
        Directory.CreateDirectory(job);
        File.WriteAllText(Path.Combine(job, "task.json"), "{}\n");
        Git(repository, "add", "-A");
        Git(repository, "commit", "-q", "-m", "tracked");
        File.WriteAllText(Path.Combine(job, "task.json"), "{\"state\":\"2-ready\"}\n");
        File.WriteAllText(Path.Combine(job, "status.md"), "status\n");
        var before = Git(repository, "rev-parse", "HEAD");
        var exported = Service(Config(TaskArtifactsSourceSetting.TaskServer, repository));

        var runBoundary = exported.TryCommitRunBoundary(repository, "DEMO-1", null, job, ReviewDecisionKind.Reissue);
        var evidence = exported.TryCommitEvidence(repository, [Path.Combine(repository, "projects", "demo")], [], "evidence\n");
        var sweep = exported.TryCommitTrackedSweep(repository);

        Assert.All([runBoundary, evidence, sweep], result =>
            Assert.Equal((false, TaskArtifactsSourceSetting.ExportSkipReason), (result.DidCommit, result.Error)));
        Assert.Equal(before, Git(repository, "rev-parse", "HEAD"));

        var authority = Service(Config(TaskArtifactsSourceSetting.Workspace, repository));
        var rolledBack = authority.TryCommitRunBoundary(repository, "DEMO-1", null, job, ReviewDecisionKind.Reissue);
        Assert.True(rolledBack.DidCommit, rolledBack.Error);
    }

    [Fact]
    public async Task Export_mode_push_worker_never_spawns_git_for_the_workspace_repository()
    {
        var repository = InitRepository();
        var calls = new List<IReadOnlyList<string>>();
        Task<WorkspaceArtifactPushWorker.PushGitResult> Run(string _, IReadOnlyList<string> args, TimeSpan __, CancellationToken ___)
        {
            calls.Add(args);
            return Task.FromResult(new WorkspaceArtifactPushWorker.PushGitResult(0, "0", "", GitProcessFailureKind.None));
        }
        var config = Config(TaskArtifactsSourceSetting.TaskServer, repository);
        var worker = new WorkspaceArtifactPushWorker(new WorkspaceArtifactPushQueue(),
            NullLogger<WorkspaceArtifactPushWorker>.Instance, config, bus: null, advisories: null,
            time: new ManualClock(), runGit: Run);

        Assert.Null(worker.StartupCatchUpRequest());
        Assert.True(await worker.ProcessAsync(new WorkspaceArtifactPushRequest(repository, "DEMO-1"), default));
        Assert.Empty(calls);

        var source = Path.Combine(_root, "source-repo");
        Directory.CreateDirectory(source);
        await worker.ProcessAsync(new WorkspaceArtifactPushRequest(source, "managed-source"), default);
        Assert.NotEmpty(calls);
    }

    private string InitRepository()
    {
        var repository = Path.Combine(_root, "workspace");
        Directory.CreateDirectory(repository);
        Git(repository, "init", "-q", "-b", "main");
        Git(repository, "config", "user.name", "test");
        Git(repository, "config", "user.email", "test@example.com");
        File.WriteAllText(Path.Combine(repository, "README.md"), "seed\n");
        Git(repository, "add", "README.md");
        Git(repository, "commit", "-q", "-m", "seed");
        return repository;
    }

    private static WorkspaceArtifactCommitService Service(IConfiguration config)
        => new(config, NullLogger<WorkspaceArtifactCommitService>.Instance);

    private static IConfiguration Config(string source, string repository)
        => new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [TaskArtifactsSourceSetting.SectionKey] = source,
            ["TaskRepository"] = repository,
            ["WorkspaceArtifacts:PushRetrySeconds"] = "0",
        }).Build();

    private static string Git(string cwd, params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = cwd,
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
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', args)}: {error}");
        return output.Trim();
    }

    private sealed class ManualClock : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => new(2026, 10, 27, 9, 0, 0, TimeSpan.Zero);
    }
}
