using AgentStudio.Pipeline;
using AgentStudio.Security;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace AgentStudio.Tests;

public sealed class GateCapacitySettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-capacity-test-" + Guid.NewGuid().ToString("N"));

    [Theory]
    [InlineData(0, 6, 24, false)]
    [InlineData(4, 0, 24, false)]
    [InlineData(4, 6, 0, false)]
    [InlineData(5, 6, 24, false)]
    public void Rejects_invalid_or_unapproved_oversubscription(int slots, int processors, int cores, bool allow)
    {
        Assert.NotNull(GateCapacityPolicy.Validate(new GateCapacity(slots, processors, cores, allow)));
    }

    [Fact]
    public void Explicit_oversubscription_and_unknown_host_cores_are_allowed()
    {
        Assert.Null(GateCapacityPolicy.Validate(new GateCapacity(5, 6, 24, true)));
        Assert.Null(GateCapacityPolicy.Validate(new GateCapacity(5, 6, null, false)));
    }

    [Fact]
    public void Configuration_is_the_default_and_each_change_is_durably_audited()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:GateSlots"] = "5", ["RemoteGate:ProcessorCount"] = "6",
            ["RemoteGate:HostCores"] = "30",
            ["RemoteGate:HostCapacity:gate-1:Slots"] = "2",
            ["RemoteGate:HostCapacity:gate-1:ProcessorCount"] = "3",
        }).Build();
        var path = Path.Combine(_root, "settings", "gate-capacity.json");
        var audit = new ControlPlaneAuditLog(Path.Combine(_root, "audit"));
        var settings = new GateCapacitySettings(config, path, audit);
        Assert.False(settings.HasOverride);
        Assert.Equal(new GateCapacity(5, 6, 30, false), settings.Current());
        Assert.Equal(new GateCapacity(2, 3, 30, false), settings.Current("gate-1"));
        Assert.Throws<ArgumentException>(() =>
            settings.Set(new GateCapacity(5, 6, 24, false), "operator-1"));
        Assert.False(File.Exists(path));
        Assert.True(settings.Set(new GateCapacity(4, 6, 24, false), "operator-1"));
        Assert.True(settings.Set(new GateCapacity(3, 6, 24, false), "operator-1"));
        Assert.False(settings.Set(new GateCapacity(3, 6, 24, false), "operator-1"));
        Assert.Equal(new GateCapacity(3, 6, 24, false), new GateCapacitySettings(config, path).Current());
        Assert.Equal(3, settings.Current("gate-1").Slots);
        var entries = audit.Read(1, 10);
        Assert.Equal(2, entries.Count);
        Assert.All(entries, entry =>
        {
            Assert.Equal("gate-capacity.change", entry.Operation);
            Assert.Equal("operator-1", entry.Actor);
            Assert.Equal("success", entry.Outcome);
        });
        Assert.Equal(5, entries[0].Details!.Value.GetProperty("previous").GetProperty("Slots").GetInt32());
        Assert.Equal(4, entries[0].Details!.Value.GetProperty("current").GetProperty("Slots").GetInt32());
    }

    [Fact]
    public void Ssh_dispatch_options_refresh_after_a_change_without_restarting_transport()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "gate.example",
            ["RemoteGate:WorkerPath"] = "/opt/gate/OrchestratorApi.dll",
            ["RemoteGate:GateSlots"] = "5", ["RemoteGate:ProcessorCount"] = "6",
        }).Build();
        var settings = new GateCapacitySettings(config, Path.Combine(_root, "capacity.json"));
        var before = RemoteGateOptions.Read(config, capacity: settings);
        settings.Set(new GateCapacity(4, 6, 24, false), "operator-1");
        var after = RemoteGateOptions.Read(config, capacity: settings);
        Assert.Equal((5, 6), (before.GateSlots, before.ProcessorCount));
        Assert.Equal((4, 6), (after.GateSlots, after.ProcessorCount));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
