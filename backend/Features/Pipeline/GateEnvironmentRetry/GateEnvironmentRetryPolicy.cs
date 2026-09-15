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
/// Stable decision reasons. A refused operator retry answers with the reason
/// the policy refused it, so these are constants the endpoint, the service, and
/// the tests share instead of inline strings that drift apart.
/// </summary>
public static class GateEnvironmentRetryReasons
{
    public const string Disabled = "gate-environment-retry-disabled";
    public const string OutsideLanes = "outside-retry-lanes";
    public const string NoCodeDelivery = "no-code-delivery";
    public const string AcceptanceIntegrationInFlight = "acceptance-integration-in-flight";
    public const string NotAGateEnvironmentFailure = "not-a-gate-environment-failure";
    public const string NoPassedReview = "no-passed-review-for-delivery-sha";
    public const string BudgetExhausted = "gate-environment-retry-budget-exhausted";
    public const string DueWithoutAnchor = "gate-environment-retry-due-without-anchor";
    public const string Backoff = "gate-environment-backoff";
    public const string Due = "gate-environment-retry-due";

    /// <summary>
    /// Not a policy verdict: the service refuses a second concurrent replay of
    /// the same card. It shares the vocabulary so callers switch on one set.
    /// </summary>
    public const string AlreadyRunning = "gate-environment-retry-already-running";

    /// <summary>
    /// Operator-facing sentence for a reason the policy refused a retry on.
    /// The slug travels next to it on the wire; this is what the card shows.
    /// </summary>
    public static string Explain(string reason) => reason switch
    {
        Disabled => "Gate-environment integration retries are disabled for this server.",
        OutsideLanes =>
            $"Retrying an integration requires a task in {TaskStates.HumanReview} or {TaskStates.Escalated}.",
        NoCodeDelivery => "This task delivers no code, so there is no integration to retry.",
        AcceptanceIntegrationInFlight =>
            "An acceptance integration for this task is already running; wait for it to settle before retrying.",
        NotAGateEnvironmentFailure => "This task has no gate-environment integration failure to retry.",
        NoPassedReview => "The current delivery has no settled passed review to reuse.",
        _ => "This task is not in a gate-environment retry state.",
    };
}

public enum GateEnvironmentRetryAction
{
    /// <summary>The card is not in a gate-environment retry state at all.</summary>
    Ignore,

    /// <summary>Eligible, but the current rung has not elapsed yet.</summary>
    Wait,

    /// <summary>Replay the integration now, reusing the passed review.</summary>
    Retry,

    /// <summary>The ladder is spent; the card must show a parked reason.</summary>
    Park,
}

/// <param name="AttemptsSpent">Automatic rungs already spent on this delivery SHA.</param>
/// <param name="MaxAttempts">Rungs the ladder offers in total.</param>
/// <param name="Wait">Remaining wait before the next rung is due; zero when due now.</param>
/// <param name="DueAt">Instant the next rung becomes due, or null when there is no next rung.</param>
public sealed record GateEnvironmentRetryDecision(
    GateEnvironmentRetryAction Action,
    string Reason,
    int AttemptsSpent,
    int MaxAttempts,
    TimeSpan Wait,
    DateTimeOffset? DueAt);

/// <summary>
/// Pure policy behind the automatic gate-environment integration retry.
/// <para>
/// A gate environment failure (CAC-18) says the build/test gate crashed before
/// it reached test discovery: a toolchain, bundler, or runtime-version problem
/// on the host, never a verdict on the reviewed change. The passed review for
/// the delivery SHA therefore stays valid, and the only thing worth repeating
/// is the integration itself. This policy decides when to repeat it, how often,
/// and when to stop; every side effect lives in
/// <see cref="GateEnvironmentRetryService"/>.
/// </para>
/// <para>
/// Same inputs, same decision: the evaluation instant and the durable receipt
/// facts are arguments, so the matrix is testable without a clock or a disk.
/// </para>
/// </summary>
public static class GateEnvironmentRetryPolicy
{
    /// <summary>
    /// Lanes a delivery rests in after an integrate-on-delivery failure. The
    /// accepted lanes (Completed / Archive) belong to the accepted-integration
    /// backstop and are deliberately not touched here.
    /// </summary>
    public static readonly IReadOnlySet<string> Lanes = new HashSet<string>(StringComparer.Ordinal)
    {
        TaskStates.HumanReview,
        TaskStates.Escalated,
    };

