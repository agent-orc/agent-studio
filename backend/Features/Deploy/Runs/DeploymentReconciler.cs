namespace AgentStudio.Deploy;

public sealed record ReconcileInput(
    IReadOnlyList<DeployEnvironment> Environments,
    IReadOnlyList<ReleaseRecord> Releases,
    IReadOnlyList<DeploymentRecord> Deployments,
    IReadOnlyList<DeployJob> Jobs,
    IReadOnlyList<EnvironmentPromotion> Promotions);

public sealed record ReconcileResult(
    IReadOnlyList<DeploymentRecord> Deployments,
    IReadOnlyList<DeploymentLogEntry> Log,
    IReadOnlyList<DeployAuditEntry> Audit,
    bool Changed);

/// <summary>
/// Folds the AGT-3176 deploy queue (jobs and promotions) into deployment runs.
/// Deterministic and idempotent: the same input yields the same runs, and a
/// second pass over its own output yields no log or audit entries. Jobs that
/// the queue starts on its own (gate staging, timed promotion, automatic
/// rollback, the older promotion routes) are adopted here, so every path ends
/// in a run with steps, log and audit. Each reclaim by a restarted host and
/// each lost host is logged.
/// </summary>
public static class DeploymentReconciler
{
    public const string LifecycleActor = "lifecycle";
    public const string HostActor = "deploy-host";

    public static ReconcileResult Reconcile(ReconcileInput input)
    {
        var runs = input.Deployments.ToList();
        var log = new List<DeploymentLogEntry>();
        var audit = new List<DeployAuditEntry>();
        var environments = input.Environments.ToDictionary(item => item.Id, StringComparer.Ordinal);
        var releases = input.Releases.ToDictionary(item => item.Release.Id, item => item.Release, StringComparer.Ordinal);

        AdoptJobs(input, runs, environments, log, audit);
        FollowJobs(input, runs, releases, environments, log, audit);
        RequestPromotionApprovals(input, runs, environments, log, audit);
        CloseDecidedPromotions(input, runs, releases, log, audit);

        for (var i = 0; i < runs.Count; i++)
        {
            var steps = DeploymentStepPlan.Build(runs[i]);
            if (!steps.SequenceEqual(runs[i].Steps)) runs[i] = runs[i] with { Steps = steps };
        }
        return new(runs, log, audit, log.Count > 0 || audit.Count > 0 || !runs.SequenceEqual(input.Deployments));
    }

    public static string StateForJob(string jobStatus) => jobStatus switch
    {
        "claimed" => DeploymentStates.Running,
        "healthy" => DeploymentStates.Succeeded,
        "failed" => DeploymentStates.Failed,
        "cancelled" => DeploymentStates.Cancelled,
        _ => DeploymentStates.Queued,
    };

    /// <summary>The newest release that carries the artifact and existed when the work was queued.</summary>
    public static Release? ReleaseForArtifact(IEnumerable<Release> releases, string artifact, DateTime notAfterUtc)
        => releases.Where(item => item.ArtifactDigests.Contains(artifact, StringComparer.Ordinal)
                && item.CreatedAtUtc <= notAfterUtc)
            .OrderBy(item => item.CreatedAtUtc).ThenBy(item => item.Id, StringComparer.Ordinal)
            .LastOrDefault();

