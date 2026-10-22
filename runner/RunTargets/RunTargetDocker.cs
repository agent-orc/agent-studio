namespace AgentRunner.RunTargets;

public enum RunTargetContainerState { Missing, Running, Restarting, Exited }

public sealed record RunTargetContainerStatus(RunTargetContainerState State, int RestartCount);

/// <summary>Outcome of one docker command; <see cref="Detail"/> is unmasked tool output.</summary>
public sealed record RunTargetDockerResult(bool Ok, string Detail)
{
    public static readonly RunTargetDockerResult Success = new(true, "");
}

/// <summary>
/// The container runtime as the run-target role uses it. The production
/// implementation shells out to the docker CLI; tests use an in-memory fake.
/// </summary>
public interface IRunTargetDocker
{
    Task<RunTargetDockerResult> PullAsync(string image, CancellationToken ct);
    Task<RunTargetDockerResult> EnsureNetworkAsync(string network, CancellationToken ct);
    Task<RunTargetDockerResult> ConnectAsync(string network, string container, CancellationToken ct);

    /// <summary>Disconnects whatever is still attached (the edge) and removes the network; a missing network is a success.</summary>
    Task<RunTargetDockerResult> RemoveNetworkAsync(string network, CancellationToken ct);
    Task<RunTargetDockerResult> ComposeUpAsync(string project, string composeFile, CancellationToken ct);
    Task<RunTargetDockerResult> ComposeStopAsync(string project, string composeFile, CancellationToken ct);
    Task<RunTargetDockerResult> ComposeDownAsync(string project, string composeFile, CancellationToken ct);
    Task<RunTargetContainerStatus> InspectAsync(string container, CancellationToken ct);

    /// <summary>HTTP GET from inside the edge container, the same path public traffic takes.</summary>
    Task<bool> ProbeAsync(string url, CancellationToken ct);
    Task<RunTargetDockerResult> ReloadEdgeAsync(CancellationToken ct);
}

/// <summary>Docker CLI implementation. Every process goes through <see cref="ProcessRunner"/>.</summary>
public sealed class DockerCliRunTarget(string docker = "docker") : IRunTargetDocker
{
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromMinutes(10);

    public Task<RunTargetDockerResult> PullAsync(string image, CancellationToken ct)
        => Run(["pull", "--quiet", image], ct);

    public async Task<RunTargetDockerResult> EnsureNetworkAsync(string network, CancellationToken ct)
    {
        var existing = await RunWithOutput(["network", "inspect", network, "--format", "{{.Internal}}"], ct);
        if (existing.Ok)
            return existing.Detail.Trim().Equals("true", StringComparison.OrdinalIgnoreCase)
                ? RunTargetDockerResult.Success
                : new(false, $"network {network} is not internal; refusing to attach an app");
        return await Run(InternalNetworkCreateArguments(network), ct);
    }

    internal static string[] InternalNetworkCreateArguments(string network) =>
        ["network", "create", "--driver", "bridge", "--internal",
            "--label", "agent-studio.run-target=network", network];

    public async Task<RunTargetDockerResult> ConnectAsync(string network, string container, CancellationToken ct)
    {
        var members = await RunWithOutput(
            ["network", "inspect", network, "--format", "{{range .Containers}}{{.Name}} {{end}}"], ct);
        if (members.Ok && members.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(container, StringComparer.Ordinal))
            return RunTargetDockerResult.Success;
        return await Run(["network", "connect", network, container], ct);
    }

    public async Task<RunTargetDockerResult> RemoveNetworkAsync(string network, CancellationToken ct)
    {
        var members = await RunWithOutput(
            ["network", "inspect", network, "--format", "{{range .Containers}}{{.Name}} {{end}}"], ct);
        if (!members.Ok)
        {
            // Inspect also fails when Docker is unavailable or access is denied.
            // Confirm absence with a successful list before acknowledging cleanup.
            var listed = await RunWithOutput(["network", "ls", "--format", "{{.Name}}"], ct);
            if (!listed.Ok) return listed;
            return listed.Detail.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .Contains(network, StringComparer.Ordinal)
                ? members : RunTargetDockerResult.Success;
        }
        foreach (var container in members.Detail.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            var disconnected = await Run(["network", "disconnect", "--force", network, container], ct);
            if (!disconnected.Ok) return disconnected;
        }
        return await Run(["network", "rm", network], ct);
    }

    public Task<RunTargetDockerResult> ComposeUpAsync(string project, string composeFile, CancellationToken ct)
        => Run(["compose", "--project-name", project, "--file", composeFile, "up", "--detach", "--remove-orphans"], ct);

    public Task<RunTargetDockerResult> ComposeStopAsync(string project, string composeFile, CancellationToken ct)
        => Run(["compose", "--project-name", project, "--file", composeFile, "stop"], ct);

    public Task<RunTargetDockerResult> ComposeDownAsync(string project, string composeFile, CancellationToken ct)
        => Run(["compose", "--project-name", project, "--file", composeFile, "down", "--remove-orphans"], ct);

    public async Task<RunTargetContainerStatus> InspectAsync(string container, CancellationToken ct)
    {
        var result = await RunWithOutput(
            ["container", "inspect", container, "--format", "{{.State.Status}} {{.RestartCount}}"], ct);
        if (!result.Ok) return new(RunTargetContainerState.Missing, 0);
        var parts = result.Detail.Trim().Split(' ');
        var restarts = parts.Length > 1 && int.TryParse(parts[1], out var count) ? count : 0;
        return new(parts[0] switch
        {
            "running" => RunTargetContainerState.Running,
            "restarting" => RunTargetContainerState.Restarting,
            _ => RunTargetContainerState.Exited,
        }, restarts);
    }

    public async Task<bool> ProbeAsync(string url, CancellationToken ct)
        => (await Run(["exec", RunTargetNames.EdgeContainer, "wget", "-q", "-T", "5", "-O", "/dev/null", url], ct)).Ok;

    public Task<RunTargetDockerResult> ReloadEdgeAsync(CancellationToken ct)
        => Run(["exec", "-w", "/etc/caddy", RunTargetNames.EdgeContainer,
            "caddy", "reload", "--config", "/etc/caddy/Caddyfile", "--adapter", "caddyfile"], ct);

    private async Task<RunTargetDockerResult> Run(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var result = await RunWithOutput(arguments, ct);
        return result.Ok ? RunTargetDockerResult.Success : result;
    }

    private async Task<RunTargetDockerResult> RunWithOutput(IReadOnlyList<string> arguments, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(CommandTimeout);
        var result = await ProcessRunner.RunAsync(docker, arguments, ct: deadline.Token);
        return result.Success
            ? new(true, result.StdOut)
            : new(false, $"docker {arguments[0]} exited {result.ExitCode}: {Tail(result.StdErr)}");
    }

    private static string Tail(string text)
    {
        text = text.Trim();
        return text.Length <= 400 ? text : text[^400..];
    }
}
