using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Projects;

// AGT-3877 managed databases on top of the IP-1 run-target protocol. The
// deploy job's snapshot says which databases an environment has, so a change
// to project.yml reaches the host only through a deploy. Studio's records
// outlive the declaration: data a deploy no longer declares is retained until
// an owner deletes it and the host confirms the deletion.
public sealed partial class ProjectEnvironmentService
{
    /// <summary>The databases of one environment for the Bereitstellung area; null when the environment is not declared.</summary>
    public IReadOnlyList<ManagedDatabaseView>? ManagedDatabases(string project, string environmentId)
    {
        var file = DeclarationFiles(project)?.FirstOrDefault(item => item.EnvironmentId == environmentId);
        if (file is null) return null;
        var kind = file.Declaration?.Kind ?? "";
        var policy = file.Declaration?.EffectiveDatabasePolicy ?? ManagedDatabaseEnvironmentPolicy.For(kind);
        var now = _clock.GetUtcNow().UtcDateTime;
        lock (_gate)
        {
            var state = TryLoad(project);
            var records = (state?.Databases ?? []).Where(item => item.EnvironmentId == environmentId).ToArray();
            var operations = state?.DatabaseOperations ?? [];
            var live = records.Where(item => item.DeletedAt is null).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            return records.OrderBy(item => item.DeletedAt is null ? 0 : 1).ThenBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => ManagedDatabasePolicy.View(project, item, kind, policy, operations, now))
                .Concat((file.Declaration?.Databases ?? []).Where(item => !live.Contains(item.Name))
                    .Select(item => ManagedDatabasePolicy.Pending(project, file.Declaration!, item)))
                .ToArray();
        }
    }

    /// <summary>
    /// Records a backup, verification or restore of a database (AGT-3878). A
    /// production restore waits for an Owner's approval; everything else is
    /// handed to the host on its next poll.
    /// </summary>
    public ManagedDatabaseOperationDecision RequestDatabaseOperation(string project, string environmentId, string name,
        ManagedDatabaseOperationRequest request, string actor)
    {
        var declaration = IsWatched(project) ? Declarations(project)?.FirstOrDefault(item => item.Id == environmentId) : null;
        if (declaration is null)
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.NotFound, "The environment is not declared.");
        lock (_gate)
        {
            if (TryLoad(project) is not { } state)
                return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.Conflict, "The environment state is unreadable.");
            var now = _clock.GetUtcNow().UtcDateTime;
            var changed = ManagedDatabaseOperationPolicy.Expire(state.DatabaseOperations, now);
            var decision = ManagedDatabaseOperationPolicy.Request(state.Databases, state.DatabaseOperations, environmentId,
                declaration.Kind, name, request, actor, DeployOpen(state, environmentId), now);
            if (decision.Operation is { } operation) state.DatabaseOperations.Add(operation);
            if (changed || decision.Operation is not null) Save(project, state);
            return decision;
        }
    }

    /// <summary>An Owner's approval or rejection of a production restore.</summary>
    public ManagedDatabaseOperationDecision DecideDatabaseOperation(string project, string environmentId, string name,
        string operationId, bool approve, string actor)
    {
        if (!IsWatched(project))
            return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.NotFound, "The operation is not known.");
        lock (_gate)
        {
            if (TryLoad(project) is not { } state)
                return ManagedDatabaseOperationDecision.Fail(ManagedDatabaseOperationOutcome.NotFound, "The operation is not known.");
            var now = _clock.GetUtcNow().UtcDateTime;
            var changed = ManagedDatabaseOperationPolicy.Expire(state.DatabaseOperations, now);
            var operation = state.DatabaseOperations.FirstOrDefault(item => item.Id == operationId
                && item.EnvironmentId == environmentId && (item.SourceDatabase == name || item.TargetDatabase == name));
            var decision = ManagedDatabaseOperationPolicy.Decide(operation, state.Databases, approve, actor,
                DeployOpen(state, environmentId), now);
            if (decision.Operation is { } decided) state.DatabaseOperations[state.DatabaseOperations.IndexOf(operation!)] = decided;
            if (changed || decision.Operation is not null) Save(project, state);
            return decision;
        }
    }

    /// <summary>
    /// The databases whose backups and volume Studio watches, with the
    /// declaration of their environment (null when it is no longer declared),
    /// plus any other record that still holds an open alert.
    /// </summary>
    public IReadOnlyList<(EnvironmentDeclaration? Environment, ManagedDatabaseRecord Record)> DatabasesToWatch(string project)
    {
        var declarations = DeclarationFiles(project)?.Select(item => item.Declaration).OfType<EnvironmentDeclaration>().ToArray() ?? [];
        lock (_gate)
        {
            return (TryLoad(project)?.Databases ?? [])
                .Where(item => item.Alerts.Any(alert => alert.ClosedAt is null) || item.DeletedAt is null && item.Declared)
                .Select(item => (declarations.FirstOrDefault(environment => environment.Id == item.EnvironmentId), item))
                .ToArray();
        }
    }

    public void SetDatabaseAlerts(string project, string environmentId, string name, string hostId,
        IReadOnlyList<ManagedDatabaseAlert> alerts)
    {
        lock (_gate)
        {
            if (TryLoad(project) is not { } state) return;
            var index = state.Databases.FindLastIndex(item => item.EnvironmentId == environmentId && item.Name == name
                && item.HostId == hostId);
            if (index < 0 || state.Databases[index].Alerts.SequenceEqual(alerts)) return;
            state.Databases[index] = state.Databases[index] with { Alerts = alerts };
            Save(project, state);
        }
    }

    /// <summary>
    /// Liveness of a database as the control plane sees it (AGT-3878, the
    /// AGT-3223 <c>database</c> check). The database publishes no port, so the
    /// signal is the host's report: a database is up while the host reports it
    /// ready, and down when the report says otherwise or stops arriving.
    /// </summary>
    public HealthSample DatabaseLivenessSample(string project, string environmentId, string name, DateTimeOffset now)
    {
        ManagedDatabaseRecord? record;
        lock (_gate)
        {
            record = TryLoad(project)?.Databases.LastOrDefault(item => item.EnvironmentId == environmentId && item.Name == name
                && item.DeletedAt is null);
        }
        if (record is null || !record.Declared || record.RunningSince is null && record.State == "provisioning")
            return new(now, false, null, null, HealthSample.NotApplicable);
        if (record.ObservedAt is not { } seen || now.UtcDateTime - seen > DatabaseReportStale)
            return new(now, false, null, null, "database-report-stale");
        if (record.State == "failed") return new(now, false, null, null, "database-failed");
        if (record.State == "unhealthy" || record.Ready == false) return new(now, false, null, null, "database-not-ready");
        return new(now, true, null, null, null);
    }

    /// <summary>The host reports every poll and Studio keeps an unchanged report at least every minute.</summary>
    public static readonly TimeSpan DatabaseReportStale = TimeSpan.FromMinutes(3);

    private static bool DeployOpen(State state, string environmentId)
        => state.Jobs.Any(job => job.EnvironmentId == environmentId && job.Status is "queued" or "claimed");

    /// <summary>
    /// Records an owner's confirmed request to delete a database's data. Only a
    /// database that neither the running deploy nor an open one declares can go;
    /// the host drops the volume on its next poll and confirms with <c>deleted</c>.
    /// </summary>
    public ManagedDatabaseDeletionOutcome RequestDatabaseDeletion(string project, string environmentId, string name,
        string actor)
    {
        if (!IsWatched(project)) return ManagedDatabaseDeletionOutcome.NotFound;
        lock (_gate)
        {
            if (TryLoad(project) is not { } state) return ManagedDatabaseDeletionOutcome.NotFound;
            var record = state.Databases.LastOrDefault(item => item.EnvironmentId == environmentId && item.Name == name);
            var deployed = state.Jobs.LastOrDefault(job => job.EnvironmentId == environmentId && job.Status == "healthy");
            var declared = state.Jobs.Where(job => job.EnvironmentId == environmentId && job.Status is "queued" or "claimed")
                .Append(deployed).Any(job => job?.Databases.Any(item => item.Name == name) == true);
            var (outcome, updated) = ManagedDatabasePolicy.RequestDeletion(record, declared, actor,
                _clock.GetUtcNow().UtcDateTime);
            if (outcome == ManagedDatabaseDeletionOutcome.Requested)
            {
                state.Databases[state.Databases.IndexOf(record!)] = updated!;
                Save(project, state);
            }
            return outcome;
        }
    }

    /// <summary>
    /// Credentials the databases of this host's environments need: every
    /// database of an open deploy job or of the running deploy. Studio
    /// generates a missing one before it answers the host's poll.
    /// </summary>
    public IReadOnlyList<ManagedDatabaseCredentialNeed> ManagedDatabaseCredentialNeeds(string hostId)
    {
        var needs = new List<ManagedDatabaseCredentialNeed>();
        if (!Identifier.IsMatch(hostId)) return needs;
        foreach (var project in _scanner.GetWatchPaths().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var (declarations, _, _) = ReadRunTargetDeclarations(project);
            foreach (var declaration in declarations?.Where(item => item.TargetHostId == hostId && item.Hosting is not null) ?? [])
            {
                if (ManagedDatabaseCredentialLease(project, declaration) is not { } lease) continue;
                needs.AddRange(lease.Names.Select(name => new ManagedDatabaseCredentialNeed(project, declaration.Id,
                    declaration.Kind, name)));
            }
        }
        return needs;
    }

    /// <summary>
    /// The databases a run host may read credentials for: those of the open or
    /// running deploy of an environment placed on it. Null when the environment
    /// is not hosted in the run zone or has no database.
    /// </summary>
    public ManagedDatabaseCredentialLease? ManagedDatabaseCredentialLease(string project, string environmentId)
    {
        var (declarations, _, _) = ReadRunTargetDeclarations(project);
        var declaration = declarations?.FirstOrDefault(item => item.Id == environmentId && item.Hosting is not null);
        return declaration is null ? null : ManagedDatabaseCredentialLease(project, declaration);
    }

    private ManagedDatabaseCredentialLease? ManagedDatabaseCredentialLease(string project, EnvironmentDeclaration declaration)
    {
        lock (_gate)
        {
            if (TryLoad(project) is not { } state) return null;
            var names = state.Jobs.Where(job => job.EnvironmentId == declaration.Id && job.HostId == declaration.TargetHostId
                    && job.Status is "queued" or "claimed")
                .Append(state.Jobs.LastOrDefault(job => job.EnvironmentId == declaration.Id && job.Status == "healthy"))
                .SelectMany(job => job?.Databases ?? []).Select(item => item.Name)
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
            return names.Length == 0 ? null
                : new(project, declaration.Id, declaration.Kind, declaration.TargetHostId, names);
        }
    }

    /// <summary>Deleted databases whose generated credential Studio has not removed yet.</summary>
    public IReadOnlyList<ManagedDatabaseCredentialNeed> RetiringDatabaseCredentials()
    {
        var result = new List<ManagedDatabaseCredentialNeed>();
        foreach (var project in _scanner.GetWatchPaths().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            lock (_gate)
            {
                result.AddRange((TryLoad(project)?.Databases ?? [])
                    .Where(item => item.DeletedAt is not null && !item.CredentialRetired)
                    .Select(item => new ManagedDatabaseCredentialNeed(project, item.EnvironmentId, "", item.Name)));
            }
        }
        return result;
    }

    public void MarkDatabaseCredentialRetired(string project, string environmentId, string name)
    {
        lock (_gate)
        {
            if (TryLoad(project) is not { } state) return;
            var index = state.Databases.FindLastIndex(item => item.EnvironmentId == environmentId && item.Name == name
                && item.DeletedAt is not null);
            if (index < 0) return;
            state.Databases[index] = state.Databases[index] with { CredentialRetired = true };
            Save(project, state);
        }
    }

    /// <summary>
    /// Refuses an open deploy job that changes the major version of data the
    /// host holds. A queued job never reached the host and simply fails; a
    /// claimed one completes as failed, which queues the usual rollback.
    /// </summary>
    private void RefuseMajorDatabaseChange(RunTargetProjectRead read, EnvironmentDeclaration declaration, string hostId,
        DateTime now)
    {
        var state = read.State;
        var job = state.Jobs.FirstOrDefault(item => item.EnvironmentId == declaration.Id && item.HostId == hostId
            && item.Status is "queued" or "claimed");
        if (job is null || job.Databases.Count == 0) return;
        var refusal = ManagedDatabasePolicy.MajorChangeRefusal(job.Databases, state.Databases, declaration.Id);
        if (refusal is null) return;
        for (var index = 0; index < state.Databases.Count; index++)
        {
            var record = state.Databases[index];
            if (record.EnvironmentId == declaration.Id && record.DeletedAt is null
                && job.Databases.Any(item => item.Name == record.Name))
                state.Databases[index] = record with { Message = refusal };
        }
        if (job.Status == "claimed")
        {
            Complete(read.Project, state, job, success: false, "deploy-step-failed", () => read.Declarations);
            return;
        }
        state.Jobs[state.Jobs.IndexOf(job)] = job with
        { Status = "failed", LeaseToken = null, FinishedAt = now, Evidence = "deploy-step-failed" };
        state.History.Add(new(job.EnvironmentId, now, new(now, "failed", job.Artifact, job.PreviousArtifact, "deploy-step-failed")));
        if (!job.GuidedFailure) ClosePromotionForResult(state, job, false, now);
    }

    /// <summary>
    /// Updates the records of one hosted environment from the snapshot Studio
    /// sends, and retains the databases of this project's environments that are
    /// no longer hosted on this host.
    /// </summary>
    private static void TrackDatabases(State state, string hostId, IReadOnlyList<(string EnvironmentId,
        IReadOnlyList<ProjectDatabaseDefinition> Databases)> hosted)
    {
        foreach (var (environmentId, databases) in hosted)
            state.Databases = ManagedDatabasePolicy.Track(state.Databases, environmentId, hostId, databases);
        for (var index = 0; index < state.Databases.Count; index++)
        {
            var record = state.Databases[index];
            if (record.HostId == hostId && record.Declared && !hosted.Any(item => item.EnvironmentId == record.EnvironmentId))
                state.Databases[index] = record with { Declared = false };
        }
    }

    private static IEnumerable<RunTargetDatabaseDeletion> DatabaseDeletions(string project, State state, string hostId)
        => state.Databases.Where(item => item.HostId == hostId && item.DeletionRequestedAt is not null && item.DeletedAt is null)
            .Select(item => new RunTargetDatabaseDeletion(project, item.EnvironmentId, item.Name));

    /// <summary>Queued operations of this host's databases; an operation past its time fails first.</summary>
    private static IEnumerable<RunTargetDatabaseOperation> DatabaseOperations(string project, State state, string hostId,
        IReadOnlyCollection<string> hosted, DateTime now)
    {
        ManagedDatabaseOperationPolicy.Expire(state.DatabaseOperations, now);
        return state.DatabaseOperations.Where(item => item.State == "queued" && item.HostId == hostId && hosted.Contains(item.EnvironmentId))
            .Select(item => ManagedDatabaseOperationPolicy.Wire(project, item)).ToArray();
    }

    /// <summary>Ends the queued operations the host answered.</summary>
    private int RecordDatabaseOperationResults(string hostId, IReadOnlyList<RunTargetDatabaseOperationResult> results, DateTime now)
    {
        var accepted = 0;
        foreach (var group in results.Where(item => item is not null).GroupBy(item => item.Project, StringComparer.Ordinal))
        {
            if (TryLoad(group.Key) is not { } state) continue;
            var changed = false;
            foreach (var result in group)
            {
                var index = state.DatabaseOperations.FindIndex(item => item.Id == result.Id);
                if (index < 0 || ManagedDatabaseOperationPolicy.Complete(state.DatabaseOperations[index], result, hostId, now)
                    is not { } completed) continue;
                state.DatabaseOperations[index] = completed;
                changed = true;
                accepted++;
            }
            if (changed) Save(group.Key, state);
        }
        return accepted;
    }

    /// <summary>
    /// Keeps the host's sanitized database reports. A report for a database
    /// Studio has no record of, on an environment placed on this host, creates a
    /// retained record, so data Studio lost track of stays visible and protected.
    /// </summary>
    private int RecordDatabaseObservations(string hostId, IReadOnlyList<RunTargetDatabaseObservation> observations,
        DateTime now, ICollection<(string Project, ManagedDatabaseRecord Record)>? measured)
    {
        var accepted = 0;
        foreach (var group in observations.GroupBy(item => item.Project, StringComparer.Ordinal))
        {
            var (declarations, _, _) = ReadRunTargetDeclarations(group.Key);
            if (TryLoad(group.Key) is not { } state) continue;
            var changed = false;
            foreach (var observation in group)
            {
                if (ManagedDatabasePolicy.Sanitize(observation, now) is not { } sanitized) continue;
                var index = state.Databases.FindLastIndex(item => item.EnvironmentId == sanitized.EnvironmentId
                    && item.Name == sanitized.Name && item.HostId == hostId && item.DeletedAt is null);
                if (index < 0)
                {
                    var placed = declarations?.Any(item => item.Id == sanitized.EnvironmentId && item.TargetHostId == hostId
                        && item.Hosting is not null) == true;
                    if (!placed || sanitized.State == "deleted") continue;
                    state.Databases.Add(new(sanitized.EnvironmentId, sanitized.Name, hostId)
                        { Version = sanitized.Version ?? "", State = sanitized.State });
                    index = state.Databases.Count - 1;
                }
                var updated = ManagedDatabasePolicy.Observe(state.Databases[index], sanitized, now);
                accepted++;
                if (updated == state.Databases[index]) continue;
                state.Databases[index] = updated;
                measured?.Add((group.Key, updated));
                changed = true;
            }
            if (changed) Save(group.Key, state);
        }
        return accepted;
    }
}
