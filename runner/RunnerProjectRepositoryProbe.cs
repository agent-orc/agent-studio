using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Reports real origin read and write evidence from the execution host.</summary>
public sealed class RunnerProjectRepositoryProbe(
    RunnerOptions options, TaskServerClient client, IRepositoryGitProof git, Action<string> log)
{
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
            var status = await client.GetProjectRepositoryAsync(project.ProjectId, ct);
            if (status is null) continue;
            if (status.Probes.Any(probe => probe.RunnerId == options.RunnerId && probe.Admitted
                && TimeProvider.System.GetUtcNow().UtcDateTime - probe.ObservedAt < TimeSpan.FromMinutes(5)))
                continue;
            var origin = status.Registration.RepositoryUrl;
            RepositoryAccessProof proof;
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(45));
                proof = await git.ProveAsync(
                    new RepositoryAccessBinding("workspace", origin, RequiresPush: true), null,
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
