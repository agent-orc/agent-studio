using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

public static class AcceptanceRailDefaults
{
    public const string ConfigurationSection = "AcceptanceRail";
    public const bool Enabled = true;
    public const int IntervalSeconds = 180;
    public const int MaxRequeues = 2;

    /// <summary>
    /// Requeues the rail spends on one card for host or account faults
    /// (AGT-2749). Deliberately smaller than <see cref="MaxRequeues"/>: a
    /// rebase requeue changes the delivery, an infrastructure requeue only
    /// replays it, so an unfixable host keeps its budget short.
    /// </summary>
    public const int MaxInfrastructureRequeues = 3;

    /// <summary>First infrastructure backoff step, doubled per further retry.</summary>
    public const int InfrastructureBackoffBaseSeconds = AcceptanceRailBackoff.BaseSeconds;

    /// <summary>Upper bound of the doubled backoff, so a long budget cannot park a card for hours.</summary>
    public const int InfrastructureBackoffCeilingSeconds = AcceptanceRailBackoff.CeilingSeconds;

    public const string OperatorHoldTag = "orchestrator-hold";
    public const string BounceConfigurationSection = "IntegrationBounceRail";
}

public sealed record AcceptanceRailOptions(
    bool Enabled,
    TimeSpan Interval,
    int MaxRequeues,
    int MaxInfrastructureRequeues,
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
                300)),
            Math.Clamp(
                section.GetValue<int?>("MaxRequeues") ?? AcceptanceRailDefaults.MaxRequeues,
                1,
                100),
            Math.Clamp(
                section.GetValue<int?>("MaxInfrastructureRequeues") ?? AcceptanceRailDefaults.MaxInfrastructureRequeues,
                1,
                20),
            holdList);
    }
}

public enum AcceptanceRailAction
{
    Ignore,
    Accept,

    /// <summary>Rebase recovery: the delivery itself must change before it can integrate.</summary>
    Requeue,

    /// <summary>
    /// Replay after a host or account fault (AGT-2749). The delivery is
    /// unchanged, so this action must never write a rebase steer; the card goes
    /// straight back to <see cref="TaskStates.AutoReview"/>.
    /// </summary>
    RequeueInfrastructure,

    Escalate,
}

public sealed record AcceptanceRailDecision(
    AcceptanceRailAction Action,
    string Reason);

/// <summary>
/// Facts of one card for the acceptance rail's AI Operator decider
/// <c>acceptance-rail</c> (AGT-3060). The rule itself is
/// <see cref="AcceptanceRailDecisionPolicy"/> in the decision library; this
/// class only reads the card, its Git-derived integration status and the
/// rail's receipt counts.
/// </summary>
public static class AcceptanceRailFacts
{
    private static readonly IReadOnlySet<string> OperatorDecisionBlockers =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            HumanReviewEscalationCategories.HumanDecisionNeeded,
            HumanReviewEscalationCategories.AgentNeedsInput,
            HumanReviewEscalationCategories.NeedsHumanInput,
            HumanReviewEscalationCategories.SteerUnanswered,
        };

    /// <param name="deliveryChainOwns">The delivery reconciler owns this stale Human Review card.</param>
    /// <param name="conflictRequeues">Rebase recoveries already spent on this card.</param>
    /// <param name="infrastructureRequeues">Infrastructure or quota replays already spent on this card.</param>
    /// <param name="lastInfrastructureRequeueAt">Timestamp of the last such replay, or null when there was none.</param>
    /// <param name="now">Evaluation instant of the replay backoff.</param>
    /// <param name="quotaResetAt">Known provider reset instant, or null when the reset is unknown.</param>
    public static AcceptanceRailOperatorFacts From(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        int conflictRequeues,
        AcceptanceRailOptions options,
        DateTimeOffset now,
        int infrastructureRequeues = 0,
        DateTimeOffset? lastInfrastructureRequeueAt = null,
        DateTimeOffset? quotaResetAt = null,
        bool deliveryChainOwns = false)
    {
        var failure = string.Equals(integration?.Status, IntegrationStatuses.ConflictSkipped, StringComparison.Ordinal)
            ? integration?.Failure
            : null;
        var replayDue = failure is not null
            && AcceptanceRailBackoff.Requeueable(failure.FailureClass)
            && AcceptanceRailBackoff.Remaining(Math.Max(0, infrastructureRequeues), failure.FailureClass,
                lastInfrastructureRequeueAt, quotaResetAt, now) <= TimeSpan.Zero;
        return new AcceptanceRailOperatorFacts(
            task.State,
            IsExplicitlyHeld(task, options.HoldList),
            IsHeld(task, options.HoldList),
            AcceptanceIntegrationPolicy.IsIntegrationRequired(task),
            IntegrationStatuses.IsMerged(integration?.Status),
            integration is not null && IntegrationVerificationStates.PermitsCompletion(integration.Verification),
            failure?.RebaseRecoveryAvailable == true,
            failure?.FailureClass,
            Math.Max(0, conflictRequeues),
            options.MaxRequeues,
            Math.Max(0, infrastructureRequeues),
            options.MaxInfrastructureRequeues,
            replayDue,
            deliveryChainOwns);
    }

    public static bool IsHeld(TaskInfo task, IReadOnlySet<string> holdList)
    {
        if (TaskSlugs.IsHumanDecisionNeeded(task.Id)) return true;
        if (task.ParkedBlocker is not null
            && OperatorDecisionBlockers.Contains(task.ParkedBlocker.BlockerType))
        {
            return true;
        }

        return IsExplicitlyHeld(task, holdList);
    }

    public static bool IsExplicitlyHeld(TaskInfo task, IReadOnlySet<string> holdList)
    {
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
}
