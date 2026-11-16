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
    IReadOnlyList<RunTargetJobReport> JobReports,
    IReadOnlyList<RunTargetDatabaseObservation>? Databases = null);

/// <summary>
/// Converges the host to Studio's desired state. A new revision is pulled,
/// started next to the running one, and health-checked through the edge; only a
/// healthy candidate receives traffic. The replaced revision is stopped but
/// kept, so a rollback restarts it instead of pulling again. A failed health
/// check leaves the running revision untouched. A crashed app is restarted with
/// <see cref="RunTargetCrashPolicy"/> backoff, and a revision that keeps
/// crashing is rolled back to the kept one. Only an explicit removal from
/// Studio takes an app down: an app Studio does not mention keeps running with
/// its routes and its previous revision. Supervision needs only the host's own
/// state, so it also runs while Studio cannot be reached
/// (<see cref="SuperviseOnlyAsync"/>). An app key belongs to the project
/// environment stored under it: a spec or removal for another environment
/// that maps to the same key is refused. Managed databases (AGT-3877) are
/// provisioned before the app revision that uses them and outlive it: a
/// database Studio no longer declares is stopped with its volume kept, and only
/// an owner's deletion from Studio removes the volume.
/// </summary>
public sealed class RunTargetReconciler(
    IRunTargetDocker docker,
    IRunTargetSecretSource secrets,
    RunTargetSecretFiles secretFiles,
    RunTargetStateStore store,
    RunTargetReconcilerSettings settings,
    TimeProvider clock,
    Func<TimeSpan, CancellationToken, Task> delay,
    Action<string> log,
    IRunTargetCredentialSource? credentials = null)
{
    private const int MaxEvents = 20;

    public Task<RunTargetCycleResult> ReconcileAsync(RunTargetDesiredState desired, CancellationToken ct)
        => CycleAsync(desired, offlineReason: null, ct);

    /// <summary>
    /// The pass for a poll that got no desired state: every app is held, so the
    /// host still restarts crashed apps, rolls back a crash loop, and writes
    /// secret files again (after a reboot the tmpfs is empty), all from its
    /// persisted state.
    /// </summary>
    public Task SuperviseOnlyAsync(string reason, CancellationToken ct)
        => CycleAsync(new RunTargetDesiredState("", clock.GetUtcNow().UtcDateTime, [], [], []), reason, ct);

    private async Task<RunTargetCycleResult> CycleAsync(RunTargetDesiredState desired, string? offlineReason,
        CancellationToken ct)
    {
        var state = store.Load();
        await RetryPendingCleanupAsync(state, ct);
        await RetryPendingStopsAsync(state, ct);
        var reports = new List<RunTargetJobReport>();
        var specs = new Dictionary<string, RunTargetEnvironmentSpec>(StringComparer.Ordinal);
        var refused = new HashSet<string>(StringComparer.Ordinal);
        foreach (var group in desired.Environments.GroupBy(item => RunTargetNames.AppKey(item.Project, item.EnvironmentId),
                     StringComparer.Ordinal))
        {
            var spec = group.First();
            var conflict = group.Skip(1).Any()
                ? $"{string.Join(", ", group.Select(Name))} share app key {group.Key}"
                : state.Apps.TryGetValue(group.Key, out var stored) && !Owns(stored, spec.Project, spec.EnvironmentId)
                    ? $"{Name(spec)} maps to app key {group.Key} of {stored.Project}/{stored.EnvironmentId}"
                    : null;
            if (conflict is null)
            {
                specs[group.Key] = spec;
                continue;
            }
            log($"run-target: refused: {conflict}");
            refused.Add(group.Key);
            foreach (var item in group) Report(item, reports, false, RunTargetEvidence.StepFailed);
        }
        var removals = new Dictionary<string, RunTargetRemoval>(StringComparer.Ordinal);
        foreach (var removal in desired.Removals ?? [])
        {
            var key = RunTargetNames.AppKey(removal.Project, removal.EnvironmentId);
            if (specs.ContainsKey(key) || refused.Contains(key)) continue;
            if (state.Apps.TryGetValue(key, out var stored) && !Owns(stored, removal.Project, removal.EnvironmentId))
            {
                log($"run-target: refused removal of {removal.Project}/{removal.EnvironmentId}: app key {key} "
                    + $"belongs to {stored.Project}/{stored.EnvironmentId}");
                continue;
            }
            removals.TryAdd(key, removal);
        }
        var removed = new List<RunTargetRemoval>();
        var deletedDatabases = new List<RunTargetDatabaseDeletion>();

        if (specs.Count > 0 || removals.Count > 0 || state.Apps.Count > 0 || (desired.DatabaseDeletions ?? []).Count > 0)
        {
            // Removals first, so a domain they free can go to a new app in this cycle.
            foreach (var (key, removal) in removals)
            {
                if (state.Apps.TryGetValue(key, out var app) && !await RemoveAppAsync(key, app, state, ct)) continue;
                removed.Add(removal);
            }
            store.Save(state);
            foreach (var deletion in desired.DatabaseDeletions ?? [])
            {
                if (await DeleteDatabaseAsync(deletion, specs, state, ct)) deletedDatabases.Add(deletion);
            }
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
                    log($"run-target: keeping {app.Project}/{app.EnvironmentId} as it is; " + (
                        offlineReason is not null ? $"Studio is unreachable ({offlineReason})"
                        : refused.Contains(key) ? "its desired state was refused"
                        : skipped.TryGetValue(app.Project, out var reason) ? $"Studio could not read its project ({reason})"
                        : "Studio sent neither a desired state nor a removal for it"));
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
        if (offlineReason is null) await MeasureDatabasesAsync(state, ct);
        var databases = state.Databases.Values
            .Select(item => new RunTargetDatabaseObservation(item.Project, item.EnvironmentId, item.Name, item.Status,
                item.Version, item.VolumeBytes, now))
            .Concat(deletedDatabases.Select(item => new RunTargetDatabaseObservation(item.Project, item.EnvironmentId,
                item.Name, "deleted", null, null, now)))
            .ToArray();
        return new(observations, reports, databases);
    }

    private static string Name(RunTargetEnvironmentSpec spec) => $"{spec.Project}/{spec.EnvironmentId}";

    private static bool Owns(RunTargetAppState app, string project, string environmentId)
        => string.Equals(app.Project, project, StringComparison.Ordinal)
            && string.Equals(app.EnvironmentId, environmentId, StringComparison.Ordinal);

    /// <summary>
    /// Takes both revisions and the app network down. Each slot is cleared and
    /// saved as soon as it is gone, so a retry continues where a failure stopped.
    /// </summary>
    private async Task<bool> RemoveAppAsync(string key, RunTargetAppState app, RunTargetHostState state, CancellationToken ct)
    {
        if (!await RemoveSlotAsync(app.Current, ct)) return false;
        if (app.Current is { } current) state.PendingStops.RemoveAll(item => item.ComposeProject == current.ComposeProject);
        app.Current = null;
        store.Save(state);
        if (!await RemoveSlotAsync(app.Previous, ct)) return false;
        if (app.Previous is { } previous) state.PendingStops.RemoveAll(item => item.ComposeProject == previous.ComposeProject);
        app.Previous = null;
        if (!await RetainDatabasesAsync(key, app.Project, app.EnvironmentId, [], state, removeContainers: true, ct))
            return false;
        var network = await docker.RemoveNetworkAsync(RunTargetNames.AppNetwork(key), ct);
        if (!network.Ok)
        {
            log($"run-target: could not remove network {RunTargetNames.AppNetwork(key)}: {network.Detail}");
            return false;
        }
        state.Apps.Remove(key);
        log($"run-target: removed {app.Project}/{app.EnvironmentId}; Studio took it off this host");
        return true;
    }

    private async Task ConvergeAsync(string key, RunTargetEnvironmentSpec spec, RunTargetAppState app,
        RunTargetHostState state, List<RunTargetJobReport> reports, CancellationToken ct)
    {
        var name = Name(spec);
        if (RunTargetNames.Invalid(spec, requireArtifact: spec.Artifact is not null) is { } invalid)
        {
            log($"run-target: {name} refused: {invalid}");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        app.Domains = [.. spec.Domains];
        app.Edge = spec.Edge;
        if ((spec.RefusedDomains ?? []).Count > 0)
        {
            NoteDomainConflict(app, $"Studio refused {string.Join(", ", spec.RefusedDomains!)}: "
                + "the project's allowed domain suffixes do not cover it, or another environment on this host owns it");
        }
        if (spec.Artifact is null)
        {
            // Studio knows no healthy release. That is not a removal: whatever runs keeps running and is supervised.
            if (app.Current is null) app.Status = "stopped";
            else if (await AttachAsync(key, name, ct)) await SuperviseAsync(key, app, state, ct);
            return;
        }
        if (!await AttachAsync(key, name, ct)) return;
        if (!await EnsureDatabasesAsync(key, spec, state, ct))
        {
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }

        var revision = RunTargetNames.Revision(spec);
        if (app.Current is { } running && running.Revision == revision)
        {
            await SuperviseAsync(key, app, state, ct);
            if (app.Current == running)
                await RetainDatabasesAsync(key, spec.Project, spec.EnvironmentId, spec.Databases ?? [], state, removeContainers: false, ct);
            if (spec.JobId is not null)
            {
                // A crash rollback in this pass replaced the revision the job asks for.
                var healthy = app.Current == running && app.Status == "running"
                    && await WaitHealthyAsync(running, attempts: 3, ct);
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
            spec.SecretRefs, SlotDatabases(key, spec));
        if (state.PendingCleanup.Any(item => item.ComposeProject == candidate.ComposeProject))
        {
            log($"run-target: {name} cannot start revision {revision} while its previous Docker cleanup is pending");
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        if (await PrepareSecretsAsync(name, app, candidate, ct) is not { } resolved)
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
            await DiscardCandidateAsync(candidate, restoring, state, ct);
            Fail(app, spec, reports, RunTargetEvidence.StepFailed, "deploy-step-failed");
            return;
        }
        AddEvent(app, "started", spec.Artifact);
        if (!await WaitHealthyAsync(candidate, settings.HealthAttempts, ct))
        {
            log($"run-target: {name} candidate {revision} failed its health check; the running revision keeps serving");
            await DiscardCandidateAsync(candidate, restoring, state, ct);
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
        await RetainDatabasesAsync(key, spec.Project, spec.EnvironmentId, spec.Databases ?? [], state, removeContainers: false, ct);
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
            await DiscardCandidateAsync(candidate, restoring, state, ct);
            return false;
        }
        if (replaced is not null)
        {
            var stopped = await docker.ComposeStopAsync(replaced.ComposeProject, replaced.ComposeFile, ct);
            if (!stopped.Ok)
            {
                log($"run-target: {name} could not stop replaced revision {replaced.Revision}: {stopped.Detail}");
                if (!state.PendingStops.Any(item => item.ComposeProject == replaced.ComposeProject))
                    state.PendingStops.Add(replaced);
            }
            else
            {
                secretFiles.Remove(replaced.ComposeProject);
                state.PendingStops.RemoveAll(item => item.ComposeProject == replaced.ComposeProject);
            }
        }
        var older = app.Previous is { } prior && prior.Revision != candidate.Revision
            && prior.Revision != replaced?.Revision ? prior : null;
        app.Previous = replaced;
        state.PendingStops.RemoveAll(item => item.ComposeProject == candidate.ComposeProject);
        if (app.CrashRollbackRevision is { } crashed && crashed != app.Current.Revision && crashed != app.Previous?.Revision)
            app.CrashRollbackRevision = null;
        app.Status = "running";
        app.CrashAttempts = 0;
        // Docker counts restarts per container; the new current one starts at zero.
        app.DockerRestarts = 0;
        app.LastRestartAt = app.NextRestartAt = null;
        app.RecentRestarts.Clear();
        store.Save(state);
        if (older is not null) await RemoveOrQueueAsync(older, state, ct);
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
        if (await PrepareSecretsAsync(name, app, slot, ct) is not { } resolved)
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
    /// often. Only after a successful swap is the crashing revision marked as
    /// rolled back from; it becomes the previous one and is not rolled back to.
    /// Studio is told through the <c>crash-rollback</c> event and queues the
    /// matching rollback job. A failed restore keeps the current revision and
    /// tries again after another full count of restarts.
    /// </summary>
    private async Task<bool> RollBackCrashLoopAsync(string name, RunTargetAppState app, RunTargetHostState state,
        CancellationToken ct)
    {
        var crashed = app.Current!;
        if (app.Previous is not { } previous || app.CrashRollbackRevision is not null) return false;
        log($"run-target: {name} restarted {app.RecentRestarts.Count} times within {RunTargetCrashPolicy.RollbackWindow.TotalMinutes:0} min; "
            + $"rolling back to {previous.Artifact}");
        if (!await RestoreForRollbackAsync(name, app, previous, state, ct))
        {
            app.RecentRestarts.Clear();
            return false;
        }
        app.CrashRollbackRevision = crashed.Revision;
        AddEvent(app, "crash-rollback", crashed.Artifact);
        AddEvent(app, "rolled-back", previous.Artifact);
        return true;
    }

    private async Task<bool> RestoreForRollbackAsync(string name, RunTargetAppState app, RunTargetSlot previous,
        RunTargetHostState state, CancellationToken ct)
    {
        if (await PrepareSecretsAsync(name, app, previous, ct) is not { } resolved) return false;
        var started = await docker.ComposeUpAsync(previous.ComposeProject, previous.ComposeFile, ct);
        if (!started.Ok || !await WaitHealthyAsync(previous, settings.HealthAttempts, ct))
        {
            log($"run-target: {name} previous revision {previous.Revision} did not come up healthy; keeping the current one"
                + (started.Ok ? "" : $": {resolved.Mask(started.Detail)}"));
            await DiscardCandidateAsync(previous, restoring: true, state, ct);
            return false;
        }
        return await SwapAsync(name, app, previous, restoring: true, state, ct);
    }

    /// <summary>
    /// Resolves the slot's secrets from its project's own root, adds the
    /// connection string of each managed database, and writes them to tmpfs.
    /// </summary>
    private async Task<RunTargetResolvedSecrets?> PrepareSecretsAsync(string name, RunTargetAppState app, RunTargetSlot slot,
        CancellationToken ct)
    {
        var secretRefs = slot.SecretRefs ?? [];
        var resolved = await RunTargetResolvedSecrets.ResolveAsync(secrets, app.Project, secretRefs, ct);
        if (resolved.Missing.Count > 0)
        {
            log($"run-target: {name} is missing secret references in its project's secret root: {string.Join(", ", resolved.Missing)}");
            return null;
        }
        if ((slot.Databases ?? []).Count > 0)
        {
            var urls = new Dictionary<string, string>(StringComparer.Ordinal);
            var passwords = new List<string>();
            foreach (var database in slot.Databases!)
            {
                if (await ReadCredentialAsync(name, app.Project, app.EnvironmentId, database.Name, database.PasswordRef, ct)
                    is not { } password) return null;
                passwords.Add(password);
                urls[RunTargetNames.DatabaseUrlFile(database.Name)] =
                    RunTargetNames.ConnectionString(database.DatabaseKey, database.Name, password);
            }
            resolved = resolved.With(urls, passwords);
        }
        if (resolved.Values.Count == 0) return resolved;
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
        if (!started.Ok)
        {
            log($"run-target: edge unavailable: {started.Detail}");
            return false;
        }
        // A recreated edge has lost the app networks attached at run time,
        // including those of apps Studio sends no spec for.
        foreach (var (key, app) in state.Apps.Where(item => item.Value.Current is not null))
            await AttachAsync(key, $"{app.Project}/{app.EnvironmentId}", ct);
        return true;
    }

    /// <summary>Creates the app network if needed and connects the edge to it.</summary>
    private async Task<bool> AttachAsync(string key, string name, CancellationToken ct)
    {
        var network = RunTargetNames.AppNetwork(key);
        var attach = await docker.EnsureNetworkAsync(network, ct);
        if (attach.Ok) attach = await docker.ConnectAsync(network, RunTargetNames.EdgeContainer, ct);
        if (!attach.Ok) log($"run-target: {name} network unavailable: {attach.Detail}");
        return attach.Ok;
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

    private async Task DiscardCandidateAsync(RunTargetSlot candidate, bool restoring, RunTargetHostState state,
        CancellationToken ct)
    {
        // A restored previous revision stays available for the next rollback.
        if (!restoring)
        {
            await RemoveOrQueueAsync(candidate, state, ct);
            return;
        }
        var stopped = await docker.ComposeStopAsync(candidate.ComposeProject, candidate.ComposeFile, ct);
        if (stopped.Ok)
        {
            secretFiles.Remove(candidate.ComposeProject);
            state.PendingStops.RemoveAll(item => item.ComposeProject == candidate.ComposeProject);
        }
        else
        {
            log($"run-target: could not stop revision {candidate.Revision}: {stopped.Detail}");
            if (!state.PendingStops.Any(item => item.ComposeProject == candidate.ComposeProject))
                state.PendingStops.Add(candidate);
        }
        store.Save(state);
    }

    private async Task RemoveOrQueueAsync(RunTargetSlot slot, RunTargetHostState state, CancellationToken ct)
    {
        if (await RemoveSlotAsync(slot, ct)) return;
        if (!state.PendingCleanup.Any(item => item.ComposeProject == slot.ComposeProject))
            state.PendingCleanup.Add(slot);
        store.Save(state);
    }

    private async Task RetryPendingCleanupAsync(RunTargetHostState state, CancellationToken ct)
    {
        foreach (var slot in state.PendingCleanup.ToArray())
        {
            if (!await RemoveSlotAsync(slot, ct)) continue;
            state.PendingCleanup.Remove(slot);
            state.PendingStops.RemoveAll(item => item.ComposeProject == slot.ComposeProject);
            store.Save(state);
        }
    }

    private async Task RetryPendingStopsAsync(RunTargetHostState state, CancellationToken ct)
    {
        foreach (var slot in state.PendingStops.ToArray())
        {
            if (state.Apps.Values.Any(app => app.Current?.ComposeProject == slot.ComposeProject))
            {
                state.PendingStops.Remove(slot);
                store.Save(state);
                continue;
            }
            var stopped = await docker.ComposeStopAsync(slot.ComposeProject, slot.ComposeFile, ct);
            if (!stopped.Ok)
            {
                log($"run-target: could not stop replaced revision {slot.Revision}: {stopped.Detail}");
                continue;
            }
            secretFiles.Remove(slot.ComposeProject);
            state.PendingStops.Remove(slot);
            store.Save(state);
        }
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

    /// <summary>Volume usage is measured at most this often; <c>du</c> reads every file of the database.</summary>
    private static readonly TimeSpan MeasureEvery = TimeSpan.FromMinutes(5);

    /// <summary>
    /// Provisions or converges each declared database before the app revision
    /// that uses it: its own volume, the data network, the password file on
    /// tmpfs, then <c>compose up</c> and a readiness check over TCP. A changed
    /// minor version or size recreates the container on the same volume. A
    /// different major version is refused; the data stays as it is.
    /// </summary>
    private async Task<bool> EnsureDatabasesAsync(string key, RunTargetEnvironmentSpec spec, RunTargetHostState state,
        CancellationToken ct)
    {
        var databases = spec.Databases ?? [];
        if (databases.Count == 0) return true;
        var name = Name(spec);
        var network = await docker.EnsureNetworkAsync(RunTargetNames.DataNetwork(key), ct);
        if (!network.Ok)
        {
            log($"run-target: {name} data network unavailable: {network.Detail}");
            return false;
        }
        foreach (var database in databases)
        {
            var databaseKey = RunTargetNames.DatabaseKey(key, database.Name);
            var label = $"{name} database {database.Name}";
            if (state.Databases.TryGetValue(databaseKey, out var stored) && !Owns(stored, spec.Project, spec.EnvironmentId, database.Name))
            {
                log($"run-target: {label} refused: key {databaseKey} belongs to {stored.Project}/{stored.EnvironmentId} database {stored.Name}");
                return false;
            }
            if (stored is not null && stored.Version.Length > 0
                && RunTargetNames.MajorOf(stored.Version) != RunTargetNames.MajorOf(database.Version))
            {
                log($"run-target: {label} holds PostgreSQL {RunTargetNames.MajorOf(stored.Version)} data; version "
                    + $"{database.Version} is refused because a major upgrade needs a dump and restore. The data is unchanged.");
                stored.Status = "upgrade-refused";
                store.Save(state);
                return false;
            }
            var composeFile = Path.Combine(store.DatabasesDirectory, databaseKey, "compose.yaml");
            var changed = store.WriteIfChanged(composeFile,
                RunTargetComposeFile.Database(spec, database, key, databaseKey, secretFiles.Directory(databaseKey)));
            if (stored is null)
                state.Databases[databaseKey] = stored = new RunTargetDatabaseState
                    { Project = spec.Project, EnvironmentId = spec.EnvironmentId, Name = database.Name };
            stored.ComposeFile = composeFile;
            stored.PasswordRef = database.PasswordRef;
            var status = await docker.InspectAsync(databaseKey, ct);
            if (!changed && status.State == RunTargetContainerState.Running && stored.Status == "running") continue;

            if (await ReadCredentialAsync(name, spec.Project, spec.EnvironmentId, database.Name, database.PasswordRef, ct)
                is not { } password || !secretFiles.IsMemoryBacked())
            {
                if (!secretFiles.IsMemoryBacked())
                    log($"run-target: {label} not started: secret directory {secretFiles.Root} is not on tmpfs, so no secret file is written there");
                stored.Status = "failed";
                store.Save(state);
                return false;
            }
            var resolved = RunTargetResolvedSecrets.FromValues(new Dictionary<string, string> { ["password"] = password });
            secretFiles.Write(databaseKey, resolved);
            var previous = stored.Version;
            var step = await docker.EnsureVolumeAsync(RunTargetNames.DatabaseVolume(databaseKey), ct);
            if (step.Ok && (changed || status.State == RunTargetContainerState.Missing))
                step = await docker.PullAsync(RunTargetNames.PostgresImage(database.Version), ct);
            if (step.Ok)
            {
                // From here the volume may hold data of this major version.
                if (stored.Version.Length == 0) stored.Version = database.Version;
                store.Save(state);
                step = await docker.ComposeUpAsync(databaseKey, composeFile, ct);
            }
            if (!step.Ok || !await WaitDatabaseReadyAsync(databaseKey, database.Name, ct))
            {
                log($"run-target: {label} did not become ready" + (step.Ok ? "" : ": " + resolved.Mask(step.Detail)));
                stored.Status = "failed";
                store.Save(state);
                return false;
            }
            stored.Version = database.Version;
            stored.Status = "running";
            store.Save(state);
            log($"run-target: {label} runs PostgreSQL {database.Version}"
                + (previous.Length > 0 && previous != database.Version ? $", upgraded from {previous}" : ""));
        }
        return true;
    }

    /// <summary>
    /// Stops the environment's databases that <paramref name="keep"/> does not
    /// name and marks them retained; their volumes stay. When the app leaves
    /// the host, every database container and the data network go as well.
    /// </summary>
    private async Task<bool> RetainDatabasesAsync(string key, string project, string environmentId,
        IReadOnlyList<RunTargetDatabaseSpec> keep, RunTargetHostState state, bool removeContainers, CancellationToken ct)
    {
        var owned = state.Databases.Where(item => item.Value.Project == project && item.Value.EnvironmentId == environmentId)
            .ToArray();
        foreach (var (databaseKey, stored) in owned.Where(item => !keep.Any(database => database.Name == item.Value.Name)))
        {
            if (stored.Status == "retained" && !removeContainers) continue;
            var result = !File.Exists(stored.ComposeFile) ? RunTargetDockerResult.Success
                : removeContainers ? await docker.ComposeDownAsync(databaseKey, stored.ComposeFile, ct)
                : await docker.ComposeStopAsync(databaseKey, stored.ComposeFile, ct);
            if (!result.Ok)
            {
                log($"run-target: could not stop {project}/{environmentId} database {stored.Name}: {result.Detail}");
                return false;
            }
            secretFiles.Remove(databaseKey);
            if (stored.Status != "retained")
                log($"run-target: {project}/{environmentId} database {stored.Name} is stopped; its volume "
                    + $"{RunTargetNames.DatabaseVolume(databaseKey)} is kept until an owner deletes it in Studio");
            stored.Status = "retained";
        }
        if (removeContainers && owned.Length > 0)
        {
            var network = await docker.RemoveNetworkAsync(RunTargetNames.DataNetwork(key), ct);
            if (!network.Ok)
            {
                log($"run-target: could not remove network {RunTargetNames.DataNetwork(key)}: {network.Detail}");
                return false;
            }
        }
        store.Save(state);
        return true;
    }

    /// <summary>
    /// Deletes a database's container and volume after an owner confirmed it in
    /// Studio. A database the environment's desired state still declares is
    /// never deleted. The volume name follows from the database's identity, so
    /// the deletion also works after the host lost its state.
    /// </summary>
    private async Task<bool> DeleteDatabaseAsync(RunTargetDatabaseDeletion deletion,
        IReadOnlyDictionary<string, RunTargetEnvironmentSpec> specs, RunTargetHostState state, CancellationToken ct)
    {
        var label = $"{deletion.Project}/{deletion.EnvironmentId} database {deletion.Name}";
        if (!RunTargetNames.IsDatabaseName(deletion.Name) || RunTargetNames.SecretScope(deletion.Project) is null)
        {
            log($"run-target: refused deletion of {label}: invalid name");
            return false;
        }
        var key = RunTargetNames.AppKey(deletion.Project, deletion.EnvironmentId);
        var databaseKey = RunTargetNames.DatabaseKey(key, deletion.Name);
        if (specs.TryGetValue(key, out var spec) && (spec.Databases ?? []).Any(item => item.Name == deletion.Name))
        {
            log($"run-target: refused deletion of {label}: the environment still declares it");
            return false;
        }
        if (state.Databases.TryGetValue(databaseKey, out var stored)
            && !Owns(stored, deletion.Project, deletion.EnvironmentId, deletion.Name))
        {
            log($"run-target: refused deletion of {label}: key {databaseKey} belongs to {stored.Project}/{stored.EnvironmentId}");
            return false;
        }
        if (stored is not null && File.Exists(stored.ComposeFile))
        {
            var down = await docker.ComposeDownAsync(databaseKey, stored.ComposeFile, ct);
            if (!down.Ok)
            {
                log($"run-target: could not remove the container of {label}: {down.Detail}");
                return false;
            }
        }
        var removed = await docker.RemoveVolumeAsync(RunTargetNames.DatabaseVolume(databaseKey), ct);
        if (!removed.Ok)
        {
            log($"run-target: could not delete the volume of {label}: {removed.Detail}");
            return false;
        }
        secretFiles.Remove(databaseKey);
        var directory = Path.Combine(store.DatabasesDirectory, databaseKey);
        if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        state.Databases.Remove(databaseKey);
        store.Save(state);
        log($"run-target: deleted the data of {label}; an owner confirmed the deletion in Studio");
        return true;
    }

    /// <summary>Records the size of each running database's data directory, at most every <see cref="MeasureEvery"/>.</summary>
    private async Task MeasureDatabasesAsync(RunTargetHostState state, CancellationToken ct)
    {
        var now = clock.GetUtcNow().UtcDateTime;
        var measured = false;
        foreach (var (databaseKey, database) in state.Databases)
        {
            if (database.Status != "running" || database.MeasuredAt is { } last && now - last < MeasureEvery) continue;
            var usage = await docker.ExecAsync(databaseKey, ["du", "-sk", RunTargetNames.DatabaseDataPath], ct);
            database.MeasuredAt = now;
            measured = true;
            if (usage.Ok && long.TryParse(usage.Detail.Split(['\t', ' '], 2)[0].Trim(), out var kilobytes))
                database.VolumeBytes = kilobytes * 1024;
            else log($"run-target: could not measure the volume of {database.Project}/{database.EnvironmentId} database {database.Name}");
        }
        if (measured) store.Save(state);
    }

    private async Task<bool> WaitDatabaseReadyAsync(string databaseKey, string database, CancellationToken ct)
    {
        // Over TCP: the image's initialisation server listens on the socket only, so ready means the final server.
        string[] check = ["pg_isready", "-q", "-h", "127.0.0.1", "-p", RunTargetNames.DatabasePort.ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-U", RunTargetNames.DatabaseUser, "-d", database];
        for (var attempt = 0; attempt < settings.HealthAttempts; attempt++)
        {
            if (attempt > 0) await delay(TimeSpan.FromSeconds(settings.HealthIntervalSeconds), ct);
            if ((await docker.ExecAsync(databaseKey, check, ct)).Ok) return true;
        }
        return false;
    }

    private static IReadOnlyList<RunTargetSlotDatabase>? SlotDatabases(string key, RunTargetEnvironmentSpec spec)
        => spec.Databases is { Count: > 0 } databases
            ? databases.Select(item => new RunTargetSlotDatabase(item.Name, RunTargetNames.DatabaseKey(key, item.Name),
                item.PasswordRef)).ToArray()
            : null;

    /// <summary>Reads a managed database password from Studio; the reference is logged, the value never.</summary>
    private async Task<string?> ReadCredentialAsync(string name, string project, string environmentId, string database,
        string reference, CancellationToken ct)
    {
        if (credentials is null)
        {
            log($"run-target: {name} cannot read the credential of database {database}: this host has no credential source");
            return null;
        }
        try
        {
            var value = await credentials.ReadAsync(project, environmentId, reference, ct);
            if (value is null) log($"run-target: {name} database {database}: Studio did not issue {reference} to this host");
            return value;
        }
        catch (HttpRequestException ex)
        {
            log($"run-target: {name} database {database}: Studio is unreachable for {reference} ({ex.Message})");
            return null;
        }
    }

    private static bool Owns(RunTargetDatabaseState database, string project, string environmentId, string name)
        => string.Equals(database.Project, project, StringComparison.Ordinal)
            && string.Equals(database.EnvironmentId, environmentId, StringComparison.Ordinal)
            && string.Equals(database.Name, name, StringComparison.Ordinal);
}
