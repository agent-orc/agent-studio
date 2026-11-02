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

    internal static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(60);

    public async Task PollAsync(CancellationToken shutdown)
    {
        // A monolith maps neither the project repository status nor the probe report.
        var absence = new FeatureRouteAbsence("repository origin probe", log);
        while (!shutdown.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(shutdown);
                absence.RecordAnswered();
            }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
            catch (Exception exception) when (absence.TryRecordAbsent(exception)) { }
            catch (Exception exception)
            {
                // Git and HTTP failures can contain credential-bearing remote text.
                log($"repository origin probe deferred type={exception.GetType().Name}");
            }
            try { await Task.Delay(absence.NextDelay(PollInterval), shutdown); }
            catch (OperationCanceledException) when (shutdown.IsCancellationRequested) { break; }
        }
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        foreach (var project in await client.ListProjectsAsync(ct))
        {
            var status = await client.GetProjectRepositoryAsync(project.ProjectId, ct);
            if (status is null) continue;
            if (status.Probes.Any(probe => probe.RunnerId == options.RunnerId && probe.Admitted
                && TimeProvider.System.GetUtcNow().UtcDateTime - probe.ObservedAt < TimeSpan.FromMinutes(5)))
                continue;
            if (options.Role == "review")
            {
                var now = TimeProvider.System.GetUtcNow().UtcDateTime;
                if (_lastReadOnlyProbe.TryGetValue(project.ProjectId, out var last) && now - last < ReadOnlyHostProbeInterval)
                    continue;
                _lastReadOnlyProbe[project.ProjectId] = now;
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
            catch (Exception)
            {
                proof = new RepositoryAccessProof("workspace", origin, false, false, true,
                    "probe-failed");
            }
            await client.ReportProjectRepositoryProbeAsync(options.RunnerId, project.ProjectId,
                new ProjectRepositoryProbeRequest(origin, origin,
                    proof.FetchVerified, proof.PushVerified, UsedFallbackRemote: false,
                    Detail: proof.Status), ct);
        }
    }
}
