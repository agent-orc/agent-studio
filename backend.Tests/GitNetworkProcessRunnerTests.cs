using System.ComponentModel;
using System.Diagnostics;

using Xunit;

namespace AgentStudio.Tests;

public sealed class GitNetworkProcessRunnerTests : IDisposable
{
    private readonly string _tempDir = Path.Combine(
        Path.GetTempPath(),
        "agent-studio-git-network-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void IntegrationCatchUpBudget_IsLongerThanMetadataProbeBudget()
    {
        Assert.Equal(TimeSpan.FromSeconds(30), GitNetworkProcessRunner.DefaultTimeout);
        Assert.Equal(TimeSpan.FromMinutes(5), GitNetworkProcessRunner.CatchUpTimeout);
        Assert.True(GitNetworkProcessRunner.CatchUpTimeout > GitNetworkProcessRunner.DefaultTimeout);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task HangingFakeRemote_TimesOutAndReapsEveryProcess()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fake remote process probe uses Linux process evidence.");
        Directory.CreateDirectory(_tempDir);
        var fakeRemote = CreateHangingFakeRemote();
        var observedPids = new List<int>();

        for (var attempt = 0; attempt < 3; attempt++)
        {
            var pidFile = Path.Combine(_tempDir, $"git-{attempt}.pid");
            var startInfo = CreateNetworkStartInfo(fakeRemote, pidFile, "ls-remote");

            var result = await GitNetworkProcessRunner.RunAsync(
                startInfo,
                stdin: null,
                timeout: TimeSpan.FromMilliseconds(250),
                CancellationToken.None);

            Assert.Equal(GitProcessFailureKind.TimedOut, result.FailureKind);
            Assert.Equal(-1, result.ExitCode);
            var pids = await ReadPidsAsync(pidFile);
            observedPids.AddRange(pids);
            foreach (var pid in pids)
                await AssertProcessExitedAsync(pid);
        }

        Assert.All(observedPids, pid => Assert.False(Directory.Exists($"/proc/{pid}")));
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task HangingFakeRemote_CancellationReapsProcessTree()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fake remote process probe uses Linux process evidence.");
        Directory.CreateDirectory(_tempDir);
        var fakeRemote = CreateHangingFakeRemote();
        var pidFile = Path.Combine(_tempDir, "cancelled-git.pid");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var result = await GitNetworkProcessRunner.RunAsync(
            CreateNetworkStartInfo(fakeRemote, pidFile, "ls-remote"),
            stdin: null,
            timeout: TimeSpan.FromSeconds(10),
            cancellation.Token);

        Assert.Equal(GitProcessFailureKind.Cancelled, result.FailureKind);
        var pids = await ReadPidsAsync(pidFile);
        foreach (var pid in pids)
            await AssertProcessExitedAsync(pid);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task SynchronousHangingFakeRemote_TimesOutAndReapsProcessTree()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fake remote process probe uses Linux process evidence.");
        Directory.CreateDirectory(_tempDir);
        var fakeRemote = CreateHangingFakeRemote();
        var pidFile = Path.Combine(_tempDir, "sync-git.pid");

        var result = GitNetworkProcessRunner.Run(
            CreateNetworkStartInfo(fakeRemote, pidFile, "ls-remote"),
            timeout: TimeSpan.FromMilliseconds(250));

        Assert.Equal(GitProcessFailureKind.TimedOut, result.FailureKind);
        Assert.Equal(-1, result.ExitCode);
        var pids = await ReadPidsAsync(pidFile);
        foreach (var pid in pids)
            await AssertProcessExitedAsync(pid);
    }

    [SkippableFact]
    [Trait("Category", "MachineBound")]
    public async Task SynchronousHangingFakeRemote_CancellationReapsProcessTree()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "The fake remote process probe uses Linux process evidence.");
        Directory.CreateDirectory(_tempDir);
        var fakeRemote = CreateHangingFakeRemote();
        var pidFile = Path.Combine(_tempDir, "sync-cancelled-git.pid");
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(250));

        var result = GitNetworkProcessRunner.Run(
            CreateNetworkStartInfo(fakeRemote, pidFile, "ls-remote"),
            timeout: TimeSpan.FromSeconds(10),
            cancellationToken: cancellation.Token);

        Assert.Equal(GitProcessFailureKind.Cancelled, result.FailureKind);
        var pids = await ReadPidsAsync(pidFile);
        foreach (var pid in pids)
            await AssertProcessExitedAsync(pid);
    }

    [Fact]
    public async Task ProcessHandleExhaustion_ReturnsTypedFailureWithoutThrowing()
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("push");

        var result = await GitNetworkProcessRunner.RunAsync(
            startInfo,
            stdin: null,
            timeout: TimeSpan.FromSeconds(1),
            CancellationToken.None,
            _ => throw new Win32Exception(24, "Too many open files"));

        Assert.Equal(GitProcessFailureKind.ResourceExhaustion, result.FailureKind);
        Assert.Equal(-1, result.ExitCode);
        Assert.Contains("resource exhaustion", result.StandardError, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        if (!Directory.Exists(_tempDir)) return;
        try { Directory.Delete(_tempDir, recursive: true); }
        catch (Exception ex) { SilentCatch.Note(ex, "GitNetworkProcessRunnerTests cleanup"); }
    }

    private string CreateHangingFakeRemote()
    {
        var path = Path.Combine(_tempDir, "fake-ssh-remote.sh");
        File.WriteAllText(
            path,
            "#!/bin/sh\nsleep 300 &\nchild=$!\nprintf '%s %s' \"$$\" \"$child\" > \"$FAKE_REMOTE_PID_FILE\"\nwait \"$child\"\n");
        if (OperatingSystem.IsLinux())
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        }
        return path;
    }

    private static ProcessStartInfo CreateNetworkStartInfo(
        string fakeRemote,
        string pidFile,
        string operation)
    {
        var startInfo = new ProcessStartInfo("git")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add(operation);
        startInfo.ArgumentList.Add("ssh://fake.invalid/repository.git");
        startInfo.Environment["GIT_SSH_COMMAND"] = fakeRemote;
        startInfo.Environment["FAKE_REMOTE_PID_FILE"] = pidFile;
        return startInfo;
    }

    private static async Task<int[]> ReadPidsAsync(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (!File.Exists(path) && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.True(File.Exists(path), "The fake Git process never published its PID.");
        return (await File.ReadAllTextAsync(path))
            .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(value => int.Parse(value, System.Globalization.CultureInfo.InvariantCulture))
            .ToArray();
    }

    private static async Task AssertProcessExitedAsync(int pid)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (Directory.Exists($"/proc/{pid}") && DateTime.UtcNow < deadline)
            await Task.Delay(20);
        Assert.False(Directory.Exists($"/proc/{pid}"), $"Git process {pid} survived bounded termination.");
    }
}