    private static void AdoptJobs(ReconcileInput input, List<DeploymentRecord> runs,
        IReadOnlyDictionary<string, DeployEnvironment> environments,
        List<DeploymentLogEntry> log, List<DeployAuditEntry> audit)
    {
        var linked = runs.Where(item => item.JobId is not null).Select(item => item.JobId!).ToHashSet(StringComparer.Ordinal);
        // A stop job ends an environment; it is not a deployment of a release.
        foreach (var job in input.Jobs.Where(item => !item.IsStop && !linked.Contains(item.Id)).OrderBy(item => item.CreatedAt))
        {
            if (!environments.TryGetValue(job.EnvironmentId, out var environment)) continue;
            // Jobs from before the release existed (pre-BD-1 history) stay unattributed.
            var release = ReleaseForArtifact(input.Releases.Select(item => item.Release), job.Artifact, job.CreatedAt);
            if (release is null) continue;
            var actor = job.RequestedBy ?? LifecycleActor;
            var waiting = job.IsRollback ? null : runs.LastOrDefault(item => item.EnvironmentId == environment.Id
                && item.ReleaseId == release.Id && item.JobId is null && item.State == DeploymentStates.AwaitingApproval
                && item.Trigger == DeploymentTriggers.Deploy);
            if (waiting is not null)
            {
                var index = runs.IndexOf(waiting);
                runs[index] = waiting with
                {
                    JobId = job.Id, State = DeploymentStates.Queued,
                    ApprovedBy = waiting.ApprovedBy ?? actor, ApprovedAtUtc = waiting.ApprovedAtUtc ?? job.CreatedAt,
                };
                if (waiting.ApprovedBy is null)
                {
                    log.Add(new(waiting.Id, DeployStepNames.Approval, job.CreatedAt, $"Approved by {actor}."));
                    audit.Add(new(job.CreatedAt, actor, "deployment-approved", release.Id, waiting.Id, environment.Id,
                        $"Release {release.Version} approved for {environment.Name}."));
                }
                log.Add(new(waiting.Id, waiting.DeployStep, job.CreatedAt, $"Queued for deploy host {job.HostId}."));
                linked.Add(job.Id);
                continue;
            }

            var trigger = !job.IsRollback ? DeploymentTriggers.Deploy
                : actor == "automatic-rollback" ? DeploymentTriggers.AutomaticRollback : DeploymentTriggers.Rollback;
            var needsApproval = StandardLifecyclePolicy.RequiresApproval(environment)
                && trigger != DeploymentTriggers.AutomaticRollback;
            var run = new DeploymentRecord
            {
                Id = job.Id, ReleaseId = release.Id, EnvironmentId = environment.Id,
                State = DeploymentStates.Queued, Trigger = trigger,
                DeployStep = DeployStepNames.ForTemplate(job.Template), NeedsApproval = needsApproval,
                RequestedBy = actor, ApprovedBy = needsApproval ? actor : null,
                ApprovedAtUtc = needsApproval ? job.CreatedAt : null, JobId = job.Id, CreatedAtUtc = job.CreatedAt,
            };
            runs.Add(run);
            linked.Add(job.Id);
            if (needsApproval)
                log.Add(new(run.Id, DeployStepNames.Approval, job.CreatedAt, $"Approved by {actor}."));
            log.Add(new(run.Id, run.DeployStep, job.CreatedAt, trigger == DeploymentTriggers.AutomaticRollback
                ? $"Automatic rollback to release {release.Version} queued after a failed deployment."
                : $"Release {release.Version} queued for {environment.Name} by {actor}."));
            audit.Add(new(job.CreatedAt, actor, trigger == DeploymentTriggers.Deploy ? "deployment-queued" : "rollback-queued",
                release.Id, run.Id, environment.Id, $"Release {release.Version} queued for {environment.Name}."));
        }
    }

