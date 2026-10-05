using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed record HostAdmissionDecision(bool Admitted, string Reason);

/// <summary>
/// Host-local permit admission. Task Server publishes globally ordered work,
/// while this policy uses facts that only the execution host can prove before
/// it accepts durable authority.
/// </summary>
public static class HostAdmissionPolicy
{
    public static HostAdmissionDecision Decide(
        WorkPermitDto permit,
        RunnerOptions options,
        GitPushProbeResult gitCapability,
        RunSpecDto? runSpec)
    {
        if (!gitCapability.CanPush)
            return new(false, $"git-push-unavailable: {gitCapability.Detail}");
        if (string.IsNullOrWhiteSpace(options.GitRemote))
            return new(false, "repository-clone-unavailable: RUNNER_GIT_REMOTE is not configured.");
        // V1 permits do not yet carry card selection. Keep it explicit here so
        // a future caller must supply the resolved spec when that contract lands.
        var selected = CliSelection.Resolve(options, runSpec);
        if (!ExecutableExists(selected.FileName))
            return new(false, $"toolchain-unavailable: '{selected.FileName}' was not found on this host.");
        if (string.IsNullOrWhiteSpace(permit.Task.Body))
            return new(false, "task-input-unavailable: the permit has no executable task body.");
        return new(true, "host capability, repository, and toolchain checks passed.");
    }

    private static bool ExecutableExists(string executable)
    {
        if (string.IsNullOrWhiteSpace(executable)) return false;
        if (Path.IsPathRooted(executable) || executable.Contains(Path.DirectorySeparatorChar))
            return File.Exists(executable);
        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        return path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries)
            .Select(directory => Path.Combine(directory, executable))
            .Any(File.Exists);
    }
}
