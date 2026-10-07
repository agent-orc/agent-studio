using AgentStudio.Pipeline;
using AgentStudio.Shared;
using AgentStudio.Tasks;

namespace AgentStudio.Runner;

/// <summary>A read receipt. Its state is derived from authority and is never a lane verdict.</summary>
public sealed record SteeringFeedbackFact(
    string Identity,
    string Kind,
    string State,
    DateTime AtUtc,
    string? CommandId,
    string? AttemptId,
    string? OwningRunId,
    string? IncidentId,
    string Reason,
    bool Current,
    string? RunnerId = null,
    string? SettlementId = null);

public sealed record TaskSteeringFeedback(
    string TaskKey,
    string Lane,
    SteeringFeedbackFact? Current,
    IReadOnlyList<SteeringFeedbackFact> History);

/// <summary>
/// Rebuilds feedback on every read from stop receipts, fenced attempts and the
/// review settlement journal. No projection row can change task authority.
/// </summary>
public sealed class SteeringFeedbackProjection
{
    private readonly AttemptAuthorityService _attempts;
    private readonly RemoteRunStopRequestStore _stops;

    public SteeringFeedbackProjection(AttemptAuthorityService attempts, RemoteRunStopRequestStore stops)
    {
        _attempts = attempts;
        _stops = stops;
    }

    public TaskSteeringFeedback Build(TaskInfo task, bool includeArchived = true)
    {
        var authority = _attempts.GetTaskProjection(task.TaskKey, includeArchived);
        var facts = new List<SteeringFeedbackFact>();
        var runs = authority.RunAttempts.ToDictionary(run => run.AttemptId, StringComparer.Ordinal);
        string Root(string id)
        {
            var visited = new HashSet<string>(StringComparer.Ordinal);
            while (runs.TryGetValue(id, out var run) && run.SourceAttemptId is { Length: > 0 } source
                   && visited.Add(id)) id = source;
            return id;
        }

        foreach (var run in authority.RunAttempts)
        {
            var current = run.AttemptId == authority.CurrentRunAttempt?.AttemptId;
            var owner = run.Lease?.ExecutorId;
            var outage = IsRouteOutage(run.TerminalReason);
            // A bounded host/time identity joins simultaneous route failures
            // across tasks while the root attempt remains the drill-down key.
            var incident = outage && run.TerminalAt is { } outageAt
                ? RouteIncidentId(run.Lease?.HostId, outageAt, Root(run.AttemptId)) : null;
            facts.Add(new($"run:{run.AttemptId}:admitted", "run", "admitted", run.CreatedAt,
                null, run.AttemptId, run.AttemptId, incident, "Run authority granted.", current, owner));
            if (run.Lease is { } lease)
                facts.Add(new($"run:{run.AttemptId}:running", "run", "running", lease.AcquiredAt,
                    null, run.AttemptId, run.AttemptId, incident, "Runner owns the fenced attempt.", current, owner));
            if (run.TerminalAt is { } terminal)
            {
                var recovered = outage && authority.RunAttempts.Any(successor =>
                    successor.AttemptId != run.AttemptId
                    && Root(successor.AttemptId) == Root(run.AttemptId)
                    && successor.State == AttemptLifecycleState.Completed
                    && successor.CreatedAt >= terminal);
                var state = recovered ? "recovered"
                    : IsQuarantine(run.TerminalOutcome, run.TerminalReason) ? "quarantined"
                    : outage ? "unresolved"
                    : run.State == AttemptLifecycleState.Completed ? "consumed" : "rejected";
                facts.Add(new($"run:{run.AttemptId}:terminal", "run", state, terminal,
                    null, run.AttemptId, run.AttemptId, incident,
                    Bound(run.TerminalReason ?? run.TerminalOutcome ?? "Run settled."), current, owner));
            }
        }

        foreach (var stop in _stops.ListForTask(task.TaskKey))
        {
            var current = stop.AttemptId == authority.CurrentRunAttempt?.AttemptId
                          && stop.FencingToken == authority.CurrentRunAttempt?.LastFence;
            var owner = stop.RunnerId ?? stop.RequestedBy;
            facts.Add(new($"stop:{stop.CommandId}:requested", "stop", "requested", stop.RequestedAtUtc,
                stop.CommandId, stop.AttemptId, stop.AttemptId, null, Bound(stop.Reason), current, owner));
            if (stop.ObservedAtUtc is { } observed)
                facts.Add(new($"stop:{stop.CommandId}:observed", "stop", "consumed", observed,
                    stop.CommandId, stop.AttemptId, stop.AttemptId, null,
                    "Owning runner acknowledged the stop.", current, owner));
            if (stop.TerminalAtUtc is { } terminal)
                facts.Add(new($"stop:{stop.CommandId}:terminal", "stop",
                    stop.TerminalReason == "settled" && stop.ObservedAtUtc is not null
                        ? "consumed" : "rejected", terminal,
                    stop.CommandId, stop.AttemptId, stop.AttemptId, null,
                    Bound(stop.TerminalReason ?? "Stop settled."), current, owner));
        }

        foreach (var review in authority.ReviewAttempts)
        {
            var current = review.AttemptId == authority.CurrentReviewAttempt?.AttemptId;
            var owner = review.Lease?.ExecutorId;
            facts.Add(new($"review:{review.AttemptId}:admitted", "review", "admitted", review.CreatedAt,
                null, review.AttemptId, review.SourceRunAttemptId, null,
                "Review attempt admitted.", current, owner));
            var accepted = review.Reports.LastOrDefault(report => report.AuthorityStatus == AttemptWriteStatus.Accepted);
            if (accepted is null) continue;
            var journal = RemoteReviewSettlementJournal.Read(task.FolderPath, review.AttemptId);
            var stage = journal.Entry?.Delivery?.Stage
                        ?? (RemoteDeliverySettlementStore.Read(task.FolderPath) is { } sidecar
                            && sidecar.ReviewAttemptId == review.AttemptId ? sidecar.Stage : null);
            var state = journal.Status == RemoteReviewSettlementReadStatus.Repair
                        || journal.Entry?.RepairReason is not null ? "unresolved"
                : stage == RemoteDeliverySettlementStage.LaneSettled ? "recovered"
                : journal.Entry?.EvidenceComplete == true ? "consumed" : "requested";
            var reason = journal.Entry?.RepairReason ?? journal.Reason
                         ?? stage?.ToString() ?? "Review report accepted; delivery projection pending.";
            facts.Add(new($"review:{review.AttemptId}:settlement", "settlement", state,
                accepted.ReceivedAt, null, review.AttemptId,
                review.SourceRunAttemptId, null, Bound(reason), current, owner,
                accepted.IdempotencyKey));
        }

        var history = facts.DistinctBy(fact => fact.Identity, StringComparer.Ordinal)
            .OrderBy(fact => fact.AtUtc).ThenBy(fact => fact.Identity, StringComparer.Ordinal).ToArray();
        var currentFact = history.Where(fact => fact.Current)
            .OrderByDescending(fact => fact.AtUtc).ThenByDescending(fact => fact.Identity, StringComparer.Ordinal)
            .FirstOrDefault();
        return new(task.TaskKey, task.State, currentFact, history);
    }

