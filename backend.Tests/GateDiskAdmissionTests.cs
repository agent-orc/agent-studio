using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateDiskAdmissionTests
{
    [Fact]
    public async Task Ssh_gate_waits_before_dispatch_and_retains_disk_reason_until_warning_clears()
    {
        var samples = new Queue<string>(["6 246 1073741824", "20 246 1073741824", "40 246 1073741824"]);
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "gate-host", ["RemoteGate:Root"] = "/srv/gates",
            ["RemoteGate:WorkerPath"] = "/opt/gate.dll",
        }).Build();
        var probe = new SshGateHostProbe(config, (_, args, _, _, _, output) =>
        {
            Assert.Contains("stat -f", args.Last());
            output!(samples.Dequeue());
            return Task.CompletedTask;
        });
        var reasons = new List<string>();
        var waits = 0;
        await probe.WaitForDiskRecoveryAsync(reasons.Add, default, (_, _) =>
        {
            waits++;
            return Task.CompletedTask;
        });
        Assert.Equal(2, waits);
        Assert.All(reasons, reason => Assert.Contains("/srv/gates", reason));
        Assert.Contains("6.0 GiB", reasons[0]);
        Assert.Equal("healthy", probe.LatestDisk!.Level);
        Assert.Equal(40L << 30, Assert.Single(probe.LatestDisk.Filesystems).FreeBytes);
    }

    [Theory]
    [InlineData("")]
    [InlineData("6 0 4096")]
    [InlineData("-1 246 4096")]
    public void Invalid_remote_readings_are_not_treated_as_healthy(string output)
        => Assert.Throws<IOException>(() => SshGateHostProbe.ParseDisk("/srv/gates", output));
}