    private static void RequestPromotionApprovals(ReconcileInput input, List<DeploymentRecord> runs,
        IReadOnlyDictionary<string, DeployEnvironment> environments,
        List<DeploymentLogEntry> log, List<DeployAuditEntry> audit)
    {
        foreach (var promotion in input.Promotions.Where(item => item.Status == "pending"))
        {
            if (!environments.TryGetValue(promotion.ProductionId, out var environment)) continue;
            var record = input.Releases.Where(item => item.Release.ArtifactDigests.Contains(promotion.Artifact, StringComparer.Ordinal)
                    && item.Release.CreatedAtUtc <= promotion.StageHealthyAt)
                .OrderBy(item => item.Release.CreatedAtUtc).ThenBy(item => item.Release.Id, StringComparer.Ordinal)
                .LastOrDefault();
            if (record is null) continue;
            var release = record.Release;
            // Any run for this release here since staging turned healthy answers
            // the promotion already, including a cancelled one.
            if (runs.Any(item => item.EnvironmentId == environment.Id && item.ReleaseId == release.Id
                    && (DeploymentStates.IsActive(item.State) || item.CreatedAtUtc >= promotion.StageHealthyAt)))
                continue;
            var run = new DeploymentRecord
            {
                Id = DeployIds.For($"{release.Id}|{environment.Id}|{promotion.StageHealthyAt.Ticks}"),
                ReleaseId = release.Id, EnvironmentId = environment.Id, State = DeploymentStates.AwaitingApproval,
                Trigger = DeploymentTriggers.Deploy, DeployStep = StepFor(record, environment.Id), NeedsApproval = true,
                RequestedBy = LifecycleActor, PromotionStageHealthyAtUtc = promotion.StageHealthyAt,
                CreatedAtUtc = promotion.StageHealthyAt,
            };
            runs.Add(run);
            log.Add(new(run.Id, DeployStepNames.Approval, run.CreatedAtUtc,
                $"Staging succeeded. Waiting for approval by {StandardLifecyclePolicy.ApprovalRoles}"
                + (promotion.ExpiresAt is { } expires ? $" until {expires:yyyy-MM-dd HH:mm} UTC." : ".")));
            audit.Add(new(run.CreatedAtUtc, LifecycleActor, "approval-requested", release.Id, run.Id, environment.Id,
                $"Release {release.Version} awaits approval for {environment.Name}."));
        }
    }

