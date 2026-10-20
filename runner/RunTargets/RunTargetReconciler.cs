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
/// <see cref="RunTargetCrashPolicy"/> backoff, and a revision that keeps
/// crashing is rolled back to the kept one. Only an explicit removal from
/// Studio takes an app down: an app Studio does not mention keeps running with
/// its routes and its previous revision.
/// </summary>
public sealed class RunTargetReconciler(
    IRunTargetDocker docker,
    IRunTargetSecretSource secrets,
    RunTargetSecretFiles secretFiles,
    RunTargetStateStore store,
    RunTargetReconcilerSettings settings,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log)
{
    private const int MaxEvents = 20;

    public async Task<RunTargetCycleResult> ReconcileAsync(RunTargetDesiredState desired, CancellationToken ct)
    {
        var state = store.Load();
        var reports = new List<RunTargetJobReport>();
        var specs = new Dictionary<string, RunTargetEnvironmentSpec>(StringComparer.Ordinal);
        foreach (var spec in desired.Environments
                     .OrderBy(item => item.Project, StringComparer.Ordinal)
                     .ThenBy(item => item.EnvironmentId, StringComparer.Ordinal))
            specs.TryAdd(RunTargetNames.AppKey(spec.Project, spec.EnvironmentId), spec);
        var removals = new Dictionary<string, RunTargetRemoval>(StringComparer.Ordinal);
        foreach (var removal in desired.Removals ?? [])
        {
            var key = RunTargetNames.AppKey(removal.Project, removal.EnvironmentId);
            if (!specs.ContainsKey(key)) removals.TryAdd(key, removal);
        }
        var removed = new List<RunTargetRemoval>();

        if (specs.Count > 0 || removals.Count > 0 || state.Apps.Count > 0)
        {
            // Removals first, so a domain they free can go to a new app in this cycle.
            foreach (var (key, removal) in removals)
            {
                if (state.Apps.TryGetValue(key, out var app))
                {
                    if (!await RemoveSlotAsync(app.Current, ct) || !await RemoveSlotAsync(app.Previous, ct)) continue;
                    state.Apps.Remove(key);
                    log($"run-target: removed {app.Project}/{app.EnvironmentId}; Studio took it off this host");
                }
                removed.Add(removal);
            }
            store.Save(state);
            var edgeReady = await EnsureEdgeAsync(state, ct);
            foreach (var (key, spec) in specs)
            {
                if (!state.Apps.TryGetValue(key, out var app))
                    state.Apps[key] = app = new RunTargetAppState { Project = spec.Project, EnvironmentId = spec.EnvironmentId };
                app.Held = false;
                // Without an edge no candidate can be checked or routed. The
                // running apps keep serving; open jobs stay claimed and retry.
                if (edgeReady) await ConvergeAsync(key, spec, app, state, reports, ct);
                store.Save(state);
            }
            var skipped = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var skip in desired.Skipped ?? []) skipped.TryAdd(skip.Project, skip.Reason);
            foreach (var (key, app) in state.Apps.Where(item => !specs.ContainsKey(item.Key)).ToArray())
            {
                if (!app.Held)
                {
                    app.Held = true;
                    log($"run-target: keeping {app.Project}/{app.EnvironmentId} as it is; " + (skipped.TryGetValue(app.Project, out var reason)
                        ? $"Studio could not read its project ({reason})" : "Studio sent neither a desired state nor a removal for it"));
                }
                if (edgeReady && app.Current is not null) await SuperviseAsync(key, app, state, ct);
            }
            if (edgeReady) await PublishRoutesAsync(state, ct);
            store.Save(state);
        }

        var now = clock.GetUtcNow().UtcDateTime;
        var observations = state.Apps.Values
            .Where(app => specs.ContainsKey(RunTargetNames.AppKey(app.Project, app.EnvironmentId)))
            .Select(app => new RunTargetObservation(app.Project, app.EnvironmentId, app.Status,
                app.Current?.Artifact, app.Previous?.Artifact, app.DockerRestarts + app.HostRestarts, now,
                app.Events.ToArray()))
            // The removed observation confirms the removal, so Studio stops sending it.
            .Concat(removed.Select(item => new RunTargetObservation(item.Project, item.EnvironmentId, "removed",
                null, null, 0, now, [])))
            .ToArray();
        return new(observations, reports);
    }

    private async Task ConvergeAsync(string key, RunTargetEnvironmentSpec spec, RunTargetAppState app,
        RunTargetHostState state, List<RunTargetJobReport> reports, CancellationToken ct)
    {
        var name = $"{spec.Project}/{spec.EnvironmentId}";
        if (spec.Artifact is null)
        {
            // Studio knows no healthy release. That is not a removal: whatever runs keeps running.
            if (app.Current is null) app.Status = "stopped";
            return;
        }
        if (RunTargetNames.Invalid(spec) is { } invalid)
        {
            log($"run-target: {name} refused: {invalid}");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        app.Domains = [.. spec.Domains];
        app.Edge = spec.Edge;
        if ((spec.RefusedDomains ?? []).Count > 0)
            NoteDomainConflict(app, $"Studio refused {string.Join(", ", spec.RefusedDomains!)}: another environment on this host owns it");
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
            await SuperviseAsync(key, app, state, ct);
            if (spec.JobId is not null)
            {
                var healthy = app.Status == "running" && await WaitHealthyAsync(app.Current, attempts: 3, ct);
                Report(spec, reports, healthy, healthy ? RunTargetEvidence.HealthOk : RunTargetEvidence.HealthFailed);
            }
            return;
        }
        if (revision == app.CrashRollbackRevision)
        {
            if (spec.JobId is null)
            {
                // Rolled back from this revision after a crash loop; only a deploy job brings it back.
                if (app.Current is not null) await SuperviseAsync(key, app, state, ct);
                return;
            }
            app.CrashRollbackRevision = null;
        }

        var restoring = app.Previous?.Revision == revision;
        var composeProject = key + "-" + revision;
        var candidate = restoring ? app.Previous! : new RunTargetSlot(revision, spec.Artifact, composeProject,
            Path.Combine(store.AppsDirectory, composeProject, "compose.yaml"), spec.ContainerPort, spec.HealthPath,
            spec.SecretRefs);
        if (await PrepareSecretsAsync(name, app.Project, candidate, ct) is not { } resolved)
        {
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        var pulled = await docker.PullAsync(spec.Artifact, ct);
        if (!pulled.Ok)
        {
            log($"run-target: {name} pull failed: {resolved.Mask(pulled.Detail)}");
            secretFiles.Remove(candidate.ComposeProject);
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, "pulled", spec.Artifact);
        store.WriteIfChanged(candidate.ComposeFile,
            RunTargetComposeFile.App(spec, key, revision, secretFiles.Directory(candidate.ComposeProject)));
        var started = await docker.ComposeUpAsync(candidate.ComposeProject, candidate.ComposeFile, ct);
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
        if (!await SwapAsync(name, app, candidate, restoring, state, ct))
        {
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, restoring || spec.IsRollback ? "rolled-back" : "swapped", spec.Artifact);
        log($"run-target: {name} now runs {spec.Artifact} (revision {revision})");
        Report(spec, reports, true, RunTargetEvidence.HealthOk);
    }

    /// <summary>
    /// Routes traffic to a healthy candidate, then stops the replaced revision,
    /// drops its secret files, and keeps it as the previous revision.
    /// </summary>
    private async Task<bool> SwapAsync(string name, RunTargetAppState app, RunTargetSlot candidate, bool restoring,
        RunTargetHostState state, CancellationToken ct)
    {
        var replaced = app.Current;
        app.Current = candidate;
        if (!await PublishRoutesAsync(state, ct))
        {
            app.Current = replaced;
            await DiscardCandidateAsync(candidate, restoring, ct);
            return false;
        }
        if (replaced is not null)
        {
            var stopped = await docker.ComposeStopAsync(replaced.ComposeProject, replaced.ComposeFile, ct);
            if (!stopped.Ok) log($"run-target: {name} could not stop replaced revision {replaced.Revision}: {stopped.Detail}");
            else secretFiles.Remove(replaced.ComposeProject);
        }
        if (app.Previous is { } older && older.Revision != candidate.Revision && older.Revision != replaced?.Revision)
            await RemoveSlotAsync(older, ct);
        app.Previous = replaced;
        if (app.CrashRollbackRevision is { } crashed && crashed != app.Current.Revision && crashed != app.Previous?.Revision)
            app.CrashRollbackRevision = null;
        app.Status = "running";
        app.CrashAttempts = 0;
        app.LastRestartAt = app.NextRestartAt = null;
        app.RecentRestarts.Clear();
        return true;
    }

    private async Task SuperviseAsync(string key, RunTargetAppState app, RunTargetHostState state, CancellationToken ct)
    {
        var slot = app.Current!;
        var name = $"{app.Project}/{app.EnvironmentId}";
        var now = clock.GetUtcNow().UtcDateTime;
        var status = await docker.InspectAsync(slot.ComposeProject, ct);
        // Docker's count starts again for a recreated container.
        var dockerRestarts = status.RestartCount >= app.DockerRestarts ? status.RestartCount - app.DockerRestarts : status.RestartCount;
        app.DockerRestarts = status.RestartCount;
        app.RecentRestarts = [.. RunTargetCrashPolicy.InWindow(app.RecentRestarts, now), .. Enumerable.Repeat(now, dockerRestarts)];
        if (RunTargetCrashPolicy.ShouldRollBack(app.RecentRestarts, now) && await RollBackCrashLoopAsync(name, app, state, ct))
            return;
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
        if (await PrepareSecretsAsync(name, app.Project, slot, ct) is not { } resolved)
        {
            log($"run-target: {name} cannot restart without its secrets");
            app.Status = "failed";
            AddEvent(app, "deploy-step-failed", slot.Artifact);
            return;
        }
        var restarted = await docker.ComposeUpAsync(slot.ComposeProject, slot.ComposeFile, ct);
        app.CrashAttempts++;
        app.HostRestarts++;
        app.RecentRestarts.Add(now);
        app.LastRestartAt = now;
        app.NextRestartAt = now + RunTargetCrashPolicy.Backoff(app.CrashAttempts);
        if (!restarted.Ok)
            log($"run-target: {name} restart failed: {resolved.Mask(restarted.Detail)}");
        AddEvent(app, "crash-restart", slot.Artifact);
        if (RunTargetCrashPolicy.IsCrashLoop(app.CrashAttempts))
        {
            if (app.Status != "crash-loop")
            {
                AddEvent(app, "crash-loop", slot.Artifact);
                log($"run-target: {name} is crash-looping; next restart at {app.NextRestartAt:O}");
            }
            app.Status = "crash-loop";
        }
        else app.Status = "starting";
    }

    /// <summary>
    /// Restores the kept previous revision when the current one restarted too
    /// often. The crashing revision becomes the previous one and is not rolled
    /// back to; Studio is told through the <c>crash-rollback</c> event and
    /// queues the matching rollback job.
    /// </summary>
    private async Task<bool> RollBackCrashLoopAsync(string name, RunTargetAppState app, RunTargetHostState state,
        CancellationToken ct)
    {
        var crashed = app.Current!;
        if (app.Previous is not { } previous || app.CrashRollbackRevision is not null) return false;
        app.CrashRollbackRevision = crashed.Revision;
        log($"run-target: {name} restarted {app.RecentRestarts.Count} times within {RunTargetCrashPolicy.RollbackWindow.TotalMinutes:0} min; "
            + $"rolling back to {previous.Artifact}");
        if (await PrepareSecretsAsync(name, app.Project, previous, ct) is not { } resolved) return false;
        var started = await docker.ComposeUpAsync(previous.ComposeProject, previous.ComposeFile, ct);
        if (!started.Ok || !await WaitHealthyAsync(previous, settings.HealthAttempts, ct))
        {
            log($"run-target: {name} previous revision {previous.Revision} did not come up healthy; keeping the current one"
                + (started.Ok ? "" : $": {resolved.Mask(started.Detail)}"));
            await DiscardCandidateAsync(previous, restoring: true, ct);
            return false;
        }
        if (!await SwapAsync(name, app, previous, restoring: true, state, ct)) return false;
        AddEvent(app, "crash-rollback", crashed.Artifact);
        AddEvent(app, "rolled-back", previous.Artifact);
        return true;
    }

    /// <summary>Resolves the slot's secrets from its project's own root and writes them to tmpfs.</summary>
    private async Task<RunTargetResolvedSecrets?> PrepareSecretsAsync(string name, string project, RunTargetSlot slot,
        CancellationToken ct)
    {
        var secretRefs = slot.SecretRefs ?? [];
        var resolved = await RunTargetResolvedSecrets.ResolveAsync(secrets, project, secretRefs, ct);
        if (resolved.Missing.Count > 0)
        {
            log($"run-target: {name} is missing secret references in its project's secret root: {string.Join(", ", resolved.Missing)}");
            return null;
        }
        if (secretRefs.Count == 0) return resolved;
        if (!secretFiles.IsMemoryBacked())
        {
            log($"run-target: {name} not started: secret directory {secretFiles.Root} is not on tmpfs, so no secret file is written there");
            return null;
        }
        secretFiles.Write(slot.ComposeProject, resolved);
        return resolved;
    }

    private async Task<bool> EnsureEdgeAsync(RunTargetHostState state, CancellationToken ct)
    {
        if (!RunTargetNames.IsDigest(settings.EdgeImage))
        {
            log($"run-target: edge unavailable: image {settings.EdgeImage} is not pinned by sha256 digest");
            return false;
        }
        // Caddy must find a Caddyfile when it first starts.
        if (store.Read(store.CaddyfilePath) is null)
            store.WriteIfChanged(store.CaddyfilePath, RenderRoutes(state).Text);
        var composeChanged = store.WriteIfChanged(store.EdgeComposePath, RunTargetComposeFile.Edge(
            settings.EdgeImage, store.CaddyDirectory, settings.CertificateDirectory, settings.HttpPort, settings.HttpsPort));
        var status = await docker.InspectAsync(RunTargetNames.EdgeContainer, ct);
        if (!composeChanged && status.State == RunTargetContainerState.Running) return true;
        var started = await docker.ComposeUpAsync(RunTargetNames.EdgeContainer, store.EdgeComposePath, ct);
        if (!started.Ok) log($"run-target: edge unavailable: {started.Detail}");
        return started.Ok;
    }

    /// <summary>Renders the routes of every current revision and reloads Caddy when they changed.</summary>
    private async Task<bool> PublishRoutesAsync(RunTargetHostState state, CancellationToken ct)
    {
        var owners = new Dictionary<string, string>(state.DomainOwners, StringComparer.Ordinal);
        var rendered = RenderRoutes(state);
        foreach (var key in rendered.ConflictingAppKeys)
            NoteDomainConflict(state.Apps[key], "it declares a domain another environment on this host routed first; that domain is not routed to it");
        var before = store.Read(store.CaddyfilePath);
        if (!store.WriteIfChanged(store.CaddyfilePath, rendered.Text)) return true;
        var reloaded = await docker.ReloadEdgeAsync(ct);
        if (reloaded.Ok) return true;
        log($"run-target: edge reload failed, keeping the previous routes: {reloaded.Detail}");
        if (before is not null) RunTargetStateStore.WriteAtomically(store.CaddyfilePath, before);
        state.DomainOwners = owners;
        return false;
    }

    private RunTargetCaddyfileResult RenderRoutes(RunTargetHostState state)
    {
        var routes = state.Apps
            .Where(item => item.Value.Current is not null)
            .Select(item => new RunTargetRoute(item.Key, item.Value.Domains, item.Value.Edge, item.Value.Current!.Upstream))
            .ToArray();
        state.DomainOwners = RunTargetCaddyfile.ClaimDomains(state.DomainOwners, routes);
        return RunTargetCaddyfile.Render(routes, state.DomainOwners, settings.Edge);
    }

    private void NoteDomainConflict(RunTargetAppState app, string detail)
    {
        if (app.Events.LastOrDefault()?.Kind == "domain-conflict") return;
        AddEvent(app, "domain-conflict", app.Current?.Artifact);
        log($"run-target: {app.Project}/{app.EnvironmentId} has a domain conflict: {detail}");
    }

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
        if (!restoring)
        {
            await RemoveSlotAsync(candidate, ct);
            return;
        }
        var stopped = await docker.ComposeStopAsync(candidate.ComposeProject, candidate.ComposeFile, ct);
        if (stopped.Ok) secretFiles.Remove(candidate.ComposeProject);
        else log($"run-target: could not stop revision {candidate.Revision}: {stopped.Detail}");
    }

    private async Task<bool> RemoveSlotAsync(RunTargetSlot? slot, CancellationToken ct)
    {
        if (slot is null) return true;
        var removed = await docker.ComposeDownAsync(slot.ComposeProject, slot.ComposeFile, ct);
        if (!removed.Ok)
        {
            log($"run-target: could not remove revision {slot.Revision}: {removed.Detail}");
            return false;
        }
        secretFiles.Remove(slot.ComposeProject);
        var directory = Path.GetDirectoryName(slot.ComposeFile);
        if (directory is not null && Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        return true;
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