    public static IReadOnlyList<TimelineEvent> Timeline(TaskSteeringFeedback feedback)
        => feedback.History.Select(fact => new TimelineEvent
        {
            Ts = fact.AtUtc,
            Kind = TimelineEventKinds.SteeringFeedback,
            Actor = "system",
            RunId = fact.OwningRunId,
            Summary = $"{fact.Kind}: {fact.State}",
            Details = new Dictionary<string, string>
            {
                ["identity"] = fact.Identity,
                ["state"] = fact.State,
                ["reason"] = fact.Reason,
                ["commandId"] = fact.CommandId ?? "",
                ["attemptId"] = fact.AttemptId ?? "",
                ["incidentId"] = fact.IncidentId ?? "",
                ["settlementId"] = fact.SettlementId ?? "",
                ["runnerId"] = fact.RunnerId ?? "",
                ["current"] = fact.Current ? "true" : "false",
            },
        }).ToArray();

    private static bool IsRouteOutage(string? reason)
        => reason is not null && (reason.Contains("lease", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("route", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("tunnel", StringComparison.OrdinalIgnoreCase)
            || reason.Contains("host", StringComparison.OrdinalIgnoreCase));

    private static bool IsQuarantine(string? outcome, string? reason)
        => (outcome ?? "").Contains("quarant", StringComparison.OrdinalIgnoreCase)
           || (reason ?? "").Contains("quarant", StringComparison.OrdinalIgnoreCase);

    private static string RouteIncidentId(string? runnerId, DateTime at, string fallbackAttemptId)
    {
        if (string.IsNullOrWhiteSpace(runnerId)) return $"route:attempt:{fallbackAttemptId}";
        var utc = at.ToUniversalTime();
        var bucket = new DateTime(utc.Year, utc.Month, utc.Day, utc.Hour,
            utc.Minute / 15 * 15, 0, DateTimeKind.Utc);
        return $"route:{runnerId}:{bucket:yyyyMMddHHmm}";
    }

    private static string Bound(string reason)
    {
        var singleLine = string.Join(' ', reason.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return singleLine.Length <= 240 ? singleLine : singleLine[..240];
    }
}