    private static void CloseDecidedPromotions(ReconcileInput input, List<DeploymentRecord> runs,
        IReadOnlyDictionary<string, Release> releases, List<DeploymentLogEntry> log, List<DeployAuditEntry> audit)
    {
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.State != DeploymentStates.AwaitingApproval || run.JobId is not null
                || run.PromotionStageHealthyAtUtc is not { } stageHealthyAt
                || !releases.TryGetValue(run.ReleaseId, out var release)) continue;
            var promotion = input.Promotions.LastOrDefault(item => item.ProductionId == run.EnvironmentId
                && item.StageHealthyAt == stageHealthyAt && release.ArtifactDigests.Contains(item.Artifact, StringComparer.Ordinal));
            if (promotion?.Status is not ("expired" or "rejected")) continue;
            var expired = promotion.Status == "expired";
            var actor = expired ? LifecycleActor : promotion.Actor ?? LifecycleActor;
            var at = expired ? promotion.ExpiresAt ?? stageHealthyAt : stageHealthyAt;
            if (at < run.CreatedAtUtc) at = run.CreatedAtUtc;
            runs[i] = run with { State = DeploymentStates.Cancelled, CancelledBy = actor, FinishedAtUtc = at };
            var message = expired ? "Approval window expired." : $"Rejected by {actor}.";
            log.Add(new(run.Id, DeployStepNames.Approval, at, message));
            audit.Add(new(at, actor, expired ? "approval-expired" : "deployment-rejected", run.ReleaseId, run.Id,
                run.EnvironmentId, message));
        }
    }

    private static void FollowJobs(ReconcileInput input, List<DeploymentRecord> runs,
        IReadOnlyDictionary<string, Release> releases, IReadOnlyDictionary<string, DeployEnvironment> environments,
        List<DeploymentLogEntry> log, List<DeployAuditEntry> audit)
    {
        var jobs = input.Jobs.ToDictionary(item => item.Id, StringComparer.Ordinal);
        for (var i = 0; i < runs.Count; i++)
        {
            var run = runs[i];
            if (run.JobId is null || !jobs.TryGetValue(run.JobId, out var job)) continue;
            if (DeploymentStates.IsFinished(run.State)) continue;
            var state = StateForJob(job.Status);
            var version = releases.TryGetValue(run.ReleaseId, out var release) ? release.Version : run.ReleaseId;
            var environmentName = environments.TryGetValue(run.EnvironmentId, out var environment)
                ? environment.Name : run.EnvironmentId;
            var updated = run;
            if (state != DeploymentStates.Cancelled && run.StartedAtUtc is null && job.ClaimedAt is { } claimedAt)
            {
                updated = updated with { StartedAtUtc = claimedAt, HostId = job.HostId, HostClaims = job.Claims };
                log.Add(new(run.Id, run.DeployStep, claimedAt, $"Deploy host {job.HostId} claimed the job."));
                if (state == DeploymentStates.Running)
                    audit.Add(new(claimedAt, job.HostId, "deployment-started", run.ReleaseId, run.Id, run.EnvironmentId,
                        $"Release {version} started in {environmentName}."));
            }
            else if (run.StartedAtUtc is not null && job.Claims > run.HostClaims && job.ClaimedAt is { } reclaimedAt)
            {
                updated = updated with { HostClaims = job.Claims };
                var message = $"Deploy host {job.HostId} claimed the job again after its lease expired (claim {job.Claims}).";
                log.Add(new(run.Id, run.DeployStep, reclaimedAt, message));
                audit.Add(new(reclaimedAt, job.HostId, "deployment-reclaimed", run.ReleaseId, run.Id, run.EnvironmentId,
                    $"Release {version} in {environmentName}: {message}"));
            }
            if (state == run.State)
            {
                if (!ReferenceEquals(updated, run)) runs[i] = updated;
                continue;
            }
            updated = updated with { State = state };
            if (DeploymentStates.IsFinished(state))
            {
                var at = job.FinishedAt ?? job.ClaimedAt ?? job.CreatedAt;
                updated = updated with { FinishedAtUtc = at, Evidence = job.Evidence };
                AddResultLog(updated, at, log);
                var actor = state == DeploymentStates.Cancelled ? run.CancelledBy ?? HostActor : job.HostId;
                if (state != DeploymentStates.Cancelled || run.CancelledBy is null)
                    audit.Add(new(at, actor, "deployment-" + state, run.ReleaseId, run.Id, run.EnvironmentId,
                        $"Release {version} {state} in {environmentName}"
                        + (job.Evidence is null ? "." : $" ({job.Evidence}).")));
            }
            runs[i] = updated;
        }
    }

    private static void AddResultLog(DeploymentRecord run, DateTime at, List<DeploymentLogEntry> log)
    {
        switch (run.State)
        {
            case DeploymentStates.Succeeded:
                log.Add(new(run.Id, run.DeployStep, at, "Deploy host completed the step."));
                log.Add(new(run.Id, DeployStepNames.Health, at, $"Health check passed ({run.Evidence ?? DeployEvidence.HealthOk})."));
                break;
            case DeploymentStates.Failed when run.Evidence == DeployEvidence.HostLost:
                log.Add(new(run.Id, run.DeployStep, at,
                    $"Deploy host {run.HostId} stopped reporting and its claim expired ({DeployEvidence.HostLost})."));
                log.Add(new(run.Id, DeployStepNames.Health, at, "Skipped because the deploy host was lost."));
                break;
            case DeploymentStates.Failed when run.Evidence == DeployEvidence.HealthFailed:
                log.Add(new(run.Id, run.DeployStep, at, "Deploy host completed the step."));
                log.Add(new(run.Id, DeployStepNames.Health, at, $"Health check failed ({DeployEvidence.HealthFailed})."));
                break;
            case DeploymentStates.Failed:
                log.Add(new(run.Id, run.DeployStep, at, $"Deploy host reported a failure ({run.Evidence ?? DeployEvidence.DeployFailed})."));
                log.Add(new(run.Id, DeployStepNames.Health, at, "Skipped because the deploy step failed."));
                break;
            case DeploymentStates.Cancelled when run.CancelledBy is null:
                log.Add(new(run.Id, run.DeployStep, at, "The deploy job was cancelled."));
                break;
        }
    }

    private static string StepFor(ReleaseRecord release, string environmentId)
        => release.Environments.FirstOrDefault(item => item.Id == environmentId) is { } declaration
            ? DeployStepNames.ForTemplate(declaration.Template) : DeployStepNames.StartContainer;
}
