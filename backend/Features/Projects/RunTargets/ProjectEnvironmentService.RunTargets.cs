using System.Text.Json;

namespace AgentStudio.Projects;

// IP-1 run-target protocol on top of the deploy queue. Both routes run under
// the queue's gate. A project Studio cannot read completely contributes
// neither specs nor removals: the host keeps its apps exactly as they are.
// A production or stage environment leaves its host only after an operator
// confirms the pending removal or the repository holds a tombstone for it.
public sealed partial class ProjectEnvironmentService
{
    /// <summary>
    /// Desired state of every edge-hosted environment placed on this host. An
    /// open deploy job is claimed, or its lease renewed, by the same read, so a
    /// polling host keeps the job while it converges. Unreadable projects with
    /// a placement on this host are listed in <see cref="RunTargetDesiredState.Skipped"/>;
    /// an environment leaves the host only through <see cref="RunTargetDesiredState.Removals"/>.
    /// A domain is offered only under the project's operator-owned suffixes.
    /// </summary>
    public RunTargetDesiredState DesiredState(string hostId)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            if (!Identifier.IsMatch(hostId)) return new(hostId, now, [], [], []);
            var skipped = new List<RunTargetSkip>();
            var removals = new List<RunTargetRemoval>();
            var projects = new List<RunTargetProjectRead>();
            foreach (var project in _scanner.GetWatchPaths().Select(item => item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var (declarations, tombstones, reason) = ReadRunTargetDeclarations(project);
                var state = TryLoad(project);
                if (declarations is null || state is null)
                {
                    // Only a host the project is placed on hears about it. With
                    // unreadable state the placements are unknown, and the host
                    // keeps its apps all the same.
                    if (state?.Placements.Any(item => item.HostId == hostId) == true)
                        skipped.Add(new(project, reason ?? "declarations-missing"));
                    continue;
                }
                var before = JsonSerializer.Serialize(state, _json);
                AdvanceQueue(state);
                var hosted = declarations.Where(item => item.TargetHostId == hostId && item.Hosting is not null).ToArray();
                var placement = RunTargetDesiredStatePolicy.Place(state.Placements, hostId,
                    hosted.Select(item => new RunTargetHostedEnvironment(item.Id, item.Kind)).ToArray(), tombstones, now);
                state.Placements = [.. placement.Placements];
                removals.AddRange(placement.Removals.Select(id => new RunTargetRemoval(project, id)));
                projects.Add(new(project, declarations, hosted, state, before));
            }

            // Lease before claiming domains: the open job's approved snapshot,
            // or the last healthy job's snapshot, owns the running routes.
            var projected = new List<(RunTargetProjectRead Read, RunTargetEnvironmentSpec Spec)>();
            foreach (var read in projects)
            {
                foreach (var declaration in read.Hosted)
                {
                    var job = LeaseJob(read, declaration, hostId, now);
                    var history = read.State.History.Where(row => row.EnvironmentId == declaration.Id)
                        .Select(row => row.Item).ToArray();
                    var settled = history.LastOrDefault(item => item.Status is "healthy" or "rolled-back");
                    var deployedJob = settled is null ? null : read.State.Jobs.LastOrDefault(item =>
                        item.EnvironmentId == declaration.Id && item.Artifact == settled.Artifact && item.Status == "healthy");
                    projected.Add((read, RunTargetDesiredStatePolicy.Project(read.Project, declaration, job, history, deployedJob)));
                }
            }

            // An owner Studio does not see as hosted keeps its domains while the
            // host may still route them: its project was skipped or is gone from
            // Studio, or its removal has not been confirmed yet.
            bool UnseenOwnerKeeps(RunTargetDomainClaim claim)
            {
                var owner = projects.FirstOrDefault(item => string.Equals(item.Project, claim.Project, StringComparison.OrdinalIgnoreCase));
                return owner is null || owner.State.Placements.Any(item => item.HostId == hostId && item.EnvironmentId == claim.EnvironmentId);
            }
            var claimsPath = DomainClaimsPath(hostId);
            var storedClaims = File.Exists(claimsPath) ? File.ReadAllText(claimsPath) : null;
            // A corrupt claims file fails the poll instead of resetting ownership.
            var claims = RunTargetDesiredStatePolicy.ClaimDomains(
                (storedClaims is null ? null : JsonSerializer.Deserialize<Dictionary<string, RunTargetDomainClaim>>(storedClaims, _json))
                    ?? new Dictionary<string, RunTargetDomainClaim>(),
                projected.Select(item => new RunTargetDomainCandidate(item.Spec.Project, item.Spec.EnvironmentId,
                    RunTargetDesiredStatePolicy.AllowedDomains(item.Spec.Domains,
                        _settings.Get(item.Spec.Project).RunTargetDomainSuffixes))).ToArray(),
                UnseenOwnerKeeps, now);

            var environments = new List<RunTargetEnvironmentSpec>();
            foreach (var item in projected)
            {
                var allowed = RunTargetDesiredStatePolicy.AllowedDomains(item.Spec.Domains,
                    _settings.Get(item.Spec.Project).RunTargetDomainSuffixes);
                var restricted = item.Spec with { Domains = allowed,
                    RefusedDomains = item.Spec.Domains.Except(allowed, StringComparer.Ordinal).ToArray() };
                var claimed = RunTargetDesiredStatePolicy.ApplyClaims(restricted, claims);
                environments.Add(claimed with { RefusedDomains = restricted.RefusedDomains!
                    .Concat(claimed.RefusedDomains ?? []).ToArray() });
            }
            foreach (var read in projects)
            {
                if (JsonSerializer.Serialize(read.State, _json) != read.Before) Save(read.Project, read.State);
            }
            var claimsText = JsonSerializer.Serialize(claims, _json);
            if (claimsText != (storedClaims ?? JsonSerializer.Serialize(new Dictionary<string, RunTargetDomainClaim>(), _json)))
                WriteStoreFile(claimsPath, claimsText);
            return new(hostId, now, environments, skipped, removals);
        }
    }

    /// <summary>
    /// Keeps the latest sanitized observation per environment placed on this
    /// host, ends removal signals the host confirms (keeping a record with the
    /// decision and its actor), and queues a rollback when the host rolled back
    /// from a crash-looping release.
    /// </summary>
    public int RecordObserved(string hostId, RunTargetObservedReport report)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var accepted = 0;
            foreach (var group in (report.Environments ?? []).GroupBy(item => item.Project, StringComparer.Ordinal))
            {
                var (declarations, _, reason) = ReadRunTargetDeclarations(group.Key);
                if (declarations is null && reason is null) continue;
                if (TryLoad(group.Key) is not { } state) continue;
                var before = JsonSerializer.Serialize(state, _json);
                foreach (var observation in group)
                {
                    if (observation.State == "removed")
                    {
                        var placement = state.Placements.FirstOrDefault(item => item.HostId == hostId
                            && item.EnvironmentId == observation.EnvironmentId);
                        if (placement is null || !RunTargetDesiredStatePolicy.AcceptsRemoval(placement)) continue;
                        state.Placements.Remove(placement);
                        state.Observed.RemoveAll(row => row.EnvironmentId == observation.EnvironmentId);
                        state.RunTargetRemovals.Add(RunTargetDesiredStatePolicy.Removed(placement, now));
                        if (state.RunTargetRemovals.Count > RunTargetDesiredStatePolicy.MaxRemovalRecords)
                            state.RunTargetRemovals.RemoveRange(0,
                                state.RunTargetRemovals.Count - RunTargetDesiredStatePolicy.MaxRemovalRecords);
                        accepted++;
                        continue;
                    }
                    // Without a valid declaration the host cannot be checked.
                    var declaration = declarations?.FirstOrDefault(item => item.Id == observation.EnvironmentId);
                    if (declaration?.Hosting is null || declaration.TargetHostId != hostId) continue;
                    var sanitized = RunTargetDesiredStatePolicy.Sanitize(observation, now);
                    if (sanitized is null) continue;
                    accepted++;
                    var stored = state.Observed.LastOrDefault(row => row.EnvironmentId == declaration.Id)?.Observation;
                    if (RunTargetDesiredStatePolicy.Supersedes(stored, sanitized))
                    {
                        state.Observed.RemoveAll(row => row.EnvironmentId == declaration.Id);
                        state.Observed.Add(new(declaration.Id, sanitized));
                    }
                    var deployed = state.History.LastOrDefault(row => row.EnvironmentId == declaration.Id)?.Item.Artifact;
                    if (RunTargetDesiredStatePolicy.NewCrashRollback(stored, sanitized, deployed)
                        && !state.Jobs.Any(job => job.EnvironmentId == declaration.Id && job.Status is "queued" or "claimed"))
                        QueueRollback(group.Key, declaration, state, "crash-rollback");
                }
                if (JsonSerializer.Serialize(state, _json) != before) Save(group.Key, state);
            }
            return accepted;
        }
    }

    /// <summary>
    /// Records the operator's decision to take a pending environment off its
    /// host (one host, or every host it is pending on). False when nothing is
    /// pending: the environment is still declared there, or already removing.
    /// </summary>
    public bool ConfirmRunTargetRemoval(string project, string environmentId, string? hostId, string actor)
    {
        if (!_scanner.GetWatchPaths().Any(item => string.Equals(item.Name, project, StringComparison.OrdinalIgnoreCase)))
            return false;
        lock (_gate)
        {
            if (TryLoad(project) is not { } state) return false;
            var now = _clock.GetUtcNow().UtcDateTime;
            var confirmed = false;
            for (var index = 0; index < state.Placements.Count; index++)
            {
                var placement = state.Placements[index];
                if (placement.EnvironmentId != environmentId || hostId is not null && placement.HostId != hostId) continue;
                if (RunTargetDesiredStatePolicy.ConfirmRemoval(placement, actor, now) is not { } decided) continue;
                state.Placements[index] = decided;
                confirmed = true;
            }
            if (confirmed) Save(project, state);
            return confirmed;
        }
    }

    private sealed record RunTargetProjectRead(string Project, IReadOnlyList<EnvironmentDeclaration> Declarations,
        EnvironmentDeclaration[] Hosted, State State, string Before);

    private DeployJob? LeaseJob(RunTargetProjectRead read, EnvironmentDeclaration declaration, string hostId, DateTime now)
    {
        var state = read.State;
        var job = state.Jobs.FirstOrDefault(item => item.EnvironmentId == declaration.Id
            && item.HostId == hostId && item.Status is "queued" or "claimed");
        if (job is null) return null;
        var action = RunTargetDesiredStatePolicy.Lease(job, now);
        if (action == RunTargetLeaseAction.Expire)
        {
            // A failed deploy step: the usual automatic rollback is queued and handed out on the next poll.
            Complete(read.Project, state, job, success: false, "deploy-step-failed", () => read.Declarations);
            return null;
        }
        var leased = job with
        {
            Status = "claimed",
            ClaimedAt = now,
            LeaseToken = action == RunTargetLeaseAction.Renew ? job.LeaseToken : Guid.NewGuid().ToString("N"),
            FirstClaimedAt = job.FirstClaimedAt ?? now,
        };
        state.Jobs[state.Jobs.IndexOf(job)] = leased;
        return leased;
    }

    /// <returns>
    /// The declarations and tombstoned ids of a complete read. Otherwise null
    /// with the skip reason, or with a null reason when Studio does not know
    /// the project.
    /// </returns>
    private (IReadOnlyList<EnvironmentDeclaration>? Declarations, IReadOnlySet<string> Tombstones, string? Skip)
        ReadRunTargetDeclarations(string project)
    {
        var none = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            var (source, declarations, tombstones) = ReadDeclarations(project);
            return source switch
            {
                DeclarationSource.Read => (declarations, tombstones, null),
                DeclarationSource.CheckoutMissing => (null, none, "checkout-missing"),
                DeclarationSource.FolderMissing => (null, none, "declarations-missing"),
                _ => (null, none, null),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // The project's environment page reports the parse error.
            return (null, none, "declaration-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, none, "declaration-unreadable");
        }
    }

    private State? TryLoad(string project)
    {
        try
        {
            return Load(project);
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private string DomainClaimsPath(string hostId)
        => Path.Combine(StoreRoot(), "run-targets", SafeProjectName(hostId) + ".json");

    /// <summary>Flushes to disk before the rename, so a crash leaves the old claims or the new ones, never a torn file.</summary>
    private static void WriteStoreFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write))
        {
            stream.Write(System.Text.Encoding.UTF8.GetBytes(content));
            stream.Flush(flushToDisk: true);
        }
        File.Move(temporary, path, true);
    }
}
