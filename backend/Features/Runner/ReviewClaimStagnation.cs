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
    DateTime ObservedAt);

/// <summary>
/// AGT-2987: remembers why pending attempts could not be claimed and logs each
/// attempt at most once per <see cref="LogInterval"/>. The stagnation watchdog
/// and the pipeline-health alarm read <see cref="Latest"/> to name the cause.
/// </summary>
public sealed class ReviewClaimUnclaimableLog
{
    public static readonly TimeSpan LogInterval = TimeSpan.FromHours(1);

    /// <summary>Observations older than this are dropped; the attempt was claimed, replanned, or settled.</summary>
    private static readonly TimeSpan Retention = TimeSpan.FromHours(6);

    private readonly ConcurrentDictionary<string, ReviewUnclaimableObservation> _latest =
        new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTime> _lastLoggedAt =
        new(StringComparer.Ordinal);
    private readonly ILogger<ReviewClaimUnclaimableLog> _logger;
    private readonly TimeProvider _time;

    public ReviewClaimUnclaimableLog(ILogger<ReviewClaimUnclaimableLog> logger, TimeProvider? time = null)
    {
        _logger = logger;
        _time = time ?? TimeProvider.System;
    }

    /// <summary>Records one claim's unclaimable attempts; returns how many were logged.</summary>
    public int Record(string executorId, IReadOnlyList<Contract.ReviewUnclaimableAttemptDto> unclaimable)
    {
        var now = _time.GetUtcNow().UtcDateTime;
        Prune(now);
        var logged = 0;
        foreach (var attempt in unclaimable)
        {
            _latest[attempt.AttemptId] = new ReviewUnclaimableObservation(
                attempt.AttemptId,
                attempt.TaskKey,
                executorId,
                attempt.MissingCapabilities,
                now);
            if (_lastLoggedAt.TryGetValue(attempt.AttemptId, out var last) && now - last < LogInterval)
                continue;
            _lastLoggedAt[attempt.AttemptId] = now;
            logged++;
            _logger.LogWarning(
                "review-claim-unclaimable reason={Reason} attempt={AttemptId} task={TaskKey} "
                + "executor={ExecutorId} missing={MissingCapabilities} pendingSince={PendingSince:O}",
                Contract.ReviewClaimEmptyReasons.UnclaimablePlanRequirements,
                attempt.AttemptId,
                attempt.TaskKey,
                executorId,
                string.Join(",", attempt.MissingCapabilities),
                attempt.CreatedAt);
        }
        return logged;
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
            _latest.TryRemove(attemptId, out _);
            _lastLoggedAt.TryRemove(attemptId, out _);
        }
    }
}
