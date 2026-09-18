using System.Globalization;

namespace AgentRunner;

/// <summary>What one finished worker actually cost, read back from its own cgroup.</summary>
internal sealed record WorkerResourceUsage(double CpuSeconds, int PeakTasks)
{
    /// <summary>Peak task count is unavailable on kernels without <c>pids.peak</c>.</summary>
    public const int UnknownTasks = -1;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"cpuSeconds={CpuSeconds:0.0} peakTasks={(PeakTasks == UnknownTasks ? "unknown" : PeakTasks.ToString(CultureInfo.InvariantCulture))}");
}

/// <summary>The command line that starts a worker inside its resource envelope.</summary>
internal sealed record WorkerLaunch(string FileName, IReadOnlyList<string> Arguments);

/// <summary>
/// Why a worker may have died, read from the counters of its own cgroup
/// (AGT-2870). <c>pids.events max</c> counts the forks the kernel refused, which
/// is the difference between "the agent stopped" and "the agent was stopped by
/// its own task ceiling"; <c>memory.events</c> carries the same evidence for
/// memory pressure on hosts that delegate the controller.
/// </summary>
public sealed record WorkerCgroupPressure(
    int TasksMax,
    int PeakTasks,
    long ForksRefused,
    long MemoryHigh,
    long MemoryMax,
    long OomKills)
{
    /// <summary>The counter is unavailable on this host or kernel.</summary>
    public const int Unknown = -1;

    /// <summary>True when the kernel refused at least one fork inside this worker.</summary>
    public bool HitTaskCeiling => ForksRefused > 0;

    public string Describe() => string.Create(
        CultureInfo.InvariantCulture,
        $"pidsMax={Format(TasksMax)} pidsPeak={Format(PeakTasks)} pidsEventsMax={Format(ForksRefused)} " +
        $"memoryEventsHigh={Format(MemoryHigh)} memoryEventsMax={Format(MemoryMax)} memoryEventsOomKill={Format(OomKills)}");

    private static string Format(long value)
        => value == Unknown ? "unknown" : value.ToString(CultureInfo.InvariantCulture);
}

/// <summary>
/// Per-worker cgroup v2 envelope (AGT-2866).
///
/// <para><b>Mechanism.</b> The cgroup v2 API directly, inside the role unit's own
/// delegated subtree, rather than a <c>systemd-run --scope</c> transient unit.
/// Both were on the table. Scopes lose on two counts: as a non-root service
/// account the daemon needs a polkit grant for
/// <c>org.freedesktop.systemd1.manage-units</c> before it may create one, and a
/// scope lives <em>outside</em> the role unit, so every worker would escape the
/// aggregate <c>CPUQuota</c>/<c>CPUWeight</c> the role already carries and the
/// review plane would silently lose the ceiling
/// docs/.../resource-governance.md decided for it. A delegated subtree needs no
/// privilege beyond the service account's own cgroup and nests the per-worker
/// ceiling <em>under</em> the role aggregate, which is what "the envelope only
/// caps peaks" requires.</para>
///
/// <para><b>Layout.</b> Below the unit cgroup: <c>daemon/</c> holds the daemon
/// itself, and one <c>worker-&lt;id&gt;/</c> per detached worker carries
/// <c>cpu.max</c>, <c>cpu.weight</c> and <c>pids.max</c>. cgroup v2 forbids a
/// cgroup from holding processes and distributing controllers at the same time,
/// so the daemon has to sit in a leaf. systemd puts it there through
/// <c>DelegateSubgroup=daemon</c>; the daemon never moves itself, because the
/// kernel then refuses to place the replacement daemon of a
/// <c>KillMode=process</c> restart back into the still-live unit cgroup.</para>
///
/// <para><b>Why this survives AGT-2750.</b> <c>KillMode=process</c> leaves
/// detached workers running across a daemon restart. Their cgroups are ordinary
/// directories in a subtree systemd promised not to touch (<c>Delegate=</c>), and
/// the unit cgroup cannot be removed while those processes live in it, so the
/// replacement daemon finds the same directories and reads the same
/// <c>cpu.stat</c>. Unlike <c>PrivateTmp=true</c>, a cgroup is not a mount: there
/// is nothing to unmount from under a surviving worker. The envelope is also
/// applied by the worker itself (<see cref="Wrap"/> writes its own pid into
/// <c>cgroup.procs</c> and then <c>exec</c>s), so the recorded pid stays the
/// worker's pid and every existing reattachment proof keeps working.</para>
///
/// <para>Every failure here is non-fatal and logged: a host without cgroup v2
/// delegation runs exactly as it did before, only without the envelope.</para>
/// </summary>
internal sealed class WorkerCgroup
{
    internal const string DefaultMountRoot = "/sys/fs/cgroup";
    internal const string DaemonLeafName = "daemon";
    internal const string WorkerPrefix = "worker-";
    internal const string RequiredControllers = "+cpu +pids";

