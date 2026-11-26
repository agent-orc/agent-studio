using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestrationDecisions;

/// <summary>RT-4 mutation admission and expiry, shared by receipt application and explicit API intents.</summary>
public static class TestQuarantineLifecyclePolicy
{
    public static bool Active(ProjectTestQuarantine entry, DateTimeOffset now)
        => entry.LiftedAtUtc is null && entry.ResolvedOn is null && now < entry.ExpiresAtUtc;

    public static string? StartRefusal(IReadOnlyList<ProjectTestQuarantine> active, string kind, bool baseTest)
    {
        if (baseTest) return "Base tests, build and prepare failures cannot be quarantined.";
        if (kind is not ("held" or "report-only")) return "Kind must be held or report-only.";
        if (active.Count >= RedTestTriageBounds.MaximumTotal) return "The project quarantine cap is eight.";
        if (kind == "held" && active.Count(entry => entry.Kind == "held") >= RedTestTriageBounds.MaximumHeld)
            return "The held quarantine cap is three.";
        return null;
    }

    public static string? RenewRefusal(ProjectTestQuarantine entry, bool owner, string? repairLane,
        DateTimeOffset now)
    {
        if (entry.LiftedAtUtc is not null) return "A lifted quarantine cannot be renewed.";
        if (entry.Kind == "report-only") return owner ? null : "Only the owner can renew report-only entries.";
        if (entry.Renewals >= RedTestTriageBounds.MaximumRenewals
            || now >= entry.StartedAtUtc + RedTestTriageBounds.HeldMaximumAge)
            return "A held quarantine may be renewed only once, up to 48 hours.";
        return repairLane is TaskStates.Progress or TaskStates.AutoReview
            ? null : "Renewal requires a repair in Progress or Auto Review, including integration wait.";
    }

    public static string? LiftRefusal(bool owner, IReadOnlyList<RedTestLiftRun> runs)
        => owner || runs.Count >= 2 && runs.TakeLast(2).All(run => run.Result == RedTestResult.Pass
            && run.StandardEnvelope && run.ContainsRepairIntegration)
            ? null : "Lifting requires two consecutive covering passes containing the repair integration.";

    public static string DevelopState(string? lineStop) => lineStop is null ? "green" : "red";

    public static bool MayCreateRelease(string? lineStop, bool heldQuarantine)
        => lineStop is null && !heldQuarantine;

    public static bool MayIntegrateLineStop(string? lineStop, string cardId, IReadOnlyList<string> repairCards)
        => lineStop is null || repairCards.Contains(cardId, StringComparer.Ordinal);
}
