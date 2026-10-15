using System.Text.Json;
using AgentStudio.TaskServer.Contracts.BatchGate;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>Facts the authority collects for one batch planning occurrence of a project scope.</summary>
public sealed record BatchPlanFacts(
    string Project, string ScopeKey, BatchPlanInput Snapshot,
    BatchPlanDecision? PriorDecision, string AdvisorMode);

/// <summary>Rendered advisor request. The authority renders the prompt; the decider only sends it.</summary>
public sealed record BatchAdvisorRequest(
    string Prompt, string CliType, string? Model, int TimeoutSeconds);

/// <summary>Payload of a <c>BatchPlan</c> orchestration occurrence.</summary>
public sealed record BatchPlanOccurrencePayload(
    BatchPlanFacts Facts, BatchAdvisorRequest? Advisor);

/// <summary>Answer of the advisor model call, or the reason why there is none.</summary>
public sealed record BatchAdvisorReply(
    string? Answer, string Model, DateTimeOffset AtUtc, string? Failure = null);

/// <summary>Deterministic step before an optional advisor call.</summary>
public sealed record BatchPlanStep(BatchPlanDecision Decision, bool ConsultAdvisor, string Rule);

/// <summary>
/// The whole batch planning decision of AGT-3052: look-ahead plan, advisor veto
/// holds, advisor merge, and the authority floors an applied intent must meet.
/// The monolith tick and the Orchestrator Engine run exactly this code.
/// </summary>
public static class BatchPlanDecisionPolicy
{
    /// <summary>Placeholder in a rendered advisor prompt that the decider replaces with <see cref="AdvisorInput"/>.</summary>
    public const string EngineInputMarker = "@@batch_input@@";
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static BatchPlanStep Decide(BatchPlanFacts facts)
    {
        var snapshot = facts.Snapshot;
        var prior = facts.PriorDecision;
        var vetoHold = prior is
        {
            Decision: "wait", Advisor.Verdict: "veto", CloseAtUtc: not null,
        } && !snapshot.CloseNow;
        var holdExpired = vetoHold && prior!.CloseAtUtc!.Value <= snapshot.NowUtc;
        var decision = BatchGateLookAheadPlanner.Plan(holdExpired
            ? snapshot with { CloseNow = true } : snapshot);
        if (vetoHold && !holdExpired)
            return new(decision with
            {
                Decision = "wait", Reason = prior!.Reason,
                CloseAtUtc = prior.CloseAtUtc, Advisor = prior.Advisor,
            }, false, "advisor-veto-hold");
        var consult = !holdExpired && !snapshot.CloseNow
            && facts.AdvisorMode is "advise" or "enforce"
            && decision.Decision == "close";
        return new(decision, consult, holdExpired ? "advisor-veto-expired" : "look-ahead");
    }

    /// <summary>Merges an advisor reply. Any invalid or missing answer keeps the deterministic decision.</summary>
    public static BatchPlanDecision Advise(BatchPlanFacts facts, BatchPlanDecision deterministic,
        BatchAdvisorReply reply)
    {
        var snapshot = facts.Snapshot;
        BatchPlanDecision Fallback(string reason) => deterministic with
        {
            Advisor = new BatchAdvisorView(facts.AdvisorMode, "fallback", [reason],
                reply.Model, reply.AtUtc),
        };
        if (reply.Failure is not null || reply.Answer is null)
            return Fallback(reply.Failure ?? "advisor returned no answer");
        var answer = reply.Answer.Trim();
        if (!BatchGateAdvisorPolicy.TryApply(answer, deterministic,
                snapshot.Waiting.Select(card => card.TaskKey).ToArray(),
                snapshot.MinimumSize, snapshot.MaximumSize, snapshot.NowUtc,
                snapshot.LookAheadMinutes, out var adjusted, out var error))
            return Fallback(error);
        var verdict = JsonSerializer.Deserialize<BatchAdvisorAnswer>(answer, Json)!;
        return (facts.AdvisorMode == "enforce" ? adjusted : deterministic) with
        {
            Advisor = new BatchAdvisorView(facts.AdvisorMode, verdict.Verdict,
                verdict.Reasons, reply.Model, reply.AtUtc),
        };
    }

    /// <summary>Planner input the advisor prompt carries; both deciders send the same shape.</summary>
    public static string AdvisorInput(BatchPlanInput snapshot, BatchPlanDecision deterministic)
        => JsonSerializer.Serialize(new
        {
            deterministic, snapshot.Waiting, snapshot.Upcoming,
            snapshot.MinimumSize, snapshot.MaximumSize,
            snapshot.LookAheadMinutes, snapshot.RiskPolicy, snapshot.Overlap,
        });

    /// <summary>Comparable action of a decision; shadow receipts compare this value.</summary>
    public static string Signature(BatchPlanDecision decision)
        => decision.Decision + "[" + string.Join(",", decision.ProposedMembers) + "]";

    /// <summary>
    /// Authority floors for a decision the authority did not compute itself.
    /// Members come from the eligible snapshot, a close respects the size
    /// bounds, and a hold never outlives the configured windows.
    /// </summary>
    public static bool Admissible(BatchPlanFacts facts, BatchPlanDecision decision, out string error)
    {
        var snapshot = facts.Snapshot;
        var members = decision.ProposedMembers;
        var waiting = snapshot.Waiting.Select(card => card.TaskKey).ToHashSet(StringComparer.Ordinal);
        error = decision.Decision is not ("idle" or "wait" or "close" or "per-task")
                ? "unknown batch decision"
            : members.Distinct(StringComparer.Ordinal).Count() != members.Count
                ? "duplicate batch member"
            : members.Any(key => !waiting.Contains(key))
                ? "batch member is not eligible"
            : decision.Decision == "close"
              && (members.Count < snapshot.MinimumSize || members.Count > snapshot.MaximumSize)
                ? "batch size outside configured bounds"
            : decision.Decision == "per-task" && members.Count == 0
                ? "per-task decision names no card"
            : decision.Decision == "idle" && (members.Count > 0 || waiting.Count > 0)
                ? "idle decision with eligible cards"
            : decision.Decision == "wait" && decision.CloseAtUtc is { } until
              && until > snapshot.NowUtc.AddMinutes(
                  Math.Max(snapshot.DeadlineMinutes, snapshot.LookAheadMinutes))
                ? "hold exceeds configured window"
            : "";
        return error.Length == 0;
    }
}
