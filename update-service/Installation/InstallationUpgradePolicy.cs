namespace AgentTaskboard.UpdateService.Installation;

/// <summary>Where an installed component runs. Exactly one updater owns each.</summary>
public enum InstallationPlacement
{
    DevStableCheckout,
    InstalledCompose,
    InstalledSystemd,
    RunnerHost,
}

public enum InstallationUpdater
{
    /// <summary>The loopback service on port 5039; dev/stable checkouts only.</summary>
    CheckoutUpdateService,
    /// <summary>deploy/release/agent-orchestrator/{update,rollback}-docker.sh.</summary>
    DockerLifecycle,
    /// <summary>deploy/release/agent-orchestrator/{update,rollback}.sh.</summary>
    SystemdLifecycle,
    /// <summary>Per-host drain, runner update and canary (B7); never the checkout updater.</summary>
    RunnerHostTooling,
}

/// <summary>Upgrade phases in order. A phase is recorded once it has completed.</summary>
public enum UpgradePhase
{
    Started,
    Drained,
    BackedUp,
    Switched,
    Healthy,
    Compatible,
    Resumed,
    CanaryPassed,
}

public enum UpgradeVerdict
{
    Succeeded,
    AwaitingCanary,
    Failed,
}

public enum RollbackDecision
{
    SwitchToPrior,
    RestoreFromBackupRequired,
    Refused,
}

public enum HostReturnDecision
{
    Admit,
    RemainPending,
}

public sealed record UpgradePreflight(bool Allowed, IReadOnlyList<string> Refusals);

public sealed record UpgradeObservation(
    string Version,
    IReadOnlyList<InstallationComponent> Components,
    string? RuntimeMode,
    bool HealthGreen,
    bool? CanaryPassed);

/// <summary>
/// Pure decisions for the installation upgrade contract (Dossier AGT-W63 D6,
/// A7/B7/C8). Side effects stay in the placement updaters; this type only
/// answers "who owns it", "may it start", "where does it resume", "did it
/// succeed", "may it roll back" and "may this host return".
/// </summary>
public static class InstallationUpgradePolicy
{
    public const int MaximumDrainSeconds = 3600;

    public static InstallationUpdater SelectUpdater(InstallationPlacement placement) => placement switch
    {
        InstallationPlacement.DevStableCheckout => InstallationUpdater.CheckoutUpdateService,
        InstallationPlacement.InstalledCompose => InstallationUpdater.DockerLifecycle,
        InstallationPlacement.InstalledSystemd => InstallationUpdater.SystemdLifecycle,
        InstallationPlacement.RunnerHost => InstallationUpdater.RunnerHostTooling,
        _ => throw new ArgumentOutOfRangeException(nameof(placement), placement, null),
    };

    /// <summary>
    /// The 5039 checkout service stays checkout-only until an explicit
    /// adapter exists; it must refuse any other placement.
    /// </summary>
    public static bool MayApply(InstallationUpdater updater, InstallationPlacement placement)
        => SelectUpdater(placement) == updater;

    public static UpgradePreflight EvaluatePreflight(
        InstallationRelease current,
        InstallationRelease candidate,
        InstallationRelease? retainedPrior,
        InstallationBackup? backup,
        int drainTimeoutSeconds)
    {
        var refusals = new List<string>();
        if (drainTimeoutSeconds <= 0 || drainTimeoutSeconds > MaximumDrainSeconds)
            refusals.Add($"Drain must be bounded between 1 and {MaximumDrainSeconds} seconds.");
        if (backup is null || !backup.Verified)
            refusals.Add("A verified backup is required before switching versions.");
        else if (current.StoreSchemaVersion is { } schema && backup.StoreSchemaVersion != schema)
            refusals.Add($"The verified backup has store schema {backup.StoreSchemaVersion?.ToString() ?? "unknown"}, not the active {schema}.");
        if (retainedPrior is null || retainedPrior.Version != current.Version)
            refusals.Add($"The active release {current.Version} must be retained as the prior release.");
        if (candidate.Version == current.Version)
            refusals.Add($"Candidate {candidate.Version} is already active.");
        return new UpgradePreflight(refusals.Count == 0, refusals);
    }

