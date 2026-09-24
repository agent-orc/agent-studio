using System.Diagnostics;
using System.Net;
using System.Text;
using AgentRunner;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class LeaseLossProcessKillTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "runner-lease-loss-" + Guid.NewGuid().ToString("N"));

    // Linux-only 02.08. (AGT-2472): the kill goes to a POSIX process group that
    // ProcessRunner creates with setsid, which only exists on Linux.
    [SkippableFact]
    [Trait(PlatformGate.TraitName, PlatformGate.Linux)]
    public async Task Rejected_lease_renewal_kills_the_agent_process_group()
    {
        PlatformGate.LinuxOnly("the agent process group is created with setsid and signalled as a group");

        Directory.CreateDirectory(_root);
        var parentPidPath = Path.Combine(_root, "parent.pid");
        var childPidPath = Path.Combine(_root, "child.pid");
        using var run = new CancellationTokenSource();
        var processTask = ProcessRunner.RunAsync(
            "/bin/sh",
            [
                "-c",
                $"echo $$ > {parentPidPath}; sleep 300 & echo $! > {childPidPath}; wait"
            ],
            workingDirectory: _root,
            ct: run.Token);
        await WaitForFileAsync(childPidPath);
        var parentPid = int.Parse(await File.ReadAllTextAsync(parentPidPath));
        var childPid = int.Parse(await File.ReadAllTextAsync(childPidPath));

        using var http = new HttpClient(new ConflictHandler())
        {
            BaseAddress = new Uri("http://localhost")
        };
        using var client = new TaskServerClient(http, "runner-test");
        var options = Options();
        var lease = new RunLeaseInfoDto(
            "AGT-2320",
            options.RunnerId,
            options.RunnerName,
            options.Hostname,
            parentPid,
            options.BackendName,
            "lease-old",
            7,
            DateTime.UtcNow.AddMinutes(-1),
            DateTime.UtcNow.AddMinutes(2));
        var heartbeat = new LeaseHeartbeat(
            client,
            options,
            lease,
            _ => { },
            (_, _) => Task.CompletedTask);

        await heartbeat.RunAsync(run, CancellationToken.None);

        Assert.True(heartbeat.LeaseLost);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => processTask);
        await WaitForExitAsync(parentPid);
        await WaitForExitAsync(childPid);
        Assert.False(IsAlive(parentPid));
        Assert.False(IsAlive(childPid));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private RunnerOptions Options() => new()
    {
        ServerUrl = "http://localhost",
        RunnerId = "runner-test",
        RunnerName = "runner-test",
        Hostname = "test-host",
        BackendName = "test",
        WorkDir = _root,
        BaseBranch = "main",
        ClaudeCliBin = "/bin/sh",
        TtlSeconds = 120,
        HeartbeatSeconds = 30,
    };

    private static async Task WaitForFileAsync(string path)
    {
        for (var attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(20);
        Assert.True(File.Exists(path), $"process did not write {path}");
    }

    private static async Task WaitForExitAsync(int pid)
    {
        for (var attempt = 0; attempt < 100 && IsAlive(pid); attempt++)
            await Task.Delay(20);
    }

    private static bool IsAlive(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private sealed class ConflictHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.Conflict)
            {
                Content = new StringContent(
                    """{"outcome":"StaleToken","granted":false,"message":"authority epoch changed"}""",
                    Encoding.UTF8,
                    "application/json")
            });
    }
}