    /// <param name="task">The delivered card.</param>
    /// <param name="integration">Its Git-derived integration verdict.</param>
    /// <param name="reviewPassed">
    /// Whether the latest settled review for the card's current delivery SHA
    /// ended in Pass. False refuses the retry: without a passed review there is
    /// nothing to reuse and the replay would integrate unreviewed work.
    /// </param>
    /// <param name="attemptsSpent">Automatic rungs already spent on this delivery SHA.</param>
    /// <param name="lastAttemptAt">Instant of the last retry, or null when there was none.</param>
    /// <param name="failedAt">Instant the gate-environment failure was recorded.</param>
    /// <param name="now">Evaluation instant.</param>
    public static GateEnvironmentRetryDecision Decide(
        TaskInfo task,
        TaskIntegrationStatus? integration,
        bool reviewPassed,
        int attemptsSpent,
        DateTimeOffset? lastAttemptAt,
        DateTimeOffset? failedAt,
        GateEnvironmentRetryOptions options,
        DateTimeOffset now)
    {
        ArgumentNullException.ThrowIfNull(task);
        ArgumentNullException.ThrowIfNull(options);

        var spent = Math.Clamp(attemptsSpent, 0, options.MaxAttempts);
        if (!options.Enabled)
            return Ignore(GateEnvironmentRetryReasons.Disabled, spent, options);
        if (!Lanes.Contains(task.State))
            return Ignore(GateEnvironmentRetryReasons.OutsideLanes, spent, options);
        if (!AcceptanceIntegrationPolicy.IsIntegrationRequired(task))
            return Ignore(GateEnvironmentRetryReasons.NoCodeDelivery, spent, options);
        // An acceptance transaction is already driving this card's merge, and
        // AcceptedIntegrationBackstopHostedService re-drives it after a restart.
        // Two owners would run two merges and spend a rung on someone else's
        // attempt.
        if (string.Equals(task.Phase, LifecyclePhases.Integrating, StringComparison.Ordinal))
            return Ignore(GateEnvironmentRetryReasons.AcceptanceIntegrationInFlight, spent, options);
        if (!IsGateEnvironmentFailure(integration))
            return Ignore(GateEnvironmentRetryReasons.NotAGateEnvironmentFailure, spent, options);
        if (!reviewPassed)
            return Ignore(GateEnvironmentRetryReasons.NoPassedReview, spent, options);

        if (spent >= options.MaxAttempts)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Park,
                GateEnvironmentRetryReasons.BudgetExhausted,
                spent,
                options.MaxAttempts,
                TimeSpan.Zero,
                DueAt: null);
        }

        // The first rung measures from the failure itself, every later rung from
        // the retry that produced the current failure. A missing anchor (legacy
        // evidence without a timestamp) is treated as due now rather than as a
        // reason to never retry.
        var anchor = spent == 0 ? failedAt ?? lastAttemptAt : lastAttemptAt ?? failedAt;
        if (anchor is not { } from)
        {
            return new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Retry,
                GateEnvironmentRetryReasons.DueWithoutAnchor,
                spent,
                options.MaxAttempts,
                TimeSpan.Zero,
                now);
        }

        var dueAt = from + options.Backoff[spent];
        return dueAt > now
            ? new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Wait,
                GateEnvironmentRetryReasons.Backoff,
                spent,
                options.MaxAttempts,
                dueAt - now,
                dueAt)
            : new GateEnvironmentRetryDecision(
                GateEnvironmentRetryAction.Retry,
                GateEnvironmentRetryReasons.Due,
                spent,
                options.MaxAttempts,
                TimeSpan.Zero,
                dueAt);
    }

    /// <summary>
    /// CAC-18 keeps a gate environment failure out of <c>conflict-skipped</c>,
    /// so the card stays Pending and only the typed failure code identifies it.
    /// </summary>
    public static bool IsGateEnvironmentFailure(TaskIntegrationStatus? integration)
        => string.Equals(
            integration?.Failure?.Code,
            AcceptedIntegrationFailureCodes.GateEnvironmentFailure,
            StringComparison.Ordinal);

    /// <summary>
    /// Operator-facing parked reason. Named after the environment failure it
    /// parks on, so the card answers "why is this stuck" without opening the
    /// pipeline evidence.
    /// </summary>
    public static string ParkedReason(
        int attempts,
        string integrationBranch,
        string? gateReason)
    {
        var detail = string.IsNullOrWhiteSpace(gateReason)
            ? "The build/test gate failed before verification reached test discovery."
            : gateReason.Trim();
        return $"Parked after {attempts} automatic gate-environment retries: the merge into "
               + $"{integrationBranch} keeps failing on the gate environment, not on the reviewed change. "
               + $"{detail} Fix the gate host, then use Retry integration to replay the passed review; "
               + "no new review round is needed.";
    }

    private static GateEnvironmentRetryDecision Ignore(
        string reason,
        int spent,
        GateEnvironmentRetryOptions options)
        => new(
            GateEnvironmentRetryAction.Ignore,
            reason,
            spent,
            options.MaxAttempts,
            TimeSpan.Zero,
            DueAt: null);
}
