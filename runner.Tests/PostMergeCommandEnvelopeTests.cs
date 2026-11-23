using AgentRunner;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentRunner.Tests;

public sealed class PostMergeCommandEnvelopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "post-merge-envelope-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Host_commands_use_a_required_attachment_to_their_own_worker_cgroup()
    {
        var delegated = Path.Combine(_root, "agent-runner.service");
        var child = Path.Combine(delegated, "worker-post-merge");
        Directory.CreateDirectory(child);
        File.WriteAllText(Path.Combine(child, "cpu.max"), "max 100000");
        File.WriteAllText(Path.Combine(child, "pids.max"), "max");
        var workerDirectory = Path.Combine(_root, "marker");
        var cgroup = WorkerCgroup.TryCreate(delegated, workerDirectory, "post-merge",
            WorkerResourceEnvelope.Compute(12, 2, 2), _ => { })!;
        using var envelope = new PostMergeCommandEnvelope(workerDirectory, cgroup, _ => { });

        var launch = envelope.Launch("sh", ["-c", "echo verify"]);

        Assert.Equal("/bin/sh", launch.FileName);
        Assert.Equal(new[] { "-c", WorkerCgroup.RequiredAttachScript, "agent-host-worker",
            Path.Combine(child, "cgroup.procs"), "sh", "-c", "echo verify" }, launch.Arguments);
        Assert.Equal("600000 100000", File.ReadAllText(Path.Combine(child, "cpu.max")));
        Assert.Equal("1536", File.ReadAllText(Path.Combine(child, "pids.max")));
        var environment = envelope.ChildEnvironment(new Dictionary<string, string?>
        {
            [ReviewBuildServerIsolation.DisableSharedCompilation] = "true",
            ["PROJECT_VALUE"] = "retained",
        });
        Assert.True(ReviewBuildServerIsolation.IsIsolated(environment));
        Assert.Equal(workerDirectory, environment[ReviewBuildServerIsolation.MsBuildDebugPath]);
        Assert.Equal("retained", environment["PROJECT_VALUE"]);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Failed_attachment_never_executes_the_project_command_in_the_daemon_leaf()
    {
        PlatformGate.LinuxOnly("Linux host commands attach through the cgroup wrapper");
        Directory.CreateDirectory(_root);
        var launched = Path.Combine(_root, "project-started");
        var launch = WorkerCgroup.Wrap(Path.Combine(_root, "missing", "cgroup.procs"),
            PosixShell.RequirePath(), ["-c", "touch \"$1\"", "project", launched], requireAttachment: true);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var result = await ProcessRunner.RunAsync(launch.FileName, launch.Arguments, ct: deadline.Token);

        Assert.Equal(125, result.ExitCode);
        Assert.False(File.Exists(launched));
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Host_command_refuses_execution_when_its_worker_envelope_is_disabled()
    {
        PlatformGate.LinuxOnly("Linux host commands require a delegated worker cgroup");
        Directory.CreateDirectory(_root);
        using var http = new HttpClient();
        var options = new PostMergeRunOptions("demo", _root, "http://test/", "develop", "origin",
            "unused", "window", 6, TimeSpan.Zero, 1, [], false);
        var worker = new PostMergeRunCommand(options, TextWriter.Null, new FakeTimeProvider(), http,
            hostOptions: RunnerOptionsFixture.WithSlots(2, 2, envelopeEnabled: false));

        var error = await Assert.ThrowsAsync<IOException>(() => worker.RunCommand("touch project-started",
            new Dictionary<string, string>(), CancellationToken.None));

        Assert.Contains("execution in the daemon leaf is refused", error.Message);
        Assert.False(File.Exists(Path.Combine(_root, "project-started")));
    }

    [Fact]
    public void Standalone_commands_do_not_borrow_a_daemon_envelope()
        => Assert.Null(PostMergeCommandEnvelope.Prepare(null, _ => { }));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
