namespace AgentStudio.Pipeline;

/// <summary>
/// Tunables for the bounded gate-environment integration retry (AGT-2824).
/// </summary>
public static class GateEnvironmentRetryDefaults
{
    public const string ConfigurationSection = "GateEnvironmentRetry";
    public const bool Enabled = true;

    /// <summary>
    /// Sweep cadence. Deliberately much shorter than the first rung so the
    /// due instant is honoured with at most one cadence of lag instead of
    /// rounding every rung up to the sweep interval.
    /// </summary>
    public const int SweepIntervalSeconds = 60;

    /// <summary>
    /// The bounded ladder. A gate environment fault is a host problem, so the
    /// first rung is short enough to absorb a transient toolchain hiccup and
    /// the last one is long enough that a genuinely broken host is not hammered
    /// while an operator fixes it. After the last rung the card parks.
    /// </summary>
    public static readonly IReadOnlyList<int> BackoffMinutes = [5, 15, 45];
}

/// <summary>Resolved, clamped configuration for one evaluation.</summary>
public sealed record GateEnvironmentRetryOptions(
    bool Enabled,
    TimeSpan SweepInterval,
    IReadOnlyList<TimeSpan> Backoff)
{
    public int MaxAttempts => Backoff.Count;

    public static GateEnvironmentRetryOptions Default { get; } = new(
        GateEnvironmentRetryDefaults.Enabled,
        TimeSpan.FromSeconds(GateEnvironmentRetryDefaults.SweepIntervalSeconds),
        [.. GateEnvironmentRetryDefaults.BackoffMinutes.Select(minutes => TimeSpan.FromMinutes(minutes))]);

    public static GateEnvironmentRetryOptions FromConfiguration(IConfiguration configuration)
    {
        var section = configuration.GetSection(GateEnvironmentRetryDefaults.ConfigurationSection);
        var configured = section.GetSection("BackoffMinutes")
            .GetChildren()
            .Select(item => int.TryParse(item.Value, out var minutes) ? minutes : -1)
            .Where(minutes => minutes > 0)
            .ToList();
        var backoff = (configured.Count > 0
                ? configured
                : [.. GateEnvironmentRetryDefaults.BackoffMinutes])
            .Select(minutes => TimeSpan.FromMinutes(Math.Clamp(minutes, 1, 24 * 60)))
            .ToList();

        return new GateEnvironmentRetryOptions(
            section.GetValue<bool?>("Enabled") ?? GateEnvironmentRetryDefaults.Enabled,
            TimeSpan.FromSeconds(Math.Clamp(
                section.GetValue<int?>("SweepIntervalSeconds") ?? GateEnvironmentRetryDefaults.SweepIntervalSeconds,
                10,
                60 * 60)),
            backoff);
    }
}

/// <summary>
/// Reads the typed facts of the gate-environment ladder for one card (AGT-3060).
/// The ladder itself is <see cref="AgentStudio.OrchestrationDecisions.GateEnvironmentRetryPolicy"/>,
/// decided by the AI Operator in the Orchestrator Engine.
/// </summary>
public static class GateEnvironmentRetryFactsReader
{
    /// <summary>
    /// Lanes a delivery rests in after an integrate-on-delivery failure. The
    /// accepted lanes (Completed / Archive) belong to the accepted-integration
    /// backstop and are deliberately not touched here.
    /// </summary>
    public static readonly IReadOnlySet<string> Lanes = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Escalated,
    };

    /// <param name="task">The delivered card.</param>
    /// <param name="integration">Its Git-derived integration verdict.</param>
    /// <param name="reviewPassed">
    /// Whether the latest review for the card's current delivery SHA
    /// ended in Pass. False refuses the retry: without a passed review there is
    /// nothing to reuse and the replay would integrate unreviewed work.
    /// </param>
    /// <param name="attemptsSpent">Automatic rungs already spent on this delivery SHA.</param>
    /// <param name="lastAttemptAt">Instant of the last retry, or null when there was none.</param>
    /// <param name="failedAt">Instant the gate-environment failure was recorded.</param>
    /// <param name="now">Evaluation instant.</param>
    /// <param name="integrationInFlight">
    /// A merge for this card holds the in-flight gate journal
    /// (<see cref="IntegrationGateJournal"/>), so its verdict is not settled yet.
    /// </param>
    public static GateEnvironmentRetryFacts Facts(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        bool reviewPassed,
        int attemptsSpent,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? failedAt,
        GateEnvironmentRetryOptions options,
        DateTimeOffset now,
        bool integrationInFlight = false)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);
        // An acceptance transaction is already driving this card's merge, and
        // AcceptedIntegrationBackstopHostedService re-drives it after a restart.
        // Two owners would run two merges and spend a rung on someone else's
        // attempt. AGT-3009: an Auto Review card in the integrating phase is
        // different. Its delivery merge already failed, and the guarded delivery
        // chain leaves it there for the gate-failure router instead of parking
        // it. Nobody else re-drives it, so only a merge that is actually running
        // (the in-flight gate journal) holds the ladder back.
        var acceptanceInFlight = integrationInFlight
            || (string.Equals(task.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal)
                && task.State != TaskStates.AutoReview);
        return new GateEnvironmentRetryFacts(
            options.Enabled,
            Lanes.Contains(task.State),
            AcceptanceIntegrationPolicy.IsIntegrationRequired(task),
            acceptanceInFlight,
            IsGateEnvironmentFailure(integration),
            reviewPassed,
            attemptsSpent,
            options.Backoff,
            lastAttemptAt,
            failedAt,
            now);
    }

    /// <summary>
    /// The two host-side gate faults this ladder replays. CAC-18 keeps a gate
    /// environment failure out of <c>conflict-skipped</c>, so the card stays
    /// Pending and only the typed failure code identifies it; AGT-2849 adds the
    /// gate that never reached a verdict at all because its process died. Both
    /// leave the reviewed delivery untouched, so both are replayed against the
    /// same delivery SHA rather than sent through a new review.
    /// </summary>
    public static bool IsGateEnvironmentFailure(TaskIntegrationStatus? integration)
        => (integration?.Failure?.Code is
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure
            or AcceptedIntegrationFailureCodes.GateInterrupted)
            && !GateFailureTriagePolicy.ContainsTransportMarker(
                integration.Failure.Reason + "\n" + integration.Failure.EvidenceExcerpt);
}
