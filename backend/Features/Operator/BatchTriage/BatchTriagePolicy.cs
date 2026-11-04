namespace AgentStudio.Operator;

/// <summary>The member operations the triage view may name (OP-4, AGT-3462).</summary>
public static class BatchTriageOperations
{
    public const string Eject = "eject";
    public const string Wait = "wait";
    public const string CloseNow = "close-now";
}

/// <summary>One flight as the per-project state route reports it.</summary>
public sealed record BatchTriageFlightFacts(string BatchId, string? ParentBatchId, string Phase);

/// <summary>
/// One open member: its state-route status, the phase and phase reason of the
/// flight it sits in, whether that flight isolates, and whether the member is
/// proven or pre-checked red.
/// </summary>
public sealed record BatchTriageMemberFacts(
    string Status, string? FlightPhase, string? FlightPhaseReason, bool Isolating, bool Red);

/// <summary>Project facts that hold every member: gate on, a push pending, a flight open, the planner's decision.</summary>
public sealed record BatchTriageProjectFacts(
    bool GateEnabled, bool PushPending, bool FlightOpen, string? PlanDecision);

/// <summary>The next safe operation for a member, a stable reason code, and one sentence for the operator.</summary>
public sealed record BatchTriageOperation(string Operation, string Reason, string Detail);

/// <summary>
/// Pure triage decisions over existing batch gate state. A flight is open
/// until it is published or abandoned; a red isolation probe and a red parent
/// whose isolation already published a child are finished. A red parent and
/// its open children isolate. During isolation eject is the only member
/// action (Dossier guardrail "No rounds to members of a batch under
/// isolation"); otherwise the member waits, or a forming batch can close now.
/// </summary>
public static class BatchTriagePolicy
{
    public static bool IsOpen(BatchTriageFlightFacts flight, IReadOnlyCollection<BatchTriageFlightFacts> all)
        => flight.Phase switch
        {
            "published" or "abandoned" => false,
            "red" when flight.ParentBatchId is not null => false,
            "red" => !all.Any(child => child.ParentBatchId == flight.BatchId && child.Phase == "published"),
            _ => true,
        };

    public static bool IsIsolating(BatchTriageFlightFacts flight, IReadOnlyCollection<BatchTriageFlightFacts> all)
        => IsOpen(flight, all) && (flight.ParentBatchId is not null || flight.Phase == "red");

    public static BatchTriageOperation NextOperation(BatchTriageMemberFacts member, BatchTriageProjectFacts project)
    {
        if (member.Isolating)
            return new(BatchTriageOperations.Eject, "isolation-active",
                "The flight isolates a red batch; eject is the only member action until isolation ends.");
        if (member.Red)
            return new(BatchTriageOperations.Wait, "red-returns-to-agent",
                "The member is red; the gate returns it to its agent with the failing evidence.");
        return member.Status switch
        {
            "in-batch" => InFlight(member),
            "fallback" => new(BatchTriageOperations.Wait, "per-task-gate",
                "The member left its batch and takes its own per-task gate."),
            "pending" => Pending(project),
            _ => new(BatchTriageOperations.Wait, "unknown-status",
                $"The member status '{member.Status}' has no triage rule; wait and read the state route."),
        };
    }

    private static BatchTriageOperation InFlight(BatchTriageMemberFacts member) => member.FlightPhase switch
    {
        "paused" when member.FlightPhaseReason == "integration-branch-red" => new(BatchTriageOperations.Wait,
            "integration-branch-red", "The integration branch is red; the batch resumes or rebuilds when the base changes."),
        "paused" => new(BatchTriageOperations.Wait, "flight-paused",
            "The flight is paused; the next planner tick returns its members to their per-task gates."),
        "green" or "publishing" => new(BatchTriageOperations.Wait, "flight-publishing",
            "The flight is green and publishing."),
        _ => new(BatchTriageOperations.Wait, "flight-running", "The flight is running its gate."),
    };

    private static BatchTriageOperation Pending(BatchTriageProjectFacts project)
    {
        if (!project.GateEnabled)
            return new(BatchTriageOperations.Wait, "gate-disabled",
                "The batch gate is off for this project; the card takes its per-task gate.");
        if (project.PushPending)
            return new(BatchTriageOperations.Wait, "pending-push",
                "An approved integration push holds the project reservation; no batch forms until it reaches origin.");
        if (project.FlightOpen)
            return new(BatchTriageOperations.Wait, "flight-open",
                "A flight of this project is open; one batch runs per project.");
        if (project.PlanDecision == "wait")
            return new(BatchTriageOperations.CloseNow, "batch-forming",
                "The planner waits for the target size or deadline; close-now forms the batch from the waiting cards.");
        return new(BatchTriageOperations.Wait, "planner",
            "The planner places the card on its next tick.");
    }
}
