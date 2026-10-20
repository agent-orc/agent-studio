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

        Assert.False(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromSeconds(34)));
        Assert.True(RemoteGateWorker.IsHeartbeatStale(now, now - TimeSpan.FromSeconds(36)));
    }

    [Fact]
    public async Task Worker_cancels_execution_when_heartbeat_is_stale()
    {
        var directory = Path.Combine(_root, "worker", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var heartbeat = Path.Combine(directory, "heartbeat");
        File.WriteAllText(heartbeat, "");
        File.SetLastWriteTimeUtc(heartbeat, (_clock.GetUtcNow() - TimeSpan.FromSeconds(36)).UtcDateTime);
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

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
