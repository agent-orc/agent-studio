using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Dispatch;

public sealed record DispatchLiveRecordV1(DispatchShadowRecordV1 Record, long Fence,
    IReadOnlyList<string> ExpiredHolds);
public sealed record DispatchRunDecisionV1(string DecisionId, string Card, DateTimeOffset At,
    string Path, bool Admitted, string Reason, string? WaitsFor, string? AttemptId = null, bool Claimed = false);

public sealed partial class DispatchStore
{
    public DispatchLiveRecordV1? ReadLive(string project)
    {
        lock (PersonaGate(project)) return Read<DispatchLiveRecordV1>(Path.Combine(Project(project), "live.json"));
    }

    internal (DispatchLiveRecordV1? Plan, DispatchLeaseV1? Lease) ReadPlanAndLease(string project)
    {
        lock (PersonaGate(project)) return (ReadLive(project), ReadLease(project));
    }

    public bool WriteLive(DispatchLiveRecordV1 record, DateTimeOffset now)
    {
        var project = record.Record.Plan.Project;
        lock (PersonaGate(project))
        {
            var lease = ReadLease(project);
            if (lease is null || lease.Fence != record.Fence || lease.ExpiresAt <= now
                || lease.Persona != record.Record.Plan.Persona) return false;
            AppendShadow(record.Record with { Shadow = false });
            Write(Path.Combine(Project(project), "live.json"), record);
            return true;
        }
    }

    public void AppendRunDecision(string project, DispatchRunDecisionV1 decision)
    {
        lock (PersonaGate(project))
        {
            var path = Path.Combine(Project(project), "run-decisions.jsonl");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.AppendAllText(path, System.Text.Json.JsonSerializer.Serialize(decision, Json) + "\n");
        }
    }

    public IReadOnlyList<DispatchRunDecisionV1> ReadRunDecisions(string project)
    {
        lock (PersonaGate(project))
        {
            var path = Path.Combine(Project(project), "run-decisions.jsonl");
            return File.Exists(path) ? File.ReadLines(path).Select(line =>
                System.Text.Json.JsonSerializer.Deserialize<DispatchRunDecisionV1>(line, Json)!).ToArray() : [];
        }
    }

    public void BindRun(string project, string card, string decisionId, string? attemptId)
    {
        lock (PersonaGate(project))
        {
            var decision = ReadRunDecisions(project).LastOrDefault(d => d.DecisionId == decisionId && d.Card == card && d.Admitted);
            if (decision is not null && !decision.Claimed)
                AppendRunDecision(project, decision with { AttemptId = attemptId, Claimed = true });
        }
    }

    public string? AdmittingDecision(string project, string card)
        => ReadRunDecisions(project).LastOrDefault(d => d.Admitted && d.Claimed
            && d.Card.Equals(card, StringComparison.OrdinalIgnoreCase))?.DecisionId;
}