    /// <summary>
    /// The next phase to run after an interruption. Before the switch the
    /// upgrade starts again from drain; after it, the candidate is
    /// re-verified rather than switched (and possibly migrated) twice.
    /// </summary>
    public static UpgradePhase ResumeFrom(UpgradePhase lastCompleted) => lastCompleted switch
    {
        UpgradePhase.Started or UpgradePhase.Drained => UpgradePhase.Drained,
        UpgradePhase.BackedUp => UpgradePhase.Switched,
        UpgradePhase.Switched or UpgradePhase.Healthy => UpgradePhase.Healthy,
        UpgradePhase.Compatible => UpgradePhase.Resumed,
        UpgradePhase.Resumed => UpgradePhase.CanaryPassed,
        UpgradePhase.CanaryPassed => UpgradePhase.CanaryPassed,
        _ => throw new ArgumentOutOfRangeException(nameof(lastCompleted), lastCompleted, null),
    };

    /// <summary>
    /// Success needs the desired version and digests observed on the running
    /// containers, a Normal runtime mode, green health and a passed canary.
    /// A pulled image or a green health endpoint alone is never success.
    /// </summary>
    public static UpgradeVerdict EvaluateCompletion(InstallationRelease desired, UpgradeObservation observed)
    {
        if (observed.Version != desired.Version || !observed.HealthGreen)
            return UpgradeVerdict.Failed;
        foreach (var component in desired.Components)
        {
            var running = observed.Components.FirstOrDefault(c => c.Role == component.Role);
            if (running?.ImageDigest is null)
                return UpgradeVerdict.Failed;
            if (component.ImageDigest is not null && running.ImageDigest != component.ImageDigest)
                return UpgradeVerdict.Failed;
        }
        if (!string.Equals(observed.RuntimeMode, "Normal", StringComparison.Ordinal))
            return UpgradeVerdict.Failed;
        return observed.CanaryPassed switch
        {
            true => UpgradeVerdict.Succeeded,
            false => UpgradeVerdict.Failed,
            null => UpgradeVerdict.AwaitingCanary,
        };
    }

    /// <summary>
    /// A release never runs against a store schema newer than the one it
    /// recorded. After a schema change the only data-preserving way back is a
    /// verified backup taken at the prior schema, restored by the operator.
    /// </summary>
    public static RollbackDecision EvaluateRollback(
        InstallationRelease active,
        InstallationRelease? target,
        InstallationBackup? backup)
    {
        if (target is null)
            return RollbackDecision.Refused;
        if (active.StoreSchemaVersion is null || target.StoreSchemaVersion is null)
            return RollbackDecision.Refused;
        if (active.StoreSchemaVersion <= target.StoreSchemaVersion)
            return RollbackDecision.SwitchToPrior;
        return backup is { Verified: true } && backup.StoreSchemaVersion == target.StoreSchemaVersion
            ? RollbackDecision.RestoreFromBackupRequired
            : RollbackDecision.Refused;
    }

    /// <summary>A host that was offline stays pending until its protocol is admissible.</summary>
    public static HostReturnDecision EvaluateHostReturn(InstallationHost host, InstallationProtocolRange authority)
        => host.Online && authority.Covers(host.ProtocolVersion)
            ? HostReturnDecision.Admit
            : HostReturnDecision.RemainPending;

    /// <summary>Host state recorded in the manifest after an authority switch.</summary>
    public static string ClassifyHost(bool online, int protocolVersion, InstallationProtocolRange authority)
        => !online ? "pending"
            : authority.Covers(protocolVersion) ? "current"
            : "incompatible";

    /// <summary>
    /// The candidate must admit every online host's protocol; offline hosts
    /// are left pending and re-checked by <see cref="EvaluateHostReturn"/>.
    /// </summary>
    public static IReadOnlyList<string> IncompatibleOnlineHosts(
        IEnumerable<InstallationHost> hosts, InstallationProtocolRange candidate)
        => hosts.Where(h => h.Online && !candidate.Covers(h.ProtocolVersion))
            .Select(h => h.HostId)
            .ToList();
}
