namespace AgentStudio.Orchestration.Decisions;

public enum GateDisposition { Pass, Reissue, Escalate }
public sealed record GateRuling(GateDisposition Action, IReadOnlyList<string> Findings, string Reason);

public static class GateTransitionPolicy
{
    public static GateRuling Completion(bool accepted, string completionReason, IReadOnlyList<string> findings,
        int used, int allowed)
    {
        if (accepted) return new(GateDisposition.Pass, [], completionReason);
        if (findings.Count == 0) return new(GateDisposition.Pass, [], "No unfinished-work evidence found.");
        return used >= allowed
            ? new(GateDisposition.Escalate, findings,
                $"Completion gate found unfinished-work evidence after {used} prior orchestrator reissue(s); user attention required.")
            : new(GateDisposition.Reissue, findings,
                $"Completion gate found {findings.Count} unfinished-work item(s); reissuing with the items foregrounded.");
    }

    public static GateRuling Evidence(IReadOnlyList<string> findings, int used, int allowed)
    {
        if (findings.Count == 0) return new(GateDisposition.Pass, [], "No evidence concerns found.");
        findings = findings.Take(8).ToArray();
        return used >= allowed
            ? new(GateDisposition.Escalate, findings,
                $"Evidence gate could not clear {findings.Count} unverified item(s) after {used} prior reissue(s); user attention required.")
            : new(GateDisposition.Reissue, findings,
                $"Evidence gate found {findings.Count} unverified item(s); reissuing with a verification demand instead of accepting with concerns.");
    }

    public static GateRuling Solution(IReadOnlyList<string> findings, int used, int allowed)
    {
        if (findings.Count == 0) return new(GateDisposition.Pass, [], "No blocking solution-quality concerns found.");
        findings = findings.Take(8).ToArray();
        return used >= allowed
            ? new(GateDisposition.Escalate, findings,
                $"Solution-quality gate could not clear {findings.Count} blocking concern(s) after {used} prior reissue(s); user attention required.")
            : new(GateDisposition.Reissue, findings,
                $"Solution-quality gate found {findings.Count} blocking concern(s); reissuing instead of accepting with concerns.");
    }

    public static GateRuling StructuredCompletion(IReadOnlyList<string> blockers, bool turnComplete,
        bool implementationComplete, IReadOnlyList<string> evidenceReasons, string reason, int used, int allowed)
    {
        if (blockers.Count > 0) return new(GateDisposition.Escalate, blockers, reason);
        if (turnComplete && implementationComplete) return new(GateDisposition.Pass, [], reason);
        var findings = evidenceReasons.Count == 0
            ? ["Structured turn or implementation completion evidence is absent."] : evidenceReasons;
        return used >= allowed
            ? new(GateDisposition.Escalate, findings, "Structured completion evidence remained incomplete after the bounded reissue budget.")
            : new(GateDisposition.Reissue, findings, "Structured completion evidence is incomplete; reissuing with explicit evidence reasons.");
    }
}
