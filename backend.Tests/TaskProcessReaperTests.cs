using System.Diagnostics;
using System.Runtime.Versioning;
using AgentStudio.Cli;
using AgentStudio.TestSupport;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Load-bearing containment contract for <see cref="TaskProcessReaper"/>: a
/// helper the agent spawns and lets detach (the real-world case is a
/// Playwright capture server / a re-parented <c>node serve.cjs</c>) must die
/// when the run-group is reaped — even though it is NOT in the CLI's
/// parent→child PID tree and so <c>taskkill /T</c> /
/// <c>Process.Kill(entireProcessTree)</c> would miss it. Without this, the
/// straggler keeps the run's worktree open and the post-run
/// <c>git worktree remove</c> fails "busy", orphaning the worktree (AGT-1791).
/// </summary>
public sealed class TaskProcessReaperTests
{
    [SkippableFact]
    [Trait("Category", "MachineBound")]
    [Trait("Category", "ReviewFlaky")]
    public async Task LinuxProcessGroupTeardown_EndsChildHoldingWorktreeCwd_ThenDirectoryRemoves()
    {
        Skip.IfNot(OperatingSystem.IsLinux(), "Linux process-group contract");
        var worktree = Path.Combine(Path.GetTempPath(), "reaper-worktree-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(worktree);
        var pidFile = Path.Combine(worktree, "server.pid");
        var script = $"python3 -m http.server 0 >/dev/null 2>&1 & echo $! > '{pidFile}'; wait";
        var startInfo = new ProcessStartInfo
        {
            FileName = "/bin/sh",
            WorkingDirectory = worktree,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        startInfo.ArgumentList.Add("-c");
        startInfo.ArgumentList.Add(script);
        Assert.True(TaskProcessReaper.WrapStartInfoForProcessGroup(startInfo));
        using var root = Process.Start(startInfo)!;
        using var reaper = TaskProcessReaper.CreateForProcess(root, NullLogger.Instance);
        Assert.NotNull(reaper);

        Process? server = null;
        try
        {
            var serverPid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(10));
            Assert.True(serverPid > 0, "fake child server PID was never written");
            server = Process.GetProcessById(serverPid);
            Assert.False(server.HasExited);

            reaper!.Terminate();

            Assert.True(await WaitForExitAsync(server, TimeSpan.FromSeconds(10)),
                "child server holding the worktree cwd survived group teardown");
            Assert.True(root.WaitForExit(10_000), "setsid root did not exit after group teardown");
            Directory.Delete(worktree, recursive: true);
            Assert.False(Directory.Exists(worktree));
        }
        finally
        {
            try { if (server is { HasExited: false }) server.Kill(entireProcessTree: true); } catch { }
            server?.Dispose();
            try { if (!root.HasExited) root.Kill(entireProcessTree: true); } catch { }
            try { if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true); } catch { }
        }
    }

    // Windows-only 02.08. (AGT-2472): the reaper is built on the Win32 Job Object
    // primitive. Linux teardown uses process groups and is covered separately.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Windows)]
    // The runtime gate is PlatformGate.WindowsOnly; this states the same fact to
    // the platform-compatibility analyzer, which only understands the annotation.
    [SupportedOSPlatform("windows")]
    public async Task Terminate_KillsDetachedGrandchild_ThatTreeKillWouldMiss()
    {
        PlatformGate.WindowsOnly("the reaper is built on the Win32 Job Object primitive");

        var worktree = Path.Combine(Path.GetTempPath(), $"reaper-worktree-{Guid.NewGuid():N}");
        Directory.CreateDirectory(worktree);
        var pidFile = Path.Combine(worktree, "server.pid");

        // The child idles briefly (so the test can assign the group BEFORE any
        // helper exists — mirroring the production spawn site), then launches a
        // DETACHED grandchild via Start-Process. Start-Process re-parents the
        // grandchild out of powershell's tree, so a tree-kill on the child
        // would never reach it; only job membership does. The grandchild PID is
        // recorded so the test can assert its death independently.
        var script =
            "Start-Sleep -Milliseconds 600; " +
            "$p = Start-Process -PassThru -WindowStyle Hidden ping -ArgumentList '-n','600','127.0.0.1'; " +
            $"Set-Content -LiteralPath '{pidFile}' -Value $p.Id; " +
            "Start-Sleep -Seconds 120";

        var child = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "powershell.exe",
                ArgumentList = { "-NoProfile", "-NonInteractive", "-Command", script },
                WorkingDirectory = worktree,
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        child.Start();

        TaskProcessReaper? reaper = null;
        Process? grandchild = null;
        try
        {
            // Assign right after spawn, before the child spawns the grandchild.
            reaper = TaskProcessReaper.CreateForProcess(child, NullLogger.Instance);
            Assert.NotNull(reaper); // on Windows the group must be created

            var gcPid = await WaitForPidAsync(pidFile, TimeSpan.FromSeconds(15));
            Assert.True(gcPid > 0, "grandchild PID was never written");

            grandchild = Process.GetProcessById(gcPid); // holds a handle → survives PID reuse
            Assert.False(grandchild.HasExited, "grandchild should be running before Terminate()");

            reaper!.Terminate();

            var died = await WaitForExitAsync(grandchild, TimeSpan.FromSeconds(10));
            Assert.True(died, "detached grandchild survived Terminate() — group containment failed");
            Assert.True(child.WaitForExit(10_000), "root process did not exit after job termination");
            Directory.Delete(worktree, recursive: true);
            Assert.False(Directory.Exists(worktree));
        }
        finally
        {
            reaper?.Dispose();
            try { if (grandchild is { HasExited: false }) grandchild.Kill(); } catch (Exception) { /* best-effort */ }
            try { if (!child.HasExited) child.Kill(entireProcessTree: true); } catch (Exception) { /* best-effort */ }
            try { if (Directory.Exists(worktree)) Directory.Delete(worktree, recursive: true); } catch (Exception) { /* best-effort */ }
        }
    }

    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Windows)]
    [SupportedOSPlatform("windows")]
    public void CreateForProcess_OnExitedProcess_ReturnsNull_NoThrow()
    {
        PlatformGate.WindowsOnly("the reaper is built on the Win32 Job Object primitive");

        using var p = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                ArgumentList = { "/c", "exit", "0" },
                UseShellExecute = false,
                CreateNoWindow = true,
            }
        };
        p.Start();
        p.WaitForExit();

        // Assigning an already-exited process must fail soft (null), never throw,
        // so the caller keeps its tree-kill fallback.
        var reaper = TaskProcessReaper.CreateForProcess(p, NullLogger.Instance);
        Assert.Null(reaper);
    }

    private static async Task<int> WaitForPidAsync(string pidFile, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (File.Exists(pidFile))
            {
                var txt = (await File.ReadAllTextAsync(pidFile)).Trim();
                if (int.TryParse(txt, out var pid) && pid > 0) return pid;
            }
            await Task.Delay(150);
        }
        return -1;
    }

    private static async Task<bool> WaitForExitAsync(Process p, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { p.Refresh(); if (p.HasExited) return true; }
            catch (Exception) { return true; } // handle gone → treat as exited
            await Task.Delay(150);
        }
        return false;
    }
}
