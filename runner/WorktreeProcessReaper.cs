using System.Diagnostics;
using System.Runtime.InteropServices;

namespace AgentRunner;

/// <summary>
/// Reaps every Linux process whose current working directory is the worktree or
/// one of its descendants before git is allowed to remove that worktree.
/// Agent CLIs run in a dedicated process group, so one group signal also reaches
/// helpers that are no longer descendants of the original CLI PID.
/// </summary>
public static class WorktreeProcessReaper
{
    private const int SigTerm = 15;
    private const int SigKill = 9;

    public static async Task<int> ReapAsync(
        string worktreePath,
        Action<string> log,
        CancellationToken ct)
    {
        if (!OperatingSystem.IsLinux())
            return 0;

        var root = Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar);
        var victims = FindByCwd(root);
        if (victims.Count == 0) return 0;

        log($"worktree-process-reap-started path={root} processes={string.Join(',', victims.Select(v => v.Pid))}");
        Signal(victims, SigTerm);
        await Task.Delay(TimeSpan.FromMilliseconds(500), ct);

        var survivors = FindByCwd(root);
        if (survivors.Count > 0)
        {
            Signal(survivors, SigKill);
            await Task.Delay(TimeSpan.FromMilliseconds(100), ct);
            survivors = FindByCwd(root);
        }

        if (survivors.Count > 0)
        {
            throw new WorktreeProcessException(
                root,
                survivors.Select(v => v.Pid).ToArray());
        }

        log($"worktree-process-reap-completed path={root} processes={victims.Count}");
        return victims.Count;
    }

    internal static IReadOnlyList<WorktreeProcess> FindByCwd(string worktreePath)
    {
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
            return [];

        var root = Path.GetFullPath(worktreePath)
            .TrimEnd(Path.DirectorySeparatorChar);
        var ownPid = Environment.ProcessId;
        var result = new List<WorktreeProcess>();
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out var pid) || pid == ownPid)
                continue;
            try
            {
                var target = new DirectoryInfo(Path.Combine(procDir, "cwd"))
                    .ResolveLinkTarget(returnFinalTarget: true);
                if (target is null) continue;
                var cwd = Path.GetFullPath(target.FullName)
                    .TrimEnd(Path.DirectorySeparatorChar);
                if (!IsWithin(cwd, root)) continue;
                result.Add(new WorktreeProcess(pid, getpgid(pid), cwd));
            }
            catch (Exception ex) when (ex is IOException
                                       or UnauthorizedAccessException
                                       or DirectoryNotFoundException)
            {
                // Processes exit while /proc is enumerated and other users may
                // be hidden by hidepid. The next sweep rechecks survivors.
            }
        }
        return result;
    }

    private static bool IsWithin(string candidate, string root)
        => string.Equals(candidate, root, StringComparison.Ordinal)
           || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal);

    /// <summary>
    /// Signal the reaped processes, group by group where a group exists.
    ///
    /// <para>AGT-2870: <c>kill(-pgid, ...)</c> is a broadcast whenever the pgid is
    /// below 2, so the group targets go through
    /// <see cref="ProcessSignalPolicy.ForProcessGroup"/> instead of the old
    /// <c>group &gt; 0</c> check, which still admitted pgid 1 and turned the
    /// call into <c>kill(-1, ...)</c>. Whatever the group pass refuses or does
    /// not cover is signalled per pid, under the same pid floor.</para>
    /// </summary>
    private static void Signal(IReadOnlyList<WorktreeProcess> victims, int signal)
    {
        var signalledGroups = victims
            .Select(v => v.ProcessGroupId)
            .Distinct()
            .Where(group => ProcessSignalGuard.TrySignalProcessGroup(
                group, signal, $"worktree-reap-group signal={signal}"))
            .ToHashSet();

        foreach (var victim in victims.Where(v => !signalledGroups.Contains(v.ProcessGroupId)))
        {
            if (signal == SigKill)
            {
                ProcessSignalGuard.TryKillTree(victim.Pid, $"worktree-reap pid={victim.Pid}");
                continue;
            }
            ProcessSignalGuard.TrySignal(victim.Pid, signal, $"worktree-reap pid={victim.Pid}");
        }
    }

    [DllImport("libc", SetLastError = true)]
    private static extern int getpgid(int pid);
}

internal sealed record WorktreeProcess(int Pid, int ProcessGroupId, string Cwd);

public sealed class WorktreeProcessException(string worktreePath, IReadOnlyList<int> processIds)
    : Exception(
        $"Worktree teardown refused to remove '{worktreePath}' because processes " +
        $"{string.Join(", ", processIds)} still have a cwd inside it.")
{
    public string WorktreePath { get; } = worktreePath;
    public IReadOnlyList<int> ProcessIds { get; } = processIds;
}
