using System.Globalization;

namespace AgentRunner;

/// <summary>One process as the build-node sweep sees it in <c>/proc</c>.</summary>
public sealed record BuildProcessObservation(
    int Pid,
    int ParentPid,
    int Uid,
    string Comm,
    string CommandLine,
    DateTime? StartedAtUtc,
    string? Cwd);

/// <summary>What the sweep knows about the runs it must not touch.</summary>
public sealed record BuildNodeSweepContext(
    int OwnUid,
    DateTime NowUtc,
    IReadOnlySet<int> ActiveRunPids,
    IReadOnlyList<string> ActiveRunPaths);

public enum BuildNodeKind
{
    None,
    MsBuildNode,
    CompilerServer,
    BuildServerCommand,
}

public enum BuildNodeVerdict
{
    NotBuildNode,
    ForeignUser,
    TooYoung,
    ActiveRun,
    LiveDriver,
    Stale,
}

/// <summary>
/// AGT-3005 pure decision: is a process a stale .NET build node the runner may
/// terminate? MSBuild worker nodes and the MSBuild server
/// (<c>MSBuild.dll /nodemode:</c>), the Roslyn compiler server
/// (<c>VBCSCompiler</c>) and a hung <c>dotnet build-server</c> command qualify
/// when they belong to the runner user, are older than
/// <see cref="MinimumAge"/>, and are neither a descendant of an active run nor
/// rooted in an active run's workspace.
/// </summary>
public static class BuildNodeSweepPolicy
{
    public static readonly TimeSpan MinimumAge = TimeSpan.FromMinutes(30);

