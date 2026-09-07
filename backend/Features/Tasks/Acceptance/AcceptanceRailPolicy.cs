namespace AgentStudio.Tasks;

public static class AcceptanceRailDefaults
{
    public const string ConfigurationSection = "AcceptanceRail";
    public const bool Enabled = true;
    public const int IntervalSeconds = 180;
    public const int MaxRequeues = 5;
    public const string OperatorHoldTag = "orchestrator-hold";
}

public sealed record AcceptanceRailOptions(
    bool Enabled,
    TimeSpan Interval,
    int MaxRequeues,
    IReadOnlySet<string> HoldList)
{
    public static AcceptanceRailOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(AcceptanceRailDefaults.ConfigurationSection);
        var holdList = section.GetSection("HoldList")
            .GetChildren()
            .Select(item => item.Value?.Trim())
            .Where(item => !string.IsNullOrWhiteSpace(item))
            .Select(item => item!)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        holdList.Add(AcceptanceRailDefaults.OperatorHoldTag);

        return new AcceptanceRailOptions(
            section.GetValue<bool?>("Enabled") ?? AcceptanceRailDefaults.Enabled,
            TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("IntervalSeconds") ?? AcceptanceRailDefaults.IntervalSeconds,
                30,
                60 * 60)),
            Math.Clamp(
                section.GetValue<int?>("MaxRequeues") ?? AcceptanceRailDefaults.MaxRequeues,
                1,
                100),
            holdList);
    }
}

public enum AcceptanceRailAction
{
    Ignore,
    Accept,
    Requeue,
    Escalate,
}

public sealed record AcceptanceRailDecision(
    AcceptanceRailAction Action,
    string Reason);

/// <summary>
/// Pure policy for the platform-owned acceptance rail. It accepts only
/// Git-derived integrated coding deliveries and requeues typed review
/// infrastructure failures or rebase-recoverable integration failures.
/// </summary>
public static class AcceptanceRailPolicy
{
    private static readonly IReadOnlySet<string> OperatorDecisionBlockers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HumanReviewEscalationCategories.HumanDecisionNeeded,
            HumanReviewEscalationCategories.AgentNeedsInput,
            HumanReviewEscalationCategories.NeedsHumanInput,
            HumanReviewEscalationCategories.SteerUnanswered,
        };

    public static AcceptanceRailDecision Decide(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        int conflictRequeues,
        AcceptanceRailOptions options)
    {
        if (task.State is not (TaskStates.HumanReview or TaskStates.Escalated))
            return Ignore("outside-rail-lanes");
        if (IsHeld(task, options.HoldList))
            return Ignore("operator-hold");

        if (task.State == TaskStates.HumanReview
            && task.ReviewFailure is { } reviewFailure
            && AgentStudio.TaskServer.Contracts.ReviewFailureClasses.IsRetryable(reviewFailure.FailureClass))
        {
            if (reviewFailure.Exhausted)
            {
                return Ignore("review-failure-retry-budget-exhausted");
            }

            if (reviewFailure.RetryAtUtc > DateTime.UtcNow)
                return Ignore("review-failure-backoff");

            return new AcceptanceRailDecision(
                AcceptanceRailAction.Requeue,
                "retryable-review-failure");
        }

        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(task))
            return Ignore("no-code-acceptance");

        if (task.State == TaskStates.HumanReview
            && string.Equals(
                integration?.Status,
                IntegrationStatuses.Integrated,
                StringComparison.Ordinal))
        {
            return new AcceptanceRailDecision(
                AcceptanceRailAction.Accept,
                "git-derived-integrated");
        }

        var recoverableConflict = string.Equals(
                integration?.Status,
                IntegrationStatuses.ConflictSkipped,
                StringComparison.Ordinal)
            && integration?.Failure?.RebaseRecoveryAvailable == true;
        if (!recoverableConflict)
            return Ignore("not-recoverable");

        return Math.Max(0, conflictRequeues) < options.MaxRequeues
            ? new AcceptanceRailDecision(
                AcceptanceRailAction.Requeue,
                "recoverable-integration-conflict")
            : new AcceptanceRailDecision(
                AcceptanceRailAction.Escalate,
                "integration-requeue-budget-exhausted");
    }

    public static bool IsHeld(TaskInfo task, IReadOnlySet<string> holdList)
    {
        if (TaskSlugs.IsHumanDecisionNeeded(task.Id)) return true;
        if (task.ParkedBlocker is not null
            && OperatorDecisionBlockers.Contains(task.ParkedBlocker.BlockerType))
        {
            return true;
        }

        if (Matches(task.Id, holdList)
            || Matches(task.Key, holdList)
            || Matches(task.TaskKey, holdList))
        {
            return true;
        }

        return (task.Tags ?? []).Any(tag => Matches(tag, holdList));
    }

    private static bool Matches(string? value, IReadOnlySet<string> holdList)
        => !string.IsNullOrWhiteSpace(value) && holdList.Contains(value.Trim());

    private static AcceptanceRailDecision Ignore(string reason)
        => new(AcceptanceRailAction.Ignore, reason);
}
