using System.Diagnostics;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

/// <summary>
/// AGT-3231: apply-zone-policy.sh takes its flock only for the mutating runs,
/// so a non-root dry run or plain verify can never create or hold the lock
/// file that a later root apply needs.
/// </summary>
public sealed class ZonePolicyLockScriptTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), $"zone-lock-{Guid.NewGuid():N}");
    private readonly string _lock;
    private readonly string _marker;

    public ZonePolicyLockScriptTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "bin"));
        _lock = Path.Combine(_root, "agent-zone-policy.lock");
        _marker = Path.Combine(_root, "agent-host.out");
    }

    public void Dispose() => Directory.Delete(_root, recursive: true);

    public static TheoryData<string[], bool> Modes => new()
    {
        { [], false },
        { ["--execute"], true },
        { ["--verify"], false },
        { ["--verify", "--record"], true },
        { ["--remove"], false },
        { ["--remove", "--execute"], true },
        { ["--render"], false },
    };

    // Linux-only: the lock is util-linux flock, which Git for Windows lacks.
    [SkippableTheory]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    [MemberData(nameof(Modes))]
    public async Task Only_mutating_runs_take_the_lock(string[] arguments, bool locked)
    {
        PlatformGate.LinuxOnly("the lock uses util-linux flock");

        var (exitCode, error) = await RunAsync(arguments, uid: 0);

        Assert.True(exitCode == 0, error);
        Assert.Equal(locked ? "locked" : "unlocked", File.ReadAllText(_marker).Trim());
        Assert.Equal(locked, File.Exists(_lock));
    }

    // Linux-only: the lock is util-linux flock, which Git for Windows lacks.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task A_symbolic_link_at_the_lock_path_stops_a_mutating_run()
    {
        PlatformGate.LinuxOnly("the lock uses util-linux flock");
        File.CreateSymbolicLink(_lock, Path.Combine(_root, "elsewhere"));

        var (exitCode, error) = await RunAsync(["--execute"], uid: 0);

        Assert.Equal(2, exitCode);
        Assert.Contains("symbolic link", error);
        Assert.False(File.Exists(_marker));
    }

    // Linux-only: the lock is util-linux flock, which Git for Windows lacks.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task A_recording_verify_needs_root()
    {
        PlatformGate.LinuxOnly("the lock uses util-linux flock");

        var (exitCode, error) = await RunAsync(["--verify", "--record"], uid: 1000);

        Assert.Equal(2, exitCode);
        Assert.Contains("run it as root", error);
        Assert.False(File.Exists(_marker));
        Assert.False(File.Exists(_lock));
    }

    private async Task<(int ExitCode, string Error)> RunAsync(string[] arguments, int uid)
    {
        var bin = Path.Combine(_root, "bin");
        // The fake id lets the test take the root paths without root.
        WriteExecutable(Path.Combine(bin, "id"), "#!/bin/sh\nprintf '%s\\n' \"$FAKE_UID\"\n");
        // A second open of the lock file cannot take the lock while the script's fd 9 holds it.
        var agentHost = Path.Combine(_root, "agent-host");
        WriteExecutable(agentHost,
            "#!/bin/sh\n"
            + "if [ -e \"$ZONE_POLICY_LOCK\" ] && ! flock -n \"$ZONE_POLICY_LOCK\" true; then state=locked; else state=unlocked; fi\n"
            + "printf '%s\\n' \"$state\" >\"$MARKER\"\n");

        var start = new ProcessStartInfo("/bin/sh")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        start.ArgumentList.Add(Path.Combine(ZonePolicyTestFiles.RepoRoot(), "deploy", "zones", "apply-zone-policy.sh"));
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        start.Environment["PATH"] = $"{bin}:{Environment.GetEnvironmentVariable("PATH")}";
        start.Environment["FAKE_UID"] = uid.ToString(System.Globalization.CultureInfo.InvariantCulture);
        start.Environment["AGENT_HOST_BIN"] = agentHost;
        start.Environment["ZONE_POLICY_LOCK"] = _lock;
        start.Environment["ZONE_POLICY_LOCK_WAIT"] = "1";
        start.Environment["MARKER"] = _marker;
        start.Environment.Remove("AGENT_ZONES");
        start.Environment.Remove("ZONE_POLICY_FILE");

        using var process = Process.Start(start)!;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
        var error = process.StandardError.ReadToEndAsync(timeout.Token);
        await process.WaitForExitAsync(timeout.Token);
        await output;
        return (process.ExitCode, await error);
    }

    private static void WriteExecutable(string path, string content)
    {
        File.WriteAllText(path, content);
        if (!OperatingSystem.IsWindows())
            File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
    }
}
