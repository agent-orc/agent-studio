using System.Diagnostics;
using System.Text;
using System.Text.Json;
using AgentStudio.TaskServer;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

// clock-independent: stores run on a ManualTimeProvider; latency is measured with Stopwatch, never the wall clock.

/// <summary>
/// Workspace repository as export (pipeline delivery target, migration M6,
/// decision D9 A, AGT-3320): the export writes the flat task-folder layout
/// today's tools read, is idempotent, commits only changes, and a failed or
/// hanging push never holds a board read.
/// </summary>
public sealed class WorkspaceExportTests
{
    [Theory]
    [InlineData("Agent Studio", "AGT-3320", "projects/Agent Studio/tasks/003/AGT-3320")]
    [InlineData("demo", "DEMO-7", "projects/demo/tasks/000/DEMO-7")]
    [InlineData("demo", "LEGACY", "projects/demo/tasks/000/LEGACY")]
    public void Task_folder_uses_the_flat_bucketed_layout(string project, string key, string expected)
        => Assert.Equal(expected, WorkspaceExportLayout.TaskFolder(project, key));

    [Theory]
    [InlineData("report.html", "results/report.html")]
    [InlineData("results/report.html", "results/report.html")]
    [InlineData("screens\\final.png", "results/screens/final.png")]
    public void Run_artifacts_land_under_results(string name, string expected)
        => Assert.Equal(expected, WorkspaceExportLayout.ArtifactPath(name));

    [Fact]
    public void Exported_task_json_keeps_mirrored_fields_and_takes_card_core_from_the_task_server()
    {
        var task = new WorkspaceExportTask("demo", "DEMO-7", "Server title", "Body", "6-completed",
            new DateTime(2026, 10, 1, 8, 0, 0, DateTimeKind.Utc), new DateTime(2026, 10, 2, 8, 0, 0, DateTimeKind.Utc), 4,
            """{"id":"legacy-slug","agent":"codex","title":"Legacy title"}""", [], [], 0);

        var fromLegacy = JsonDocument.Parse(WorkspaceExportLayout.RenderTaskJson(task, null)).RootElement;
        var fromMirror = JsonDocument.Parse(WorkspaceExportLayout.RenderTaskJson(task,
            """{"id":"demo-7-slug","agent":"claude","tags":["x"],"order":9}"""u8.ToArray())).RootElement;
        var fromNothing = JsonDocument.Parse(WorkspaceExportLayout.RenderTaskJson(task with { LegacyTaskJson = "[1]" }, "{"u8.ToArray())).RootElement;

        Assert.Equal(("legacy-slug", "codex", "Server title", "6-completed", 4),
            (fromLegacy.GetProperty("id").GetString(), fromLegacy.GetProperty("agent").GetString(),
                fromLegacy.GetProperty("title").GetString(), fromLegacy.GetProperty("state").GetString(),
                fromLegacy.GetProperty("order").GetInt32()));
        Assert.Equal(("demo-7-slug", "claude", "DEMO-7", 9),
            (fromMirror.GetProperty("id").GetString(), fromMirror.GetProperty("agent").GetString(),
                fromMirror.GetProperty("key").GetString(), fromMirror.GetProperty("order").GetInt32()));
        Assert.Equal("DEMO-7", fromNothing.GetProperty("id").GetString());
        Assert.Equal("2026-10-02T08:00:00.0000000Z", fromNothing.GetProperty("updatedAt").GetString());
    }

