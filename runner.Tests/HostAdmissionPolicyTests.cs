using AgentRunner;
using AgentStudio.TaskServer.Contracts;
using Xunit;

namespace AgentRunner.Tests;

public sealed class HostAdmissionPolicyTests
{
    [Fact]
    public void Admission_uses_host_git_clone_toolchain_and_task_input_facts()
    {
        using var executable = new TemporaryExecutable();
        var options = Options(executable.Path);
        var permit = Permit(body: "Implement the task.");

        var admitted = HostAdmissionPolicy.Decide(
            permit,
            options,
            new GitPushProbeResult(GitPushProbe.Ready, "push dry-run passed"),
            runSpec: null);
        var readOnly = HostAdmissionPolicy.Decide(
            permit,
            options,
            new GitPushProbeResult(GitPushProbe.ReadOnly, "permission denied"),
            runSpec: null);
        var missingTool = HostAdmissionPolicy.Decide(
            permit,
            Options(Path.Combine(executable.Directory, "missing-cli")),
            new GitPushProbeResult(GitPushProbe.Ready, "push dry-run passed"),
            runSpec: null);
        var missingInput = HostAdmissionPolicy.Decide(
            Permit(body: null),
            options,
            new GitPushProbeResult(GitPushProbe.Ready, "push dry-run passed"),
            runSpec: null);

        Assert.True(admitted.Admitted);
        Assert.Contains("passed", admitted.Reason);
        Assert.False(readOnly.Admitted);
        Assert.StartsWith("git-push-unavailable", readOnly.Reason);
        Assert.False(missingTool.Admitted);
        Assert.StartsWith("toolchain-unavailable", missingTool.Reason);
        Assert.False(missingInput.Admitted);
        Assert.StartsWith("task-input-unavailable", missingInput.Reason);
    }

    [Fact]
    public void Admission_checks_the_card_selected_provider_binary()
    {
        using var executable = new TemporaryExecutable();
        var options = Options(
            executable.Path,
            Path.Combine(executable.Directory, "missing-codex"));
        var git = new GitPushProbeResult(GitPushProbe.Ready, "push dry-run passed");
        var permit = Permit("Run Codex on this card.");

        var hostDefault = HostAdmissionPolicy.Decide(permit, options, git, runSpec: null);
        var cardSelected = HostAdmissionPolicy.Decide(
            permit, options, git, new RunSpecDto(CliType: CliSelection.CodexCli));

        Assert.True(hostDefault.Admitted);
        Assert.False(cardSelected.Admitted);
        Assert.Contains("missing-codex", cardSelected.Reason);
    }

    private static RunnerOptions Options(string cli, string? codexCli = null)
        => new()
        {
            ServerUrl = "http://127.0.0.1:5071",
            RunnerId = "runner-a",
            RunnerName = "runner-a",
            Hostname = "host-a",
            BackendName = "test",
            GitRemote = "https://example.test/repository.git",
            WorkDir = Path.GetTempPath(),
            BaseBranch = "develop",
            ClaudeCliBin = cli,
            CodexCliBin = codexCli ?? cli,
            TtlSeconds = 120,
            HeartbeatSeconds = 30,
            RunTimeoutSeconds = 300,
            HostMaxParallelism = 2,
            PollSeconds = 5,
        };

    private static WorkPermitDto Permit(string? body)
    {
        var now = DateTime.UtcNow;
        return new WorkPermitDto(
            "permit-a",
            new TaskDto("task-a", "project-a", "TS-1", "Task", "2-ready", 1, now, now, body),
            1,
            now.AddMinutes(5));
    }

    private sealed class TemporaryExecutable : IDisposable
    {
        public TemporaryExecutable()
        {
            Directory = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "host-admission-tests",
                Guid.NewGuid().ToString("N"));
            System.IO.Directory.CreateDirectory(Directory);
            Path = System.IO.Path.Combine(Directory, "agent-cli");
            File.WriteAllText(Path, string.Empty);
        }

        public string Directory { get; }
        public string Path { get; }

        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory))
                System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
