using System.Text.RegularExpressions;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Xunit;

namespace AgentStudio.Tests;

// clock-independent: lane entry instants are pure policy inputs; the policy receives an explicit now.

/// <summary>
/// DS-3 acceptance (Dossier docs/dispatcher-personas): the Queue keeper
/// strategy <c>lane-entry-v1</c> reproduces today's pickup exactly. On 200
/// seeded random queues its plan order equals <see cref="LaneSortApplier.PickupOrder"/>
/// and its slot decisions equal the pick loop of the local runner, which takes
/// the first card that <see cref="ParallelSlotPolicy.Decide"/> admits against
/// the running cards, once per free slot.
/// </summary>
public sealed class DispatchPlanPolicyEquivalenceTests
{
    private static readonly DateTime Base = new(2026, 11, 17, 9, 0, 0, DateTimeKind.Utc);
    private static readonly string[] ScopePool =
    [
        "backend/Features/Runner", "backend/Features/Tasks", "backend/Features/Tasks/Acceptance",
        "frontend/src/app/features/board", "frontend/src/app/features/persona-team", "docs/dispatcher-personas",
        "orchestration-decisions", "backend/Features", "tools/scripts",
    ];

    [Fact]
    public void Lane_entry_plan_equals_todays_pickup_order_and_admissions_on_200_random_queues()
    {
        var random = new Random(3993);
        for (var round = 0; round < 200; round++)
        {
            var queue = RandomQueue(random, round);
            var expected = TodaysPickup(queue);
            var plan = DispatchPlanPolicy.Plan(queue.Facts, DispatchStrategies.Starter(DispatchStrategies.LaneEntry));

            Assert.Equal(expected.Order, plan.Order.Select(entry => entry.CardKey).ToArray());
            Assert.All(plan.Order, entry => Assert.Equal(DispatchCandidateState.Eligible, entry.State));
            Assert.Equal(expected.Slots.Count, plan.Slots.Count);
            for (var slot = 0; slot < expected.Slots.Count; slot++)
            {
                var want = expected.Slots[slot];
                var got = plan.Slots[slot];
                Assert.True(want.CardKey == got.CardKey, $"round {round} slot {slot + 1}: {want.CardKey} vs {got.CardKey}");
                Assert.Equal(want.Action, got.Action);
                Assert.Equal(want.RunsAlone, got.RunsAlone);
                if (want.Action == DispatchSlotAction.Admit) Assert.Equal(want.ReasonCode, got.ReasonCode);
                Assert.Equal(
                    want.PassedOver.Select(item => (item.CardKey, item.ReasonCode, item.With, item.Detail)),
                    got.PassedOver.Select(item => (item.CardKey, item.ReasonCode, item.With, item.Detail)));
            }
            Assert.Empty(plan.Holds);
            Assert.Empty(plan.DependenciesToAdd);
            Assert.Empty(plan.ExclusiveMarks);
        }
    }

    private sealed record Queue(IReadOnlyList<TaskInfo> Jobs, IReadOnlyDictionary<string, TaskParallelism> Scopes,
        IReadOnlyList<RunningTask> Running, int MaxParallelism, DispatchFacts Facts);

    private sealed record Expected(IReadOnlyList<string> Order, IReadOnlyList<DispatchSlotDecision> Slots);

    private static Queue RandomQueue(Random random, int round)
    {
        var count = random.Next(0, 14);
        var jobs = new List<TaskInfo>();
        var scopes = new Dictionary<string, TaskParallelism>(StringComparer.OrdinalIgnoreCase);
        var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < count; index++)
        {
            string key;
            do key = (random.Next(4) == 0 ? "OPS-" : "AGT-") + random.Next(1, 60);
            while (!used.Add(key));
            var pinned = random.Next(4) == 0;
            jobs.Add(new TaskInfo
            {
                Id = key,
                Key = key,
                ProjectName = "agent-studio",
                State = TaskStates.Ready,
                Order = pinned ? random.Next(0, 4) : LaneSortStrategies.UnpinnedOrder,
                // Few distinct instants, so ties fall to the key tiebreaker.
                EnteredLaneAt = Base.AddMinutes(random.Next(0, 5)),
                CreatedAt = Base.AddMinutes(-random.Next(0, 50)),
            });
            scopes[key] = RandomParallelism(random, allowExclusive: true);
        }

        var max = random.Next(1, 6);
        var runningCount = random.Next(0, max + 1);
        var running = Enumerable.Range(0, runningCount)
            .Select(index => new RunningTask($"RUN-{round}-{index}", RandomParallelism(random, allowExclusive: random.Next(6) == 0)))
            .ToList();

