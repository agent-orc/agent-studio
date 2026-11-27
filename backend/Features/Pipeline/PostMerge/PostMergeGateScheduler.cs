using AgentStudio.Runner;
using AgentStudio.Registry;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Queues T3 on the same durable authority that gate workers claim.</summary>
public sealed class PostMergeGateScheduler(IBatchGateSubjectAuthority gates, PostMergeRunStore store,
    ProjectRegistry projects, ProjectSettingsService settings, TimeProvider clock, GateCapacitySettings? capacity = null)
{
    private readonly SemaphoreSlim _queue = new(1);

    public async Task<PostMergePlacement> PlaceAsync(ProjectRecord project, string tip,
        PostMergeGateWork work, CancellationToken ct)
    {
        await _queue.WaitAsync(ct);
        try
        {
            var hosts = await gates.PostMergeHostsAsync(ct);
            var state = store.Read(project.Id);
            if (state.GateSubjectId is { } existing)
            {
                var status = await gates.GetAsync(existing, ct);
                if (status is not null && !GateStates.IsTerminal(status.Phase))
                {
                    if (hosts.Gate && status.Subject.ExpectedSha == tip || !MonolithTransitionAdapter.PostMergeMaySupersede(status.Phase))
                        return new("gate", status.Phase == GateStates.Queued ? "waiting for gate capacity"
                            : $"gate {status.Phase} on {status.ActiveHost ?? "an assigned host"}", existing);
                    if (!await gates.CancelBeforeTestsAsync(existing, ct))
                        return new("gate", "post-merge tests already started", existing);
                }
                store.EndGateRun(project.Id, existing, clock.GetUtcNow(), "gate attempt ended before a completed measurement");
                store.SetGateSubject(project.Id, null);
            }
            if (!hosts.Gate) return new("run", "no connected gate host");
            var repository = RemoteProjectRepositoryResolver.Resolve(project, work.Branch);
            if (repository is null)
            {
                store.CloseWindow(project.Id, "no repository configured", clock.GetUtcNow());
                return new("closed", "no repository configured");
            }
            work = work with { GateSlots = capacity?.Current().Slots ?? work.GateSlots };
            var command = new GateCommand("post-merge", "post-merge", [], 21600);
            var plan = new GatePlan("post-build-test-gate", 1, [command], "", 21600,
                [GateCapabilities.PostMerge, CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess], 32768, "always", PostMerge: work);
            var hash = GateRules.PlanHash(plan);
            var source = $"post-merge:{project.Id}:{work.Kind}:{tip}:{Guid.NewGuid():N}";
            var request = new CreateGateSubjectRequest(project.Id, source, repository.RepositoryId,
                repository.RepositoryUrl, tip, tip, null, null, hash, hash, "1", hash,
                plan, clock.GetUtcNow().UtcDateTime.AddHours(24), MaxAttempts: 2, Kind: GateSubjectKinds.PostMerge);
            var queued = await gates.CreateAsync(request, ct);
            store.SetGateSubject(project.Id, queued.Subject.SubjectId);
            return new("gate", "waiting for gate capacity", queued.Subject.SubjectId);
        }
        finally { _queue.Release(); }
    }

    /// <summary>Closes abandoned obligations even when their discovery host no longer polls.</summary>
    public async Task MaintainAsync(CancellationToken ct)
    {
        var hosts = await gates.PostMergeHostsAsync(ct);
        foreach (var project in projects.List().Where(project => !project.Archived))
        {
            var state = store.Read(project.Id);
            if (state.GateSubjectId is { } subjectId)
            {
                var status = await gates.GetAsync(subjectId, ct);
                if (status is null || GateStates.IsTerminal(status.Phase))
                {
                    store.EndGateRun(project.Id, subjectId, clock.GetUtcNow(), "gate ended without a completed measurement");
                    store.SetGateSubject(project.Id, null);
                    state = store.Read(project.Id);
                }
            }
            if (state.PendingWindow is null) continue;
            var reason = !hosts.Gate && !hosts.Coding ? "no connected execution host"
                : RemoteProjectRepositoryResolver.ResolveRepositoryUrl(project) is null ? "no repository configured"
                : !TestingStrategies.ExpectsPostMergeRuns(TestingStrategies.Resolve(settings.Get(project.DisplayName)))
                    ? "project no longer uses post-merge verification" : null;
            if (reason is not null)
            {
                if (state.GateSubjectId is { } id && !await gates.CancelBeforeTestsAsync(id, ct)) continue;
                store.CloseWindow(project.Id, reason, clock.GetUtcNow());
            }
            else if (hosts.Gate && MonolithTransitionAdapter.PostMergeDeadlineReached(state.PendingWindow.OpenedAtUtc,
                state.PendingWindow.WindowMinutes, clock.GetUtcNow()))
            {
                var branch = settings.Get(project.DisplayName).IntegrationBranch;
                await PlaceAsync(project, state.PendingWindow.TipSha,
                    new(project.DisplayName, string.IsNullOrWhiteSpace(branch) ? "develop" : branch, "window"), ct);
            }
        }
    }
}
