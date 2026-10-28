using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>The authority facts of one subject that admission and reconciliation read.</summary>
internal sealed record PostWorkSubjectFacts(
    string Owner,
    string State,
    string CohortPhase,
    string LaneEntryId,
    DateTime? RetryAt,
    string? LiveAttemptKind,
    string? LiveAttemptInstanceId,
    DateTime? LiveAttemptExpiresAt);

internal sealed record PostWorkAdmission(bool Admitted, string Reason, bool SupersedesLiveAttempt = false);

/// <summary>
/// Pure post-work authority rules (AGT-3171 DP-S8). One subject has at most
/// one live attempt; a backend pass and a host claim are both attempts, so
/// they can never process the same card concurrently. Lifecycle effects are
/// decided elsewhere; this policy only answers who may hold the subject.
/// </summary>
internal static class PostWorkAuthorityPolicy
{
    /// <summary>
    /// A backend pass may run for a backend-owned queued, waiting or blocked
    /// subject, or as the server apply step of an accepted host report. A live
    /// pass of an earlier backend process is superseded; that process is gone
    /// because the monolith is the only backend authority client.
    /// </summary>
    public static PostWorkAdmission AdmitBackendPass(PostWorkSubjectFacts subject, string backendInstanceId, DateTime now)
    {
        if (subject.State == PostWorkStates.Applied) return new(false, "already-applied");
        if (subject.State == PostWorkStates.Retired) return new(false, "retired");
        var supersedes = false;
        if (LiveAt(subject, now))
        {
            if (subject.LiveAttemptKind != PostWorkExecutorKinds.Backend
                || subject.LiveAttemptInstanceId == backendInstanceId)
                return new(false, "authority-held");
            supersedes = true;
        }
        if (subject.CohortPhase == PostWorkCohortPhases.Draining) return new(false, "admission-stopped");
        if (subject.State == PostWorkStates.Reported) return new(true, "apply-host-report", supersedes);
        if (subject.Owner == PostWorkOwners.Host) return new(false, "host-owned");
        return subject.State is PostWorkStates.Queued or PostWorkStates.Waiting or PostWorkStates.Blocked
            or PostWorkStates.Running
            ? new(true, "backend-owned", supersedes)
            : new(false, "state-" + subject.State);
    }

    /// <summary>Host claims only reach host-owned subjects whose retry time has passed.</summary>
    public static PostWorkAdmission AdmitHostClaim(PostWorkSubjectFacts subject, DateTime now)
    {
        if (PostWorkStates.IsClosed(subject.State)) return new(false, "closed");
        if (subject.Owner != PostWorkOwners.Host || subject.CohortPhase != PostWorkCohortPhases.Host)
            return new(false, "backend-owned");
        if (LiveAt(subject, now)) return new(false, "authority-held");
        if (subject.State is not (PostWorkStates.Queued or PostWorkStates.Waiting or PostWorkStates.Running))
            return new(false, "state-" + subject.State);
        if (subject.RetryAt is { } retryAt && retryAt > now) return new(false, "retry-pending");
        return new(true, "eligible");
    }

    /// <summary>
    /// A fenced report or renewal is accepted only for the current live lease.
    /// After lease loss, revocation or a newer fence the report is inert.
    /// </summary>
    public static string? RefuseFencedCall(
        PostWorkAuthority presented, string attemptState, string executorId, string instanceId,
        string leaseId, long fence, long subjectFence, long authorityEpoch, DateTime expiresAt, DateTime now)
    {
        if (presented.ExecutorId != executorId || presented.InstanceId != instanceId
            || presented.LeaseId != leaseId || presented.Fence != fence
            || presented.AuthorityEpoch != authorityEpoch)
            return "stale-fence";
        if (fence != subjectFence) return "stale-fence";
        if (attemptState != "live") return "post-work-lease-lost";
        return expiresAt <= now ? "post-work-lease-lost" : null;
    }

    /// <summary>
    /// Restart and rollback reconciliation of one open subject against the
    /// durable lane state and recorded outcome. A recorded decision is never
    /// rerun; a card that left the lane or re-entered it under a new lane
    /// entry closes the old subject.
    /// </summary>
    public static (string Action, string Reason) Reconcile(
        PostWorkSubjectFacts subject, PostWorkLaneFact? lane, DateTime now)
    {
        if (subject.State == PostWorkStates.Applied) return (PostWorkReconcileActions.None, "decision-applied");
        if (subject.State == PostWorkStates.Retired) return (PostWorkReconcileActions.None, "retired");
        if (lane is null || !lane.InAutoReviewLane) return (PostWorkReconcileActions.Retire, "card-left-lane");
        if (!string.Equals(lane.LaneEntryId, subject.LaneEntryId, StringComparison.Ordinal))
            return (PostWorkReconcileActions.Retire, "lane-entry-superseded");
        if (lane.DecisionRecorded) return (PostWorkReconcileActions.MarkApplied, "lane-decision-recorded");
        if (LiveAt(subject, now)) return (PostWorkReconcileActions.Hold, "authority-held");
        if (subject.State == PostWorkStates.Reported)
            return (PostWorkReconcileActions.ApplyHostReport, "host-report-accepted");
        if (subject.Owner == PostWorkOwners.Host)
            return (PostWorkReconcileActions.AwaitHost, subject.State);
        if (subject.CohortPhase == PostWorkCohortPhases.Draining)
            return (PostWorkReconcileActions.Hold, "admission-stopped");
        return subject.State == PostWorkStates.Waiting && subject.RetryAt is { } retryAt && retryAt > now
            ? (PostWorkReconcileActions.RetainDeferral, "deferred")
            : (PostWorkReconcileActions.RequeueBackend, "unfinished");
    }

    /// <summary>
    /// A draining subject converts to host ownership only after no backend
    /// pass holds it and its outcome is reconciled.
    /// </summary>
    public static string CohortConversion(PostWorkSubjectFacts subject, PostWorkLaneFact? lane, DateTime now)
    {
        if (PostWorkStates.IsClosed(subject.State)) return "closed";
        if (LiveAt(subject, now)) return "draining";
        var (action, _) = Reconcile(subject with { CohortPhase = PostWorkCohortPhases.None }, lane, now);
        return action switch
        {
            PostWorkReconcileActions.Retire => "retire",
            PostWorkReconcileActions.MarkApplied => "mark-applied",
            _ => "host",
        };
    }

    private static bool LiveAt(PostWorkSubjectFacts subject, DateTime now)
        => subject.LiveAttemptKind is not null && subject.LiveAttemptExpiresAt > now;
}