    public static BuildNodeKind Classify(string comm, string commandLine)
    {
        var arguments = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (arguments.Length == 0) return BuildNodeKind.None;
        if (string.Equals(comm, "VBCSCompiler", StringComparison.Ordinal)
            && IsCompilerExecutable(arguments[0]))
            return BuildNodeKind.CompilerServer;
        if (!string.Equals(comm, "dotnet", StringComparison.Ordinal)
            || !IsDotnetHost(arguments[0]))
            return BuildNodeKind.None;

        var entryPointIndex = arguments.Length > 1 && arguments[1] == "exec" ? 2 : 1;
        if (arguments.Length <= entryPointIndex) return BuildNodeKind.None;
        var entryPoint = arguments[entryPointIndex];
        if (string.Equals(entryPoint, "build-server", StringComparison.Ordinal) && entryPointIndex == 1)
            return BuildNodeKind.BuildServerCommand;
        if (string.Equals(Path.GetFileName(entryPoint), "VBCSCompiler.dll", StringComparison.OrdinalIgnoreCase))
            return BuildNodeKind.CompilerServer;
        if (string.Equals(Path.GetFileName(entryPoint), "MSBuild.dll", StringComparison.OrdinalIgnoreCase)
            && arguments.Skip(entryPointIndex + 1).Any(argument =>
                argument.StartsWith("/nodemode:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("-nodemode:", StringComparison.OrdinalIgnoreCase)
                || argument.StartsWith("/nodeReuse:", StringComparison.OrdinalIgnoreCase)))
            return BuildNodeKind.MsBuildNode;
        return BuildNodeKind.None;
    }

    /// <summary>True for the dotnet host itself or a process it runs, for the host report.</summary>
    public static bool IsDotnetProcess(string comm, string commandLine)
    {
        if (string.Equals(comm, "dotnet", StringComparison.Ordinal)) return true;
        var first = commandLine.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return first is not null && IsDotnetHost(first)
               || Classify(comm, commandLine) != BuildNodeKind.None;
    }

    /// <summary>
    /// A node whose parent is still a live build driver (a <c>dotnet build</c>,
    /// a shell, another role's worker) is part of a running build even when this
    /// daemon does not own it, so it is <see cref="BuildNodeVerdict.LiveDriver"/>.
    /// Only a node reparented to init or the systemd manager, or parented by
    /// another build node, can be stale.
    /// </summary>
    public static BuildNodeVerdict Decide(
        BuildProcessObservation process,
        Func<int, BuildProcessObservation?> lookup,
        BuildNodeSweepContext context)
    {
        if (Classify(process.Comm, process.CommandLine) == BuildNodeKind.None) return BuildNodeVerdict.NotBuildNode;
        if (process.Uid != context.OwnUid) return BuildNodeVerdict.ForeignUser;
        // An unknown start time is treated as young: the sweep never guesses.
        if (process.StartedAtUtc is not { } started || context.NowUtc - started < MinimumAge)
            return BuildNodeVerdict.TooYoung;
        if (IsUnder(process.Cwd, context.ActiveRunPaths)) return BuildNodeVerdict.ActiveRun;
        var seen = new HashSet<int>();
        for (int? pid = process.ParentPid; pid is > 1 && seen.Add(pid.Value); pid = lookup(pid.Value)?.ParentPid)
        {
            if (context.ActiveRunPids.Contains(pid.Value)) return BuildNodeVerdict.ActiveRun;
        }
        var parent = process.ParentPid > 1 ? lookup(process.ParentPid) : null;
        if (parent is not null
            && !IsProcessManager(parent.Comm)
            && Classify(parent.Comm, parent.CommandLine) == BuildNodeKind.None)
            return BuildNodeVerdict.LiveDriver;
        return BuildNodeVerdict.Stale;
    }

    private static bool IsProcessManager(string comm)
        => comm is "systemd" or "init" or "tini" or "dumb-init";

    private static bool IsDotnetHost(string executable)
        => string.Equals(Path.GetFileName(executable), "dotnet", StringComparison.Ordinal)
           || string.Equals(Path.GetFileName(executable), "dotnet.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsCompilerExecutable(string executable)
        => string.Equals(Path.GetFileName(executable), "VBCSCompiler", StringComparison.Ordinal)
           || string.Equals(Path.GetFileName(executable), "VBCSCompiler.exe", StringComparison.OrdinalIgnoreCase);

    private static bool IsUnder(string? cwd, IReadOnlyList<string> roots)
    {
        if (string.IsNullOrWhiteSpace(cwd)) return false;
        const string deleted = " (deleted)";
        var candidate = cwd.EndsWith(deleted, StringComparison.Ordinal) ? cwd[..^deleted.Length] : cwd;
        candidate = candidate.TrimEnd(Path.DirectorySeparatorChar);
        return roots
            .Where(root => !string.IsNullOrWhiteSpace(root))
            .Select(root => root.TrimEnd(Path.DirectorySeparatorChar))
            .Any(root => string.Equals(candidate, root, StringComparison.Ordinal)
                         || candidate.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.Ordinal));
    }
}

/// <summary>Outcome of one sweep pass, carried into the host report.</summary>
public sealed record BuildNodeSweepResult(
    DateTime ObservedAt,
    int DotnetProcesses,
    int StaleBuildNodes,
    int Terminated);

/// <summary>
/// AGT-3005 side effects of <see cref="BuildNodeSweepPolicy"/>: reads
/// <c>/proc</c>, terminates the stale nodes through
/// <see cref="ProcessSignalGuard"/>, and keeps the last result plus a running
/// total for the host report. Runs at daemon startup and once per
/// <see cref="Interval"/> in both the coding and the review daemon.
/// </summary>
public static class BuildNodeSweep
{
    public static readonly TimeSpan Interval = TimeSpan.FromHours(1);

    private static long _terminatedTotal;
    private static BuildNodeSweepResult? _latest;

    public static BuildNodeSweepResult? Latest => Volatile.Read(ref _latest);

    public static long TerminatedTotal => Interlocked.Read(ref _terminatedTotal);