    [Fact]
    public async Task Full_export_writes_task_folders_once_and_removes_files_no_card_holds()
    {
        using var temp = new TempDirectory();
        var store = TaskArtifactsContentTests.Store(Path.Combine(temp.Path, "store"));
        await store.InitializeAsync();
        var (project, task) = await TaskArtifactsContentTests.SeedTaskAsync(store, "Export");
        var archived = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Archived card", null, "7-archive"), "test", default);
        var large = Encoding.UTF8.GetBytes(new string('E', 4000));
        await TaskArtifactsContentTests.MirrorAsync(store, project.Name, task.TaskKey, "results/report.html", large);
        await TaskArtifactsContentTests.MirrorAsync(store, project.Name, task.TaskKey, "review/verdict.json", "{}"u8.ToArray());
        var root = Path.Combine(temp.Path, "export");
        var service = new WorkspaceExportService(store);

        var first = await service.ExportAsync(root, default);

        var folder = Path.Combine(root, "projects", "Export", "tasks", "000", task.TaskKey);
        var taskJson = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(folder, "task.json"))).RootElement;
        Assert.Equal((task.TaskKey, "Artefact task", task.State),
            (taskJson.GetProperty("key").GetString(), taskJson.GetProperty("title").GetString(), taskJson.GetProperty("state").GetString()));
        Assert.Equal("Do the work", File.ReadAllText(Path.Combine(folder, "prompt.md")));
        Assert.Equal(large, File.ReadAllBytes(Path.Combine(folder, "results", "report.html")));
        Assert.True(File.Exists(Path.Combine(root, "projects", "Export", "tasks", "000", archived.TaskKey, "task.json")));
        Assert.Equal((1, 2, 5, 0), (first.Projects, first.Tasks, first.FilesWritten, first.FilesRemoved));
        Assert.True(File.Exists(Path.Combine(root, WorkspaceExportLayout.ManifestFileName)));

        var second = await service.ExportAsync(root, default);
        Assert.Equal((0, 5), (second.FilesWritten, second.FilesUnchanged));

        File.WriteAllText(Path.Combine(folder, "stale.txt"), "left behind");
        await store.MirrorWorkspaceTaskFilesAsync(project.Name, new TaskFileMirrorRequest(
            [new TaskFileMirrorTask(task.TaskKey, [], ["review/verdict.json"])]), "mirror", default);
        var third = await service.ExportAsync(root, default);
        Assert.Equal(2, third.FilesRemoved);
        Assert.False(Directory.Exists(Path.Combine(folder, "review")));
    }

    [Fact]
    [Trait("Category", "Git")]
    public async Task Export_job_commits_only_changes_and_pushes_the_backlog()
    {
        using var temp = new TempDirectory();
        var (store, remote, clone, project, taskKey) = await SeedRepositoryAsync(temp.Path);
        // A source-repository ignore rule in the clone must not drop exported task folders.
        Directory.CreateDirectory(Path.Combine(clone, "projects", project));
        File.WriteAllText(Path.Combine(clone, "projects", project, ".gitignore"), "tasks/\n");
        File.WriteAllText(Path.Combine(clone, "notes.md"), "not export-owned\n");
        var status = new WorkspaceExportStatus();
        var job = Job(store, status, clone, push: true);

        var first = await job.RunOnceAsync(default);
        Assert.NotNull(first.LastCommitSha);
        var committed = Git(clone, "ls-files");
        Assert.Contains($"projects/{project}/tasks/000/{taskKey}/task.json", committed);
        Assert.DoesNotContain("notes.md", committed);
        Assert.Equal((true, 0), (first.LastPushSucceeded, first.AheadCommits));
        Assert.Equal(first.LastCommitSha, Git(remote, "rev-parse", "refs/heads/main").Trim());

        var second = await job.RunOnceAsync(default);
        Assert.Equal(first.LastCommitSha, second.LastCommitSha);
        Assert.Equal("1", Git(clone, "rev-list", "--count", "HEAD").Trim());

        Git(clone, "remote", "set-url", "origin", Path.Combine(temp.Path, "missing-remote.git"));
        await TaskArtifactsContentTests.MirrorAsync(store, project, taskKey, "status.md", "changed"u8.ToArray());
        var failed = await job.RunOnceAsync(default);
        Assert.False(failed.LastPushSucceeded);
        Assert.Equal(1, failed.AheadCommits);
        Assert.Contains("git push", failed.LastError);
    }

    [Fact]
    public async Task A_hanging_export_push_does_not_hold_a_board_read()
    {
        using var temp = new TempDirectory();
        var store = TaskArtifactsContentTests.Store(Path.Combine(temp.Path, "store"));
        await store.InitializeAsync();
        var (project, _) = await TaskArtifactsContentTests.SeedTaskAsync(store, "Latency");
        var clone = Path.Combine(temp.Path, "export");
        Directory.CreateDirectory(Path.Combine(clone, ".git"));
        var pushStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releasePush = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var git = new WorkspaceExportGit("git", async (_, args, _, ct) =>
        {
            if (args[0] == "push")
            {
                pushStarted.SetResult();
                await releasePush.Task.WaitAsync(ct);
                return new WorkspaceExportGitResult(0, "", "");
            }
            return args[0] switch
            {
                "diff" => new WorkspaceExportGitResult(1, "", ""),
                "rev-list" => new WorkspaceExportGitResult(0, "2000\n", ""),
                _ => new WorkspaceExportGitResult(0, "abc\n", ""),
            };
        });
        var job = Job(store, new WorkspaceExportStatus(), clone, push: true, git);

        var cycle = job.RunOnceAsync(default);
        await pushStarted.Task.WaitAsync(TimeSpan.FromSeconds(30));
        var board = await store.GetBoardReadModelAsync(project.Name, default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, board.LiveCount);
        Assert.False(cycle.IsCompleted);

        releasePush.SetResult();
        Assert.True((await cycle).LastPushSucceeded);
    }

    /// <summary>
    /// The card's measurement: board read latency with the export idle against
    /// a push that hangs on a remote hook while 2,000 export commits wait.
    /// Machine-bound, because it measures wall time on the host; the numbers go
    /// to <c>WORKSPACE_EXPORT_MEASURE_OUT</c> when that variable names a file.
    /// </summary>
    [Fact]
    [Trait("Category", "MachineBound")]
    public async Task Push_backlog_of_the_export_does_not_change_board_latency()
    {
        using var temp = new TempDirectory();
        var (store, remote, clone, project, _) = await SeedRepositoryAsync(temp.Path, tasks: 300);
        const int backlog = 2000;
        Git(clone, "-c", "user.name=t", "-c", "user.email=t@local", "commit", "-q", "--allow-empty", "-m", "seed");
        for (var index = 0; index < backlog; index++)
            Git(clone, "-c", "user.name=t", "-c", "user.email=t@local", "commit", "-q", "--allow-empty", "-m", $"backlog {index}");
        var hook = Path.Combine(remote, "hooks", "pre-receive");
        var started = Path.Combine(temp.Path, "push-started");
        File.WriteAllText(hook, $"#!/bin/sh\ntouch '{started}'\nsleep 20\nexit 1\n");
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(hook, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        var job = Job(store, new WorkspaceExportStatus(), clone, push: true, timeoutSeconds: 15);

        var idle = await MeasureBoardAsync(store, project, 400);
        var cycle = job.RunOnceAsync(default);
        for (var wait = 0; !File.Exists(started) && wait < 600; wait++) await Task.Delay(100);
        Assert.True(File.Exists(started), "the export push never reached the remote");
        var duringPush = await MeasureBoardAsync(store, project, 400);
        var status = await cycle;

        var measurement = new
        {
            boardReads = 400,
            cards = 300,
            backlogCommits = status.AheadCommits,
            pushSucceeded = status.LastPushSucceeded,
            pushError = status.LastError,
            idleP50Ms = idle.P50, idleP95Ms = idle.P95,
            duringPushP50Ms = duringPush.P50, duringPushP95Ms = duringPush.P95,
        };
        if (Environment.GetEnvironmentVariable("WORKSPACE_EXPORT_MEASURE_OUT") is { Length: > 0 } output)
            File.WriteAllText(output, JsonSerializer.Serialize(measurement, new JsonSerializerOptions { WriteIndented = true }));
        Assert.False(status.LastPushSucceeded);
        Assert.True(status.AheadCommits >= backlog, $"backlog {status.AheadCommits}");
        Assert.True(duringPush.P95 <= Math.Max(idle.P95 * 2, idle.P95 + 20),
            $"board p95 {duringPush.P95:0.00} ms during the push against {idle.P95:0.00} ms idle");
    }

    [Fact]
    public void Export_command_needs_a_target()
    {
        var parsed = TaskServerCommandLine.Parse(["export", "--target", "/tmp/export", "--commit"]);
        Assert.Equal((TaskServerCommandKind.Export, "/tmp/export", true), (parsed.Kind, parsed.ExportTarget, parsed.ExportCommit));
        Assert.Throws<ArgumentException>(() => TaskServerCommandLine.Parse(["export"]));
    }

    private static async Task<(double P50, double P95)> MeasureBoardAsync(TaskServerStore store, string project, int reads)
    {
        var samples = new List<double>(reads);
        for (var index = 0; index < reads; index++)
        {
            var watch = Stopwatch.StartNew();
            await store.GetBoardReadModelAsync(project, default);
            samples.Add(watch.Elapsed.TotalMilliseconds);
        }
        samples.Sort();
        return (samples[reads / 2], samples[(int)Math.Ceiling(reads * 0.95) - 1]);
    }

    private static WorkspaceExportHostedService Job(
        TaskServerStore store, WorkspaceExportStatus status, string root, bool push,
        WorkspaceExportGit? git = null, int timeoutSeconds = 60)
        => new(new WorkspaceExportService(store), status, Options.Create(new TaskServerOptions
            {
                WorkspaceExportRoot = root,
                WorkspaceExportPush = push,
                WorkspaceExportBranch = "main",
                WorkspaceExportPushTimeoutSeconds = timeoutSeconds,
            }), new ManualTimeProvider(new DateTimeOffset(2026, 10, 27, 9, 0, 0, TimeSpan.Zero)),
            NullLogger<WorkspaceExportHostedService>.Instance, git);

    private static async Task<(TaskServerStore Store, string Remote, string Clone, string Project, string TaskKey)> SeedRepositoryAsync(
        string root, int tasks = 1)
    {
        var store = TaskArtifactsContentTests.Store(Path.Combine(root, "store"));
        await store.InitializeAsync();
        var workspace = await store.CreateWorkspaceAsync(new CreateWorkspaceRequest("Seeded"), "test", default);
        var project = await store.CreateProjectAsync(new CreateProjectRequest(workspace.WorkspaceId, "Seeded", "SEE"), "test", default);
        var first = await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest("Card 0", "Body 0"), "test", default);
        for (var index = 1; index < tasks; index++)
            await store.CreateTaskAsync(project.ProjectId, new CreateTaskRequest($"Card {index}", $"Body {index}"), "test", default);
        var remote = Path.Combine(root, "remote.git");
        var clone = Path.Combine(root, "clone");
        Git(root, "init", "-q", "--bare", "-b", "main", remote);
        Git(root, "init", "-q", "-b", "main", clone);
        Git(clone, "remote", "add", "origin", remote);
        return (store, remote, clone, project.Name, first.TaskKey);
    }

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
        return output;
    }
}
