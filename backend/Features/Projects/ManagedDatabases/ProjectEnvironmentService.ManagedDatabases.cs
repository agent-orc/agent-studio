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
        lock (_gate)
        {
            var records = (TryLoad(project)?.Databases ?? []).Where(item => item.EnvironmentId == environmentId).ToArray();
            var live = records.Where(item => item.DeletedAt is null).Select(item => item.Name).ToHashSet(StringComparer.Ordinal);
            return records.OrderBy(item => item.DeletedAt is null ? 0 : 1).ThenBy(item => item.Name, StringComparer.Ordinal)
                .Select(item => ManagedDatabasePolicy.View(project, item))
                .Concat((file.Declaration?.Databases ?? []).Where(item => !live.Contains(item.Name))
                    .Select(item => ManagedDatabasePolicy.Pending(project, environmentId, item)))
                .ToArray();
        }
    }

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

    /// <summary>
    /// Keeps the host's sanitized database reports. A report for a database
    /// Studio has no record of, on an environment placed on this host, creates a
    /// retained record, so data Studio lost track of stays visible and protected.
    /// </summary>
    private int RecordDatabaseObservations(string hostId, IReadOnlyList<RunTargetDatabaseObservation> observations,
        DateTime now)
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
                changed = true;
            }
            if (changed) Save(group.Key, state);
        }
        return accepted;
    }
}
