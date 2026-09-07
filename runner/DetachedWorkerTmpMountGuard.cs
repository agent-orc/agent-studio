namespace AgentRunner;

/// <summary>
/// Detects a detached worker whose private <c>/tmp</c> mount was torn down by
/// a daemon restart while it kept running (<c>KillMode=process</c> leaves it
/// alive; a <c>PrivateTmp=true</c> unit unmounts its namespace-scoped
/// <c>/tmp</c> on every restart regardless). The process keeps referencing
/// the deleted mount: dotnet/MSBuild node pipes and NuGet's mkdtemp mutex
/// then fail with <c>MSB1025</c>, <c>SocketException (99)</c>, or
/// <c>ENOENT</c> roughly an hour later, and the grader turns that into a
/// product failure instead of infrastructure. Positively detecting the
/// deleted mount at reattachment time lets the daemon fail the slot
/// immediately instead of waiting for the doomed build to finish.
/// </summary>
internal static class DetachedWorkerTmpMountGuard
{
    /// <summary>
    /// True when <c>/proc/&lt;processId&gt;/mountinfo</c> shows the process' own
    /// <c>/tmp</c> mount pointing at a deleted root (the systemd-private
    /// bind-mount source was removed by a sibling unit restart). Linux only;
    /// a missing or unreadable mountinfo is not evidence of teardown.
    /// </summary>
    internal static bool TmpMountWasTornDown(int processId, out string detail)
    {
        detail = string.Empty;
        if (!OperatingSystem.IsLinux()) return false;
        string[] lines;
        try
        {
            lines = File.ReadAllLines($"/proc/{processId}/mountinfo");
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
        return TmpMountWasTornDown(lines, out detail);
    }

    /// <summary>Pure parsing seam over already-read mountinfo lines, for tests.</summary>
    internal static bool TmpMountWasTornDown(IReadOnlyList<string> mountInfoLines, out string detail)
    {
        detail = string.Empty;
        foreach (var line in mountInfoLines)
        {
            var fields = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            // /proc/<pid>/mountinfo: field[3] is the mount root, field[4] is the
            // mount point (man 5 proc). A deleted bind-mount source shows up in
            // the root field while the mount point itself still reads "/tmp".
            if (fields.Length < 5) continue;
            if (!string.Equals(fields[4], "/tmp", StringComparison.Ordinal)) continue;
            if (!fields[3].Contains("deleted", StringComparison.OrdinalIgnoreCase)) continue;
            detail =
                $"process /tmp mount root '{fields[3]}' was deleted, most likely by a unit " +
                "restart tearing down PrivateTmp while this worker kept running";
            return true;
        }
        return false;
    }
}
