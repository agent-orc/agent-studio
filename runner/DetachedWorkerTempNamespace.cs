using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Startup self-check for a re-adopted detached worker whose temp namespace the
/// daemon restart took away.
/// </summary>
/// <remarks>
/// A runner unit configured with <c>PrivateTmp</c> owns a lifecycle-bound
/// <c>/tmp</c>. Because the units also use <c>KillMode=process</c>, a restart
/// stops the daemon but leaves detached workers running on the now unmounted
/// namespace, which <c>/proc/&lt;pid&gt;/mountinfo</c> reports with a
/// <c>//deleted</c> mount root. Such a worker can no longer create an MSBuild
/// node pipe or a NuGet mutex directory, so every build and test it still has
/// ahead of it is guaranteed to fail. The shipped units no longer set
/// <c>PrivateTmp</c>; this check keeps a host that predates that change from
/// spending another hour on a doomed run before grading it.
/// </remarks>
internal static class DetachedWorkerTempNamespace
{
    // The kernel appends this marker to the mount root of a deleted dentry.
    private const string DeletedMarker = "//deleted";
    private const string TempMountPoint = "/tmp";

    /// <summary>
    /// True when <paramref name="mountInfo"/>, the content of a process'
    /// <c>/proc/&lt;pid&gt;/mountinfo</c>, mounts <c>/tmp</c> from a root that
    /// no longer exists.
    /// </summary>
    internal static bool HasDeletedTempMount(string? mountInfo)
    {
        if (string.IsNullOrEmpty(mountInfo)) return false;
        foreach (var line in mountInfo.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            // id parent major:minor root mount-point options ...
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (fields.Length < 5) continue;
            if (!string.Equals(fields[4], TempMountPoint, StringComparison.Ordinal)) continue;
            if (fields[3].EndsWith(DeletedMarker, StringComparison.Ordinal)) return true;
        }
        return false;
    }

    /// <summary>
    /// Reports whether the process still holds a deleted <c>/tmp</c> mount, and
    /// stops it if it does.
    /// </summary>
    /// <remarks>
    /// Terminating here is the point, not a side effect. This is the only case
    /// in which a liveness proof rejects a process that is demonstrably alive,
    /// so leaving it running would strand a worker that can never produce a
    /// valid result while the daemon releases its lease: a coding worker could
    /// still push to the attempt branch after the task was requeued, and a
    /// review workspace would be deleted underneath a live writer. The caller
    /// re-reads any durable result after this returns, so a worker that already
    /// finished is still adopted on its result rather than on its process.
    /// A mountinfo that cannot be read yields <c>false</c>: the proof must not
    /// reject an adoptable worker over a missing diagnostic.
    /// </remarks>
    internal static bool IsLost(Process process, out string reason)
    {
        reason = string.Empty;
        if (!OperatingSystem.IsLinux()) return false;
        string mountInfo;
        try
        {
            mountInfo = File.ReadAllText($"/proc/{process.Id}/mountinfo");
        }
        catch (Exception exception) when (exception is IOException
                                          or UnauthorizedAccessException
                                          or NotSupportedException)
        {
            return false;
        }
        if (!HasDeletedTempMount(mountInfo)) return false;
        reason = "worker holds a deleted /tmp mount after a daemon restart; "
                 + "builds and tests in this namespace cannot succeed";
        try
        {
            process.Kill(entireProcessTree: true);
            reason += "; worker terminated";
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or NotSupportedException
                                          or System.ComponentModel.Win32Exception
                                          or AggregateException)
        {
            reason += $"; worker could not be terminated: {exception.Message}";
        }
        return true;
    }
}
