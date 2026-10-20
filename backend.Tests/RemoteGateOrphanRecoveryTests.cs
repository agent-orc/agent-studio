using AgentStudio.Pipeline;
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
        string? sshCommand = null;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            if (fileName == "ssh") sshCommand = args.Last();
            return Task.CompletedTask;
        }
        var transport = Transport(current, Run);

        await transport.CancelOrphanedRunsAsync();

        Assert.Contains("/srv/gates/" + record.RunId + "/cancel", sshCommand);
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
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> run)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "gate-a",
            ["RemoteGate:WorkerPath"] = "/opt/gate/worker.dll",
            ["RemoteGate:Root"] = "/srv/gates",
        }).Build();
        return new RemoteGateTransport(configuration, NullLogger<RemoteGateTransport>.Instance,
            _clock, Path.Combine(_root, "local"), _ => { }, runProcess: run, runRegistry: registry);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
