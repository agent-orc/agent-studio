using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Pipeline;

/// <summary>
/// AGT-3710: the flight queue of formed batches. Each formed batch has a
/// durable order key, so the start pass, recovery, and the batch gate state
/// agree on one first in, first out order that never depends on how the
/// store enumerates its hashed batch folders.
/// </summary>
public static class BatchGateFlightQueue
{
    /// <summary>
    /// Formed batches in start order (<see cref="BatchGateStartPolicy.Order"/>).
    /// <paramref name="deferred"/> names the batch of a project whose flight was
    /// cancelled at its lease deadline, or null.
    /// </summary>
    public static IReadOnlyList<BatchGateManifest> Order(BatchGateStore store,
        IEnumerable<BatchGateManifest> formed, Func<string, string?> deferred)
    {
        var byId = formed.DistinctBy(manifest => manifest.BatchId, StringComparer.Ordinal)
            .ToDictionary(manifest => manifest.BatchId, StringComparer.Ordinal);
        var order = MonolithTransitionAdapter.BatchStartOrder(byId.Values.Select(manifest =>
            new BatchGateStartCandidate(manifest.BatchId, QueuedSince(store, manifest),
                manifest.Members.Count == 0 ? long.MaxValue
                    : manifest.Members.Min(member => member.EnqueueSequence),
                manifest.StackedOnBatchId is not null,
                manifest.ParentBatchId is not null,
                deferred(manifest.Scope.Project) == manifest.BatchId)));
        return order.Select(id => byId[id]).ToArray();
    }

    /// <summary>
    /// One-based flight queue position of each formed batch within its project.
    /// </summary>
    public static IReadOnlyDictionary<string, int> Positions(BatchGateStore store,
        IEnumerable<BatchGateManifest> formed, Func<string, string?> deferred)
    {
        var positions = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var project in Order(store, formed, deferred)
                     .GroupBy(manifest => manifest.Scope.Project, StringComparer.OrdinalIgnoreCase))
        {
            var position = 0;
            foreach (var manifest in project) positions[manifest.BatchId] = ++position;
        }
        return positions;
    }

    /// <summary>
    /// The formation time that orders a batch: its recorded queue time, or the
    /// close time of the first batch of its re-form chain. A batch re-formed on a
    /// moved base, or repacked from older formed batches, keeps their place.
    /// </summary>
    public static DateTimeOffset QueuedSince(BatchGateStore store, BatchGateManifest manifest)
    {
        var since = Earliest(manifest);
        var previous = manifest.ReformedFromBatchId;
        for (var hops = 0; previous is not null && hops < 32; hops++)
        {
            BatchGateManifest predecessor;
            try { predecessor = store.ReadManifest(previous); }
            catch (Exception ex) when (ex is IOException or InvalidDataException
                or System.Text.Json.JsonException or UnauthorizedAccessException)
            {
                AgentStudio.Diagnostics.SilentCatch.Note(ex, "Re-formed batch predecessor unreadable");
                break;
            }
            if (Earliest(predecessor) < since) since = Earliest(predecessor);
            previous = predecessor.ReformedFromBatchId;
        }
        return since;
    }

    private static DateTimeOffset Earliest(BatchGateManifest manifest)
        => manifest.QueuedSinceUtc is { } queued && queued < manifest.ClosedAtUtc
            ? queued : manifest.ClosedAtUtc;
}
