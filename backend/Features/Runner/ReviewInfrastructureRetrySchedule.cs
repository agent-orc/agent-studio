namespace AgentStudio.Runner;

/// <summary>
/// Result of <see cref="AttemptAuthorityService.ScheduleReviewInfrastructureRetry"/>.
/// <see cref="RetryNumber"/>, <see cref="DueAtUtc"/>, <see cref="Delay"/>, and
/// <see cref="Reason"/> are populated only when <see cref="Status"/> is
/// <see cref="AttemptWriteStatus.Accepted"/>.
/// </summary>
public sealed record ReviewInfrastructureRetryScheduleResult(
    AttemptWriteStatus Status,
    string AttemptId,
    string? Message = null,
    ReviewAttemptDto? ReviewAttempt = null,
    int RetryNumber = 0,
    int RetryBudget = 0,
    DateTime? DueAtUtc = null,
    TimeSpan? Delay = null,
    string? Reason = null)
{
    public bool Accepted => Status == AttemptWriteStatus.Accepted;
}

/// <summary>One review attempt whose scheduled infrastructure retry is due, as
/// returned by <see cref="AttemptAuthorityService.DueReviewInfrastructureRetries"/>.</summary>
public sealed record ReviewInfrastructureRetryDue(
    string AttemptId,
    string TaskKey,
    int RetryNumber,
    int RetryBudget,
    string Reason,
    DateTime DueAtUtc);
