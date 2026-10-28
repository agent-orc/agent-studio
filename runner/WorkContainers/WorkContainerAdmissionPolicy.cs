using System.Globalization;

namespace AgentRunner.WorkContainers;

/// <summary>
/// The AGT-3019 host grant one claimed attempt holds: its half-core count and
/// the allocation version it was admitted under, plus the version this host
/// last acknowledged applying.
/// </summary>
public sealed record WorkContainerGrant(long AllocationVersion, long AcknowledgedAllocationVersion, int HalfCores);

/// <summary>Host facts read immediately before launch, under the host grant lock.</summary>
public sealed record WorkContainerHostBudget(
    int ReservedHalfCores,
    long AvailableMemoryBytes,
    long TotalMemoryBytes);

/// <summary>The enforced container limits derived from one grant.</summary>
public sealed record WorkContainerLimits(int HalfCores, long MemoryBytes, int PidsLimit)
{
    /// <summary>Docker and Podman <c>--cpus</c> value, for example <c>1.5</c>.</summary>
    public string Cpus => (HalfCores / 2m).ToString("0.0", CultureInfo.InvariantCulture);

    /// <summary>Thread hint for .NET builds inside the container; never below one.</summary>
    public int ProcessorHint => Math.Max(1, (HalfCores + 1) / 2);
}

public sealed record WorkContainerAdmission(bool Admitted, string Reason, WorkContainerLimits? Limits)
{
    public static WorkContainerAdmission Deny(string reason) => new(false, reason, null);
}

/// <summary>
/// Pure launch admission. The container limit is exactly the AGT-3019 grant;
/// the host profile ceiling bounds both the grant and the sum of live grants.
/// A denied launch creates no container, volume or secret mount.
/// </summary>
public static class WorkContainerAdmissionPolicy
{
    /// <summary>AGT-3019 safety floor: keep the larger of 4 GiB or 15% of RAM free.</summary>
    public static long MemoryFloorBytes(long totalMemoryBytes)
        => Math.Max(4L << 30, totalMemoryBytes * 15 / 100);

    public static WorkContainerAdmission Decide(
        WorkContainerGrant grant, WorkContainerHostBudget budget, WorkContainerSettings settings)
    {
        if (grant.HalfCores <= 0)
            return WorkContainerAdmission.Deny("grant-empty: the attempt holds no half-core grant.");
        if (grant.AllocationVersion <= 0 || grant.AcknowledgedAllocationVersion != grant.AllocationVersion)
            return WorkContainerAdmission.Deny(
                $"allocation-stale: grant version {grant.AllocationVersion} is not the acknowledged host version {grant.AcknowledgedAllocationVersion}.");
        if (grant.HalfCores > settings.ProfileMaxHalfCores)
            return WorkContainerAdmission.Deny(
                $"profile-ceiling: grant of {grant.HalfCores} half-cores exceeds the host profile ceiling of {settings.ProfileMaxHalfCores}.");
        if (budget.ReservedHalfCores < 0
            || budget.ReservedHalfCores + grant.HalfCores > settings.ProfileMaxHalfCores)
            return WorkContainerAdmission.Deny(
                $"host-grant-exhausted: {budget.ReservedHalfCores} reserved plus {grant.HalfCores} requested exceeds {settings.ProfileMaxHalfCores} half-cores.");

        var memory = grant.HalfCores * settings.MemoryBytesPerHalfCore;
        if (memory > settings.ProfileMaxMemoryBytes)
            return WorkContainerAdmission.Deny(
                $"profile-memory-ceiling: {memory >> 20} MiB exceeds the host profile ceiling of {settings.ProfileMaxMemoryBytes >> 20} MiB.");
        if (budget.AvailableMemoryBytes - memory < MemoryFloorBytes(budget.TotalMemoryBytes))
            return WorkContainerAdmission.Deny(
                $"memory-floor: admitting {memory >> 20} MiB leaves less than {MemoryFloorBytes(budget.TotalMemoryBytes) >> 20} MiB available.");

        // Same fork-bomb fuse as the process envelope: 512 tasks per granted core.
        var pids = Math.Clamp(
            grant.HalfCores * WorkerResourceEnvelope.TasksPerCore / 2,
            WorkerResourceEnvelope.MinimumTasksMax,
            WorkerResourceEnvelope.MaximumTasksMax);
        return new WorkContainerAdmission(true, "admitted within the host grant and profile ceiling.",
            new WorkContainerLimits(grant.HalfCores, memory, pids));
    }
}
