using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

internal enum ReviewSlotContinuationKind
{
    Reattach,
    SettleNonAdoptable,
}

internal sealed record ReviewSlotContinuation(
    PersistedReviewSlot Slot,
    ReviewSlotContinuationKind Kind,
    string Reason);

internal sealed record ReviewSlotReconciliation(
    int Scanned,
    IReadOnlyList<ReviewSlotContinuation> Continuations,
    int Purged,
    int AgedPurged,
    int Deferred)
{
    internal string JournalLine(string scope)
        => $"review-slot-reconciliation scope={scope} scanned={Scanned} " +
           $"recovered={Continuations.Count} purged={Purged} deferred={Deferred}";

    internal string AgingJournalLine(string scope, TimeSpan maximumDormantAge)
        => $"review-slot-aging scope={scope} purged={AgedPurged} " +
           $"thresholdHours={maximumDormantAge.TotalHours:0}";
}

internal sealed record ReviewProcessObservation(bool Live, string Reason);

internal enum ReviewSlotRecoveryAction
{
    Reattach,
    SettleNonAdoptable,
    PurgeInvalidAuthority,
    PurgeAged,
    Defer,
}

internal static class ReviewSlotRecoveryPolicy
{
    internal static ReviewSlotRecoveryAction Decide(
        bool processLive,
        bool hasDurableResult,
        bool exceedsMaximumDormantAge,
        bool? leaseValid)
    {
        if (processLive) return ReviewSlotRecoveryAction.Reattach;
        if (exceedsMaximumDormantAge) return ReviewSlotRecoveryAction.PurgeAged;
        if (leaseValid is null) return ReviewSlotRecoveryAction.Defer;
        if (!leaseValid.Value) return ReviewSlotRecoveryAction.PurgeInvalidAuthority;
        return hasDurableResult
            ? ReviewSlotRecoveryAction.Reattach
            : ReviewSlotRecoveryAction.SettleNonAdoptable;
    }

    internal static bool LeaseMatches(
        PersistedReviewSlot slot,
        ReviewAttemptDto? serverAttempt,
        DateTime utcNow,
        out string reason)
    {
        if (serverAttempt is null)
        {
            reason = "review attempt no longer exists on the Task Server";
            return false;
        }

        var localAttempt = slot.Claim.Attempt!;
        var lease = slot.Claim.Lease!;
        if (!string.Equals(serverAttempt.Status, "leased", StringComparison.OrdinalIgnoreCase))
        {
            reason = $"review attempt is server-side status '{serverAttempt.Status}'";
            return false;
        }
        if (!string.Equals(serverAttempt.AttemptId, localAttempt.AttemptId, StringComparison.Ordinal)
            || !string.Equals(serverAttempt.SubjectId, localAttempt.SubjectId, StringComparison.Ordinal)
            || serverAttempt.Fence != lease.Fence
            || !string.Equals(serverAttempt.ExecutorId, lease.ExecutorId, StringComparison.Ordinal)
            || !string.Equals(serverAttempt.HostId, lease.HostId, StringComparison.Ordinal))
        {
            reason = "server-side review authority does not match the persisted lease";
            return false;
        }
        if (lease.ExpiresAt <= utcNow)
        {
            reason = $"persisted review lease expired at {lease.ExpiresAt:O}";
            return false;
        }

        reason = "server-side review attempt and unexpired persisted lease match";
        return true;
    }
}

/// <summary>
/// Reconciles host-local review handoff records with process and Task Server
/// authority before the daemon lets them consume admission slots.
/// </summary>
internal sealed class ReviewSlotReconciler
{
    internal static readonly TimeSpan MaximumDormantAge = TimeSpan.FromHours(24);

    private readonly ReviewStateStore _state;
    private readonly Func<string, CancellationToken, Task<ReviewAttemptDto?>> _getAttempt;
    private readonly Func<PersistedReviewSlot, ReviewProcessObservation> _observeProcess;
    private readonly Func<PersistedReviewSlot, bool> _hasDurableResult;
    private readonly Action<string> _log;

    internal ReviewSlotReconciler(
        ReviewStateStore state,
        Func<string, CancellationToken, Task<ReviewAttemptDto?>> getAttempt,
        Func<PersistedReviewSlot, ReviewProcessObservation>? observeProcess = null,
        Func<PersistedReviewSlot, bool>? hasDurableResult = null,
        Action<string>? log = null)
    {
        _state = state;
        _getAttempt = getAttempt;
        _observeProcess = observeProcess ?? ObserveProcess;
        _hasDurableResult = hasDurableResult ?? DurableReviewProcess.HasCompleted;
        _log = log ?? (_ => { });
    }

