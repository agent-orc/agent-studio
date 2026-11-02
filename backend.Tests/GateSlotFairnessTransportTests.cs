using System.Text.Json;
using AgentStudio.Pipeline;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3423: the transport classifies a gate from its project's recent gate
/// history, sends the class to the worker, records the slot hold, and shows
/// which slot runs what and why a gate waits.
/// </summary>
public sealed class GateSlotFairnessTransportTests : IDisposable
{
    private static readonly string Sha = new('1', 40);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "gate-fairness-tests", Guid.NewGuid().ToString("N"));
    private readonly FakeTimeProvider _clock = new(DateTimeOffset.Parse("2026-11-02T08:00:00Z"));

    [Fact]
    public async Task Short_project_history_reaches_the_worker_and_the_slot_board()
    {
        var slots = new GateSlotUsageTracker(clock: _clock);
        var history = new GateDurationHistory(Path.Combine(_root, "history.json"));
        var sent = new List<GateDurationClass>();
        GateSlotBoard? waitingBoard = null;
        GateSlotBoard? runningBoard = null;
        Task Run(string fileName, IReadOnlyList<string> args, string cwd, TimeSpan timeout,
            CancellationToken ct, Action<string>? output)
        {
            if (fileName == "scp" && args[^2] == "request.json")
            {
                var invocation = JsonSerializer.Deserialize<RemoteGateInvocation>(
                    File.ReadAllText(Path.Combine(cwd, "request.json")), RemoteBuildTestGateRunner.Json)!;
                sent.Add(invocation.Request.DurationClass);
            }
            else if (fileName == "scp" && args[^1] == "response.json")
            {
                var runId = Path.GetFileName(cwd);
                File.WriteAllText(Path.Combine(cwd, "response.json"), JsonSerializer.Serialize(
                    new RemoteGateResponse(1, runId, new string('0', 64), Passed()), RemoteBuildTestGateRunner.Json));
            }
            else if (fileName == "ssh" && args[^1].Contains("git init --bare", StringComparison.Ordinal))
                output?.Invoke("CACHE_WARM");
            else if (fileName == "ssh" && args[^1].Contains("--remote-build-test-gate-worker", StringComparison.Ordinal))
            {
                var runId = Path.GetFileName(cwd);
                output?.Invoke($"REMOTE_GATE_WAITING {runId} 2/2");
                waitingBoard = slots.Board();
                output?.Invoke($"REMOTE_GATE_SLOT {runId} 1/2");
                runningBoard = slots.Board();
                _clock.Advance(TimeSpan.FromMinutes(2));
            }
            return Task.CompletedTask;
        }
        var transport = Transport(Run, slots, history);

        for (var gate = 0; gate < GateSlotFairnessPolicy.MinimumSamples + 1; gate++)
        {
            var result = await transport.RunAsync(new BuildTestGateRequest(_root, Sha, "integration-gate")
                { Project = "ai-patterns" }, [], null, PostStepMode.Fail, TimeSpan.FromMinutes(30), CancellationToken.None);
            Assert.Equal(BuildTestGateVerdict.Ok, result.Verdict);
        }

        Assert.Equal([GateDurationClass.Long, GateDurationClass.Long, GateDurationClass.Long, GateDurationClass.Short], sent);
        var waiter = Assert.Single(waitingBoard!.Waiting);
        Assert.Equal(("ai-patterns", GateDurationClass.Short, "All 2 slots are busy."),
            (waiter.Project, waiter.DurationClass, waiter.Reason));
        Assert.Equal(1, runningBoard!.ShortSlot);
        Assert.Empty(runningBoard.Waiting);
        var held = runningBoard.Slots[1];
        Assert.Equal(("ai-patterns", GateDurationClass.Short, true), (held.Project, held.DurationClass, held.ShortSlot));
        Assert.Null(runningBoard.Slots[0].Project);
        Assert.Empty(slots.Board().Waiting);
        Assert.All(slots.Board().Slots, slot => Assert.Null(slot.Project));
        var reopened = new GateDurationHistory(Path.Combine(_root, "history.json")).Estimate("ai-patterns");
        Assert.Equal((GateDurationClass.Short, TimeSpan.FromMinutes(2), 4), (reopened.Class, reopened.Median, reopened.Samples));
    }

    [Fact]
    public void Long_gate_on_the_board_explains_the_kept_short_slot()
    {
        var slots = new GateSlotUsageTracker(clock: _clock);
        slots.Configure(5);
        slots.Queued("run-long", "agent-studio", "build-test", GateDurationClass.Long);
        slots.RemoteWaiting("run-long", 4, 5);
        _clock.Advance(TimeSpan.FromMinutes(30));

        var waiter = Assert.Single(slots.Board().Waiting);

        Assert.Equal("All 4 shared slots are busy; slot 4 is kept for short gates and opens to this long gate in 15 min.",
            waiter.Reason);
    }

    private RemoteGateTransport Transport(
        Func<string, IReadOnlyList<string>, string, TimeSpan, CancellationToken, Action<string>?, Task> run,
        GateSlotUsageTracker slots, GateDurationHistory history)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RemoteGate:SshHost"] = "gate-a",
            ["RemoteGate:WorkerPath"] = "/opt/gate/worker.dll",
            ["RemoteGate:Root"] = "/srv/gates",
            ["RemoteGate:GateSlots"] = "2",
        }).Build();
        var registry = new RemoteGateRunRegistry(Path.Combine(_root, "runs"), _clock, new string('a', 32), new string('b', 32));
        return new RemoteGateTransport(configuration, NullLogger<RemoteGateTransport>.Instance,
            _clock, Path.Combine(_root, "local"), _ => { }, slots, runProcess: run, runRegistry: registry,
            ownerIsAlive: _ => true, durations: history);
    }

    private static BuildTestGateResult Passed() => new(BuildTestGateVerdict.Ok, 0, 1, "", "passed", true, false)
    {
        ExpectedSha = Sha, TestedSha = Sha, GateId = PipelineCatalogue.BuildTestGateStepId,
    };

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
