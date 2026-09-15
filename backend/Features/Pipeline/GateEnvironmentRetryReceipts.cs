namespace AgentStudio.Pipeline;

/// <summary>Retry budget already spent on one delivery SHA.</summary>
/// <param name="Count">Retry receipts written for this delivery SHA.</param>
/// <param name="LastAt">Instant of the newest receipt, or null when there is none.</param>
/// <param name="Parked">A parked receipt for this delivery SHA already exists.</param>
public sealed record GateEnvironmentRetryLedger(int Count, DateTimeOffset? LastAt, bool Parked);

/// <summary>
/// AGT-2824 - reads the gate-environment retry rail's own timeline receipts.
///
/// <para>The retry ledger is deliberately the timeline and not a new sidecar:
/// <c>pipeline-execution.json</c> keeps one row per step id, so a replay
/// overwrites the merge step instead of appending to it and cannot carry a
/// count. The receipts are scoped by <c>deliverySha</c>, so a new delivery
/// starts with a fresh budget without anything having to reset state.</para>
///
/// <para>Same shape and reasoning as <see cref="AcceptanceRailReceipts"/>: the
/// rail that spends the budget and the surface that reports it read one
/// definition.</para>
/// </summary>
public static class GateEnvironmentRetryReceipts
{
    /// <summary>Detail value on receipts written by the periodic sweep.</summary>
    public const string SweepSource = "gate-environment-retry-sweep";

    /// <summary>Detail value on receipts written by the operator action.</summary>
    public const string OperatorSource = "gate-environment-retry-operator";

    /// <summary>Detail key carrying the delivery SHA a receipt belongs to.</summary>
    public const string DeliveryShaDetail = "deliverySha";

    public static GateEnvironmentRetryLedger Read(
        TimelineLog timeline,
        string folderPath,
        string? deliverySha)
    {
        ArgumentNullException.ThrowIfNull(timeline);
        if (string.IsNullOrWhiteSpace(deliverySha))
            return new GateEnvironmentRetryLedger(0, null, false);

        var attempts = new List<DateTimeOffset>();
        var parked = false;
        foreach (var entry in timeline.ReadAll(folderPath))
        {
            if (!BelongsTo(entry, deliverySha)) continue;
            if (entry.Kind == TimelineEventKinds.IntegrationRetryAttempted)
                attempts.Add(Utc(entry.Ts));
            else if (entry.Kind == TimelineEventKinds.IntegrationRetryExhausted)
                parked = true;
        }

        return new GateEnvironmentRetryLedger(
            attempts.Count,
            attempts.Count == 0 ? null : attempts.Max(),
            parked);
    }

    private static bool BelongsTo(TimelineEvent entry, string deliverySha)
        => string.Equals(
            entry.Details?.GetValueOrDefault(DeliveryShaDetail)?.Trim(),
            deliverySha.Trim(),
            StringComparison.OrdinalIgnoreCase);

    private static DateTimeOffset Utc(DateTime value)
        => new(value.Kind == DateTimeKind.Utc ? value : value.ToUniversalTime());
}
