using AgentRunner.RunTargets;

namespace AgentRunner.Tests;

/// <summary>In-memory container runtime for the run-target reconciler tests.</summary>
internal sealed class FakeRunTargetDocker : IRunTargetDocker
{
    public sealed record ComposeUp(string Project, string ComposeText);

    public List<string> Calls { get; } = [];
    public List<ComposeUp> Ups { get; } = [];
    public Dictionary<string, RunTargetContainerStatus> Containers { get; } = new(StringComparer.Ordinal);
    public HashSet<string> UnhealthyProjects { get; } = new(StringComparer.Ordinal);
    public bool EdgeFails { get; set; }
    public bool DownFails { get; set; }

    /// <summary>Compose projects whose <c>down</c> fails while the others succeed.</summary>
    public HashSet<string> DownFailsFor { get; } = new(StringComparer.Ordinal);
    public string? FailUpWith { get; set; }
    public int Reloads { get; private set; }

    public Task<RunTargetDockerResult> PullAsync(string image, CancellationToken ct)
    {
        Calls.Add("pull " + image);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> EnsureNetworkAsync(string network, CancellationToken ct)
    {
        Calls.Add("network " + network);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> ConnectAsync(string network, string container, CancellationToken ct)
    {
        Calls.Add($"connect {network} {container}");
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> RemoveNetworkAsync(string network, CancellationToken ct)
    {
        Calls.Add("network-rm " + network);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> ComposeUpAsync(string project, string composeFile, CancellationToken ct)
    {
        Calls.Add("up " + project);
        Ups.Add(new(project, File.ReadAllText(composeFile)));
        if (project == RunTargetNames.EdgeContainer && EdgeFails)
            return Task.FromResult(new RunTargetDockerResult(false, "bind: address already in use"));
        if (FailUpWith is not null && project != RunTargetNames.EdgeContainer)
            return Task.FromResult(new RunTargetDockerResult(false, FailUpWith));
        var restarts = Containers.TryGetValue(project, out var existing) ? existing.RestartCount : 0;
        Containers[project] = new(RunTargetContainerState.Running, restarts);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> ComposeStopAsync(string project, string composeFile, CancellationToken ct)
    {
        Calls.Add("stop " + project);
        Containers[project] = new(RunTargetContainerState.Exited, 0);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> ComposeDownAsync(string project, string composeFile, CancellationToken ct)
    {
        Calls.Add("down " + project);
        if (DownFails || DownFailsFor.Contains(project)) return Task.FromResult(new RunTargetDockerResult(false, "daemon not responding"));
        Containers.Remove(project);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetContainerStatus> InspectAsync(string container, CancellationToken ct)
        => Task.FromResult(Containers.TryGetValue(container, out var status)
            ? status : new RunTargetContainerStatus(RunTargetContainerState.Missing, 0));

    public Task<bool> ProbeAsync(string url, CancellationToken ct)
    {
        Calls.Add("probe " + url);
        var host = new Uri(url).Host;
        return Task.FromResult(Containers.TryGetValue(host, out var status)
            && status.State == RunTargetContainerState.Running && !UnhealthyProjects.Contains(host));
    }

    public Task<RunTargetDockerResult> ReloadEdgeAsync(CancellationToken ct)
    {
        Calls.Add("reload");
        Reloads++;
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    /// <summary>The container exits after Docker's own <c>on-failure:3</c> restarts.</summary>
    public void Crash(string project)
        => Containers[project] = new(RunTargetContainerState.Exited,
            (Containers.TryGetValue(project, out var status) ? status.RestartCount : 0) + 3);
}

/// <summary>Secret values keyed by <c>project/secretRef</c>, like the per-project host secret roots.</summary>
internal sealed class FakeRunTargetSecrets(Dictionary<string, string> values) : IRunTargetSecretSource
{
    public Task<string?> ReadAsync(string project, string secretRef, CancellationToken ct)
        => Task.FromResult(values.TryGetValue(project + "/" + secretRef, out var value) ? value : null);
}
