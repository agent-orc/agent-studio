namespace AgentRunner.WorkContainers;

/// <summary>A managed container or volume as the runtime reports it, keyed by its labels.</summary>
public sealed record ManagedWorkResource(
    string Kind,
    string Name,
    string AttemptId,
    string LeaseId,
    long Fence,
    bool Running);

/// <summary>
/// What host authority knows about one attempt: the persisted host-agent
/// inventory, which mirrors the Task Server lease. Absent means the host holds
/// no authority for the attempt any more.
/// </summary>
public sealed record WorkAttemptAuthority(string AttemptId, string LeaseId, long Fence, bool Live);

public enum WorkResourceAction { Keep, FlushAndRemove }

public sealed record WorkResourceVerdict(WorkResourceAction Action, string Reason);

/// <summary>
/// Pure restart reconciliation. The only resource kept is a running container
/// whose exact lease and fence still hold live authority. A stopped container
/// is never started again, and a container or volume from a superseded fence is
/// removed after its log is flushed: reconciliation cannot resurrect a fenced
/// attempt.
/// </summary>
public static class WorkContainerReconciliationPolicy
{
    public static WorkResourceVerdict Decide(ManagedWorkResource resource, WorkAttemptAuthority? authority)
    {
        if (authority is null)
            return new(WorkResourceAction.FlushAndRemove, "attempt-unknown: host holds no authority for this attempt.");
        if (!authority.Live)
            return new(WorkResourceAction.FlushAndRemove, "attempt-terminal: the attempt already finished or was fenced.");
        if (authority.Fence != resource.Fence || !string.Equals(authority.LeaseId, resource.LeaseId, StringComparison.Ordinal))
            return new(WorkResourceAction.FlushAndRemove,
                $"fence-superseded: resource fence {resource.Fence} does not match live fence {authority.Fence}.");
        if (resource.Kind == "volume")
            return new(WorkResourceAction.Keep, "attempt-volume-of-live-attempt.");
        if (!resource.Running)
            return new(WorkResourceAction.FlushAndRemove,
                "exited-not-restartable: a stopped work container is never started again; the attempt reports through its lease.");
        return new(WorkResourceAction.Keep, "live-attempt-current-fence.");
    }
}

public enum WorkExecutorKind { Process, Container }

public sealed record WorkExecutorSelection(WorkExecutorKind Kind, bool RecordContainerShadow, string Reason);

/// <summary>
/// Pure executor choice. Windows always keeps the bounded process executor and
/// never consults a container runtime. Linux uses a container only in canary
/// mode with a reachable runtime and a pinned role image; otherwise the process
/// and cgroup executor remains the rollback.
/// </summary>
public static class WorkExecutorSelectionPolicy
{
    public static WorkExecutorSelection Select(
        bool isWindows, bool isLinux, WorkContainerMode mode, bool runtimeAvailable, bool roleImagePinned)
    {
        if (isWindows)
            return new(WorkExecutorKind.Process, false, "windows-process-executor: Job Object bounded process tree; Docker not required.");
        if (!isLinux)
            return new(WorkExecutorKind.Process, false, "container-executor-linux-only.");
        return mode switch
        {
            WorkContainerMode.Off => new(WorkExecutorKind.Process, false, "container-mode-off."),
            WorkContainerMode.Shadow => new(WorkExecutorKind.Process, true, "container-shadow: process executor runs; container plan recorded."),
            _ when !runtimeAvailable => new(WorkExecutorKind.Process, true, "container-runtime-unavailable: canary falls back to the process executor."),
            _ when !roleImagePinned => new(WorkExecutorKind.Process, true, "role-image-unpinned: canary falls back to the process executor."),
            _ => new(WorkExecutorKind.Container, false, "container-canary."),
        };
    }
}
