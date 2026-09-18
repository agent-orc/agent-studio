using System.Diagnostics;

namespace AgentRunner;

/// <summary>
/// Keeps a detached worker's .NET build servers inside the run that started
/// them.
///
/// <para>
/// AGT-2868: on 18.09.2026 the first rollout of the AGT-2866 envelope failed to
/// delegate on both role units of <c>agent-runner-01</c>. cgroup v2 refuses to
/// enable controllers in <c>cgroup.subtree_control</c> while any process sits
/// directly in the unit cgroup, and what sat there were seven MSBuild worker
/// nodes on the review unit and two more on the coding unit, between 1.7 and 2.7
/// days old. No worker owned them any more: MSBuild's node reuse keeps a node
/// alive for a later build, the node is reparented when its worker dies, and
/// <c>KillMode=process</c> (AGT-2750) then carries it across every daemon
/// restart. The envelope logged <c>applied=no</c> and every run continued
/// uncapped, so the protection AGT-2866 added silently did not exist on a host
/// that had run anything before.
/// </para>
///
/// <para>
/// The fence is therefore part of the worker launch, not of one build command:
/// a coding run's agent invokes <c>dotnet build</c> and <c>dotnet test</c>
/// through its own shell, where the runner has no say over the command line,
/// but it does own the environment the worker is started with and every
/// descendant inherits it. <see cref="ReviewBuildServerIsolation"/> stays the
/// stricter, attempt-scoped fence for a fenced review plan (it also removes the
/// shared Roslyn compiler server and redirects MSBuild's debug output); this one
/// is the host-hygiene floor both roles carry.
/// </para>
/// </summary>
internal static class WorkerBuildServerHygiene
{
    /// <summary>MSBuild worker nodes exit with the build instead of lingering for reuse.</summary>
    internal const string DisableNodeReuse = ReviewBuildServerIsolation.DisableNodeReuse;

    /// <summary>The dotnet CLI drives MSBuild in-process instead of through a long-lived server.</summary>
    internal const string DisableMsBuildServer = ReviewBuildServerIsolation.DisableMsBuildServer;

    /// <summary>
    /// How long the belt-and-braces shutdown below may take. It runs once per
    /// attempt, on a worker that has already finished its work, so a hung build
    /// server costs nothing but this budget.
    /// </summary>
    internal static readonly TimeSpan ShutdownBudget = TimeSpan.FromSeconds(30);

    /// <summary>
    /// The variables every detached coding and review worker is started with.
    /// Both values are the "no server" spelling: <c>1</c> disables node reuse,
    /// <c>0</c> disables the MSBuild server, which is the CLI's own inverted
    /// convention rather than a typo.
    /// </summary>
    internal static IReadOnlyDictionary<string, string> Variables { get; } =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            [DisableNodeReuse] = "1",
            [DisableMsBuildServer] = "0",
        };

    /// <summary>
    /// Applies the fence onto a worker environment the caller is still building.
    /// The fence always wins: a value inherited from the daemon's own
    /// environment must never be able to re-enable a host-shared build server
    /// for a worker.
    /// </summary>
    internal static IDictionary<string, string?> ApplyTo(IDictionary<string, string?> environment)
    {
        foreach (var (key, value) in Variables) environment[key] = value;
        return environment;
    }

    /// <summary>True when every variable of the fence carries its required value.</summary>
    internal static bool IsFenced(IReadOnlyDictionary<string, string?> environment)
        => Variables.All(entry =>
            environment.TryGetValue(entry.Key, out var value)
            && string.Equals(value, entry.Value, StringComparison.Ordinal));

    /// <summary>
    /// Belt and braces on worker teardown: ask the SDK to stop the build servers
    /// this worker's own builds may still have started, even though
    /// <see cref="Variables"/> is meant to prevent any from existing. Runs in the
    /// worker process and ahead of its terminal result file, because that file is
    /// what lets the daemon tear the worker cgroup down; a shutdown started after
    /// it would race the teardown and be counted as a leftover it had to kill.
    /// Failures are swallowed: the run is over and the cgroup teardown is the
    /// authoritative sweep.
    /// </summary>
    internal static void ShutdownBuildServers(Action<string>? log = null)
    {
        try
        {
            var start = new ProcessStartInfo("dotnet")
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            start.ArgumentList.Add("build-server");
            start.ArgumentList.Add("shutdown");
            ApplyTo(start.Environment);
            using var process = Process.Start(start);
            if (process is null) return;
            if (!process.WaitForExit((int)ShutdownBudget.TotalMilliseconds))
            {
                process.Kill(entireProcessTree: true);
                log?.Invoke("[runner] dotnet build-server shutdown exceeded its budget and was killed");
            }
        }
        catch (Exception exception) when (
            exception is System.ComponentModel.Win32Exception
                or InvalidOperationException
                or IOException
                or UnauthorizedAccessException)
        {
            // No SDK on PATH, or nothing to shut down. The cgroup teardown in
            // WorkerCgroup.KillResidents is the authoritative sweep either way.
            log?.Invoke($"[runner] dotnet build-server shutdown unavailable: {exception.Message}");
        }
    }
}
