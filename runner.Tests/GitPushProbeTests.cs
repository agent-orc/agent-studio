using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class GitPushProbeTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "agent-runner-git-push-probe-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Workflow_probe_pushes_and_deletes_its_throwaway_branch()
    {
        var source = Path.Combine(_root, "source");
        var remote = Path.Combine(_root, "remote.git");
        var work = Path.Combine(_root, "work");
        Directory.CreateDirectory(source);
        Directory.CreateDirectory(work);
        await Git(_root, "init", "--bare", "--initial-branch=main", remote);
        await Git(source, "init", "--initial-branch=main");
        await File.WriteAllTextAsync(Path.Combine(source, "README.md"), "probe fixture\n");
        await Git(source, "add", "README.md");
        await Git(
            source,
            "-c", "user.name=Runner Test",
            "-c", "user.email=runner-test@example.invalid",
            "commit", "-m", "seed");
        await Git(source, "remote", "add", "origin", remote);
        await Git(source, "push", "-u", "origin", "main");

        var result = await GitPushProbe.RunAsync(
            new RunnerOptions
            {
                ServerUrl = "http://task-server",
                RunnerId = "runner-test",
                RunnerName = "runner-test",
                Hostname = "test-host",
                BackendName = "test",
                GitRemote = remote,
                WorkDir = work,
                BaseBranch = "main",
                ClaudeCliBin = "codex",
            },
            _ => { },
            CancellationToken.None);

        Assert.Equal(GitPushProbe.Ready, result.Status);
        var refs = await RunGit(
            _root,
            "--git-dir", remote,
            "for-each-ref",
            "--format=%(refname)",
            "refs/heads/runner-capability-probe");
        Assert.True(refs.Success, refs.StdErr);
        Assert.Equal("", refs.StdOut.Trim());
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); }
        catch { /* best effort */ }
    }

    private static async Task Git(string workingDirectory, params string[] args)
    {
        var result = await RunGit(workingDirectory, args);
        Assert.True(
            result.Success,
            $"git {string.Join(' ', args)} failed ({result.ExitCode}): {result.StdErr}");
    }

    private static Task<ProcessResult> RunGit(string workingDirectory, params string[] args)
        => ProcessRunner.RunAsync("git", args, workingDirectory, ct: CancellationToken.None);
}
