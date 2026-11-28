using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Xunit;

namespace AgentRunner.Tests;

public sealed class LinuxStructureCollectorTests : IDisposable
{
    private readonly TempWorkspace _root = new("structure-collector");
    private readonly DateTime _at = new(2026, 11, 27, 10, 0, 0, DateTimeKind.Utc);
    private LinuxStructureCollector Collector() => new(Path.Join(_root.Path, "proc"), Path.Join(_root.Path, "sys"),
        Path.Join(_root.Path, "metadata"), 100);
    private void Write(string path, string value)
    {
        path = Path.Join(_root.Path, path);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, value);
    }

    [Fact]
    public void Machine_counters_use_proc_and_a_missing_mount_does_not_hide_other_readings()
    {
        if (!OperatingSystem.IsLinux()) return;
        Write("proc/stat", "cpu 1 0 2 3 0 0 0 0\ncpu0 1\ncpu1 1\n");
        Write("proc/loadavg", "2.5 2 1 1/1 10");
        Write("proc/meminfo", "MemTotal: 1024 kB\nMemAvailable: 256 kB\n");
        var sampler = new HostTelemetrySampler(probe: FixedHostFilesystemProbe.Healthy, proc: Path.Join(_root.Path, "proc"));
        var machine = sampler.SampleNow(1);
        Assert.Equal((2, 2.5, 768L * 1024), (machine.CpuCores, machine.Load1, machine.MemoryUsedBytes));
        File.Delete(Path.Join(_root.Path, "proc/meminfo"));
        var partial = sampler.SampleNow(1);
        Assert.Null(partial.MemoryTotalBytes);
        Assert.Equal(2.5, partial.Load1);
    }

    [Fact]
    public void Device_usage_joins_on_kernel_device_number_and_mount()
    {
        Write("sys/class/dmi/id/product_name", "Virtual Machine\n");
        Write("sys/block/md0/size", "1000");
        Write("sys/block/md0/dev", "9:0");
        Write("sys/block/md0/md/level", "raid1");
        Write("sys/block/md0/slaves/disk-a/dev", "8:1");
        Write("sys/block/md0/slaves/disk-b/dev", "8:2");
        Write("proc/self/mountinfo", "34 1 9:0 / /data rw - ext4 /dev/md0 rw\n");
        var mount = new HostFilesystemUsageDto("runner work", "/data", 50, 100);
        var report = Collector().Read(_at, "host", "Coding runner", "coding", [mount], new Dictionary<string, string>());
        var disk = Assert.Single(report.Devices);
        Assert.Equal(("md0", "raid1", 512000L), (disk.Device, disk.RaidLevel, disk.SizeBytes));
        Assert.Equal(["disk-a", "disk-b"], disk.Members);
        Assert.Equal(mount, Assert.Single(disk.Filesystems));
        Assert.Equal(("Virtual Machine", _at, "host", _at.AddSeconds(90)),
            (report.DmiProduct, report.ObservedAt, report.ObservedBy, report.FreshUntil));
        Assert.Null(report.StudioContainers);
    }

    [Fact]
    public void Service_workers_and_post_merge_use_their_own_counters()
    {
        Write("proc/self/cgroup", "0::/unit/daemon\n");
        Write("sys/fs/cgroup/unit/cpu.stat", "usage_usec 3000000\n");
        Write("sys/fs/cgroup/unit/memory.current", "300");
        Write("sys/fs/cgroup/unit/worker-one/memory.current", "120");
        Write(@"sys/fs/cgroup/system.slice/system-agent\x2dpost\x2dmerge\x2dwindow.slice/agent-post-merge-window@test.service/memory.current", "50");
        var cards = new Dictionary<string, string> { ["worker-one"] = "CARD-1" };
        var reader = Collector();
        var report = reader.Read(_at, "host", "Coding runner", "coding", [], cards);
        Assert.Equal(300, report.Service!.MemoryBytes);
        Assert.Equal(3000000, report.Service.CpuUsageMicroseconds);
        Assert.Null(report.Service.CpuCoresUsed);
        Assert.Equal(new("CARD-1", new(null, 120)), Assert.Single(report.Workers));
        Assert.Equal(50, Assert.Single(report.PostMergeUnits).Resources.MemoryBytes);
        Write("sys/fs/cgroup/unit/cpu.stat", "usage_usec 5000000\n");
        Assert.Equal(0.4, reader.Read(_at.AddSeconds(5), "host", "Coding", "coding", [], cards).Service!.CpuCoresUsed);
    }

    [Theory]
    [InlineData("OrchestratorApi.dll", "", "api")]
    [InlineData("orchestrator-engine.dll", "", "engine")]
    [InlineData("caddy", "Caddyfile.frontend", "web")]
    [InlineData("caddy", "Caddyfile.cloudflare", "edge")]
    public void Studio_slice_uses_container_init_and_keeps_partial_readings(string command, string mount, string role)
    {
        Write("sys/fs/cgroup/studio.slice/docker-demo.scope/cgroup.procs", "7\n");
        Write("sys/fs/cgroup/studio.slice/docker-demo.scope/memory.current", "111");
        Write("sys/fs/cgroup/system.slice/docker-other.scope/memory.current", "222");
        Write("proc/7/status", "NSpid:\t7\t1\n");
        Write("proc/7/stat", "7 (name with spaces) S " + string.Join(" ", Enumerable.Repeat("0", 18)) + " 200");
        Write("proc/7/cmdline", "entry\0" + command + "\0");
        Write("proc/7/mountinfo", $"20 1 8:0 /config/{mount} /etc/caddy/Caddyfile rw - ext4 /dev/sda rw");
        Write("proc/uptime", "20 0");
        var reader = Collector();
        StudioContainerReading Read() => Assert.Single(reader.Read(_at, "host", "Coding", "coding", [],
            new Dictionary<string, string>()).StudioContainers!);
        var container = Read();
        Assert.Equal((role, 111L, 18d), (container.Role, container.Resources.MemoryBytes, container.UptimeSeconds));
        Assert.Null(container.Restarts);
        Assert.Null(container.ImageTag);
        Write("metadata/containers/demo/config.v2.json", """{"Config":{"Image":"local/api:1"},"RestartCount":3}""");
        Assert.Equal(("local/api:1", 3), (Read().ImageTag, Read().Restarts));
        Write("metadata/containers/demo/config.v2.json", "{");
        Assert.Equal(111, Read().Resources.MemoryBytes);
    }

    public void Dispose() => _root.Dispose();
}
