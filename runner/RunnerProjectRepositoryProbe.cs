using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Reports real origin read and write evidence from the execution host.</summary>
public sealed class RunnerProjectRepositoryProbe(
    RunnerOptions options, TaskServerClient client, IRepositoryGitProof git, Action<string> log)
{
    // A review host holds read tokens, so its write proof fails by design. It must
    // not fetch each private repository again every minute because of that.
    private static readonly TimeSpan ReadOnlyHostProbeInterval = TimeSpan.FromMinutes(30);
    private readonly Dictionary<string, DateTime> _lastReadOnlyProbe = new(StringComparer.Ordinal);

    public async Task PollAsync(CancellationToken shutdown)
    {
        while (!shutdown.IsCancellationRequested)
        {
            try { await RunOnceAsync(shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception exception)
            {
                // Git and HTTP failures can contain credential-bearing remote text.
                log($"repository origin probe deferred type={exception.GetType().Name}");
            }
            try { await Task.Delay(TimeSpan.FromSeconds(60), shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var project in await client.ListProjectsAsync(ct))
        {
            try { await ProbeProjectAsync(project.ProjectId, ct); }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception exception)
            {
                // Git and HTTP failures can contain credential-bearing remote text.
                log($"repository origin probe deferred projectId={project.ProjectId} type={exception.GetType().Name}");
            }
        }
    }

    private async Task ProbeProjectAsync(string projectId, CancellationToken ct)
    {
        var status = await client.GetProjectRepositoryAsync(projectId, ct);
        if (status is null || string.IsNullOrWhiteSpace(status.Registration.RepositoryUrl)) return;
        if (status.Probes.Any(probe => probe.RunnerId == options.RunnerId && probe.Admitted
            && TimeProvider.System.GetUtcNow().UtcDateTime - probe.ObservedAt < TimeSpan.FromMinutes(5)))
            return;
        if (options.Role == "review")
        {
            var now = TimeProvider.System.GetUtcNow().UtcDateTime;
            if (_lastReadOnlyProbe.TryGetValue(projectId, out var last) && now - last < ReadOnlyHostProbeInterval)
                return;
            _lastReadOnlyProbe[projectId] = now;
        }
        var origin = status.Registration.RepositoryUrl;
        var keyPath = RunnerFirstRunGitCredentials.Key(options.StateDir, origin);
        var helperPath = keyPath is null
            ? RunnerFirstRunGitCredentials.Helper(options.StateDir, origin) : null;
        IRepositoryGitProof? firstRunGit = keyPath is null && helperPath is null ? null
            : new PlatformRepositoryGitProof(_ => keyPath, options.StateDir,
                credentialHelperForGeneration: _ => helperPath);
        RepositoryAccessProof proof;
        try
        {
            using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
            deadline.CancelAfter(TimeSpan.FromSeconds(45));
            proof = await (firstRunGit ?? git).ProveAsync(
                new RepositoryAccessBinding("workspace", origin, RequiresPush: true),
                firstRunGit is null ? null : "first-run",
                deadline.Token);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception exception)
        {
            log($"repository origin probe deferred projectId={projectId} type={exception.GetType().Name}");
            proof = new RepositoryAccessProof("workspace", origin, false, false, true,
                "probe-failed");
        }
        await client.ReportProjectRepositoryProbeAsync(options.RunnerId, projectId,
            new ProjectRepositoryProbeRequest(origin, origin,
                proof.FetchVerified, proof.PushVerified, UsedFallbackRemote: false,
                Detail: proof.Status), ct);
    }
}
