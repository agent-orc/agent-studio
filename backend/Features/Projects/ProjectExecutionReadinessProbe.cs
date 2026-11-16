using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

public sealed record ProjectExecutionProbeRequest(string HostId);
public sealed record ProjectExecutionProbeCheck(string Key, string Label, bool Passed, string Detail);

/// <summary>Evaluates the server's delivery proof and the runner's advertised capabilities.</summary>
public static class ProjectExecutionReadinessProbe
{
    public static IReadOnlyList<ProjectExecutionProbeCheck> Evaluate(
        string integrationBranch,
        IReadOnlyList<string> stack,
        RunnerProjectPreflight? preflight,
        IReadOnlyList<CapabilityHealthDto> capabilities,
        string? hostStatus)
    {
        var branch = integrationBranch.Trim();
        var deliveryReady = preflight?.Status == "ready"
            && string.Equals(preflight.TargetBranch, branch, StringComparison.OrdinalIgnoreCase);
        var code = new ProjectExecutionProbeCheck("code", "Code channel", deliveryReady,
            deliveryReady ? "Project delivery preflight proved repository fetch and write access."
                : preflight?.Detail ?? "No project delivery preflight is available for this host.");
        var branchReady = deliveryReady && branch.Length > 0;
        var branchCheck = new ProjectExecutionProbeCheck("branch", "Integration branch", branchReady,
            branchReady ? $"Integration branch {branch} was verified by delivery preflight."
                : $"Integration branch {branch} has no matching successful delivery preflight.");

        bool Ready(string key) => capabilities.Any(capability =>
            string.Equals(capability.Key, key, StringComparison.OrdinalIgnoreCase)
            && capability.IsFresh && capability.AdvertisedStatus == "ready"
            && capability.HealthState == CapabilityHealthStates.Healthy);
        var required = stack.Select(value => value.Trim().ToLowerInvariant())
            .Where(value => value.Length > 0 && value != "shell")
            .Select(value => $"toolchain:{value}").Distinct(StringComparer.Ordinal).ToArray();
        var missing = required.Where(key => !Ready(key)).ToArray();
        var cliReady = capabilities.Any(capability =>
            capability.Key.StartsWith("cli-execution:", StringComparison.OrdinalIgnoreCase)
            && Ready(capability.Key));
        var toolchainReady = stack.Count > 0 && missing.Length == 0 && cliReady;
        var toolchainDetail = stack.Count == 0
            ? "The project stack could not be detected from project.yml."
            : missing.Length > 0
                ? $"Missing ready capability for {string.Join(", ", missing)}."
                : !cliReady ? "No ready coding CLI is advertised by this host."
                : $"Detected stack {string.Join(", ", stack)} and a coding CLI are ready.";
        var toolchain = new ProjectExecutionProbeCheck("toolchain", "Toolchain", toolchainReady, toolchainDetail);

        var hostReady = (string.Equals(hostStatus, "active", StringComparison.OrdinalIgnoreCase)
                || string.Equals(hostStatus, "online", StringComparison.OrdinalIgnoreCase))
            && Ready(CapabilityProtocol.CodingExecutor);
        var host = new ProjectExecutionProbeCheck("noop", "Host readiness", hostReady,
            hostReady ? "Coding host is online and ready to accept work."
                : "Coding host is not currently ready to accept work.");
        return [code, branchCheck, toolchain, host];
    }
}
