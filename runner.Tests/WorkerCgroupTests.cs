using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// Minimal <see cref="RunnerOptions"/> for the AGT-2866 envelope suites. Only the
/// required members plus the slot budget matter here; nothing in this file starts
/// a CLI.
/// </summary>
internal static class RunnerOptionsFixture
{
    internal static RunnerOptions WithSlots(
        int codingSlots,
        int reviewSlots,
        double burst = WorkerResourceEnvelope.DefaultCpuBurst,
        bool envelopeEnabled = true)
        => new()
        {
            ServerUrl = "http://127.0.0.1:5030",
            RunnerId = "agent-runner-envelope-test",
            RunnerName = "agent-runner-envelope-test",
            Hostname = "envelope-test-host",
            BackendName = "envelope-test",
            WorkDir = Path.Combine(Path.GetTempPath(), "agent-runner-envelope-test"),
            BaseBranch = "main",
            CliBin = "claude",
            CliArgs = "-p",
            HostMaxParallelism = Math.Max(1, codingSlots),
            HostCodingSlots = codingSlots,
            HostReviewSlots = reviewSlots,
            WorkerCpuBurst = burst,
            WorkerEnvelopeEnabled = envelopeEnabled,
        };
}

/// <summary>
/// The cgroup side of the AGT-2866 envelope. Everything below the mount root is
/// ordinary file work, so the limits actually written are asserted against a
/// temporary directory that mimics a delegated cgroup v2 subtree. The one
/// behaviour that cannot be faked, a runaway child tree being refused by
/// <c>pids.max</c>, runs only where a writable subtree really exists.
/// </summary>
public sealed class WorkerCgroupTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "worker-cgroup-tests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void Worker_cgroup_is_created_with_the_computed_limits()
    {
        var delegated = FakeDelegatedRoot("AGT-2866");
        var workerDirectory = Path.Combine(_root, "worker");
        var envelope = WorkerResourceEnvelope.Compute(hostCores: 12, codingSlots: 2, reviewSlots: 2);

        var cgroup = WorkerCgroup.TryCreate(delegated, workerDirectory, "AGT-2866", envelope, Ignore);

        Assert.NotNull(cgroup);
        var directory = Path.Combine(delegated, "worker-AGT-2866");
        Assert.Equal(directory, cgroup!.CgroupDirectory);
        Assert.Equal("600000 100000", File.ReadAllText(Path.Combine(directory, "cpu.max")));
        Assert.Equal("100", File.ReadAllText(Path.Combine(directory, "cpu.weight")));
        Assert.Equal("384", File.ReadAllText(Path.Combine(directory, "pids.max")));
        // The marker is what lets a replacement daemon report the same worker's
        // usage after a KillMode=process restart.
        Assert.Equal(
            directory,
            File.ReadAllText(Path.Combine(workerDirectory, WorkerCgroup.MarkerFileName)));
    }

    [Fact]
    public void Caps_written_to_the_cgroup_follow_the_slot_counts()
    {
        var delegated = FakeDelegatedRoot("two-slots", "eight-slots");

        WorkerCgroup.TryCreate(
            delegated,
            Path.Combine(_root, "two"),
            "two-slots",
            WorkerResourceEnvelope.Compute(12, 1, 1),
            Ignore);
        WorkerCgroup.TryCreate(
            delegated,
            Path.Combine(_root, "eight"),
            "eight-slots",
            WorkerResourceEnvelope.Compute(12, 4, 4),
            Ignore);

        Assert.Equal(
            "1200000 100000",
            File.ReadAllText(Path.Combine(delegated, "worker-two-slots", "cpu.max")));
        Assert.Equal("768", File.ReadAllText(Path.Combine(delegated, "worker-two-slots", "pids.max")));
        Assert.Equal(
            "300000 100000",
            File.ReadAllText(Path.Combine(delegated, "worker-eight-slots", "cpu.max")));
        Assert.Equal("192", File.ReadAllText(Path.Combine(delegated, "worker-eight-slots", "pids.max")));
    }

    [Fact]
    public void A_subtree_without_delegated_controllers_leaves_the_launch_alone()
    {
        // A unit without Delegate=cpu pids has no cpu.max/pids.max below it.
        var delegated = Path.Combine(_root, "undelegated");
        Directory.CreateDirectory(delegated);
        var messages = new List<string>();

        var cgroup = WorkerCgroup.TryCreate(
            delegated,
            Path.Combine(_root, "worker"),
            "AGT-2866",
            WorkerResourceEnvelope.Compute(12, 2, 2),
            messages.Add);

        Assert.Null(cgroup);
        Assert.Contains(messages, message => message.Contains("not delegated", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(delegated, "worker-AGT-2866")));
    }

    [Fact]
    public void Launch_without_a_cgroup_is_the_unchanged_pre_envelope_command()
    {
        var launch = WorkerCgroup.Launch("/opt/agent-host/current/agent-host", ["--detached-worker", "spec.json"], null);

        Assert.Equal("/opt/agent-host/current/agent-host", launch.FileName);
        Assert.Equal(new[] { "--detached-worker", "spec.json" }, launch.Arguments);
    }

    [Fact]
    public void Launch_inside_a_cgroup_attaches_first_and_then_execs_the_worker()
    {
        var delegated = FakeDelegatedRoot("AGT-2866");
        var cgroup = WorkerCgroup.TryCreate(
            delegated,
            Path.Combine(_root, "worker"),
            "AGT-2866",
            WorkerResourceEnvelope.Compute(12, 2, 2),
            Ignore)!;

        var launch = WorkerCgroup.Launch(
            "/opt/agent-host/current/agent-host",
            ["--detached-worker", "/var/lib/agent-runner/state/w/spec.json"],
            cgroup);

        Assert.Equal("/bin/sh", launch.FileName);
        Assert.Equal(
            new[]
            {
                "-c",
                WorkerCgroup.AttachScript,
                "agent-host-worker",
                Path.Combine(delegated, "worker-AGT-2866", "cgroup.procs"),
                "/opt/agent-host/current/agent-host",
                "--detached-worker",
                "/var/lib/agent-runner/state/w/spec.json",
            },
            launch.Arguments);
    }

    /// <summary>
    /// The load-bearing property of the wrapper: the pid written into
    /// <c>cgroup.procs</c> is the pid the payload keeps, because the shell
    /// <c>exec</c>s instead of forking. Every reattachment proof in
    /// <c>DurableAgentProcess</c> and <c>DurableReviewProcess</c> (persisted pid,
    /// start time, <c>/proc/&lt;pid&gt;/cwd</c>) depends on it.
    ///
    /// <para>Linux-only, and not merely "needs a shell": the assertion is that
    /// <c>exec</c> replaces the process image, which is a POSIX kernel property.
    /// An MSYS <c>sh</c> from Git for Windows accepts the same script but Windows
    /// has no <c>execve</c>, so the payload always starts as a new process and the
    /// attached pid can never equal the worker pid there. Gating this on
    /// <see cref="PlatformGate.RequiresPosixShell"/> would therefore assert a
    /// property the platform cannot have.</para>
    /// </summary>
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void The_attached_pid_is_the_worker_pid_because_the_shell_execs()
    {
        PlatformGate.LinuxOnly("the wrapper relies on exec replacing the process image");
        Directory.CreateDirectory(_root);
        var procs = Path.Combine(_root, "cgroup.procs");
        var launch = WorkerCgroup.Wrap(procs, PosixShell.RequirePath(), ["-c", "echo $$"]);

        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var payloadPid = process.StandardOutput.ReadToEnd().Trim();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Equal(payloadPid, File.ReadAllText(procs).Trim());
        Assert.Equal(process.Id.ToString(), payloadPid);
    }

    [SkippableFact]
    public void An_unwritable_cgroup_never_costs_the_run_its_worker()
    {
        PlatformGate.RequiresPosixShell();
        Directory.CreateDirectory(_root);
        var launch = WorkerCgroup.Wrap(
            Path.Combine(_root, "missing-subtree", "cgroup.procs"),
            PosixShell.RequirePath(),
            ["-c", "echo worker-still-ran"]);

        var start = new ProcessStartInfo
        {
            FileName = PosixShell.RequirePath(),
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = _root,
        };
        foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEnd();
        var diagnostics = process.StandardError.ReadToEnd();
        process.WaitForExit();

        Assert.Equal(0, process.ExitCode);
        Assert.Contains("worker-still-ran", output);
        Assert.Contains("worker cgroup attach failed", diagnostics);
    }

    [Fact]
    public void Finished_worker_reports_cpu_seconds_and_peak_tasks()
    {
        var delegated = FakeDelegatedRoot("AGT-2866");
        var workerDirectory = Path.Combine(_root, "worker");
        var cgroup = WorkerCgroup.TryCreate(
            delegated,
            workerDirectory,
            "AGT-2866",
            WorkerResourceEnvelope.Compute(12, 2, 2),
            Ignore)!;
        File.WriteAllText(
            Path.Combine(cgroup.CgroupDirectory, "cpu.stat"),
            "usage_usec 2000500000\nuser_usec 1800000000\nsystem_usec 200500000\n");
        File.WriteAllText(Path.Combine(cgroup.CgroupDirectory, "pids.peak"), "271\n");

        var usage = WorkerCgroup.ReadUsageFor(workerDirectory);

        Assert.NotNull(usage);
        Assert.Equal(2000.5, usage!.CpuSeconds, 3);
        Assert.Equal(271, usage.PeakTasks);
        // The card's test: 2,000 CPU seconds of real work must read differently
        // from a run that spun.
        Assert.Equal("cpuSeconds=2000.5 peakTasks=271", usage.Describe());
    }

    [Fact]
    public void Usage_is_absent_rather_than_wrong_when_no_envelope_was_applied()
    {
        var workerDirectory = Path.Combine(_root, "worker");
        Directory.CreateDirectory(workerDirectory);

        Assert.Null(WorkerCgroup.ReadUsageFor(workerDirectory));
        WorkerCgroup.ReleaseFor(workerDirectory);
    }

    [Fact]
    public void Release_and_sweep_remove_only_finished_worker_cgroups()
    {
        var delegated = FakeDelegatedRoot("finished");
        var workerDirectory = Path.Combine(_root, "worker");
        var cgroup = WorkerCgroup.TryCreate(
            delegated,
            workerDirectory,
            "finished",
            WorkerResourceEnvelope.Compute(12, 2, 2),
            Ignore)!;
        var abandoned = Path.Combine(delegated, "worker-previous-generation");
        Directory.CreateDirectory(abandoned);
        // The kernel removes a cgroup on plain rmdir together with its own
        // interface files, and refuses only while processes remain in it. A
        // temporary directory cannot model that, so the seeded interface files
        // are cleared first and the assertion stays about the right target.
        foreach (var file in Directory.EnumerateFiles(cgroup.CgroupDirectory)) File.Delete(file);

        WorkerCgroup.ReleaseFor(workerDirectory);
        WorkerCgroup.SweepAbandoned(delegated, [], Ignore);

        Assert.False(Directory.Exists(cgroup.CgroupDirectory));
        Assert.False(Directory.Exists(abandoned));
    }

    /// <summary>
    /// A worker that finished while the daemon was down still has its counters
    /// read once the replacement daemon reports it, so the startup sweep must
    /// not take the cgroup of a slot that is about to be adopted.
    /// </summary>
    [Fact]
    public void Sweep_keeps_the_cgroup_of_a_slot_this_daemon_is_about_to_adopt()
    {
        var delegated = FakeDelegatedRoot(WorkerCgroup.NameFor(Path.Combine(_root, "adopted")));
        var adoptedWorkerDirectory = Path.Combine(_root, "adopted");
        var cgroup = WorkerCgroup.TryCreate(
            delegated,
            adoptedWorkerDirectory,
            WorkerCgroup.NameFor(adoptedWorkerDirectory),
            WorkerResourceEnvelope.Compute(12, 2, 2),
            Ignore)!;
        var abandoned = Path.Combine(delegated, "worker-previous-generation");
        Directory.CreateDirectory(abandoned);

        WorkerCgroup.SweepAbandoned(delegated, [adoptedWorkerDirectory], Ignore);

        Assert.True(Directory.Exists(cgroup.CgroupDirectory));
        Assert.False(Directory.Exists(abandoned));
    }

    [Fact]
    public void Worker_names_stay_unique_across_a_same_session_resume()
    {
        var attempt = Path.Combine(_root, "state", "attempt-1");
        var resume = Path.Combine(attempt, "resume-1");

        Assert.StartsWith("attempt-1-", WorkerCgroup.NameFor(attempt), StringComparison.Ordinal);
        Assert.StartsWith("resume-1-", WorkerCgroup.NameFor(resume), StringComparison.Ordinal);
        Assert.NotEqual(WorkerCgroup.NameFor(attempt), WorkerCgroup.NameFor(resume));
        Assert.Equal(WorkerCgroup.NameFor(attempt), WorkerCgroup.NameFor(attempt));
    }

    [Fact]
    public void The_daemon_delegates_from_the_unit_cgroup_systemd_parked_it_below()
    {
        var unit = Path.Combine(_root, "system.slice", "agent-runner.service");
        Directory.CreateDirectory(Path.Combine(unit, WorkerCgroup.DaemonLeafName));
        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), string.Empty);
        File.WriteAllText(
            Path.Combine(unit, WorkerCgroup.DaemonLeafName, "cpu.weight"),
            "100");

        var root = WorkerCgroup.TryPrepareDelegationRoot(
            _root,
            "/system.slice/agent-runner.service/daemon",
            Ignore);

        Assert.Equal(unit, root);
        Assert.Equal(
            WorkerCgroup.RequiredControllers,
            File.ReadAllText(Path.Combine(unit, "cgroup.subtree_control")));
        // Lease renewal outranks the run it is keeping alive.
        Assert.Equal(
            WorkerResourceEnvelope.SupervisorCpuWeight.ToString(),
            File.ReadAllText(Path.Combine(unit, WorkerCgroup.DaemonLeafName, "cpu.weight")));
    }

    [Fact]
    public void Controllers_already_delegated_by_systemd_are_not_rewritten()
    {
        var unit = Path.Combine(_root, "system.slice", "agent-runner-review.service");
        Directory.CreateDirectory(Path.Combine(unit, WorkerCgroup.DaemonLeafName));
        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), "cpu pids");

        var root = WorkerCgroup.TryPrepareDelegationRoot(
            _root,
            "/system.slice/agent-runner-review.service/daemon",
            Ignore);

        Assert.Equal(unit, root);
        Assert.Equal("cpu pids", File.ReadAllText(Path.Combine(unit, "cgroup.subtree_control")));
    }

    /// <summary>
    /// The daemon must never move itself out of the unit cgroup. Once that
    /// cgroup distributes controllers the kernel refuses to put a process back
    /// into it, and <c>KillMode=process</c> keeps it alive across a restart, so
    /// a self-relocating daemon would leave its own replacement unstartable for
    /// as long as one worker survived. The unit has to say
    /// <c>DelegateSubgroup=daemon</c> instead, and the message says so.
    /// </summary>
    [Fact]
    public void A_daemon_still_in_the_unit_cgroup_refuses_to_move_itself()
    {
        var unit = Path.Combine(_root, "system.slice", "agent-runner.service");
        Directory.CreateDirectory(unit);
        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), string.Empty);
        var messages = new List<string>();

        var root = WorkerCgroup.TryPrepareDelegationRoot(
            _root,
            "/system.slice/agent-runner.service",
            messages.Add);

        Assert.Null(root);
        Assert.Contains(
            messages,
            message => message.Contains("DelegateSubgroup=daemon", StringComparison.Ordinal));
        Assert.False(Directory.Exists(Path.Combine(unit, WorkerCgroup.DaemonLeafName)));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(unit, "cgroup.subtree_control")));
    }

    [Fact]
    public void A_host_without_a_delegated_subtree_says_what_the_operator_must_add()
    {
        var messages = new List<string>();

        var root = WorkerCgroup.TryPrepareDelegationRoot(
            Path.Combine(_root, "not-a-cgroup-mount"),
            "/system.slice/agent-runner.service/daemon",
            messages.Add);

        Assert.Null(root);
        Assert.Contains(messages, message => message.Contains("Delegate=cpu pids", StringComparison.Ordinal));
    }

    [Fact]
    public void A_process_outside_a_service_cgroup_never_reshapes_the_subtree_it_landed_in()
    {
        // A login shell or a CI container sits in a cgroup it does not own.
        var scope = Path.Combine(_root, "user.slice", "session-3.scope");
        Directory.CreateDirectory(Path.Combine(scope, WorkerCgroup.DaemonLeafName));
        File.WriteAllText(Path.Combine(scope, "cgroup.subtree_control"), string.Empty);
        var messages = new List<string>();

        var root = WorkerCgroup.TryPrepareDelegationRoot(
            _root,
            "/user.slice/session-3.scope/daemon",
            messages.Add);

        Assert.Null(root);
        Assert.Contains(
            messages,
            message => message.Contains("not a systemd service cgroup", StringComparison.Ordinal));
        Assert.Equal(string.Empty, File.ReadAllText(Path.Combine(scope, "cgroup.subtree_control")));
    }

    /// <summary>
    /// The runaway case the card asks for, proven against the kernel rather than
    /// a fake: a worker that forks more tasks than its envelope allows is refused
    /// by <c>pids.max</c> instead of filling the host's process table. Runs only
    /// where this process can really create a cgroup below its own, which is the
    /// delegated agent-host unit on a runner host.
    /// </summary>
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public void A_runaway_child_tree_is_contained_by_pids_max()
    {
        PlatformGate.LinuxOnly("pids.max containment is a cgroup v2 controller");
        var delegated = WritableDelegatedSubtree();
        Skip.If(
            delegated is null,
            "No writable cgroup v2 subtree: run on an agent-host unit with 'Delegate=cpu pids'.");

        var workerDirectory = Path.Combine(_root, "worker");
        var envelope = WorkerResourceEnvelope.Compute(12, 2, 2) with { TasksMax = 8 };
        var cgroup = WorkerCgroup.TryCreate(
            delegated!,
            workerDirectory,
            "runaway-" + Guid.NewGuid().ToString("N")[..8],
            envelope,
            Ignore);
        Skip.If(cgroup is null, "The cpu and pids controllers are not delegated to this subtree.");

        try
        {
            // Forty detached busy loops is the shape of the 17.09.2026 incident,
            // scaled down. pids.max must refuse most of them and the shell must
            // report the refusal.
            var launch = WorkerCgroup.Wrap(
                cgroup!.ProcsPath,
                "/bin/sh",
                ["-c", "i=0; while [ $i -lt 40 ]; do sh -c 'sleep 5' & i=$((i+1)); done; wait"]);
            var start = new ProcessStartInfo
            {
                FileName = launch.FileName,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                WorkingDirectory = _root,
            };
            foreach (var argument in launch.Arguments) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var diagnostics = process.StandardError.ReadToEnd();
            process.WaitForExit(30_000);

            var peak = WorkerCgroup.ReadUsageFor(workerDirectory)!.PeakTasks;
            // The envelope, not the host, is what stopped the fan-out.
            Assert.InRange(peak, 1, envelope.TasksMax);
            Assert.NotEqual(string.Empty, diagnostics.Trim());
        }
        finally
        {
            WorkerCgroup.ReleaseFor(workerDirectory);
        }
    }

    /// <summary>
    /// A directory shaped like a delegated cgroup v2 subtree. The kernel grows
    /// <c>cpu.max</c> and <c>pids.max</c> inside a child the moment it is
    /// created below a cgroup that distributes those controllers, and
    /// <see cref="WorkerCgroup.TryCreate"/> treats their absence as proof that
    /// the unit lacks <c>Delegate=cpu pids</c>. The fake therefore seeds the
    /// children it is asked for, and the undelegated case simply omits them.
    /// </summary>
    private string FakeDelegatedRoot(params string[] workerNames)
    {
        var delegated = Path.Combine(_root, "system.slice", "agent-runner.service");
        Directory.CreateDirectory(delegated);
        File.WriteAllText(Path.Combine(delegated, "cgroup.subtree_control"), "cpu pids");
        foreach (var name in workerNames)
        {
            var child = Path.Combine(delegated, WorkerCgroup.WorkerPrefix + name);
            Directory.CreateDirectory(child);
            File.WriteAllText(Path.Combine(child, "cpu.max"), "max 100000");
            File.WriteAllText(Path.Combine(child, "cpu.weight"), "100");
            File.WriteAllText(Path.Combine(child, "pids.max"), "max");
        }
        return delegated;
    }

    /// <summary>
    /// The real cgroup subtree this process may carve workers out of, if any.
    ///
    /// <para>This deliberately does not go through
    /// <see cref="WorkerCgroup.EnsureDelegationRoot"/>: that one refuses anything
    /// that is not a systemd <c>.service</c> cgroup, which is the right
    /// production guard and has its own tests, but it would also refuse the one
    /// place a test host can legitimately get a delegated subtree. Two hosts
    /// qualify: an agent-host unit with <c>Delegate=cpu pids</c>, and a run
    /// wrapped in <c>systemd-run --user --scope -p Delegate=yes</c>.</para>
    ///
    /// <para>The subtree is only reshaped when it was created for this run: an
    /// agent-host <c>.service</c> or a <c>run-*.scope</c> transient unit. A
    /// plain <c>dotnet test</c> from a login shell lands in the session's own
    /// scope and skips, rather than moving the shell that started it into a
    /// leaf cgroup.</para>
    /// </summary>
    private static string? WritableDelegatedSubtree()
    {
        if (!OperatingSystem.IsLinux()) return null;
        try
        {
            var unified = File.ReadAllLines("/proc/self/cgroup")
                .FirstOrDefault(line => line.StartsWith("0::", StringComparison.Ordinal));
            if (unified is null) return null;
            var own = Path.GetFullPath(Path.Combine(
                WorkerCgroup.DefaultMountRoot,
                unified[3..].Trim().TrimStart('/')));
            var root = string.Equals(
                Path.GetFileName(own),
                WorkerCgroup.DaemonLeafName,
                StringComparison.Ordinal)
                ? Path.GetDirectoryName(own)!
                : own;

            var name = Path.GetFileName(root);
            var ours = name.EndsWith(".service", StringComparison.Ordinal)
                       || (name.StartsWith("run-", StringComparison.Ordinal)
                           && name.EndsWith(".scope", StringComparison.Ordinal));
            if (!ours) return null;

            var leaf = Path.Combine(root, WorkerCgroup.DaemonLeafName);
            Directory.CreateDirectory(leaf);
            // cgroup v2 refuses to distribute controllers out of a cgroup that
            // still holds processes, so the whole test run steps into the leaf
            // first. The daemon never does this to itself (see
            // A_daemon_still_in_the_unit_cgroup_refuses_to_move_itself); a
            // transient test scope is not restarted, so here it is safe and it
            // is the only way to get a delegated subtree without systemd's
            // DelegateSubgroup=.
            foreach (var resident in File.ReadAllLines(Path.Combine(root, "cgroup.procs"))
                         .Where(line => line.Trim().Length > 0))
                File.WriteAllText(Path.Combine(leaf, "cgroup.procs"), resident.Trim());
            var subtree = Path.Combine(root, "cgroup.subtree_control");
            if (!File.ReadAllText(subtree).Contains("pids", StringComparison.Ordinal))
                File.WriteAllText(subtree, WorkerCgroup.RequiredControllers);
            return root;
        }
        catch (Exception exception) when (
            exception is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return null;
        }
    }

    private static void Ignore(string message) { }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
