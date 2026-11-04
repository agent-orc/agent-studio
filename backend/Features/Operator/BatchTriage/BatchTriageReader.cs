using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.AspNetCore.Http.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.Operator;

/// <summary>Response of <c>GET /api/operator/batch-triage</c>.</summary>
public sealed record BatchTriageResponse(
    DateTimeOffset GeneratedAtUtc, double PendingPushAlertMinutes, IReadOnlyList<BatchTriageProject> Projects);

/// <summary>
/// One project's open batch gate work. <see cref="Plan"/>, every
/// <see cref="BatchTriageFlight.State"/> and <see cref="BatchTriageMember.State"/>
/// are the verbatim entries of the project's <c>batch-gate/state</c> response;
/// <see cref="QueueStalls"/> is its <c>batch-gate/queue-stalls</c> response.
/// </summary>
public sealed record BatchTriageProject(
    string Project, bool BatchGateEnabled, JsonElement Plan,
    IReadOnlyList<BatchTriageFlight> Flights, IReadOnlyList<BatchTriageMember> Members,
    IReadOnlyList<string> RedMembers, IReadOnlyList<string> IsolatingMembers,
    IReadOnlyList<BatchTriagePendingPush> PendingPushes, GateQueueStallReport QueueStalls);

public sealed record BatchTriageFlight(
    string BatchId, string? ParentBatchId, string Phase, string? PhaseReason, bool Isolating,
    IReadOnlyList<string> MemberKeys, JsonElement State);

public sealed record BatchTriageMember(
    string TaskKey, string Status, string? BatchId, bool Red, bool Isolating,
    BatchTriageOperation NextOperation, JsonElement State);

/// <summary>A pending integration push; <see cref="Overdue"/> is the state route's <c>ageAlert</c>.</summary>
public sealed record BatchTriagePendingPush(
    string JobId, string? ApprovedSha, DateTimeOffset SinceUtc, double AgeMinutes,
    double AlertAfterMinutes, bool Overdue, string Reason);

