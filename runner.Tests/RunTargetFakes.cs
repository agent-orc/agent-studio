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
    public bool NetworkRemoveFails { get; set; }

    /// <summary>Compose projects whose <c>down</c> fails while the others succeed.</summary>
    public HashSet<string> DownFailsFor { get; } = new(StringComparer.Ordinal);
    public HashSet<string> StopFailsFor { get; } = new(StringComparer.Ordinal);
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
        return Task.FromResult(NetworkRemoveFails
            ? new RunTargetDockerResult(false, "network is still in use")
            : RunTargetDockerResult.Success);
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
        if (StopFailsFor.Contains(project)) return Task.FromResult(new RunTargetDockerResult(false, "daemon not responding"));
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

    /// <summary>Named volumes that exist; a deleted volume takes its data with it.</summary>
    public HashSet<string> Volumes { get; } = new(StringComparer.Ordinal);

    /// <summary>Database containers whose <c>pg_isready</c> fails.</summary>
    public HashSet<string> NotReady { get; } = new(StringComparer.Ordinal);

    public Task<RunTargetDockerResult> EnsureVolumeAsync(string volume, CancellationToken ct)
    {
        Calls.Add("volume " + volume);
        Volumes.Add(volume);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> RemoveVolumeAsync(string volume, CancellationToken ct)
    {
        Calls.Add("volume-rm " + volume);
        Volumes.Remove(volume);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    /// <summary>Every command run in a container, with its arguments joined by spaces.</summary>
    public List<string> Execs { get; } = [];

    /// <summary>A command this returns true for fails like a broken tool would.</summary>
    public Func<string, IReadOnlyList<string>, bool>? FailExec { get; set; }

    /// <summary>Dump files as the database containers see them under <c>/backups</c>; the host directory behind them is shared per environment.</summary>
    public HashSet<string> BackupFiles { get; } = new(StringComparer.Ordinal);

    /// <summary>Scratch containers started for verification, with the host directory each one mounted.</summary>
    public List<(string Container, string Image, string HostDirectory, string MountPath)> Scratches { get; } = [];
    public int TablesInScratch { get; set; } = 12;

    public Task<RunTargetDockerResult> ExecAsync(string container, IReadOnlyList<string> command, CancellationToken ct)
    {
        Calls.Add($"exec {container} {command[0]}");
        Execs.Add($"{container} {string.Join(' ', command)}");
        if (!Containers.TryGetValue(container, out var status) || status.State != RunTargetContainerState.Running)
            return Task.FromResult(new RunTargetDockerResult(false, "container is not running"));
        if (FailExec?.Invoke(container, command) == true)
            return Task.FromResult(new RunTargetDockerResult(false, $"{command[0]}: error: simulated failure"));
        RunTargetDockerResult Ok(string output = "") => new(true, output);
        RunTargetDockerResult Missing(string path) => new(false, $"{command[0]}: {path}: No such file or directory");
        return Task.FromResult(command[0] switch
        {
            "pg_isready" => NotReady.Contains(container)
                ? new RunTargetDockerResult(false, "no response") : RunTargetDockerResult.Success,
            "du" => new RunTargetDockerResult(true, "2048\t" + command[^1] + "\n"),
            "pg_dump" => Touch(command[^1], add: true),
            "pg_restore" => BackupFiles.Contains(command[^1]) ? Ok("; Archive created\n") : Missing(command[^1]),
            "mv" => BackupFiles.Remove(command[1]) && BackupFiles.Add(command[2]) ? Ok() : Missing(command[1]),
            "stat" => BackupFiles.Contains(command[^1]) ? Ok("4096\n") : Missing(command[^1]),
            "rm" => Touch(command[^1], add: false),
            "psql" => Ok(string.Join(' ', command).Contains("max_connections", StringComparison.Ordinal) ? "3|100\n"
                : string.Join(' ', command).Contains("pg_class", StringComparison.Ordinal) ? TablesInScratch + "\n" : ""),
            _ => new RunTargetDockerResult(false, "unexpected command"),
        });
    }

    private RunTargetDockerResult Touch(string path, bool add)
    {
        if (add) BackupFiles.Add(path);
        else BackupFiles.Remove(path);
        return RunTargetDockerResult.Success;
    }

    public Task<RunTargetDockerResult> StartScratchAsync(string container, string image, string hostDirectory, string mountPath,
        CancellationToken ct)
    {
        Calls.Add("scratch " + container);
        Scratches.Add((container, image, hostDirectory, mountPath));
        Containers[container] = new(RunTargetContainerState.Running, 0);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> RemoveContainerAsync(string container, CancellationToken ct)
    {
        Calls.Add("rm " + container);
        Containers.Remove(container);
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    public Task<RunTargetDockerResult> RunOnceAsync(string image, string hostDirectory, IReadOnlyList<string> command,
        CancellationToken ct)
    {
        Calls.Add($"run-once {image} {hostDirectory} {string.Join(' ', command)}");
        if (command is ["rm", "-rf", var directory]) BackupFiles.RemoveWhere(item => item.StartsWith(directory + "/", StringComparison.Ordinal));
        return Task.FromResult(RunTargetDockerResult.Success);
    }

    /// <summary>The container exits after Docker's own <c>on-failure:3</c> restarts.</summary>
    public void Crash(string project)
        => Containers[project] = new(RunTargetContainerState.Exited,
            (Containers.TryGetValue(project, out var status) ? status.RestartCount : 0) + 3);
}

/// <summary>Credentials Studio issues, keyed by <c>project/environment/reference</c>; <see cref="Reads"/> records each request.</summary>
internal sealed class FakeRunTargetCredentials(Dictionary<string, string> values) : IRunTargetCredentialSource
{
    public List<string> Reads { get; } = [];
    public bool Unreachable { get; set; }

    public Task<string?> ReadAsync(string project, string environmentId, string reference, CancellationToken ct)
    {
        Reads.Add(reference);
        if (Unreachable) throw new HttpRequestException("connection refused");
        return Task.FromResult(values.TryGetValue($"{project}/{environmentId}/{reference}", out var value) ? value : null);
    }
}

/// <summary>Secret values keyed by <c>project/secretRef</c>, like the per-project host secret roots.</summary>
internal sealed class FakeRunTargetSecrets(Dictionary<string, string> values) : IRunTargetSecretSource
{
    public Task<string?> ReadAsync(string project, string secretRef, CancellationToken ct)
        => Task.FromResult(values.TryGetValue(project + "/" + secretRef, out var value) ? value : null);
}