    /// <summary>Marker written next to <c>spec.json</c> so a replacement daemon finds the same cgroup.</summary>
    internal const string MarkerFileName = "cgroup.path";

    /// <summary>
    /// Attaches the worker to its cgroup and then replaces itself with the real
    /// command, so the worker is inside its envelope from its first instruction
    /// and no child can be forked ahead of the cap. <c>$$</c> is the shell's own
    /// pid, which <c>exec</c> preserves.
    /// </summary>
    internal const string AttachScript =
        "printf '%s\\n' \"$$\" >\"$1\" 2>/dev/null || "
        + "printf '[runner] worker cgroup attach failed: %s\\n' \"$1\" >&2; "
        + "shift; exec \"$@\"";

    private static readonly object ResolveGate = new();
    private static string? _delegationRoot;
    private static bool _delegationResolved;

    private WorkerCgroup(string directory, WorkerResourceEnvelope envelope)
    {
        CgroupDirectory = directory;
        Envelope = envelope;
    }

    internal string CgroupDirectory { get; }
    internal WorkerResourceEnvelope Envelope { get; }
    internal string ProcsPath => Path.Combine(CgroupDirectory, "cgroup.procs");

    // ---------------------------------------------------------------- boundary

    /// <summary>
    /// True when this host can carry per-worker envelopes at all. Linux with a
    /// unified hierarchy; everything else keeps the pre-AGT-2866 launch.
    /// </summary>
    internal static bool IsSupported
        => OperatingSystem.IsLinux() && File.Exists(Path.Combine(DefaultMountRoot, "cgroup.controllers"));

    // ------------------------------------------------------------ coordination

    /// <summary>
    /// Prepare the whole envelope for one worker: resolve the delegated subtree
    /// once per process, create the worker cgroup, write the limits, and record
    /// the path in the worker directory. Returns null when the host cannot carry
    /// an envelope, after logging exactly why.
    /// </summary>
    internal static WorkerCgroup? TryPrepare(
        RunnerOptions options,
        string workerDirectory,
        Action<string> log)
    {
        if (!options.WorkerEnvelopeEnabled) return null;
        if (!IsSupported)
        {
            log($"[runner] worker resource envelope unavailable: no cgroup v2 hierarchy at {DefaultMountRoot}");
            return null;
        }

        var root = EnsureDelegationRoot(log);
        if (root is null) return null;

        return TryCreate(
            root,
            workerDirectory,
            NameFor(workerDirectory),
            WorkerResourceEnvelope.FromOptions(options),
            log);
    }

