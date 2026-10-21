using System.Diagnostics;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteGateOrphanRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "remote-gate-recovery-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-10-20T02:00:00Z"));

    [Fact]
    public void Run_records_are_durable_and_reloaded_after_registry_recreation()
    {
        var registry = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        var record = registry.Record(new string('c', 32), "gate-a", "/srv/gates");

        var reopened = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));

        Assert.Equal(record, Assert.Single(reopened.ReadAll()));
        Assert.Equal(_clock.GetUtcNow(), record.StartedAtUtc);
    }

    [Fact]
    public async Task Startup_cancels_previous_instance_run_on_configured_host()
    {
        var registry = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        var record = registry.Record(new string('c', 32), "gate-a", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var remoteExists = true;
        var commands = new List<string>();
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            if (fileName == "ssh")
            {
                var command = args.Last();
                commands.Add(command);
                if (command.Contains("rm -rf --", StringComparison.Ordinal)) remoteExists = false;
                if (command.StartsWith("test ! -e", StringComparison.Ordinal) && remoteExists)
                    throw new IOException("remote directory remains");
            }
            return Task.CompletedTask;
        }
        var transport = Transport(current, Run);

        await transport.CancelOrphanedRunsAsync();

        Assert.Contains(commands, command => command.Contains("/srv/gates/" + record.RunId + "/cancel", StringComparison.Ordinal));
        Assert.False(remoteExists);
        Assert.Empty(current.ReadAll());
    }

    [Fact]
    public async Task Never_started_orphan_directory_is_removed_before_record_is_deleted()
    {
        var previous = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        var record = previous.Record(new string('c', 32), "gate-a", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var remoteExists = true;
        var refuseRemoval = true;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            var command = args.Last();
            if (command.Contains("rm -rf --", StringComparison.Ordinal) && !refuseRemoval)
                remoteExists = false;
            if (command.StartsWith("test ! -e", StringComparison.Ordinal) && remoteExists)
                throw new IOException("remote directory remains");
            return Task.CompletedTask;
        }
        var transport = Transport(current, Run);

        await transport.CancelOrphanedRunsAsync();
        Assert.True(remoteExists);
        Assert.Equal(record, Assert.Single(current.ReadAll()));

        refuseRemoval = false;
        await transport.CancelOrphanedRunsAsync();
        Assert.False(remoteExists);
        Assert.Empty(current.ReadAll());
    }

    [Fact]
    public async Task Failed_ssh_after_mkdir_keeps_the_record_until_the_directory_is_removed()
    {
        var repository = Path.Combine(_root, "source");
        Directory.CreateDirectory(_root);
        Git("init", "-q", repository);
        Git("-C", repository, "-c", "user.name=Fixture", "-c", "user.email=fixture@example.invalid",
            "commit", "-q", "--allow-empty", "-m", "fixture");
        var sha = Git("-C", repository, "rev-parse", "HEAD").Trim();
        var registry = new RemoteGateRunRegistry(Path.Combine(_root, "records"), _clock,
            new string('a', 32), new string('b', 32));
        var remoteExists = false;
        var allowRemoval = false;
        var commands = new List<string>();
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            if (fileName != "ssh") return Task.CompletedTask;
            var command = args.Last();
            commands.Add(command);
            if (command.Contains("; mkdir -- '/srv/gates/", StringComparison.Ordinal))
            {
                remoteExists = true;
                throw new IOException("SSH lost its acknowledgement after mkdir");
            }
            if (command.Contains("rm -rf --", StringComparison.Ordinal) && allowRemoval)
                remoteExists = false;
            if (command.StartsWith("test ! -e", StringComparison.Ordinal) && remoteExists)
                throw new IOException("remote directory remains");
            return Task.CompletedTask;
        }
        var transport = Transport(registry, Run);

        await Assert.ThrowsAsync<IOException>(() => transport.RunAsync(
            new BuildTestGateRequest(repository, sha, "integration-gate"), [], null,
            PostStepMode.Fail, TimeSpan.FromMinutes(1), CancellationToken.None));

        Assert.True(remoteExists);
        var record = Assert.Single(registry.ReadAll());
        Assert.Contains(commands, command => command.Contains(record.RunId + "/cancel", StringComparison.Ordinal));

        allowRemoval = true;
        await transport.CancelOrphanedRunsAsync();

        Assert.False(remoteExists);
        Assert.Empty(registry.ReadAll());
    }

    [Fact]
    public async Task Default_identity_path_is_unique_per_installation_under_one_account()
    {
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var data = Path.Combine(_root, "shared-account");
        var firstPath = Path.Combine(_root, "installation-a");
        var secondPath = Path.Combine(_root, "installation-b");
        var first = RemoteGateTransport.CreateDefaultRegistry(_clock, config, firstPath, data);
        var reopened = RemoteGateTransport.CreateDefaultRegistry(_clock, config, firstPath, data);
        var second = RemoteGateTransport.CreateDefaultRegistry(_clock, config, secondPath, data);
        first.Record(new string('c', 32), "gate-a", "/srv/gates");

        Assert.Equal(first.InstallationId, reopened.InstallationId);
        Assert.NotEqual(first.InstallationId, second.InstallationId);
        Assert.Single(reopened.ReadAll());
        Assert.Empty(second.ReadAll());
        var calls = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            calls++;
            return Task.CompletedTask;
        }
        await Transport(second, Run).CancelOrphanedRunsAsync();
        Assert.Equal(0, calls);
        Assert.Single(reopened.ReadAll());
    }

    [Fact]
    public async Task New_prebuilt_release_recovers_records_from_the_same_installation()
    {
        var data = Path.Combine(_root, "shared-account");
        var installation = Path.Combine(_root, "studio-a");
        var otherInstallation = Path.Combine(_root, "studio-b");
        var firstRelease = Path.Combine(installation, "releases", "17");
        var nextRelease = Path.Combine(installation, "releases", "18");
        Directory.CreateDirectory(firstRelease);
        Directory.CreateDirectory(nextRelease);
        var firstConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:InstallationRoot"] = installation,
            ["RemoteGate:WorkerPath"] = Path.Combine(firstRelease, "worker.dll"),
        }).Build();
        var nextConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:InstallationRoot"] = installation,
            ["RemoteGate:WorkerPath"] = Path.Combine(nextRelease, "worker.dll"),
        }).Build();
        var otherConfig = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:InstallationRoot"] = otherInstallation,
        }).Build();
        var old = RemoteGateTransport.CreateDefaultRegistry(_clock, firstConfig, dataDirectory: data);
        var record = old.Record(new string('c', 32), "gate-a", "/srv/gates");

        var current = RemoteGateTransport.CreateDefaultRegistry(_clock, nextConfig, dataDirectory: data);
        var fallbackOld = RemoteGateTransport.CreateDefaultRegistry(_clock, firstConfig, dataDirectory: "");
        var fallbackNew = RemoteGateTransport.CreateDefaultRegistry(_clock, nextConfig, dataDirectory: "");
        var other = RemoteGateTransport.CreateDefaultRegistry(_clock, otherConfig, dataDirectory: data);
        var implicitScope = RemoteGateTransport.CreateDefaultRegistry(_clock,
            new ConfigurationBuilder().Build(), dataDirectory: data);
        var workingDirectoryScope = RemoteGateTransport.CreateDefaultRegistry(_clock,
            new ConfigurationBuilder().Build(), Directory.GetCurrentDirectory(), data);

        Assert.Equal(old.InstallationId, current.InstallationId);
        Assert.Equal(fallbackOld.InstallationId, fallbackNew.InstallationId);
        Assert.Equal(record, Assert.Single(current.ReadAll()));
        Assert.NotEqual(current.InstallationId, other.InstallationId);
        Assert.Empty(other.ReadAll());
        Assert.Equal(workingDirectoryScope.InstallationId, implicitScope.InstallationId);

        var remoteExists = true;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            var command = args.Last();
            if (command.Contains("rm -rf --", StringComparison.Ordinal)) remoteExists = false;
            if (command.StartsWith("test ! -e", StringComparison.Ordinal) && remoteExists)
                throw new IOException("remote directory remains");
            return Task.CompletedTask;
        }
        await Transport(current, Run).CancelOrphanedRunsAsync();
        Assert.False(remoteExists);
        Assert.Empty(current.ReadAll());
    }

    [Fact]
    public async Task Runs_from_other_installations_are_never_cancelled()
    {
        var foreign = new RemoteGateRunRegistry(_root, _clock, new string('e', 32), new string('f', 32));
        foreign.Record(new string('c', 32), "gate-a", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var calls = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            calls++;
            return Task.CompletedTask;
        }

        await Transport(current, Run).CancelOrphanedRunsAsync();

        Assert.Equal(0, calls);
        Assert.Single(current.ReadAll());
    }

    [Fact]
    public async Task Run_owned_by_live_process_is_not_cancelled()
    {
        var registry = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var runId = new string('c', 32);
        registry.Record(runId, "gate-a", "/srv/gates");
        var calls = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            calls++;
            return Task.CompletedTask;
        }

        var transport = Transport(registry, Run);
        transport.TrackLiveRun(runId);
        await transport.CancelOrphanedRunsAsync();

        Assert.Equal(0, calls);
        Assert.Single(registry.ReadAll());
    }

    [Fact]
    public async Task Earlier_process_with_live_invocation_is_left_alone_until_it_exits()
    {
        var previous = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        previous.Record(new string('c', 32), "gate-a", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var ownerAlive = true;
        var calls = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            calls++;
            return Task.CompletedTask;
        }
        var transport = Transport(current, Run, ownerIsAlive: _ => ownerAlive);

        await transport.CancelOrphanedRunsAsync();
        Assert.Equal(0, calls);
        Assert.Single(current.ReadAll());

        ownerAlive = false;
        await transport.CancelOrphanedRunsAsync();
        Assert.True(calls > 0);
        Assert.Empty(current.ReadAll());
    }

    [Fact]
    public async Task Orphan_cancellation_is_appended_to_product_activity()
    {
        var watchPath = Path.Combine(_root, "project");
        var previous = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        var record = previous.Record(new string('c', 32), "gate-a", "/srv/gates", "project", watchPath);
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        _clock.Advance(TimeSpan.FromMinutes(25));
        var activity = new OrchestratorLog(NullLogger<OrchestratorLog>.Instance);

        await Transport(current, (_, _, _, _, _, _) => Task.CompletedTask,
            activity: activity).CancelOrphanedRunsAsync();

        var entry = Assert.Single(activity.Read(watchPath));
        Assert.Equal("remote-gate-orphan", entry.Topic);
        Assert.Equal(OrchestratorLogKinds.Action, entry.Kind);
        Assert.Contains(record.RunId, entry.Reasoning);
        Assert.Contains("gate-a", entry.Reasoning);
        Assert.Contains("1500 seconds", entry.Reasoning);
    }

    [Fact]
    public async Task Startup_sweeps_an_additional_configured_gate_host()
    {
        var previous = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        var record = previous.Record(new string('c', 32), "gate-b", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var commands = new List<string>();
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            commands.Add(args.Last());
            return Task.CompletedTask;
        }
        var shard = Transport(current, Run, section: "RemoteGate:ShardHosts:0");
        var slots = new GateSlotUsageTracker(clock: _clock);
        var dispatcher = new ShardedRemoteGateTransport(
            new GateShardHost("gate-a", shard, slots),
            [new GateShardHost("gate-b", shard, slots)],
            new GateShardTimingStore(Path.Combine(_root, "timings"), _clock),
            NullLogger<ShardedRemoteGateTransport>.Instance, _clock);

        await dispatcher.CancelOrphanedRunsAsync(CancellationToken.None);

        Assert.Contains(commands, command => command.Contains(record.RunId + "/cancel", StringComparison.Ordinal));
        Assert.Empty(current.ReadAll());
    }

    [Fact]
    public void Worker_stops_when_injected_clock_passes_heartbeat_bound()
    {
        var now = _clock.GetUtcNow();

        Assert.False(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromMinutes(4) - TimeSpan.FromSeconds(59)));
        Assert.True(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public void Worker_keeps_a_live_session_through_two_connect_timeouts()
    {
        var now = _clock.GetUtcNow();
        Assert.False(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromSeconds(35)));
        Assert.False(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromMinutes(3)));
        Assert.True(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)));
    }

    [Fact]
    public async Task Startup_sweep_failure_does_not_disable_later_recovery()
    {
        var previous = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('b', 32));
        previous.Record(new string('c', 32), "gate-a", "/srv/gates");
        var current = new RemoteGateRunRegistry(_root, _clock, new string('a', 32), new string('d', 32));
        var attempts = 0;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            attempts++;
            if (attempts == 1) throw new System.ComponentModel.Win32Exception("ssh missing");
            return Task.CompletedTask;
        }
        var transport = Transport(current, Run);
        var slots = new GateSlotUsageTracker(clock: _clock);
        var sharded = new ShardedRemoteGateTransport(
            new GateShardHost("gate-a", transport, slots), [],
            new GateShardTimingStore(Path.Combine(_root, "timings"), _clock),
            NullLogger<ShardedRemoteGateTransport>.Instance, _clock);
        var service = new RemoteGateOrphanSweepService(transport, sharded,
            NullLogger<RemoteGateOrphanSweepService>.Instance, _clock);

        await service.StartAsync(CancellationToken.None);
        Assert.True(SpinWait.SpinUntil(() => attempts == 1, TimeSpan.FromSeconds(5)));
        Assert.Single(current.ReadAll());
        _clock.Advance(TimeSpan.FromMinutes(1));
        Assert.True(SpinWait.SpinUntil(() => current.ReadAll().Count == 0, TimeSpan.FromSeconds(5)));
        await service.StopAsync(CancellationToken.None);
        Assert.True(attempts >= 2);
    }

    [Fact]
    public void Invalid_installation_id_is_rejected_before_dispatch()
    {
        var registry = new RemoteGateRunRegistry(_root, _clock, "invalid id");
        Assert.Throws<InvalidDataException>(() => Transport(registry,
            (_, _, _, _, _, _) => Task.CompletedTask));
    }

    [Fact]
    public void Registry_io_failure_does_not_prevent_transport_construction()
    {
        var blocked = Path.Combine(_root, "blocked");
        Directory.CreateDirectory(_root);
        File.WriteAllText(blocked, "not a directory");
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:InstallationRoot"] = blocked,
        }).Build();
        var registry = RemoteGateTransport.CreateDefaultRegistrySafe(_clock, config,
            NullLogger<RemoteGateTransport>.Instance, dataDirectory: blocked);
        Assert.Empty(registry.ReadAll());
        Assert.Matches("^[0-9a-f]{32}$", registry.InstallationId);
    }

    [Fact]
    public async Task Worker_cancels_execution_when_heartbeat_is_stale()
    {
        var directory = Path.Combine(_root, "worker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var heartbeat = Path.Combine(directory, "heartbeat");
        File.WriteAllText(heartbeat, "");
        File.SetLastWriteTimeUtc(heartbeat, (_clock.GetUtcNow() - TimeSpan.FromMinutes(5) - TimeSpan.FromSeconds(1)).UtcDateTime);
        using var execution = new CancellationTokenSource();
        using var stopWatcher = new CancellationTokenSource();

        var watch = RemoteGateWorker.WatchCancellationAsync(directory, new string('a', 32),
            execution, stopWatcher.Token, _clock);
        _clock.Advance(TimeSpan.FromSeconds(2));

        Assert.True(await watch);
        Assert.True(execution.IsCancellationRequested);
    }

    private RemoteGateTransport Transport(RemoteGateRunRegistry registry,
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> run,
        Func<RemoteGateRunRecord, bool>? ownerIsAlive = null, OrchestratorLog? activity = null,
        string section = RemoteGateOptions.PrimarySection)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "gate-a",
            ["RemoteGate:WorkerPath"] = "/opt/gate/worker.dll",
            ["RemoteGate:Root"] = "/srv/gates",
            ["RemoteGate:ShardHosts:0:SshHost"] = "gate-b",
        }).Build();
        return new RemoteGateTransport(configuration, NullLogger<RemoteGateTransport>.Instance,
            _clock, Path.Combine(_root, "local"), _ => { }, runProcess: run, runRegistry: registry,
            ownerIsAlive: ownerIsAlive ?? (_ => false), activity: activity, section: section);
    }

    private static string Git(params string[] args)
    {
        var start = new ProcessStartInfo("git")
        {
            UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
        };
        foreach (var arg in args) start.ArgumentList.Add(arg);
        using var process = Process.Start(start) ?? throw new IOException("git did not start");
        var output = process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) throw new IOException("git failed: " + error);
        return output;
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