    public static BuildNodeSweepResult Run(
        IEnumerable<int> activeRunPids,
        IEnumerable<string> activeRunPaths,
        Action<string> log)
    {
        var now = DateTime.UtcNow;
        if (!OperatingSystem.IsLinux() || !Directory.Exists("/proc"))
            return Publish(new BuildNodeSweepResult(now, 0, 0, 0));

        var processes = Observe();
        var byPid = processes.ToDictionary(process => process.Pid);
        var context = new BuildNodeSweepContext(
            ReadUid("/proc/self") ?? -1,
            now,
            activeRunPids.Where(pid => pid > 0).Append(Environment.ProcessId).ToHashSet(),
            activeRunPaths.Where(path => !string.IsNullOrWhiteSpace(path)).Select(Path.GetFullPath).ToArray());
        var dotnet = processes.Count(process =>
            process.Uid == context.OwnUid && BuildNodeSweepPolicy.IsDotnetProcess(process.Comm, process.CommandLine));
        var stale = processes
            .Where(process => BuildNodeSweepPolicy.Decide(
                process, pid => byPid.GetValueOrDefault(pid), context)
                == BuildNodeVerdict.Stale)
            .ToArray();
        var terminated = 0;
        foreach (var process in stale)
        {
            if (!ProcessSignalGuard.TryKillSingle(
                    process.Pid, $"build-node-sweep pid={process.Pid}", process.StartedAtUtc, log))
                continue;
            terminated++;
            log(
                $"build-node-reaped pid={process.Pid} " +
                $"kind={BuildNodeSweepPolicy.Classify(process.Comm, process.CommandLine)} " +
                $"ageHours={(now - process.StartedAtUtc!.Value).TotalHours.ToString("F1", CultureInfo.InvariantCulture)}");
        }
        Interlocked.Add(ref _terminatedTotal, terminated);
        log($"build-node-sweep dotnetProcesses={dotnet} staleNodes={stale.Length} terminated={terminated}");
        return Publish(new BuildNodeSweepResult(now, dotnet, stale.Length, terminated));
    }

    private static BuildNodeSweepResult Publish(BuildNodeSweepResult result)
    {
        Volatile.Write(ref _latest, result);
        return result;
    }

    private static List<BuildProcessObservation> Observe()
    {
        var observations = new List<BuildProcessObservation>();
        foreach (var procDir in Directory.EnumerateDirectories("/proc"))
        {
            if (!int.TryParse(Path.GetFileName(procDir), out var pid)) continue;
            try
            {
                var status = File.ReadAllLines(Path.Combine(procDir, "status"));
                var comm = File.ReadAllText(Path.Combine(procDir, "comm")).Trim();
                var cmdline = File.ReadAllText(Path.Combine(procDir, "cmdline")).Replace('\0', ' ').Trim();
                var ppid = StatusField(status, "PPid:") ?? 0;
                var uid = StatusField(status, "Uid:") ?? -1;
                string? cwd = null;
                try
                {
                    cwd = new DirectoryInfo(Path.Combine(procDir, "cwd"))
                        .ResolveLinkTarget(returnFinalTarget: false)?.FullName;
                }
                catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { }
                observations.Add(new BuildProcessObservation(pid, ppid, uid, comm, cmdline, StartTime(pid), cwd));
            }
            catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
            {
                // The process exited between enumeration and read, or is not ours.
            }
        }
        return observations;
    }

    private static int? ReadUid(string procDir)
    {
        try { return StatusField(File.ReadAllLines(Path.Combine(procDir, "status")), "Uid:"); }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException) { return null; }
    }

    private static int? StatusField(IEnumerable<string> status, string prefix)
    {
        var line = status.FirstOrDefault(entry => entry.StartsWith(prefix, StringComparison.Ordinal));
        var first = line?[prefix.Length..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault();
        return int.TryParse(first, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    }

    private static DateTime? StartTime(int pid)
    {
        try
        {
            using var process = System.Diagnostics.Process.GetProcessById(pid);
            return process.StartTime.ToUniversalTime();
        }
        catch (Exception exception) when (exception is ArgumentException
                                          or InvalidOperationException
                                          or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }
}