        var facts = new DispatchFacts(
            Project: "agent-studio",
            NowUtc: new DateTimeOffset(Base.AddHours(1)),
            Ready: jobs.Select(job => new DispatchReadyCard(job.Key!, job.Order, new DateTimeOffset(job.EnteredLaneAt),
                [], true, DispatchForecast.Unknown, scopes[job.Key!].PredictedScope,
                scopes[job.Key!].Exclusive, scopes[job.Key!].ReadOnly)).ToArray(),
            InFlight: running.Select(run => new DispatchInFlightCard(run.TaskId, true, DispatchForecast.Unknown,
                run.Parallelism.PredictedScope, run.Parallelism.Exclusive, run.Parallelism.ReadOnly)).ToArray(),
            FreeSlots: ParallelSlotPolicy.FreeSlots(max, running.Count),
            Ledger: DispatchLedgerSummary.Empty,
            HubFiles: ["backend/Host/Program.cs"],
            OverlapWindow: DispatchOverlapWindow.Running);
        return new Queue(jobs, scopes, running, max, facts);
    }

    private static TaskParallelism RandomParallelism(Random random, bool allowExclusive)
    {
        var roll = random.Next(10);
        if (roll == 0) return TaskParallelism.ReadOnlyTask;
        if (roll == 1 && allowExclusive) return new TaskParallelism(true, []);
        if (roll <= 3) return TaskParallelism.Default;
        var scope = Enumerable.Range(0, random.Next(1, 3))
            .Select(_ => ScopePool[random.Next(ScopePool.Length)])
            .Select(path => random.Next(5) == 0 ? path.ToUpperInvariant().Replace('/', '\\') + "/" : path)
            .ToArray();
        return new TaskParallelism(false, scope);
    }

    /// <summary>Today's pickup: the lane-entry pickup order, then once per free slot the first card the pick-gate admits.</summary>
    private static Expected TodaysPickup(Queue queue)
    {
        var order = LaneSortApplier.PickupOrder(queue.Jobs, _ => new ProjectSettings())
            .Select(job => job.Key!).ToArray();
        var running = queue.Running.ToList();
        var picked = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<DispatchSlotDecision>();
        var free = ParallelSlotPolicy.FreeSlots(queue.MaxParallelism, running.Count);
        for (var slot = 1; slot <= free; slot++)
        {
            var passedOver = new List<DispatchPassOver>();
            DispatchSlotDecision? decision = null;
            foreach (var key in order.Where(key => !picked.Contains(key)))
            {
                var admission = ParallelSlotPolicy.Decide(key, queue.Scopes[key], running, queue.MaxParallelism);
                if (!admission.Admitted)
                {
                    passedOver.Add(PassOver(key, admission));
                    continue;
                }
                picked.Add(key);
                running.Add(new RunningTask(key, queue.Scopes[key]));
                decision = new DispatchSlotDecision(slot, DispatchSlotAction.Admit, key, AdmitCode(admission.Reason),
                    admission.Decision == SlotDecision.RunExclusive, passedOver);
                break;
            }
            slots.Add(decision ?? new DispatchSlotDecision(slot, DispatchSlotAction.Serialize, null, "", false, passedOver));
        }
        return new Expected(order, slots);
    }

    private static string AdmitCode(string reason) => reason switch
    {
        "parallel-ok: first slot" => DispatchReasonCodes.FirstSlot,
        "exclusive: runs alone" => DispatchReasonCodes.RunAlone,
        _ when reason.StartsWith("parallel-ok: read-only", StringComparison.Ordinal) => DispatchReasonCodes.ReadOnly,
        _ when reason.Contains("optimistic", StringComparison.Ordinal) => DispatchReasonCodes.ParallelOptimistic,
        _ when reason.StartsWith("parallel-ok: disjoint", StringComparison.Ordinal) => DispatchReasonCodes.ParallelDisjoint,
        _ => throw new InvalidOperationException("unmapped admission reason: " + reason),
    };

    private static DispatchPassOver PassOver(string key, SlotAdmission admission)
    {
        // DS-0: a scope serialize carries the conflicting card and the shared
        // prefix as fields; its reason text also names the overlap window.
        if (admission.IsScopeConflict)
            return new DispatchPassOver(key, DispatchReasonCodes.ScopeOverlap, admission.ConflictTaskId, admission.SharedPrefix);
        var reason = admission.Reason;
        if (Regex.Match(reason, "^exclusive task '(.+)' is running$") is { Success: true } exclusive)
            return new DispatchPassOver(key, DispatchReasonCodes.ExclusiveRunning, exclusive.Groups[1].Value);
        if (reason.StartsWith("exclusive: waits for", StringComparison.Ordinal))
            return new DispatchPassOver(key, DispatchReasonCodes.ExclusiveWaitsForDrain, null);
        throw new InvalidOperationException("unmapped serialize reason: " + reason);
    }
}
