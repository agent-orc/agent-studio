using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: the store reads no clock; intents and receipts carry the instants they are given.

/// <summary>
/// AGT-3218: the batch tick takes an engine batch plan while the applier may
/// write the next one. Until 20.10.2026 the tick read the intent and then
/// deleted the file, so a plan written in between was deleted unread and no
/// receipt ever named it. The tick now claims the file by an atomic rename
/// under the lock the applier writes with.
/// </summary>
public sealed class BatchGateEngineIntentStoreTests : IDisposable
{
    private const string Scope = "batch-plan:documentation";
    private static readonly DateTimeOffset At = new(2026, 10, 20, 8, 0, 0, TimeSpan.Zero);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "atp-engine-intents-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public async Task A_plan_written_while_the_tick_takes_the_older_one_is_taken_or_superseded_never_lost()
    {
        var store = new BatchGateStore(_root);
        const int plans = 200;
        var taken = new System.Collections.Concurrent.ConcurrentBag<string>();
        var writer = Task.Run(() =>
        {
            for (var index = 0; index < plans; index++)
                store.WriteEngineIntent(new BatchGateEngineIntent($"orch_{index}", Scope, Plan(), At));
        });
        var taker = Task.Run(async () =>
        {
            while (!writer.IsCompleted)
            {
                if (store.TakeEngineIntent(Scope) is { } intent) taken.Add(intent.RunId);
                await Task.Yield();
            }
        });
        await Task.WhenAll(writer, taker);
        if (store.TakeEngineIntent(Scope) is { } last) taken.Add(last.RunId);

        Assert.Null(store.ReadEngineIntent(Scope));
        Assert.All(Enumerable.Range(0, plans).Select(index => $"orch_{index}"), runId => Assert.True(
            taken.Contains(runId) || store.ReadEngineIntentReceipt(runId)?.Outcome == "superseded",
            $"{runId} was neither taken nor superseded."));
    }

    [Fact]
    public void Receipts_are_recorded_once_and_pruned_after_the_retention()
    {
        var store = new BatchGateStore(_root);
        store.RecordEngineIntentReceipt(new("orch_old", Scope, "applied", "wait", At));
        store.RecordEngineIntentReceipt(new("orch_old", Scope, "rejected", "second record", At.AddMinutes(1)));
        Assert.Equal("applied", store.ReadEngineIntentReceipt("orch_old")?.Outcome);

        store.RecordEngineIntentReceipt(new("orch_recent", Scope, "applied", "wait",
            At + BatchGateStore.EngineIntentReceiptRetention));
        Assert.NotNull(store.ReadEngineIntentReceipt("orch_old"));

        store.RecordEngineIntentReceipt(new("orch_new", Scope, "expired", null,
            At + BatchGateStore.EngineIntentReceiptRetention + TimeSpan.FromMinutes(1)));
        Assert.Null(store.ReadEngineIntentReceipt("orch_old"));
        Assert.NotNull(store.ReadEngineIntentReceipt("orch_recent"));
        Assert.NotNull(store.ReadEngineIntentReceipt("orch_new"));
    }

    private static BatchPlanDecision Plan() => new("wait", "Members are still arriving.", null, [], 4, []);
}