/// <summary>
/// Builds the cross-project triage view from the same per-project state the
/// batch gate routes return, so the two never disagree. It reads only: no Git,
/// no lease, no store write.
/// </summary>
public sealed class BatchTriageReader(
    BatchGatePilotService pilot, BatchIntegrationReservationService reservations,
    ProjectSettingsService settings, TimeProvider time, IOptions<JsonOptions> json)
{
    /// <summary>Flights read per project; the state route's own upper bound.</summary>
    public const int FlightHistory = 100;

    private static readonly HashSet<string> OpenStatuses = new(StringComparer.Ordinal) { "pending", "in-batch", "pre-check-red" };

    public BatchTriageResponse Read(IEnumerable<string> projects)
    {
        var alertMinutes = reservations.AlertAfter.TotalMinutes;
        return new BatchTriageResponse(time.GetUtcNow(), alertMinutes,
            projects.Distinct(StringComparer.OrdinalIgnoreCase).Order(StringComparer.OrdinalIgnoreCase)
                .Select(project => ReadProject(project, alertMinutes)).ToArray());
    }

    private BatchTriageProject ReadProject(string project, double alertMinutes)
    {
        var state = JsonSerializer.SerializeToElement(pilot.State(project, FlightHistory), json.Value.SerializerOptions);
        var plan = state.GetProperty("plan");
        var batches = state.GetProperty("batches").EnumerateArray().ToArray();
        var facts = batches.Select(batch => new BatchTriageFlightFacts(
            Text(batch, "batchId")!, Text(batch, "parentBatchId"), Text(batch, "phase")!)).ToArray();
        var flights = batches.Zip(facts)
            .Where(item => BatchTriagePolicy.IsOpen(item.Second, facts))
            .Select(item => new BatchTriageFlight(item.Second.BatchId, item.Second.ParentBatchId,
                item.Second.Phase, Text(item.First, "phaseReason"),
                BatchTriagePolicy.IsIsolating(item.Second, facts),
                item.First.GetProperty("members").EnumerateArray().Select(member => Text(member, "taskKey")!).ToArray(),
                item.First))
            .ToArray();
        var isolating = flights.Where(flight => flight.Isolating)
            .SelectMany(flight => flight.MemberKeys).ToHashSet(StringComparer.Ordinal);
        var inOpenFlight = flights.SelectMany(flight => flight.MemberKeys).ToHashSet(StringComparer.Ordinal);
        var parents = facts.ToDictionary(flight => flight.BatchId, flight => flight.ParentBatchId, StringComparer.Ordinal);
        // The newest pending record of a card carries its current result SHA.
        var pendingShas = state.GetProperty("pending").EnumerateArray()
            .GroupBy(item => Text(item, "taskKey")!, StringComparer.Ordinal)
            .ToDictionary(group => group.Key, group => Text(group.Last(), "resultSha"), StringComparer.Ordinal);
        var findings = state.GetProperty("isolationFindings").EnumerateArray().Select(item => new Finding(
                Text(item, "taskKey")!, Text(item, "parentBatchId")!, Text(item, "resultSha"), Text(item, "status")!,
                item.GetProperty("recordedAtUtc").GetDateTimeOffset()))
            .ToLookup(item => item.TaskKey, StringComparer.Ordinal);
        var pushes = state.GetProperty("pushWaits").EnumerateArray().Select(wait => new BatchTriagePendingPush(
            Text(wait, "jobId")!, Text(wait, "approvedSha"), wait.GetProperty("sinceUtc").GetDateTimeOffset(),
            wait.GetProperty("ageMinutes").GetDouble(), alertMinutes, wait.GetProperty("ageAlert").GetBoolean(),
            Text(wait, "reason")!)).ToArray();
        var projectFacts = new BatchTriageProjectFacts(settings.Get(project).BatchGate.Enabled,
            pushes.Length > 0, flights.Length > 0, Text(plan, "decision"));
        var phases = flights.ToDictionary(flight => flight.BatchId, StringComparer.Ordinal);
        var members = state.GetProperty("members").EnumerateObject()
            .Where(entry => OpenStatuses.Contains(Text(entry.Value, "status") ?? "") || inOpenFlight.Contains(entry.Name))
            .Select(entry =>
            {
                var status = Text(entry.Value, "status")!;
                var batchId = Text(entry.Value, "batchId");
                var flight = batchId is null ? null : phases.GetValueOrDefault(batchId);
                var red = status == "pre-check-red"
                    || ProvenRed(findings[entry.Name], batchId, parents, pendingShas.GetValueOrDefault(entry.Name))
                    || entry.Value.TryGetProperty("preCheck", out var check) && check.ValueKind == JsonValueKind.Object
                    && Text(check, "outcome") == "red";
                var member = new BatchTriageMemberFacts(status, flight?.Phase ?? Text(entry.Value, "phase"),
                    flight?.PhaseReason, isolating.Contains(entry.Name), red);
                return new BatchTriageMember(entry.Name, status, batchId, red, member.Isolating,
                    BatchTriagePolicy.NextOperation(member, projectFacts), entry.Value);
            }).ToArray();
        return new BatchTriageProject(project, projectFacts.GateEnabled, plan, flights, members,
            members.Where(member => member.Red).Select(member => member.TaskKey).ToArray(),
            members.Where(member => member.Isolating).Select(member => member.TaskKey).ToArray(),
            pushes, pilot.QueueStallReport(project));
    }

    private sealed record Finding(string TaskKey, string ParentBatchId, string? ResultSha, string Status, DateTimeOffset At);

    /// <summary>
    /// True when the member's latest current isolation finding is proven red.
    /// A finding is current for a member in a flight of the same isolation
    /// (the red parent or its child), or for a pending member with the same
    /// result SHA; a later delivery is not red by an older finding.
    /// </summary>
    private static bool ProvenRed(IEnumerable<Finding> findings, string? batchId,
        IReadOnlyDictionary<string, string?> parents, string? pendingSha)
        => findings.Where(finding => batchId is not null
                ? finding.ParentBatchId == batchId || finding.ParentBatchId == parents.GetValueOrDefault(batchId)
                : pendingSha is not null && string.Equals(finding.ResultSha, pendingSha, StringComparison.OrdinalIgnoreCase))
            .MaxBy(finding => finding.At)?.Status == "proven-red";

    private static string? Text(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;
}
