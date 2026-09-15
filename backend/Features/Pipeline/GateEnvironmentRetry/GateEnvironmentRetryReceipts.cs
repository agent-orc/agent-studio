namespace AgentStudio.Pipeline;

/// <summary>Sources that may drive one gate-environment integration replay.</summary>
public static class GateEnvironmentRetrySources
{
    /// <summary>The periodic bounded ladder.</summary>
    public const string Sweep = "sweep";

    /// <summary>The explicit "Retry integration" operator action.</summary>
    public const string Operator = "operator";
}

/// <param name="AttemptsSpent">
/// Automatic rungs spent on the current ladder for this delivery SHA. An
/// operator retry starts a fresh ladder, so rungs before it do not count.
/// </param>
/// <param name="LastAttemptAt">Instant of the most recent replay from any source.</param>
/// <param name="Parked">Whether a parked receipt for this delivery SHA is already durable.</param>
public sealed record GateEnvironmentRetryLedger(
    int AttemptsSpent,
    DateTimeOffset? LastAttemptAt,
    bool Parked)
{
    public static GateEnvironmentRetryLedger Empty { get; } = new(0, null, false);
}

/// <summary>
/// Durable receipts for the gate-environment ladder (AGT-2824), read from and
/// written to the card's own timeline.
/// <para>
/// <c>pipeline-execution.json</c> cannot carry the count: it keeps exactly one
/// row per step id, so every replay overwrites the previous merge attempt. The
/// timeline is append-only, which is what a bounded budget needs, and it is
/// already the ledger the acceptance rail counts its own replays from.
/// </para>
/// <para>
/// Receipts are scoped to the delivery SHA. A new delivery is a new review and
/// therefore a new ladder; the old rungs must not shorten it.
/// </para>
/// </summary>
public static class GateEnvironmentRetryReceipts
{
    /// <summary>Detail key carrying the delivery SHA a receipt belongs to.</summary>
    public const string DeliveryShaKey = "deliverySha";

    /// <summary>Detail key carrying <see cref="GateEnvironmentRetrySources"/>.</summary>
    public const string SourceKey = "source";

    /// <summary>Detail key carrying the 1-based rung this receipt represents.</summary>
    public const string RungKey = "rung";

    public static GateEnvironmentRetryLedger Read(
        TimelineLog timeline,
        string folderPath,
        string? deliverySha)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrWhiteSpace(deliverySha)) return GateEnvironmentRetryLedger.Empty;

        var entries = timeline.ReadAll(folderPath)
            .Where(entry => IsForDelivery(entry, deliverySha))
            .OrderBy(entry => Utc(entry.Ts))
            .ToList();
        if (entries.Count == 0) return GateEnvironmentRetryLedger.Empty;

        var retries = entries
            .Where(entry => entry.Kind == TimelineEventKinds.IntegrationGateEnvironmentRetried)
            .ToList();

        // An explicit operator retry means "I fixed the host, try again": it
        // restarts the bounded ladder instead of arriving into an already spent
        // budget that would park the card on its very next failure.
        var lastOperatorRetry = retries.LastOrDefault(entry =>
            entry.Details?.GetValueOrDefault(SourceKey) == GateEnvironmentRetrySources.Operator);
        var spent = lastOperatorRetry is null
            ? retries.Count
            : retries.Count(entry => Utc(entry.Ts) > Utc(lastOperatorRetry.Ts));

        var parked = entries.Any(entry =>
            entry.Kind == TimelineEventKinds.IntegrationGateEnvironmentParked
            && (lastOperatorRetry is null || Utc(entry.Ts) > Utc(lastOperatorRetry.Ts)));

        return new GateEnvironmentRetryLedger(
            spent,
            retries.Count == 0 ? null : retries.Max(entry => Utc(entry.Ts)),
            parked);
    }

    public static void RecordRetry(
        TimelineLog timeline,
        string folderPath,
        string deliverySha,
        string source,
        int rung,
        string summary)
        => timeline.Append(
            folderPath,
            TimelineEventKinds.IntegrationGateEnvironmentRetried,
            TimelineActors.System,
            summary,
            details: new Dictionary<string, string>
            {
                [DeliveryShaKey] = deliverySha,
                [SourceKey] = source,
                [RungKey] = rung.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    public static void RecordParked(
        TimelineLog timeline,
        string folderPath,
        string deliverySha,
        int attempts,
        string reason)
        => timeline.Append(
            folderPath,
            TimelineEventKinds.IntegrationGateEnvironmentParked,
            TimelineActors.System,
            reason,
            details: new Dictionary<string, string>
            {
                [DeliveryShaKey] = deliverySha,
                ["attempts"] = attempts.ToString(System.Globalization.CultureInfo.InvariantCulture),
            });

    private static bool IsForDelivery(TimelineEvent entry, string deliverySha)
        => entry.Kind is TimelineEventKinds.IntegrationGateEnvironmentRetried
               or TimelineEventKinds.IntegrationGateEnvironmentParked
           && string.Equals(
               entry.Details?.GetValueOrDefault(DeliveryShaKey),
               deliverySha,
               StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset Utc(DateTime value)
        => new(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
}