    /// <summary>
    /// Stable cgroup name for a worker directory. The leaf alone is not unique
    /// (a same-session resume nests <c>resume-1</c> under its attempt), so the
    /// full path is folded in. Deterministic, so a replacement daemon that
    /// reattaches derives the same name.
    /// </summary>
    internal static string NameFor(string workerDirectory)
    {
        var full = Path.GetFullPath(workerDirectory);
        var digest = System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(full));
        return $"{Sanitize(Path.GetFileName(Path.TrimEndingDirectorySeparator(full)))}-{Convert.ToHexString(digest)[..8].ToLowerInvariant()}";
    }

    /// <summary>
    /// Resolve (once) the cgroup directory this daemon may create children in,
    /// stepping into the <c>daemon/</c> leaf first when systemd has not already
    /// placed us in one.
    ///
    /// <para><paramref name="strays"/> is supplied by the daemon's startup
    /// announcement and by nobody else. Resolution is cached, so the sweep it
    /// carries runs exactly once per daemon generation, before the first worker
    /// asks for an envelope (AGT-2868).</para>
    /// </summary>
    internal static string? EnsureDelegationRoot(Action<string> log, StraySweepContext? strays = null)
    {
        lock (ResolveGate)
        {
            if (_delegationResolved) return _delegationRoot;
            _delegationResolved = true;
            var relative = ReadSelfCgroupRelativePath();
            if (relative is null)
            {
                log("[runner] worker resource envelope unavailable: /proc/self/cgroup has no unified entry");
                return _delegationRoot = null;
            }
            _delegationRoot = TryPrepareDelegationRoot(DefaultMountRoot, relative, log, strays);
            return _delegationRoot;
        }
    }

    /// <summary>
    /// Filesystem seam over <see cref="EnsureDelegationRoot"/>: everything below
    /// is plain directory and file work, so a test can point
    /// <paramref name="mountRoot"/> at a temporary directory and assert on the
    /// exact values written.
    /// </summary>
    internal static string? TryPrepareDelegationRoot(
        string mountRoot,
        string selfCgroupRelativePath,
        Action<string> log,
        StraySweepContext? strays = null)
    {
        var own = Path.GetFullPath(Path.Combine(
            mountRoot,
            selfCgroupRelativePath.TrimStart('/')));
        // The daemon must already be in its leaf, placed there by systemd's
        // DelegateSubgroup=. It deliberately never moves itself: once a unit
        // cgroup distributes controllers, the kernel refuses to put a process
        // back into it (EBUSY), and KillMode=process keeps that cgroup alive
        // across a restart. A daemon that stepped aside by hand would therefore
        // make its own replacement unstartable for as long as one worker
        // survived. systemd >= 254 is the floor for this directive.
        if (!string.Equals(Path.GetFileName(own), DaemonLeafName, StringComparison.Ordinal))
        {
            log(
                $"[runner] worker resource envelope unavailable: this daemon runs in '{own}', "
                + $"not in a '{DaemonLeafName}' subgroup. Add 'Delegate=cpu pids' and "
                + $"'DelegateSubgroup={DaemonLeafName}' to the agent-host unit (systemd 254 or "
                + "newer) and reload systemd; runs continue uncapped until then.");
            return null;
        }
        var root = Path.GetDirectoryName(own)!;
        // Only a systemd service cgroup is ours to delegate. A user session
        // scope or a container root would otherwise be reshaped by a process
        // that does not own it.
        if (!Path.GetFileName(root).EndsWith(".service", StringComparison.Ordinal))
        {
            log(
                $"[runner] worker resource envelope unavailable: '{root}' is not a systemd "
                + "service cgroup, so there is no delegated subtree to carve workers out of");
            return null;
        }

        try
        {
            if (!File.Exists(Path.Combine(root, "cgroup.subtree_control")))
                throw new IOException($"{root} is not a cgroup v2 directory");
            // AGT-2868: processes no worker owns any more sit directly in the
            // unit cgroup on every host that has run before, and cgroup v2
            // refuses the controller write below while any of them is there.
            // Emptying the cgroup first is what makes delegation succeed on a
            // used host instead of logging applied=no for the rest of its life.
            if (strays is not null) UnitCgroupStraySweep.Sweep(root, strays, log);
            EnableControllers(root);
            // The daemon leaf outranks its workers so lease renewal cannot be
            // starved by the run it is keeping alive.
            TryWrite(Path.Combine(own, "cpu.weight"), FormatId(WorkerResourceEnvelope.SupervisorCpuWeight));
            return root;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log(
                $"[runner] worker resource envelope unavailable: cannot delegate {root} "
                + $"({exception.Message}). Add 'Delegate=cpu pids' to the agent-host unit "
                + "and reload systemd; runs continue uncapped until then.");
            return null;
        }
    }

    /// <summary>Create and configure one worker cgroup below an already delegated root.</summary>
    internal static WorkerCgroup? TryCreate(
        string delegationRoot,
        string workerDirectory,
        string workerName,
        WorkerResourceEnvelope envelope,
        Action<string> log)
    {
        var directory = Path.Combine(delegationRoot, WorkerPrefix + Sanitize(workerName));
        try
        {
            Directory.CreateDirectory(directory);
            var cpuMax = Path.Combine(directory, "cpu.max");
            var pidsMax = Path.Combine(directory, "pids.max");
            if (!File.Exists(cpuMax) || !File.Exists(pidsMax))
                throw new IOException(
                    "the cpu and pids controllers are not delegated to this subtree");
            File.WriteAllText(cpuMax, envelope.CpuMax);
            File.WriteAllText(Path.Combine(directory, "cpu.weight"), FormatId(envelope.CpuWeight));
            File.WriteAllText(pidsMax, FormatId(envelope.TasksMax));
            Directory.CreateDirectory(workerDirectory);
            File.WriteAllText(Path.Combine(workerDirectory, MarkerFileName), directory);
            return new WorkerCgroup(directory, envelope);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            log($"[runner] worker resource envelope not applied at {directory}: {exception.Message}");
            TryRemove(directory);
            return null;
        }
    }

    // ------------------------------------------------------------ pure decision

    /// <summary>
    /// Wrap a launch so the started process joins <paramref name="procsPath"/>
    /// and then becomes the requested command. The pid the caller observes is
    /// the worker's pid, because <c>exec</c> keeps it.
    /// </summary>
    internal static WorkerLaunch Launch(
        string fileName,
        IReadOnlyList<string> arguments,
        WorkerCgroup? cgroup)
        => cgroup is null
            ? new WorkerLaunch(fileName, arguments)
            : Wrap(cgroup.ProcsPath, fileName, arguments);

    internal static WorkerLaunch Wrap(string procsPath, string fileName, IReadOnlyList<string> arguments)
    {
        var wrapped = new List<string>(arguments.Count + 4)
        {
            "-c",
            AttachScript,
            "agent-host-worker",
            procsPath,
            fileName,
        };
        wrapped.AddRange(arguments);
        return new WorkerLaunch("/bin/sh", wrapped);
    }

    // ------------------------------------------------------- bounded side effects

    /// <summary>
    /// What the worker in this directory consumed, or null when it ran without an
    /// envelope. Must be read before <see cref="ReleaseFor"/> removes the cgroup.
    /// </summary>
    internal static WorkerResourceUsage? ReadUsageFor(string workerDirectory)
    {
        var directory = ReadMarker(workerDirectory);
        return directory is null ? null : ReadUsage(directory);
    }

    /// <summary>
    /// The pressure counters of the worker in this directory, or null when it ran
    /// without an envelope. Like <see cref="ReadUsageFor"/> this has to be read
    /// before <see cref="ReleaseFor"/> removes the cgroup.
    /// </summary>
    internal static WorkerCgroupPressure? ReadPressureFor(string workerDirectory)
    {
        var directory = ReadMarker(workerDirectory);
        return directory is null ? null : ReadPressure(directory);
    }

    internal static WorkerCgroupPressure? ReadPressure(string cgroupDirectory)
    {
        try
        {
            if (!Directory.Exists(cgroupDirectory)) return null;
            var memory = ReadEventCounters(
                Path.Combine(cgroupDirectory, "memory.events"),
                ["high", "max", "oom_kill"]);
            return new WorkerCgroupPressure(
                (int)ReadCounter(Path.Combine(cgroupDirectory, "pids.max")),
                ReadTaskPeak(cgroupDirectory),
                ReadEventCounters(Path.Combine(cgroupDirectory, "pids.events"), ["max"])[0],
                memory[0],
                memory[1],
                memory[2]);
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    internal static WorkerResourceUsage? ReadUsage(string cgroupDirectory)
    {
        try
        {
            var cpuSeconds = 0d;
            var statPath = Path.Combine(cgroupDirectory, "cpu.stat");
            if (!File.Exists(statPath)) return null;
            foreach (var line in File.ReadAllLines(statPath))
            {
                if (!line.StartsWith("usage_usec ", StringComparison.Ordinal)) continue;
                if (long.TryParse(
                        line["usage_usec ".Length..].Trim(),
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out var microseconds))
                    cpuSeconds = microseconds / 1_000_000d;
                break;
            }
            return new WorkerResourceUsage(cpuSeconds, ReadTaskPeak(cgroupDirectory));
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// Tear down the worker cgroup of a finished run and return how many
    /// processes were still in it. Silent when there was none.
    ///
    /// <para>AGT-2868: a worker's process tree is its cgroup, so whatever is
    /// still in <c>worker-&lt;attempt&gt;/</c> after the worker wrote its
    /// terminal result is a leftover by definition, whether it is an MSBuild
    /// node, a <c>qs-dev-stack</c> fixture, a dev server, or a file watcher.
    /// Before this, such a process was reparented to init, kept the run's
    /// working directory alive, and survived every later daemon restart because
    /// <c>KillMode=process</c> deliberately does not touch it. The count is
    /// reported on the <c>worker-envelope</c> line so a fixture that leaks is
    /// visible in the journal instead of only in a cgroup listing days
    /// later.</para>
    /// </summary>
    internal static int ReleaseFor(string workerDirectory, Action<string>? log = null)
    {
        var directory = ReadMarker(workerDirectory);
        if (directory is null) return 0;
        // AGT-2870: the path comes from a file on disk, so it is input, not a
        // constant. Writing cgroup.kill one level up would take the daemon and
        // every sibling worker with it, so a marker that does not name a
        // worker-* directory below this daemon's delegated root is refused.
        if (!ProcessSignalGuard.MayEmptyCgroup(
                directory, _delegationRoot, $"worker-cgroup-release worker={NameFor(workerDirectory)}", log))
            return 0;
        var killed = KillResidents(directory, log);
        TryRemove(directory);
        return killed;
    }

    /// <summary>
    /// Kill everything left in one worker cgroup and return how many processes
    /// that was. <c>cgroup.kill</c> (kernel 5.14 and newer) kills the whole
    /// subtree atomically, which is the only variant a forking leftover cannot
    /// escape; older kernels fall back to a signal per pid.
    /// </summary>
    internal static int KillResidents(string cgroupDirectory, Action<string>? log = null)
    {
        var procs = Path.Combine(cgroupDirectory, "cgroup.procs");
        var residents = ReadResidentPids(procs);
        if (residents.Count == 0) return 0;

        var killSwitch = Path.Combine(cgroupDirectory, "cgroup.kill");
        if (!TryWriteValue(killSwitch, "1"))
            foreach (var pid in residents) TryKill(pid, cgroupDirectory, log);

        // The kernel reaps asynchronously, and rmdir below refuses while the
        // cgroup is still populated. A short bounded wait keeps the directory
        // from being left behind for the next generation's sweep.
        for (var attempt = 0; attempt < 20 && ReadResidentPids(procs).Count > 0; attempt++)
            Thread.Sleep(50);
        return residents.Count;
    }

    /// <summary>
    /// Remove <c>worker-*</c> cgroups left behind by a previous daemon
    /// generation. Two things are protected: a cgroup that still holds a
    /// surviving detached worker is not empty, so the kernel refuses the
    /// removal, and a cgroup belonging to a slot this daemon is about to adopt
    /// is retained by name so its usage counters survive to be reported even if
    /// its worker finished while the daemon was down.
    /// </summary>
    internal static void SweepAbandoned(
        string delegationRoot,
        IEnumerable<string> retainedWorkerDirectories,
        Action<string> log)
    {
        var retained = retainedWorkerDirectories
            .Select(directory => WorkerPrefix + NameFor(directory))
            .ToHashSet(StringComparer.Ordinal);
        try
        {
            foreach (var directory in Directory.EnumerateDirectories(delegationRoot, WorkerPrefix + "*"))
            {
                if (retained.Contains(Path.GetFileName(directory))) continue;
                if (!TryRemove(directory)) continue;
                log($"[runner] removed abandoned worker cgroup {Path.GetFileName(directory)}");
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // A sweep is hygiene only; the next one gets another chance.
        }
    }

    internal WorkerResourceUsage? ReadUsage() => ReadUsage(CgroupDirectory);

    // -------------------------------------------------------------------- local

    private static int ReadTaskPeak(string cgroupDirectory)
    {
        foreach (var candidate in new[] { "pids.peak", "pids.current" })
        {
            var path = Path.Combine(cgroupDirectory, candidate);
            if (!File.Exists(path)) continue;
            if (int.TryParse(
                    File.ReadAllText(path).Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var peak))
                return peak;
        }
        return WorkerResourceUsage.UnknownTasks;
    }

    /// <summary>
    /// One numeric cgroup knob. The literal <c>max</c> (no limit configured) and
    /// a missing file are both reported as unknown rather than as a number.
    /// </summary>
    private static long ReadCounter(string path)
    {
        if (!File.Exists(path)) return WorkerCgroupPressure.Unknown;
        return long.TryParse(
            File.ReadAllText(path).Trim(),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : WorkerCgroupPressure.Unknown;
    }

    /// <summary>
    /// Selected rows of a cgroup <c>*.events</c> file ("&lt;key&gt; &lt;count&gt;" per
    /// line), in the order the keys were requested. A key the kernel does not
    /// publish stays unknown, which is not the same as zero.
    /// </summary>
    private static long[] ReadEventCounters(string path, IReadOnlyList<string> keys)
    {
        var counters = new long[keys.Count];
        Array.Fill(counters, WorkerCgroupPressure.Unknown);
        if (!File.Exists(path)) return counters;
        foreach (var line in File.ReadAllLines(path))
        {
            var separator = line.IndexOf(' ');
            if (separator <= 0) continue;
            var key = line[..separator];
            var index = -1;
            for (var candidate = 0; candidate < keys.Count; candidate++)
                if (string.Equals(keys[candidate], key, StringComparison.Ordinal)) index = candidate;
            if (index < 0) continue;
            if (long.TryParse(
                    line[(separator + 1)..].Trim(),
                    NumberStyles.Integer,
                    CultureInfo.InvariantCulture,
                    out var value))
                counters[index] = value;
        }
        return counters;
    }

    private static string? ReadMarker(string workerDirectory)
    {
        try
        {
            var marker = Path.Combine(workerDirectory, MarkerFileName);
            if (!File.Exists(marker)) return null;
            var path = File.ReadAllText(marker).Trim();
            return path.Length > 0 && Directory.Exists(path) ? path : null;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void EnableControllers(string root)
    {
        var path = Path.Combine(root, "cgroup.subtree_control");
        if (!File.Exists(path)) return;
        var enabled = File.ReadAllText(path);
        if (enabled.Contains("cpu", StringComparison.Ordinal)
            && enabled.Contains("pids", StringComparison.Ordinal))
            return;
        File.WriteAllText(path, RequiredControllers);
    }

    private static bool TryRemove(string directory)
    {
        try
        {
            // rmdir on a cgroup fails while it still holds processes, which is
            // exactly the protection a surviving detached worker needs.
            if (Directory.Exists(directory)) Directory.Delete(directory);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    private static IReadOnlyList<int> ReadResidentPids(string procsPath)
    {
        try
        {
            if (!File.Exists(procsPath)) return [];
            return File.ReadAllLines(procsPath)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0)
                .Select(line => int.TryParse(line, NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid)
                    ? pid
                    : 0)
                .Where(pid => pid > 0)
                .Distinct()
                .ToArray();
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return [];
        }
    }

    private static bool TryWriteValue(string path, string value)
    {
        try
        {
            if (!File.Exists(path)) return false;
            File.WriteAllText(path, value);
            return true;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return false;
        }
    }

    /// <summary>
    /// The fallback for kernels without <c>cgroup.kill</c>: one signal per listed
    /// member. Already gone, or not ours to kill, is not an error - the next
    /// generation's startup sweep sees whatever survives. AGT-2870 routes it
    /// through the shared guard so a truncated or malformed <c>cgroup.procs</c>
    /// line cannot become a broadcast pid.
    /// </summary>
    private static void TryKill(int pid, string cgroupDirectory, Action<string>? log)
        => ProcessSignalGuard.TryKillSingle(
            pid, $"worker-cgroup-resident cgroup={Path.GetFileName(cgroupDirectory)}", log: log);

    private static void TryWrite(string path, string value)
    {
        try { if (File.Exists(path)) File.WriteAllText(path, value); }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            // Best effort: a missing knob is not worth failing a run over.
        }
    }

    private static string? ReadSelfCgroupRelativePath()
    {
        try
        {
            foreach (var line in File.ReadAllLines("/proc/self/cgroup"))
            {
                if (!line.StartsWith("0::", StringComparison.Ordinal)) continue;
                var relative = line[3..].Trim();
                if (relative.Length > 0) return relative;
            }
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
        return null;
    }

    private static string FormatId(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>cgroup directory names are filenames: keep them short and boring.</summary>
    internal static string Sanitize(string name)
    {
        var sanitized = new string(name
            .Select(character => char.IsLetterOrDigit(character) || character is '-' or '_' ? character : '-')
            .ToArray())
            .Trim('-');
        if (sanitized.Length == 0) sanitized = "unnamed";
        return sanitized.Length <= 96 ? sanitized : sanitized[..96];
    }
}
