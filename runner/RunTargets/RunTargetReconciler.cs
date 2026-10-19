namespace AgentRunner.RunTargets;

public sealed record RunTargetReconcilerSettings(
    string EdgeImage,
    RunTargetEdgeSettings Edge,
    string? CertificateDirectory,
    int HttpPort = 80,
    int HttpsPort = 443,
    int HealthAttempts = 20,
    double HealthIntervalSeconds = 3);

public sealed record RunTargetCycleResult(
    IReadOnlyList<RunTargetObservation> Observations,
    IReadOnlyList<RunTargetJobReport> JobReports);

/// <summary>
/// Converges the host to Studio's desired state. A new revision is pulled,
/// started next to the running one, and health-checked through the edge; only a
/// healthy candidate receives traffic. The replaced revision is stopped but
/// kept, so a rollback restarts it instead of pulling again. A failed health
/// check leaves the running revision untouched. A crashed app is restarted with
/// <see cref="RunTargetCrashPolicy"/> backoff.
/// </summary>
public sealed class RunTargetReconciler(
    IRunTargetDocker docker,
    IRunTargetSecretSource secrets,
    RunTargetStateStore store,
    RunTargetReconcilerSettings settings,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    private const int MaxEvents = 20;
    private static readonly IReadOnlyDictionary<string, string> NoEnvironment = new Dictionary<string, string>();

    public async Task<RunTargetCycleResult> ReconcileAsync(RunTargetDesiredState desired, CancellationToken ct)
    {
        var state = store.Load();
        var reports = new List<RunTargetJobReport>();
        var specs = new Dictionary<string, RunTargetEnvironmentSpec>(StringComparer.Ordinal);
        foreach (var spec in desired.Environments
                     .OrderBy(item => item.Project, StringComparer.Ordinal)
                     .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal))
            specs.TryAdd(RunTargetNames.AppKey(spec.Project, spec.EnvironmentId), spec);

        if (specs.Count > 0 || state.Apps.Count > 0)
        {
            var edgeReady = await EnsureEdgeAsync(state, specs, ct);
            foreach (var (key, spec) in specs)
            {
                if (!state.Apps.TryGetValue(key, out var app))
                    state.Apps[key] = app = new RunTargetAppState { Project = spec.Project, EnvironmentId = spec.EnvironmentId };
                // Without an edge no candidate can be checked or routed. The
                // running apps keep serving; open jobs stay claimed and retry.
                if (edgeReady) await ConvergeAsync(key, spec, app, state, specs, reports, ct);
                store.Save(state);
            }
            foreach (var key in state.Apps.Keys.Where(key => !specs.ContainsKey(key)).ToArray())
            {
                var app = state.Apps[key];
                await RemoveSlotAsync(app.Current, ct);
                await RemoveSlotAsync(app.Previous, ct);
                state.Apps.Remove(key);
                log($"run-target: removed {app.Project}/{app.EnvironmentId}; it is no longer placed on this host");
            }
            if (edgeReady) await PublishRoutesAsync(state, specs, ct);
            store.Save(state);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var observations = state.Apps.Values
            .Where(app => specs.ContainsKey(RunTargetNames.AppKey(app.Project, app.EnvironmentId)))
            .Select(app => new RunTargetObservation(app.Project, app.EnvironmentId, app.Status,
                app.Current?.Artifact, app.Previous?.Artifact, app.DockerRestarts + app.HostRestarts, now,
                app.Events.ToArray()))
            .ToArray();
        return new(observations, reports);
    }

    private async Task ConvergeAsync(string key, RunTargetEnvironmentSpec spec, RunTargetAppState app,
        RunTargetHostState state, IReadOnlyDictionary<string, RunTargetEnvironmentSpec> specs,
        List<RunTargetJobReport> reports, CancellationToken ct)
    {
        var name = $"{spec.Project}/{spec.EnvironmentId}";
        if (spec.Artifact is null)
        {
            if (app.Current is null && app.Previous is null) return;
            await RemoveSlotAsync(app.Current, ct);
            await RemoveSlotAsync(app.Previous, ct);
            app.Current = app.Previous = null;
            app.Status = "stopped";
            AddEvent(app, "stopped", null);
            return;
        }
        if (RunTargetNames.Invalid(spec) is { } invalid)
        {
            log($"run-target: {name} refused: {invalid}");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        var network = RunTargetNames.AppNetwork(key);
        var attach = await docker.EnsureNetworkAsync(network, ct);
        if (attach.Ok) attach = await docker.ConnectAsync(network, RunTargetNames.EdgeContainer, ct);
        if (!attach.Ok)
        {
            log($"run-target: {name} network unavailable: {attach.Detail}");
            return;
        }

        var revision = RunTargetNames.Revision(spec);
        if (app.Current?.Revision == revision)
        {
            await SuperviseAsync(spec, app, ct);
            if (spec.JobId is not null)
            {
                var healthy = app.Status == "running" && await WaitHealthyAsync(app.Current, attempts: 3, ct);
                Report(spec, reports, healthy, healthy ? RunTargetEvidence.HealthOk : RunTargetEvidence.HealthFailed);
            }
            return;
        }

        var restoring = app.Previous?.Revision == revision;
        var composeProject = key + "-" + revision;
        var candidate = restoring ? app.Previous! : new RunTargetSlot(revision, spec.Artifact, composeProject,
            Path.Combine(store.AppsDirectory, composeProject, "compose.yaml"), spec.ContainerPort, spec.HealthPath);
        var resolved = await RunTargetResolvedSecrets.ResolveAsync(secrets, spec.SecretRefs, ct);
        if (resolved.Missing.Count > 0)
        {
            log($"run-target: {name} is missing secret references on this host: {string.Join(", ", resolved.Missing)}");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        var pulled = await docker.PullAsync(spec.Artifact, ct);
        if (!pulled.Ok)
        {
            log($"run-target: {name} pull failed: {resolved.Mask(pulled.Detail)}");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, "pulled", spec.Artifact);
        store.WriteIfChanged(candidate.ComposeFile, RunTargetComposeFile.App(spec, key, revision));
        var started = await docker.ComposeUpAsync(candidate.ComposeProject, candidate.ComposeFile, resolved.Environment, ct);
        if (!started.Ok)
        {
            log($"run-target: {name} start failed: {resolved.Mask(started.Detail)}");
            await DiscardCandidateAsync(candidate, restoring, ct);
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, "started", spec.Artifact);
        if (!await WaitHealthyAsync(candidate, settings.HealthAttempts, ct))
        {
            log($"run-target: {name} candidate {revision} failed its health check; the running revision keeps serving");
            await DiscardCandidateAsync(candidate, restoring, ct);
            Fail(app, spec, reports, RunTargetEvidence.HealthFailed, "health-failed");
            return;
        }
        AddEvent(app, "health-ok", spec.Artifact);

        var replaced = app.Current;
        app.Current = candidate;
        if (!await PublishRoutesAsync(state, specs, ct))
        {
            app.Current = replaced;
            await DiscardCandidateAsync(candidate, restoring, ct);
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, restoring || spec.IsRollback ? "rolled-back" : "swapped", spec.Artifact);
        if (replaced is not null)
        {
            var stopped = await docker.ComposeStopAsync(replaced.ComposeProject, replaced.ComposeFile, ct);
            if (!stopped.Ok) log($"run-target: {name} could not stop replaced revision {replaced.Revision}: {stopped.Detail}");
        }
        if (app.Previous is { } older && older.Revision != candidate.Revision && older.Revision != replaced?.Revision)
            await RemoveSlotAsync(older, ct);
        app.Previous = replaced;
        app.Status = "running";
        app.CrashAttempts = 0;
        app.LastRestartAt = app.NextRestartAt = null;
        log($"run-target: {name} now runs {spec.Artifact} (revision {revision})");
        Report(spec, reports, true, RunTargetEvidence.HealthOk);
    }

    private async Task SuperviseAsync(RunTargetEnvironmentSpec spec, RunTargetAppState app, CancellationToken ct)
    {
        var slot = app.Current!;
        var now = clock.GetUtcNow().UtcDateTime;
        var status = await docker.InspectAsync(slot.ComposeProject, ct);
        app.DockerRestarts = status.RestartCount;
        if (status.State == RunTargetContainerState.Running)
        {
            if (app.CrashAttempts > 0 && RunTargetCrashPolicy.IsStable(now, app.LastRestartAt))
            {
                app.CrashAttempts = 0;
                app.NextRestartAt = null;
            }
            app.Status = "running";
            return;
        }
        if (status.State == RunTargetContainerState.Restarting)
        {
            app.Status = "starting";
            return;
        }
        if (!RunTargetCrashPolicy.RestartDue(now, app.NextRestartAt))
        {
            app.Status = RunTargetCrashPolicy.IsCrashLoop(app.CrashAttempts) ? "crash-loop" : "unhealthy";
            return;
        }
        var resolved = await RunTargetResolvedSecrets.ResolveAsync(secrets, spec.SecretRefs, ct);
        if (resolved.Missing.Count > 0)
        {
            log($"run-target: {spec.Project}/{spec.EnvironmentId} cannot restart, missing secret references: {string.Join(", ", resolved.Missing)}");
            app.Status = "failed";
            AddEvent(app, "deploy-step-failed", slot.Artifact);
            return;
        }
        var restarted = await docker.ComposeUpAsync(slot.ComposeProject, slot.ComposeFile, resolved.Environment, ct);
        app.CrashAttempts++;
        app.HostRestarts++;
        app.LastRestartAt = now;
        app.NextRestartAt = now + RunTargetCrashPolicy.Backoff(app.CrashAttempts);
        if (!restarted.Ok)
            log($"run-target: {spec.Project}/{spec.EnvironmentId} restart failed: {resolved.Mask(restarted.Detail)}");
        AddEvent(app, "crash-restart", slot.Artifact);
        if (RunTargetCrashPolicy.IsCrashLoop(app.CrashAttempts))
        {
            if (app.Status != "crash-loop")
            {
                AddEvent(app, "crash-loop", slot.Artifact);
                log($"run-target: {spec.Project}/{spec.EnvironmentId} is crash-looping; next restart at {app.NextRestartAt:O}");
            }
            app.Status = "crash-loop";
        }
        else app.Status = "starting";
    }

    private async Task<bool> EnsureEdgeAsync(RunTargetHostState state,
        IReadOnlyDictionary<string, RunTargetEnvironmentSpec> specs, CancellationToken ct)
    {
        // Caddy must find a Caddyfile when it first starts.
        if (store.Read(store.CaddyfilePath) is null)
            store.WriteIfChanged(store.CaddyfilePath, RenderRoutes(state, specs).Text);
        var composeChanged = store.WriteIfChanged(store.EdgeComposePath, RunTargetComposeFile.Edge(
            settings.EdgeImage, store.CaddyDirectory, settings.CertificateDirectory, settings.HttpPort, settings.HttpsPort));
        var status = await docker.InspectAsync(RunTargetNames.EdgeContainer, ct);
        if (!composeChanged && status.State == RunTargetContainerState.Running) return true;
        var started = await docker.ComposeUpAsync(RunTargetNames.EdgeContainer, store.EdgeComposePath, NoEnvironment, ct);
        if (!started.Ok) log($"run-target: edge unavailable: {started.Detail}");
        return started.Ok;
    }

    /// <summary>Renders the routes of every current revision and reloads Caddy when they changed.</summary>
    private async Task<bool> PublishRoutesAsync(RunTargetHostState state,
        IReadOnlyDictionary<string, RunTargetEnvironmentSpec> specs, CancellationToken ct)
    {
        var rendered = RenderRoutes(state, specs);
        foreach (var key in rendered.ConflictingAppKeys)
        {
            var app = state.Apps[key];
            if (app.Events.LastOrDefault()?.Kind != "domain-conflict")
            {
                AddEvent(app, "domain-conflict", app.Current?.Artifact);
                log($"run-target: {app.Project}/{app.EnvironmentId} declares a domain another environment already routes; not routed");
            }
        }
        var before = store.Read(store.CaddyfilePath);
        if (!store.WriteIfChanged(store.CaddyfilePath, rendered.Text)) return true;
        var reloaded = await docker.ReloadEdgeAsync(ct);
        if (reloaded.Ok) return true;
        log($"run-target: edge reload failed, keeping the previous routes: {reloaded.Detail}");
        if (before is not null) RunTargetStateStore.WriteAtomically(store.CaddyfilePath, before);
        return false;
    }

    private RunTargetCaddyfileResult RenderRoutes(RunTargetHostState state,
        IReadOnlyDictionary<string, RunTargetEnvironmentSpec> specs)
        => RunTargetCaddyfile.Render(state.Apps
            .Where(item => item.Value.Current is not null && specs.ContainsKey(item.Key))
            .Select(item => new RunTargetRoute(item.Key, specs[item.Key].Domains, specs[item.Key].Edge,
                item.Value.Current!.Upstream)), settings.Edge);

    private async Task<bool> WaitHealthyAsync(RunTargetSlot slot, int attempts, CancellationToken ct)
    {
        var url = $"http://{slot.ComposeProject}:{slot.ContainerPort}{slot.HealthPath}";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (attempt > 0) await delay(TimeSpan.FromSeconds(settings.HealthIntervalSeconds), ct);
            if (await docker.ProbeAsync(url, ct)) return true;
        }
        return false;
    }

    private async Task DiscardCandidateAsync(RunTargetSlot candidate, bool restoring, CancellationToken ct)
    {
        // A restored previous revision stays available for the next rollback.
        if (restoring) await docker.ComposeStopAsync(candidate.ComposeProject, candidate.ComposeFile, ct);
        else await RemoveSlotAsync(candidate, ct);
    }

    private async Task RemoveSlotAsync(RunTargetSlot? slot, CancellationToken ct)
    {
        if (slot is null) return;
        var removed = await docker.ComposeDownAsync(slot.ComposeProject, slot.ComposeFile, ct);
        if (!removed.Ok)
        {
            log($"run-target: could not remove revision {slot.Revision}: {removed.Detail}");
            return;
        }
        var directory = Path.GetDirectoryName(slot.ComposeFile);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
    }

    private void Fail(RunTargetAppState app, RunTargetEnvironmentSpec spec, List<RunTargetJobReport> reports,
        string evidence, string eventKind)
    {
        AddEvent(app, eventKind, spec.Artifact);
        if (app.Current is null) app.Status = "failed";
        Report(spec, reports, false, evidence);
    }

    private static void Report(RunTargetEnvironmentSpec spec, List<RunTargetJobReport> reports, bool healthy, string evidence)
    {
        if (spec.JobId is { } jobId && spec.LeaseToken is { } lease)
            reports.Add(new(spec.Project, jobId, lease, healthy, evidence));
    }

    private void AddEvent(RunTargetAppState app, string kind, string? artifact)
    {
        app.Events.Add(new(clock.GetUtcNow().UtcDateTime, kind, artifact));
        if (app.Events.Count > MaxEvents) app.Events.RemoveRange(0, app.Events.Count - MaxEvents);
    }
}
