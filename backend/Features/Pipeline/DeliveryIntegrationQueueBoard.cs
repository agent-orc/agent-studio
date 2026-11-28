using System.Collections.Concurrent;
using System.Globalization;

namespace AgentStudio.Pipeline;

/// <summary>One row of a project's delivery integration queue, in drain order.</summary>
public sealed record DeliveryIntegrationQueueRow(string JobId, string Activity, DateTimeOffset? SinceUtc)
{
    internal string? ReviewAttemptId { get; init; }
    internal string? ResultSha { get; init; }
    internal string? Target { get; init; }
    internal DateTimeOffset? HoldsObservedAtUtc { get; init; }
    internal IReadOnlyList<ObservedConflictHold> Holds { get; init; } = [];
}

internal sealed record DeliveryIntegrationQueueSnapshot(
    IReadOnlyList<DeliveryIntegrationQueueRow> Rows, DateTimeOffset PublishedAtUtc);

/// <summary>Where one card stands in its project's delivery integration queue.</summary>
public sealed record DeliveryIntegrationQueuePosition(
    int Position, int Total, IReadOnlyList<DeliveryIntegrationQueueRow> Ahead, string? PreviousFailure,
    string Activity = DeliveryIntegrationQueueBoard.Waiting)
{
    /// <summary>
    /// The queue sentence on the card (AGT-4020): its position, what is in
    /// front of it and, after a failed attempt, why that attempt failed.
    /// </summary>
    public string Describe()
    {
        var text = Ahead.Count == 0
            ? Activity == DeliveryIntegrationQueueBoard.Waiting
                ? $"integration queue position 1 of {Total}; nothing in front"
                : $"integration queue position 1 of {Total}; {Activity} now"
            : $"integration queue position {Position} of {Total}; in front: "
              + string.Join(", ", Ahead.Take(MaxNamed).Select(DescribeRow))
              + (Ahead.Count > MaxNamed ? $" and {Ahead.Count - MaxNamed} more" : "");
        return string.IsNullOrWhiteSpace(PreviousFailure)
            ? text
            : $"{text}; previous attempt failed: {Trim(PreviousFailure)}";
    }

    private const int MaxNamed = 4;

    private static string DescribeRow(DeliveryIntegrationQueueRow row)
        => row.Activity == DeliveryIntegrationQueueBoard.Waiting
            ? row.JobId
            : row.SinceUtc is { } since
                ? $"{row.JobId} ({row.Activity} since {since.UtcDateTime.ToString("HH:mm:ss'Z'", CultureInfo.InvariantCulture)})"
                : $"{row.JobId} ({row.Activity})";

    private static string Trim(string value)
    {
        var line = value.ReplaceLineEndings(" ").Trim();
        return line.Length <= 240 ? line : line[..237] + "...";
    }
}

/// <summary>
/// The live delivery integration queue per project, published by
/// <see cref="RemoteDeliveryIntegrationCoordinator"/> and read by the card's
/// live status. Before AGT-4020 a waiting card said only "waiting for delivery
/// integration", so six accepted cards behind one 55-minute gate looked idle.
/// </summary>
public sealed class DeliveryIntegrationQueueBoard(TimeProvider? time = null)
{
    public const string Waiting = "waiting";
    public const string Integrating = "integrating";

    public static string CompilingTrain(int cards) => $"compiling the combined tree of {cards} cards";

    private readonly ConcurrentDictionary<string, DeliveryIntegrationQueueSnapshot> _queues =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _failures = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _completions = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    internal DateTimeOffset ObservationTime => _time.GetUtcNow();

    public int WaitingCount => _queues.Values.Sum(snapshot => snapshot.Rows.Count(row => row.Activity == Waiting));

    public void Publish(string project, IReadOnlyList<DeliveryIntegrationQueueRow> rows)
        => _queues[project] = new(rows.ToArray(), ObservationTime);

    internal DeliveryIntegrationQueueSnapshot? ReadProject(string project)
        => _queues.GetValueOrDefault(project);

    internal void RecordCompletion(string project) => _completions[project] = ObservationTime;

    internal DateTimeOffset? LastCompletion(string project)
        => _completions.TryGetValue(project, out var completed) ? completed : null;

    /// <summary>A detached fleet snapshot, including current work and pending deliveries.</summary>
    public IReadOnlyDictionary<string, IReadOnlyList<DeliveryIntegrationQueueRow>> Read()
        => _queues.ToDictionary(item => item.Key,
            item => (IReadOnlyList<DeliveryIntegrationQueueRow>)item.Value.Rows.ToArray(),
            StringComparer.OrdinalIgnoreCase);

    public void Clear(string project) => _queues.TryRemove(project, out _);

    /// <summary>Remembers the failure of the card's last attempt; a success forgets it.</summary>
    public void RecordOutcome(string project, string jobId, string? failure)
    {
        var key = project + "\0" + jobId;
        if (string.IsNullOrWhiteSpace(failure)) _failures.TryRemove(key, out _);
        else _failures[key] = failure;
    }

    public DeliveryIntegrationQueuePosition? PositionOf(string project, string jobId)
    {
        if (!_queues.TryGetValue(project, out var snapshot)) return null;
        var rows = snapshot.Rows;
        var index = -1;
        for (var i = 0; i < rows.Count; i++)
        {
            if (!string.Equals(rows[i].JobId, jobId, StringComparison.OrdinalIgnoreCase)) continue;
            index = i;
            break;
        }
        if (index < 0) return null;
        _failures.TryGetValue(project + "\0" + jobId, out var failure);
        return new DeliveryIntegrationQueuePosition(index + 1, rows.Count, rows.Take(index).ToArray(), failure,
            rows[index].Activity);
    }
}
