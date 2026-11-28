using AgentStudio.Dispatch;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;

namespace AgentStudio.Operator;

/// <summary>Reads scoped, current ownership evidence without running admission, Git or recovery.</summary>
public sealed class CodingCapacityWaitReader(DeliveryIntegrationQueueBoard queue,
    BatchIntegrationReservationService? integration = null, GateSlotUsageTracker? gates = null,
    GateRunLedger? ledger = null, DispatcherShadowService? dispatch = null, AttemptAuthorityService? authority = null)
{
    internal const int MaxDeliveries = 50;
    private const string LostOwnership = "confirmed-lost-ownership";
    private static readonly TimeSpan RecheckInterval = MonolithTransitionAdapter.IntegrationHoldRecheckInterval;

    internal CapacityProjectWait Read(string project, ProjectSettings settings,
        IReadOnlyList<TaskInfo> tasks, DateTimeOffset now)
    {
        var byId = tasks.Where(card => !card.Fixture && string.Equals(card.ProjectName, project, StringComparison.OrdinalIgnoreCase))
            .GroupBy(card => card.Id, StringComparer.OrdinalIgnoreCase).Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.Single(), StringComparer.OrdinalIgnoreCase);
        var snapshot = queue.ReadProject(project);
        var integrationFinished = queue.LastCompletion(project);
        var queued = (snapshot?.Rows ?? []).Where(row => byId.ContainsKey(row.JobId))
            .Where(row => row.Activity == DeliveryIntegrationQueueBoard.Waiting
                || integrationFinished is null || row.SinceUtc > integrationFinished)
            .Take(MaxDeliveries + 1).ToArray();
        var receipts = new Dictionary<string, IntegrationConflictReceipt?>(StringComparer.OrdinalIgnoreCase);
        var truncated = queued.Length > MaxDeliveries;
        var deliveries = queued.Take(MaxDeliveries).Select(ProjectDelivery).ToArray();
        var counts = deliveries.SelectMany(row => row.Reservations.Select(hold => (Holder: hold.CardKey, Dependent: row.CardKey)))
            .ToLookup(pair => pair.Holder, pair => pair.Dependent, StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < deliveries.Length; i++)
        {
            var row = deliveries[i];
            deliveries[i] = row with
            {
                Reservations = row.Reservations.Select(hold => hold with
                    { DependentCount = counts[hold.CardKey].Distinct(StringComparer.OrdinalIgnoreCase).Count() }).ToArray(),
            };
        }

        var gate = gates?.ReadHolder(project);
        var gateOwner = gate is { } activeGate ? new CapacityWaitOwner("gate", activeGate.GateId, activeGate.SinceUtc) : null;
        var active = deliveries.FirstOrDefault(row => row.QueuePhase != DeliveryIntegrationQueueBoard.Waiting
            && row.Status != LostOwnership);
        var integrationOwner = integration?.ReadHolder(project) is { } holder
            ? new CapacityWaitOwner(holder.Kind, holder.TaskKey, holder.SinceUtc)
            : active?.QueueSinceUtc is { } since
                ? new CapacityWaitOwner(active.QueuePhase, active.CardKey, since) : null;
        var (plan, lease) = dispatch?.ReadActivePlan(project, settings.DispatchMode, now) ?? default;
        var finished = ledger?.LastObservedFinish(project);
        DateTimeOffset?[] progress = [finished, gateOwner?.SinceUtc, integrationOwner?.SinceUtc, integrationFinished];
        return new(settings.DispatchMode,
            plan is null ? null : new(plan.DecisionId, plan.Plan.Persona, plan.At),
            lease is null ? null : new(lease.Persona, lease.Fence, lease.ExpiresAt),
            gateOwner, integrationOwner, progress.Where(at => at <= now).DefaultIfEmpty().Max(),
            new(now, snapshot?.PublishedAtUtc, finished, gateOwner?.SinceUtc, integrationOwner?.SinceUtc, integrationFinished),
            deliveries, truncated);

        CapacityDeliveryWait ProjectDelivery(DeliveryIntegrationQueueRow row)
        {
            var card = byId[row.JobId];
            var knownGeneration = row.ReviewAttemptId is not null || row.ResultSha is not null;
            var lost = row.ReviewAttemptId is { } attempt && row.ResultSha is { } sha
                && authority?.HasSupersededReviewedDelivery(CodingCapacityProjection.Key(card), attempt, sha) == true;
            var fresh = row.ResultSha is not null && row.HoldsObservedAtUtc is { } observed
                && observed <= now && now - observed <= RecheckInterval * 2;
            CapacityWaitHolder[] holds = lost || !fresh ? [] : row.Holds.Take(ObservedConflictHold.ReservationLimit)
                .Select(hold => ConfirmHold(hold, row.Target)).OfType<CapacityWaitHolder>().ToArray();
            if (!lost && fresh && row.Holds.Count > ObservedConflictHold.ReservationLimit) truncated = true;
            var holding = holds.Length > 0;
            return new(CodingCapacityProjection.Key(card), card.State,
                knownGeneration ? new(row.ReviewAttemptId, row.ResultSha) : null, row.Activity,
                row.SinceUtc, row.SinceUtc is { } entered && entered <= now ? (now - entered).TotalSeconds : null,
                holding ? "intentional-wait" : lost ? LostOwnership : "unknown",
                holding ? "wait-for-holder" : lost ? "reviewed-delivery-superseded" : "insufficient-evidence",
                holds, row.HoldsObservedAtUtc, holding ? now + RecheckInterval : null);
        }

        CapacityWaitHolder? ConfirmHold(ObservedConflictHold hold, string? target)
        {
            if (target != hold.Target || !byId.TryGetValue(hold.Holder, out var card))
                return null;
            if (!receipts.TryGetValue(hold.Holder, out var receipt))
                receipts[hold.Holder] = receipt = IntegrationConflictRecovery.ReadReservation(card);
            if (receipt is null || receipt.Target != hold.Target || receipt.OriginalDeliveredAtUtc != hold.SinceUtc
                || receipt.DeliverySha != hold.ReceiptDeliverySha) return null;
            var files = (receipt.ReservedPaths ?? receipt.Result.ConflictedFiles)
                .Intersect(hold.OverlappingPaths, StringComparer.Ordinal).ToArray();
            return files.Length == 0 ? null : new(CodingCapacityProjection.Key(card),
                files.Take(ObservedConflictHold.FileLimit).ToArray(), hold.SinceUtc, null, 0,
                hold.OverlappingPaths.Count > ObservedConflictHold.FileLimit);
        }
    }
}
