using System.Text.Json;

namespace AgentStudio.Projects;

// IP-1 run-target protocol on top of the deploy queue. Both routes run under
// the queue's gate. A project Studio cannot read completely contributes
// neither specs nor removals: the host keeps its apps exactly as they are.
public sealed partial class ProjectEnvironmentService
{
    /// <summary>
    /// Desired state of every edge-hosted environment placed on this host. An
    /// open deploy job is claimed, or its lease renewed, by the same read, so a
    /// polling host keeps the job while it converges. Unreadable projects are
    /// listed in <see cref="RunTargetDesiredState.Skipped"/>; an environment
    /// leaves the host only through <see cref="RunTargetDesiredState.Removals"/>.
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
                var (declarations, reason) = ReadRunTargetDeclarations(project);
                if (declarations is null)
                {
                    skipped.Add(new(project, reason ?? "declarations-missing"));
                    continue;
                }
                if (TryLoad(project) is not { } state)
                {
                    skipped.Add(new(project, "state-unreadable"));
                    continue;
                }
                var before = JsonSerializer.Serialize(state, _json);
                AdvanceTimedPromotions(project, declarations, state);
                var hosted = declarations.Where(item => item.TargetHostId == hostId && item.Hosting is not null).ToArray();
                var placement = RunTargetDesiredStatePolicy.Place(state.Placements, hostId,
                    hosted.Select(item => item.Id).ToArray(), now);
                state.Placements = [.. placement.Placements];
                removals.AddRange(placement.Removals.Select(id => new RunTargetRemoval(project, id)));
                projects.Add(new(project, declarations, hosted, state, before));
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
                projects.SelectMany(read => read.Hosted.Select(item =>
                    new RunTargetDomainCandidate(read.Project, item.Id, item.Hosting!.Domains))).ToArray(),
                UnseenOwnerKeeps, now);

            var environments = new List<RunTargetEnvironmentSpec>();
            foreach (var read in projects)
            {
                foreach (var declaration in read.Hosted)
                {
                    var job = LeaseJob(read, declaration, hostId, now);
                    var history = read.State.History.Where(row => row.EnvironmentId == declaration.Id).Select(row => row.Item).ToArray();
                    environments.Add(RunTargetDesiredStatePolicy.ApplyClaims(
                        RunTargetDesiredStatePolicy.Project(read.Project, declaration, job, history), claims));
                }
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
    /// host, ends removal signals the host confirms, and queues a rollback when
    /// the host rolled back from a crash-looping release.
    /// </summary>
    public int RecordObserved(string hostId, RunTargetObservedReport report)
    {
        lock (_gate)
        {
            var now = _clock.GetUtcNow().UtcDateTime;
            var accepted = 0;
            foreach (var group in (report.Environments ?? []).GroupBy(item => item.Project, StringComparer.Ordinal))
            {
                var (declarations, reason) = ReadRunTargetDeclarations(group.Key);
                if (declarations is null && reason is null) continue;
                if (TryLoad(group.Key) is not { } state) continue;
                var before = JsonSerializer.Serialize(state, _json);
                foreach (var observation in group)
                {
                    if (observation.State == "removed")
                    {
                        var placement = state.Placements.FirstOrDefault(item => item.HostId == hostId
                            && item.EnvironmentId == observation.EnvironmentId);
                        if (placement is null || !RunTargetDesiredStatePolicy.AcceptsRemoval(placement, now)) continue;
                        state.Placements.Remove(placement);
                        state.Observed.RemoveAll(row => row.EnvironmentId == observation.EnvironmentId);
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
    /// The declarations of a complete read. Otherwise null with the skip
    /// reason, or with a null reason when Studio does not know the project.
    /// </returns>
    private (IReadOnlyList<EnvironmentDeclaration>? Declarations, string? Skip) ReadRunTargetDeclarations(string project)
    {
        try
        {
            var (source, declarations) = ReadDeclarations(project);
            return source switch
            {
                DeclarationSource.Read => (declarations, null),
                DeclarationSource.CheckoutMissing => (null, "checkout-missing"),
                DeclarationSource.FolderMissing => (null, "declarations-missing"),
                _ => (null, null),
            };
        }
        catch (Exception ex) when (ex is JsonException or InvalidOperationException or KeyNotFoundException or FormatException)
        {
            // The project's environment page reports the parse error.
            return (null, "declaration-invalid");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return (null, "declaration-unreadable");
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

    private static void WriteStoreFile(string path, string content)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        File.WriteAllText(temporary, content);
        File.Move(temporary, path, true);
    }
}
