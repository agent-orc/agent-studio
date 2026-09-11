using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tasks;

public static class AcceptanceRailDefaults
{
    public const string ConfigurationSection = "AcceptanceRail";
    public const bool Enabled = true;
    public const int IntervalSeconds = 180;
    public const int MaxRequeues = 5;

    /// <summary>
    /// Requeues the rail spends on one card for host or account faults
    /// (AGT-2749). Deliberately smaller than <see cref="MaxRequeues"/>: a
    /// rebase requeue changes the delivery, an infrastructure requeue only
    /// replays it, so an unfixable host keeps its budget short.
    /// </summary>
    public const int MaxInfrastructureRequeues = 3;

    /// <summary>First infrastructure backoff step, doubled per further retry.</summary>
    public const int InfrastructureBackoffBaseSeconds = 60;

    /// <summary>Upper bound of the doubled backoff, so a long budget cannot park a card for hours.</summary>
    public const int InfrastructureBackoffCeilingSeconds = 1800;

    public const string OperatorHoldTag = "orchestrator-hold";
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
                60 * 60)),
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
/// Pure policy for the platform-owned acceptance rail. It accepts only
/// Git-derived integrated coding deliveries, requeues typed rebase-recoverable
/// integration failures, and replays failures the shared taxonomy attributes to
/// the host or the provider account instead of parking them (AGT-2749).
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

    /// <param name="conflictRequeues">Rebase recoveries already spent on this card.</param>
    /// <param name="now">Evaluation instant. Passed in so the policy stays pure.</param>
    /// <param name="infrastructureRequeues">Infrastructure or quota replays already spent on this card.</param>
    /// <param name="lastInfrastructureRequeueAt">Timestamp of the last such replay, or null when there was none.</param>
    /// <param name="quotaResetAt">Known provider reset instant, or null when the reset is unknown.</param>
    public static AcceptanceRailDecision Decide(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        int conflictRequeues,
        AcceptanceRailOptions options,
        DateTimeOffset now,
        int infrastructureRequeues = 0,
        DateTimeOffset? lastInfrastructureRequeueAt = null,
        DateTimeOffset? quotaResetAt = null)
    {
        if (task.State is not (TaskStates.HumanReview or TaskStates.Escalated))
            return Ignore("outside-rail-lanes");
        if (IsHeld(task, options.HoldList))
            return Ignore("operator-hold");
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

        var failure = string.Equals(
                integration?.Status,
                IntegrationStatuses.ConflictSkipped,
                StringComparison.Ordinal)
            ? integration?.Failure
            : null;

        if (failure?.RebaseRecoveryAvailable == true)
        {
            return Math.Max(0, conflictRequeues) < options.MaxRequeues
                ? new AcceptanceRailDecision(
                    AcceptanceRailAction.Requeue,
                    "recoverable-integration-conflict")
                : new AcceptanceRailDecision(
                    AcceptanceRailAction.Escalate,
                    "integration-requeue-budget-exhausted");
        }

        // AGT-2749: a host or account fault says nothing about the reviewed
        // change, so parking the card asks an operator to judge a diff that was
        // never verified. Replay it instead, bounded and backed off, and escalate
        // only once the replay budget is gone.
        if (failure is not null && Requeueable(failure.FailureClass))
        {
            var spent = Math.Max(0, infrastructureRequeues);
            if (spent >= options.MaxInfrastructureRequeues)
                return new AcceptanceRailDecision(
                    AcceptanceRailAction.Escalate,
                    "infrastructure-requeue-budget-exhausted");

            var wait = Backoff(
                spent,
                failure.FailureClass,
                lastInfrastructureRequeueAt,
                quotaResetAt,
                now);
            if (wait > TimeSpan.Zero) return Ignore("infrastructure-backoff");

            return new AcceptanceRailDecision(
                AcceptanceRailAction.RequeueInfrastructure,
                $"requeueable-{Slug(failure.FailureClass)}-failure");
        }

        return Ignore("not-recoverable");
    }

    /// <summary>
    /// Remaining wait before the rail may replay this card, measured from
    /// <paramref name="now"/>. Zero means "replay now".
    /// <list type="bullet">
    /// <item>A known quota reset is an absolute instant, so it also holds the
    /// very first replay: retrying before the account resets only burns the
    /// budget.</item>
    /// <item>Everything else backs off from the previous replay, doubling per
    /// attempt up to a ceiling, so a host that stays broken is not hammered.</item>
    /// </list>
    /// Pure: same inputs, same wait.
    /// </summary>
    public static TimeSpan Backoff(
        int attempt,
        RunFailureClass failureClass,
        DateTimeOffset? lastRequeueAt,
        DateTimeOffset? quotaResetAt,
        DateTimeOffset now)
    {
        if (failureClass == RunFailureClass.Quota && quotaResetAt is { } resetAt)
            return resetAt > now ? resetAt - now : TimeSpan.Zero;

        var spent = Math.Max(0, attempt);
        if (spent == 0 || lastRequeueAt is not { } last) return TimeSpan.Zero;

        var seconds = Math.Min(
            AcceptanceRailDefaults.InfrastructureBackoffBaseSeconds * Math.Pow(2, Math.Min(spent - 1, 16)),
            AcceptanceRailDefaults.InfrastructureBackoffCeilingSeconds);
        var due = last + TimeSpan.FromSeconds(seconds);
        return due > now ? due - now : TimeSpan.Zero;
    }

    private static bool Requeueable(RunFailureClass failureClass)
        => failureClass is RunFailureClass.Infrastructure or RunFailureClass.Quota;

    private static string Slug(RunFailureClass failureClass)
        => failureClass.ToString().ToLowerInvariant();

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
