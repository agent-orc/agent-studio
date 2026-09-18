using System.Diagnostics;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-2868 deliverable 2. Everything the sweep does below the mount root is
/// ordinary directory and file work, so the classification and the parking are
/// asserted against a temporary directory shaped like a role unit's cgroup. The
/// one thing a temporary directory cannot model, the kernel refusing
/// <c>cgroup.subtree_control</c> while the unit cgroup holds a process, is the
/// reason the sweep is called before <c>EnableControllers</c> at all; the last
/// test here pins that both happen in one preparation.
/// </summary>
public sealed class UnitCgroupStraySweepTests : IDisposable
{
    private const int SelfPid = 4242;
    private static readonly DateTime Now = new(2026, 9, 18, 5, 15, 0, DateTimeKind.Utc);
    private static readonly TimeSpan RunTimeout = TimeSpan.FromHours(1);

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "unit-cgroup-stray-tests",
        Guid.NewGuid().ToString("N"));

    private readonly string _workRoot;

    public UnitCgroupStraySweepTests()
        => _workRoot = Path.Combine(_root, "var", "lib", "agent-runner", "work");

    // ------------------------------------------------------------ classification

    [Fact]
    public void A_process_started_below_a_work_root_is_attributed_to_its_attempt_directory()
    {
        var (generation, isLive) = UnitCgroupStraySweep.Attribute(
            Path.Combine(_workRoot, "AGT-2867", "backend.Tests"),
            [new StrayGenerationRoot(_workRoot)]);

        Assert.Equal("AGT-2867", generation);
        Assert.False(isLive);
    }

    [Fact]
    public void A_slot_this_daemon_adopts_outranks_the_work_root_that_contains_it()
    {
        var worktree = Path.Combine(_workRoot, "AGT-2867");

        var (generation, isLive) = UnitCgroupStraySweep.Attribute(
            Path.Combine(worktree, "frontend"),
            [
                new StrayGenerationRoot(_workRoot),
                new StrayGenerationRoot(worktree, "attempt-7f21"),
            ]);

        Assert.Equal("attempt-7f21", generation);
        Assert.True(isLive);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("/usr/lib/systemd")]
    public void A_process_outside_every_known_root_stays_unattributed(string? workingDirectory)
    {
        var (generation, isLive) = UnitCgroupStraySweep.Attribute(
            workingDirectory,
            [new StrayGenerationRoot(_workRoot)]);

        Assert.Null(generation);
        Assert.False(isLive);
    }

    /// <summary>
    /// Killing is the narrow case: attributable, not adopted, and past the
    /// longest a run may legitimately take. The 18.09.2026 host's MSBuild nodes
    /// were 1.7 to 2.7 days old against a one-hour run timeout.
    /// </summary>
    [Theory]
    [InlineData("AGT-2867", false, 2.7 * 24, true)]
    [InlineData("AGT-2867", false, 0.5, false)]
    [InlineData("attempt-7f21", true, 2.7 * 24, false)]
    [InlineData(null, false, 2.7 * 24, false)]
    public void Only_an_aged_stray_of_a_finished_generation_is_terminated(
        string? generation,
        bool isLive,
        double ageHours,
        bool expected)
    {
        var stray = new CgroupStray(
            9001,
            TimeSpan.FromHours(ageHours),
            "dotnet MSBuild.dll /nodemode:1",
            generation,
            isLive);

        Assert.Equal(expected, UnitCgroupStraySweep.ShouldTerminate(stray, RunTimeout));
    }

    // ------------------------------------------------------------------- sweeping

    [Fact]
    public void The_daemons_own_process_is_never_a_stray_of_its_own_unit()
    {
        var unit = FakeUnitCgroup(SelfPid);
        var messages = new List<string>();

        var outcome = Sweep(unit, messages, []);

        Assert.Equal(new StraySweepOutcome(0, 0, 0), outcome);
        Assert.Empty(messages);
        Assert.False(Directory.Exists(Path.Combine(unit, UnitCgroupStraySweep.LeafName)));
    }

    [Fact]
    public void An_unattributable_stray_is_parked_in_the_strays_leaf_and_left_alive()
    {
        var unit = FakeUnitCgroup(SelfPid, 5001);
        var messages = new List<string>();
        var killed = new List<int>();

        var outcome = Sweep(
            unit,
            messages,
            killed,
            Facts(5001, hoursOld: 480, command: "git remote-https origin", cwd: "/tmp"));

        Assert.Equal(new StraySweepOutcome(1, 1, 0), outcome);
        Assert.Empty(killed);
        Assert.Equal(
            "5001",
            File.ReadAllText(Path.Combine(unit, UnitCgroupStraySweep.LeafName, "cgroup.procs")).Trim());
        Assert.Contains(messages, message => message.Contains("pid=5001", StringComparison.Ordinal)
                                             && message.Contains("age=480.0h", StringComparison.Ordinal)
                                             && message.Contains("generation=unknown", StringComparison.Ordinal)
                                             && message.Contains("action=moved", StringComparison.Ordinal)
                                             && message.Contains("git remote-https origin", StringComparison.Ordinal));
    }

    /// <summary>
    /// The exact shape of the 18.09.2026 finding: MSBuild worker nodes days old,
    /// started in an attempt directory no slot claims any more. They are moved
    /// out of the way first and killed second, so a process that ignores the
    /// signal still cannot block the controller write.
    /// </summary>
    [Fact]
    public void An_aged_stray_of_a_finished_attempt_is_moved_and_then_killed()
    {
        var unit = FakeUnitCgroup(SelfPid, 5002);
        var messages = new List<string>();
        var killed = new List<int>();

        var outcome = Sweep(
            unit,
            messages,
            killed,
            Facts(
                5002,
                hoursOld: 2.5 * 24,
                command: "dotnet MSBuild.dll /nodemode:1",
                cwd: Path.Combine(_workRoot, "AGT-2820", "backend")));

        Assert.Equal(new StraySweepOutcome(1, 1, 1), outcome);
        Assert.Equal([5002], killed);
        Assert.Equal(
            "5002",
            File.ReadAllText(Path.Combine(unit, UnitCgroupStraySweep.LeafName, "cgroup.procs")).Trim());
        Assert.Contains(messages, message => message.Contains("generation=AGT-2820", StringComparison.Ordinal)
                                             && message.Contains("action=terminated", StringComparison.Ordinal));
        Assert.Contains(messages, message => message.Contains("found=1 moved=1 terminated=1", StringComparison.Ordinal));
    }

    /// <summary>
    /// A helper of a worker that <c>KillMode=process</c> deliberately left
    /// running belongs to a slot this daemon is adopting. It has to leave the
    /// unit cgroup so delegation can proceed, but killing it would kill the run.
    /// </summary>
    [Fact]
    public void A_stray_belonging_to_an_adopted_slot_is_parked_but_never_killed()
    {
        var unit = FakeUnitCgroup(SelfPid, 5003);
        var worktree = Path.Combine(_workRoot, "AGT-2868");
        var messages = new List<string>();
        var killed = new List<int>();

        var outcome = UnitCgroupStraySweep.Sweep(
            unit,
            new StraySweepContext(
                RunTimeout,
                [new StrayGenerationRoot(_workRoot), new StrayGenerationRoot(worktree, "attempt-live")]),
            messages.Add,
            Facts(5003, hoursOld: 30, command: "node /tmp/qs-dev-stack-a/api.mjs", cwd: worktree),
            pid => { killed.Add(pid); return true; },
            Now,
            SelfPid);

        Assert.Equal(new StraySweepOutcome(1, 1, 0), outcome);
        Assert.Empty(killed);
        Assert.Contains(messages, message => message.Contains("generation=attempt-live (live)", StringComparison.Ordinal)
                                             && message.Contains("action=moved", StringComparison.Ordinal));
    }

    /// <summary>
    /// Parking is not a place to retire in. A stray that was too young, or not
    /// yet attributable, when a previous generation parked it is judged again on
    /// every start, otherwise it would hold its file descriptors and inotify
    /// instances for the life of the host.
    /// </summary>
    [Fact]
    public void A_stray_parked_by_an_earlier_generation_is_judged_again_and_killed_once_it_qualifies()
    {
        var unit = FakeUnitCgroup(SelfPid);
        var leaf = Path.Combine(unit, UnitCgroupStraySweep.LeafName);
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "cgroup.procs"), "5007\n");
        var messages = new List<string>();
        var killed = new List<int>();

        var outcome = Sweep(
            unit,
            messages,
            killed,
            Facts(
                5007,
                hoursOld: 40,
                command: "node /tmp/qs-dev-stack-b/web.mjs",
                cwd: Path.Combine(_workRoot, "AGT-2790")));

        // Nothing was blocking delegation this time, so nothing had to move.
        Assert.Equal(new StraySweepOutcome(1, 0, 1), outcome);
        Assert.Equal([5007], killed);
    }

    [Fact]
    public void A_parked_stray_that_still_does_not_qualify_is_left_where_it_is()
    {
        var unit = FakeUnitCgroup(SelfPid);
        var leaf = Path.Combine(unit, UnitCgroupStraySweep.LeafName);
        Directory.CreateDirectory(leaf);
        File.WriteAllText(Path.Combine(leaf, "cgroup.procs"), "5008\n");
        var messages = new List<string>();
        var killed = new List<int>();

        var outcome = Sweep(
            unit,
            messages,
            killed,
            Facts(5008, hoursOld: 480, command: "git remote-https origin", cwd: "/tmp"));

        Assert.Equal(new StraySweepOutcome(1, 0, 0), outcome);
        Assert.Empty(killed);
        Assert.Contains(messages, message => message.Contains("action=parked", StringComparison.Ordinal));
    }

    [Fact]
    public void A_process_that_exits_between_the_listing_and_the_read_is_not_counted_at_all()
    {
        var unit = FakeUnitCgroup(SelfPid, 5004, 5005);
        var messages = new List<string>();

        var outcome = Sweep(
            unit,
            messages,
            [],
            pid => pid == 5004
                ? new StrayProcessFacts(5004, Now.AddHours(-2), "sleep 5", null)
                : null);

        Assert.Equal(new StraySweepOutcome(1, 1, 0), outcome);
        Assert.DoesNotContain(messages, message => message.Contains("pid=5005", StringComparison.Ordinal));
    }

    /// <summary>
    /// The reason the sweep exists: on a host that has run before, the unit
    /// cgroup is not empty and cgroup v2 answers the controller write with
    /// <c>EBUSY</c>. Preparation therefore empties the cgroup and only then
    /// distributes the controllers, so delegation succeeds instead of leaving
    /// every run uncapped with <c>applied=no</c>.
    /// </summary>
    [SkippableFact]
    public void Delegation_preparation_empties_a_unit_cgroup_a_previous_generation_left_behind()
    {
        PlatformGate.RequiresPosixShell();
        var mountRoot = Path.Combine(_root, "sys", "fs", "cgroup");
        var unit = Path.Combine(mountRoot, "system.slice", "agent-runner.service");
        Directory.CreateDirectory(Path.Combine(unit, WorkerCgroup.DaemonLeafName));
        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), string.Empty);
        // A real live process, so the sweep's own /proc reader is what resolves
        // its age, command, and working directory.
        using var leftover = Process.Start(new ProcessStartInfo(PosixShell.RequirePath())
        {
            ArgumentList = { "-c", "sleep 30" },
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        File.WriteAllText(Path.Combine(unit, "cgroup.procs"), $"{leftover.Id}\n");
        var messages = new List<string>();

        try
        {
            var root = WorkerCgroup.TryPrepareDelegationRoot(
                mountRoot,
                "/system.slice/agent-runner.service/daemon",
                messages.Add,
                new StraySweepContext(RunTimeout, [new StrayGenerationRoot(_workRoot)]));

            Assert.Equal(unit, root);
            Assert.Equal(
                leftover.Id.ToString(),
                File.ReadAllText(
                    Path.Combine(unit, UnitCgroupStraySweep.LeafName, "cgroup.procs")).Trim());
            Assert.Equal(
                WorkerCgroup.RequiredControllers,
                File.ReadAllText(Path.Combine(unit, "cgroup.subtree_control")));
            // Unattributable, so it is parked out of the way and left alive.
            Assert.False(leftover.HasExited);
        }
        finally
        {
            try { leftover.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
        }
    }

    // -------------------------------------------------------------------- local

    private StraySweepOutcome Sweep(
        string unit,
        List<string> messages,
        List<int> killed,
        Func<int, StrayProcessFacts?>? inspect = null)
        => UnitCgroupStraySweep.Sweep(
            unit,
            new StraySweepContext(RunTimeout, [new StrayGenerationRoot(_workRoot)]),
            messages.Add,
            inspect ?? (_ => null),
            pid => { killed.Add(pid); return true; },
            Now,
            SelfPid);

    private static Func<int, StrayProcessFacts?> Facts(
        int pid,
        double hoursOld,
        string command,
        string? cwd)
        => candidate => candidate == pid
            ? new StrayProcessFacts(pid, Now.AddHours(-hoursOld), command, cwd)
            : null;

    private string FakeUnitCgroup(params int[] pids)
    {
        var unit = Path.Combine(_root, "system.slice", "agent-runner.service");
        Directory.CreateDirectory(Path.Combine(unit, WorkerCgroup.DaemonLeafName));
        File.WriteAllText(Path.Combine(unit, "cgroup.subtree_control"), string.Empty);
        File.WriteAllLines(Path.Combine(unit, "cgroup.procs"), pids.Select(pid => pid.ToString()));
        return unit;
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true); }
        catch (IOException) { }
    }
}
