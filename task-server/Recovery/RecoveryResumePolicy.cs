using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer.Recovery;

/// <summary>Live facts about a restored authority before it may leave Maintenance.</summary>
public sealed record RecoveryResumeFacts(
    TaskServerMode Mode,
    bool RestoredFromRecoverySet,
    int UnresolvedAttempts,
    bool OldWriterClosedAttested,
    IReadOnlyList<string> StaleRunnerPrincipals,
    IReadOnlyList<RecoveryHostObligation> LiveObligations,
    bool ObligationsRetainedAttested,
    IReadOnlyList<RecoveryFinding> OpenSetFindings,
    bool IdentityComparisonsPassed = false);

public sealed record RecoveryResumeBlocker(string Code, string Subject, string Guidance);

public sealed record RecoveryResumeDecision(bool Allowed, IReadOnlyList<RecoveryResumeBlocker> Blockers);

/// <summary>
/// Pure gate for releasing a restored authority. Restoration leaves the target in Maintenance; this
/// decides whether drain, attempt resolution, the single-writer switch and stale-host fencing are proven.
/// </summary>
public static class RecoveryResumePolicy
{
    /// <summary>Codes re-evaluated from the live store instead of the capture-time copy report.</summary>
    public static readonly IReadOnlySet<string> LiveReevaluatedCodes =
        new HashSet<string>(StringComparer.Ordinal) { "pending-host-obligation", "client-credentials-lost", "copy-receipt-missing" };

    public static RecoveryResumeDecision Decide(RecoveryResumeFacts facts)
    {
        var blockers = new List<RecoveryResumeBlocker>();
        if (!facts.RestoredFromRecoverySet)
            blockers.Add(new("no-recovery-restore", "restore receipt",
                "This store has no recovery restore receipt. Resume applies only to a target restored with `task-server recovery restore`."));
        if (facts.RestoredFromRecoverySet && !facts.IdentityComparisonsPassed)
            blockers.Add(new("identity-comparison-failed", "restore receipt",
                "The restored server, schema, tasks, projects, workspaces or cold evidence did not match the manifest. Inspect the receipt comparisons and restore a verified set to an empty target before resuming."));
        if (facts.Mode != TaskServerMode.Maintenance)
            blockers.Add(new("maintenance-required", facts.Mode.ToString(),
                "A restored authority stays in Maintenance until this gate passes. Return it to Maintenance and run the check again."));
        if (facts.UnresolvedAttempts > 0)
            blockers.Add(new("attempts-unresolved", $"{facts.UnresolvedAttempts} attempt(s)",
                "Active or process-unknown attempts still hold fences from the old authority. Resolve each one through the audited attempt recovery before resuming."));
        if (!facts.OldWriterClosedAttested)
            blockers.Add(new("old-writer-open", "previous authority",
                "Stop the previous authority, or prove it is unreachable, and pass --old-writer-closed. Two writers must never accept work for the same installation."));
        if (facts.StaleRunnerPrincipals.Count > 0)
            blockers.Add(new("stale-hosts-unfenced", string.Join(", ", facts.StaleRunnerPrincipals),
                "These runner principals still hold credentials issued before the restore. Rotate or revoke each one, then redeliver the new credential to the host you mean to reconnect."));
        // A host can only drain against a writable authority, so an unreconciled obligation does not
        // block resume by itself. The administrator must attest that its outbox and salvage are kept.
        if (!facts.ObligationsRetainedAttested)
            foreach (var obligation in facts.LiveObligations)
                blockers.Add(new("host-obligation-unreconciled", $"{obligation.RunnerId}/{obligation.RunId}",
                    $"Outbox state '{obligation.State}' with {obligation.Backlog} unacknowledged record(s). Keep that host's outbox and salvage, then pass --obligations-retained. The host drains after its fenced reconnect."));
        foreach (var finding in facts.OpenSetFindings.Where(item =>
                     item.Severity != RecoveryFindingSeverity.Advisory && !LiveReevaluatedCodes.Contains(item.Code)))
            blockers.Add(new(finding.Code, finding.Subject, finding.Guidance));
        return new RecoveryResumeDecision(blockers.Count == 0, blockers);
    }
}