    internal async Task<ReviewSlotReconciliation> ReconcileAsync(
        IReadOnlySet<string> activeAttemptIds,
        DateTime utcNow,
        CancellationToken shutdown)
    {
        var slots = _state.LoadAll()
            .Where(slot => !activeAttemptIds.Contains(slot.AttemptId))
            .ToArray();
        var continuations = new List<ReviewSlotContinuation>();
        var purged = 0;
        var agedPurged = 0;
        var deferred = 0;

        foreach (var persisted in slots)
        {
            shutdown.ThrowIfCancellationRequested();
            var slot = await RecoverLaunchingIdentityAsync(persisted, shutdown);
            var process = _observeProcess(slot);
            var hasDurableResult = _hasDurableResult(slot);
            var createdAt = slot.CreatedAtUtc
                            ?? slot.Claim.Attempt?.CreatedAt
                            ?? slot.UpdatedAtUtc;
            var dormantAge = utcNow - createdAt;
            var exceedsMaximumDormantAge = !process.Live
                                           && dormantAge >= MaximumDormantAge;

            bool? leaseValid = null;
            var reason = process.Reason;
            if (!process.Live && !exceedsMaximumDormantAge)
            {
                try
                {
                    var serverAttempt = await _getAttempt(slot.AttemptId, shutdown);
                    leaseValid = ReviewSlotRecoveryPolicy.LeaseMatches(
                        slot,
                        serverAttempt,
                        utcNow,
                        out reason);
                }
                catch (OperationCanceledException) when (shutdown.IsCancellationRequested)
                {
                    throw;
                }
                catch (TaskServerException exception) when (exception.StatusCode is 401 or 403)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    reason = $"Task Server authority check deferred: {exception.Message}";
                }
            }

            var action = ReviewSlotRecoveryPolicy.Decide(
                process.Live,
                hasDurableResult,
                exceedsMaximumDormantAge,
                leaseValid);
            switch (action)
            {
                case ReviewSlotRecoveryAction.Reattach:
                    continuations.Add(new ReviewSlotContinuation(
                        slot,
                        ReviewSlotContinuationKind.Reattach,
                        process.Live ? process.Reason : reason));
                    break;
                case ReviewSlotRecoveryAction.SettleNonAdoptable:
                    continuations.Add(new ReviewSlotContinuation(
                        slot,
                        ReviewSlotContinuationKind.SettleNonAdoptable,
                        process.Reason));
                    break;
                case ReviewSlotRecoveryAction.PurgeInvalidAuthority:
                    await MarkCleanedAndDeleteAsync(slot, reason, shutdown);
                    purged++;
                    break;
                case ReviewSlotRecoveryAction.PurgeAged:
                    await MarkCleanedAndDeleteAsync(
                        slot,
                        $"record age {Math.Max(0, dormantAge.TotalHours):0.0}h exceeded " +
                        $"the {MaximumDormantAge.TotalHours:0}h dormant limit",
                        shutdown);
                    purged++;
                    agedPurged++;
                    break;
                case ReviewSlotRecoveryAction.Defer:
                    deferred++;
                    break;
                default:
                    throw new ArgumentOutOfRangeException(nameof(action), action, null);
            }
        }

        return new ReviewSlotReconciliation(
            slots.Length,
            continuations,
            purged,
            agedPurged,
            deferred);
    }

    private async Task<PersistedReviewSlot> RecoverLaunchingIdentityAsync(
        PersistedReviewSlot slot,
        CancellationToken shutdown)
    {
        if (slot.ProcessId is not null || _hasDurableResult(slot)) return slot;
        var attempts = string.Equals(slot.Phase, "launching", StringComparison.Ordinal)
            ? 20
            : 1;
        var reason = "no persisted review process identity";
        for (var attempt = 0; attempt < attempts; attempt++)
        {
            if (DurableReviewProcess.TryRecoverIdentity(slot, out var recovered, out reason))
                return _state.Save(recovered with { Phase = "running" });
            if (attempt + 1 < attempts)
                await Task.Delay(TimeSpan.FromMilliseconds(250), shutdown);
        }
        return slot with { AdoptionFailure = reason };
    }

    private async Task MarkCleanedAndDeleteAsync(PersistedReviewSlot slot, string reason, CancellationToken ct)
    {
        try
        {
            slot = _state.Save(slot with
            {
                Phase = "cleaned",
                AdoptionFailure = reason,
            });
            // The worker generation that owned this workspace is confirmed dead
            // (both purge branches require !process.Live). Reap any CLI child it
            // left behind now, on the reconciliation cadence, instead of leaving
            // it for the day-scale workspace retention sweep to eventually
            // out-live (AGT-2759).
            await CliProcessReaper.ReapWorkspaceAsync(slot.WorkspacePath, slot.AttemptId, _log, ct);
        }
        finally
        {
            _state.Delete(slot);
        }
    }

    private static ReviewProcessObservation ObserveProcess(PersistedReviewSlot slot)
    {
        var live = DurableReviewProcess.VerifyLive(slot, out var reason);
        return new ReviewProcessObservation(live, reason);
    }
}
