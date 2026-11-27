using System.Text.RegularExpressions;

namespace AgentStudio.OrchestrationDecisions;

public sealed record IntegrationBlockerFacts(
    bool KnownCodingParent, bool ValidDependency);

/// <summary>Recognizes an integration prerequisite rather than an operator decision.</summary>
public static partial class IntegrationBlockerPolicy
{
    public const string WaitLane = TaskStates.Ready;

    public static bool MayWait(IntegrationBlockerFacts facts)
        => facts.KnownCodingParent && facts.ValidDependency;
    [GeneratedRegex(@"\b(?:(?<key>[a-z][a-z0-9]*-\d+)-implementation-not-integrated|missing-(?<key>[a-z][a-z0-9]*-\d+)-implementation|implementation\s+of\s+(?:card\s+)?(?<key>[a-z][a-z0-9]*-\d+)\s+(?:is\s+)?not\s+integrated|(?<key>[a-z][a-z0-9]*-\d+)\s+implementation\s+(?:is\s+)?not\s+integrated)\b", RegexOptions.IgnoreCase)]
    private static partial Regex Prerequisite();

    public static string? Dependency(string category, string reason)
    {
        if (category is not ("agent-blocked" or "environment-blocker" or "needs-human-input")) return null;
        // Questions and stated forks retain human authority, even if they cite a prerequisite.
        if (reason.Contains('?') || Regex.IsMatch(reason, @"\b(?:choose|decide|scope|owner hold|operator hold)\b", RegexOptions.IgnoreCase)) return null;
        var keys = Prerequisite().Matches(reason).Select(match => match.Groups["key"].Value.ToUpperInvariant())
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        return keys.Length == 1 ? keys[0] : null;
    }
}

public enum MachineBlockerAction
{
    HumanChoice,
    Wait,
    Requeue,
    PlaceVerification,
    RepairAndQuarantine,
    RecordUnavailable,
}

public sealed record IntegrationResolutionFacts(
    bool Exists, bool CodingParent, bool ValidDependency, bool Integrated, bool Failed,
    bool OwnerHold, DateTimeOffset Now, DateTimeOffset Deadline, bool RecoveryBudgetSpent = false);

public sealed record VerificationResolutionFacts(
    bool AuthoritativeRequirement, bool Mandatory, bool CapableHostAvailable,
    bool OwnerHold, DateTimeOffset Now, DateTimeOffset Deadline);

public sealed record MachineBlockerDisposition(MachineBlockerAction Action, string Reason);

public static partial class IntegrationBlockerPolicy
{
    public const string QuarantineCategory = "machine-blocker-quarantine";
    public static readonly TimeSpan DependencyWait = TimeSpan.FromMinutes(30);
    public static readonly TimeSpan CapabilityWait = TimeSpan.FromMinutes(15);

    public static MachineBlockerDisposition Resolve(IntegrationResolutionFacts facts)
    {
        if (facts.OwnerHold) return new(MachineBlockerAction.HumanChoice, "Explicit owner hold or product choice remains in force.");
        if (!facts.Exists) return new(MachineBlockerAction.RepairAndQuarantine, "The referenced card is missing from the archive-inclusive platform index.");
        if (!facts.CodingParent || !facts.ValidDependency)
            return new(MachineBlockerAction.HumanChoice, "The reference is not a valid coding integration prerequisite.");
        if (facts.RecoveryBudgetSpent) return new(MachineBlockerAction.RepairAndQuarantine, "The shared automatic recovery round budget is exhausted.");
        if (facts.Integrated) return new(MachineBlockerAction.Requeue, "The current dependency delivery is integrated into the successor's target branch. Re-run from the current platform base.");
        if (facts.Failed) return new(MachineBlockerAction.RepairAndQuarantine, "The dependency has a recorded integration failure.");
        if (facts.Now >= facts.Deadline) return new(MachineBlockerAction.RepairAndQuarantine, "The integration dependency deadline expired.");
        return new(MachineBlockerAction.Wait, "Waiting for platform-confirmed current-delivery integration.");
    }

    public static bool IsVerificationBlocker(string reason)
        => !reason.Contains('?') && !Regex.IsMatch(reason, @"\b(?:choose|decide|scope|owner hold|operator hold)\b", RegexOptions.IgnoreCase)
           && (reason.StartsWith("verification-capability-unavailable:", StringComparison.Ordinal)
               || Regex.IsMatch(reason, @"\b(?:hardware|device) verification\b.*\b(?:unavailable|cannot execute|not available)\b", RegexOptions.IgnoreCase));

    public static MachineBlockerDisposition Verification(VerificationResolutionFacts facts)
    {
        if (facts.OwnerHold) return new(MachineBlockerAction.HumanChoice, "Explicit owner hold or product choice remains in force.");
        if (!facts.AuthoritativeRequirement)
            return new(MachineBlockerAction.RepairAndQuarantine, "The source finding and mandatory evidence requirement could not be reconstructed. No acceptance evidence is inferred.");
        if (facts.CapableHostAvailable)
            return new(MachineBlockerAction.PlaceVerification, "A capable review host can claim the unchanged immutable verification subject.");
        if (facts.Now < facts.Deadline) return new(MachineBlockerAction.Wait, "Waiting for a host admitted for the sealed verification requirements.");
        return facts.Mandatory
            ? new(MachineBlockerAction.RepairAndQuarantine, "Mandatory verification capability is unavailable at the deadline. Acceptance remains unsatisfied.")
            : new(MachineBlockerAction.RecordUnavailable, "Optional verification is unavailable. Record that check as unavailable; no passing evidence is inferred.");
    }
}
