using System.Collections.Concurrent;
using Contract = AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Claim-side facts of the canonical review queue (AGT-2987). Read from
/// attempt authority only, so coding activity and the legacy post-processing
/// queue cannot move them.
/// </summary>
public sealed record ReviewClaimActivity(
    int PendingAttempts,
    string? OldestPendingAttemptId,
    string? OldestPendingTaskKey,
    DateTime? OldestPendingCreatedAt,
    DateTime? LastClaimAt);

public sealed record ReviewClaimStagnation(
    bool IsStagnant,
    DateTime? WaitingSince,
    TimeSpan Waiting);

/// <summary>
/// AGT-2987: the review lane is stagnant when pending ReviewAttempts exist and
/// no attempt has been claimed for the threshold. The clock starts at the later
/// of the last delivered claim and the oldest pending attempt's creation, so a
/// fresh attempt behind an idle lane gets the full threshold, and nothing but a
/// claim can restart it.
/// </summary>
public static class ReviewClaimStagnationPolicy
{
    public static ReviewClaimStagnation Evaluate(
        ReviewClaimActivity activity,
        DateTime nowUtc,
        TimeSpan threshold)
    {
        if (activity.PendingAttempts <= 0 || activity.OldestPendingCreatedAt is not { } oldest)
            return new ReviewClaimStagnation(false, null, TimeSpan.Zero);
        var since = activity.LastClaimAt is { } claim && claim > oldest ? claim : oldest;
        var waiting = nowUtc - since;
        if (waiting < TimeSpan.Zero) waiting = TimeSpan.Zero;
        return new ReviewClaimStagnation(waiting >= threshold, since, waiting);
    }
}

/// <summary>Latest unclaimable verdict the server observed for one pending attempt.</summary>
public sealed record ReviewUnclaimableObservation(
    string AttemptId,
    string TaskKey,
    string ExecutorId,
    IReadOnlyList<string> MissingCapabilities,
    DateTime ObservedAt,
    string Reason);

/// <summary>
/// AGT-2987: remembers why pending attempts could not be claimed and logs each
/// attempt when its reason changes. The stagnation watchdog
/// and the pipeline-health alarm read <see cref="Latest"/> to name the cause.
/// </summary>
public sealed class ReviewClaimUnclaimableLog
{
    /// <summary>Observations older than this are dropped; the attempt was claimed, replanned, or settled.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, ReviewUnclaimableObservation> _latest =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, string> _lastLoggedState =
        new(StringComparer.Ordinal);
    private readonly ILogger<ReviewClaimUnclaimableLog> _logger;
    private readonly TimeProvider _time;

    public ReviewClaimUnclaimableLog(ILogger<ReviewClaimUnclaimableLog> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Records one claim's unclaimable attempts; returns how many were logged.</summary>
    public int Record(
        string executorId,
        IReadOnlyList<Contract.ReviewUnclaimableAttemptDto> unclaimable,
        string? reason = null)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        Prune(now);
        var logged = 0;
        reason ??= Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements;
        foreach (var attempt in unclaimable)
        {
            _latest[attempt.AttemptId] = new ReviewUnclaimableObservation(
                attempt.AttemptId,
                attempt.TaskKey,
                executorId,
                attempt.MissingCapabilities,
                now,
                reason);
            var state = reason + "|" + string.Join(",", attempt.MissingCapabilities.Order(StringComparer.Ordinal));
            if (!TryReserveLog(attempt.AttemptId, state))
                continue;
            logged++;
            _logger.LogWarning(
                "review-claim-unclaimable reason={Reason} attempt={AttemptId} task={TaskKey} "
                + "executor={ExecutorId} missing={MissingCapabilities} pendingSince={PendingSince:O}",
                reason,
                attempt.AttemptId,
                attempt.TaskKey,
                executorId,
                string.Join(",", attempt.MissingCapabilities),
                attempt.CreatedAt);
        }
        return logged;
    }

    private bool TryReserveLog(string attemptId, string state)
    {
        while (true)
        {
            if (!_lastLoggedState.TryGetValue(attemptId, out var last))
            {
                if (_lastLoggedState.TryAdd(attemptId, state)) return true;
                continue;
            }
            if (last == state) return false;
            if (_lastLoggedState.TryUpdate(attemptId, state, last)) return true;
        }
    }

    public void Clear(string attemptId)
    {
        _latest.TryRemove(attemptId, out _);
        _lastLoggedState.TryRemove(attemptId, out _);
    }

    public ReviewUnclaimableObservation? Latest(string? attemptId)
        => attemptId is not null && _latest.TryGetValue(attemptId, out var observation)
            ? observation
            : null;

    private void Prune(DateTime now)
    {
        foreach (var (attemptId, observation) in _latest)
        {
            if (now - observation.ObservedAt <= Retention) continue;
            // Remove only the stale value we inspected. A concurrent claim may
            // already have refreshed either dictionary for this attempt.
            ((ICollection<KeyValuePair<string, ReviewUnclaimableObservation>>)_latest)
                .Remove(new(attemptId, observation));
            _lastLoggedState.TryRemove(attemptId, out _);
        }
    }
}
