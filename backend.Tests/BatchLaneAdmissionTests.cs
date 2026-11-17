using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3856: on 3 and 4 November 2026 per-card full suites used 1,752.6 of
/// 2,749 gate minutes, 36 of the 40 green ones were followed by a batch gate
/// for the same card, and a published batch gate took a median 13.7 minutes
/// with at most two gates on the host and 27.6 minutes with more. While a
/// batch gate runs, at most one other full suite starts, and a card a batch
/// gates anyway gets no per-card suite.
/// </summary>
public sealed class BatchLaneAdmissionTests
{
    private static readonly DateTimeOffset Now = new(2026, 11, 16, 10, 0, 0, TimeSpan.Zero);

    [Theory]
    [InlineData(0, 0, false)]
    [InlineData(0, 5, false)]
    [InlineData(1, 0, false)]
    [InlineData(1, 1, true)]
    [InlineData(2, 1, true)]
    [InlineData(1, 3, true)]
    public void While_a_batch_gate_runs_one_other_full_suite_is_admitted(
        int batchGates, int companions, bool refused)
        => Assert.Equal(refused ? BatchGateLookAheadPlanner.CompanionSuiteWaitReason : null,
            BatchGateLookAheadPlanner.CompanionSuiteRefusal(batchGates, companions));

    [Theory]
    [InlineData("look-ahead plan: close condition reached below minimum batch size", true)]
    [InlineData("threshold plan: oldest eligible card reached deadline without a batch partner", true)]
    [InlineData("look-ahead plan: oldest card exceeds batch risk limit", false)]
    [InlineData("look-ahead plan: operator close-now below minimum batch size", false)]
    [InlineData("threshold plan: close requested below minimum batch size", false)]
    [InlineData(null, false)]
    public void Only_a_per_task_route_chosen_for_lack_of_a_partner_can_be_refused(string? planEvent, bool partnerMissing)
        => Assert.Equal(partnerMissing, BatchGateLookAheadPlanner.PartnerMissingRoute(planEvent));

    [Fact]
    public void Look_ahead_close_now_below_minimum_names_the_operator_route()
    {
        var input = new BatchPlanInput(Now, [new BatchPlanCard("A", Now.AddMinutes(-5), .1, ["a.cs"], [])],
            [], 2, 4, 20, 15, [], "balanced", "separate");

        Assert.Equal(BatchGateLookAheadPlanner.CloseNowBelowMinimumReason,
            BatchGateLookAheadPlanner.Plan(input with { CloseNow = true }).Reason);
        Assert.Equal(BatchGateLookAheadPlanner.BelowMinimumReason,
            BatchGateLookAheadPlanner.Plan(input with { NowUtc = Now.AddMinutes(20) }).Reason);
    }

    [Fact]
    public void A_batch_partner_gets_no_per_card_suite()
    {
        var facts = Facts(eligible: 1);

        Assert.Equal("1 batch partner waits: the next batch gates this card instead of a per-card suite",
            BatchGateLookAheadPlanner.PerTaskSuiteRefusal(facts));
        Assert.Equal("already a member of batch 0123abcd: the batch gate tests it",
            BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts() with { BatchId = "0123abcd99", PartnerMissingRoute = false }));
        Assert.StartsWith("3 batch partners wait",
            BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts(eligible: 3)));
    }

    [Fact]
    public void A_card_whose_partners_finish_their_pre_check_before_its_deadline_waits_for_them()
    {
        var before = Facts(queued: 1) with { DeadlineUtc = Now.AddMinutes(5) };

        Assert.Equal("1 batch partner finishes its pre-check before this card's deadline at 10:05 UTC:"
            + " the next batch gates this card instead of a per-card suite",
            BatchGateLookAheadPlanner.PerTaskSuiteRefusal(before));
        Assert.Null(BatchGateLookAheadPlanner.PerTaskSuiteRefusal(before with { DeadlineUtc = Now }));
    }

    [Fact]
    public void A_card_that_is_not_a_batch_candidate_keeps_its_suite()
    {
        // No partner at all.
        Assert.Null(BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts()));
        // High risk, close-now or a scope rule sent it: partners do not change that.
        Assert.Null(BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts(eligible: 4) with { PartnerMissingRoute = false }));
        // One refusal per review generation, so a card can never starve.
        Assert.Null(BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts(eligible: 4) with { RefusedBefore = true }));
        // A minimum size of one never waits for a partner.
        Assert.Null(BatchGateLookAheadPlanner.PerTaskSuiteRefusal(Facts(eligible: 4) with { MinimumSize = 1 }));
    }

    [Fact]
    public async Task A_second_per_card_suite_waits_while_a_batch_gate_runs()
    {
        var admission = new BatchLaneAdmission { RecheckInterval = TimeSpan.FromMilliseconds(50) };
        var batch = await admission.EnterAsync(Request("batch-gate", batchId: "b1"), CancellationToken.None);
        var first = await admission.EnterAsync(Request("merge-into-develop-build-gate", job: "WEB-1"),
            CancellationToken.None);
        string? reported = null;
        var second = admission.EnterAsync(Request("batch-fallback", job: "AGT-2") with
        {
            OnBatchLaneWaiting = reason => reported = reason,
        }, CancellationToken.None);

        await WaitForAsync(() => admission.Snapshot().Waiting.Count == 1);
        Assert.False(second.IsCompleted);
        Assert.Equal(BatchGateLookAheadPlanner.CompanionSuiteWaitReason, reported);
        var waiter = Assert.Single(admission.Snapshot().Waiting);
        Assert.Equal(["AGT-2"], waiter.Cards);
        Assert.Equal("batch-fallback", waiter.GateId);
        Assert.Equal((1, 1, 1), (admission.Snapshot().BatchGates, admission.Snapshot().CompanionSuites,
            admission.Snapshot().Limit));

        first!.Dispose();
        using (await second.WaitAsync(TimeSpan.FromSeconds(10)))
            Assert.Empty(admission.Snapshot().Waiting);

        // Without a batch gate every full suite starts; the slot rules still apply.
        batch!.Dispose();
        using var third = await admission.EnterAsync(Request("batch-fallback", job: "AGT-3"), CancellationToken.None);
        using var fourth = await admission.EnterAsync(Request("batch-fallback", job: "AGT-4"), CancellationToken.None);
        Assert.Equal(2, admission.Snapshot().CompanionSuites);
    }

    [Fact]
    public async Task The_batch_lane_and_checks_that_are_no_full_suite_are_never_held()
    {
        var admission = new BatchLaneAdmission();
        using var batch = await admission.EnterAsync(Request("batch-gate", batchId: "b1"), CancellationToken.None);
        using var companion = await admission.EnterAsync(Request("batch-fallback", job: "AGT-2"), CancellationToken.None);

        var stacked = admission.EnterAsync(Request("batch-gate", batchId: "b2"), CancellationToken.None);
        var baseCheck = admission.EnterAsync(Request("batch-base", batchId: "b1"), CancellationToken.None);
        var preCheck = admission.EnterAsync(Request("batch-pre-check", job: "AGT-5",
            level: TestExecutionLevels.BatchPreCheck), CancellationToken.None);
        var narrowed = admission.EnterAsync(Request("merge-into-develop-build-gate", job: "AGT-6") with
        {
            NarrowCheck = new GateNarrowCheck("changed-files", []),
        }, CancellationToken.None);

        Assert.True(stacked.IsCompleted && baseCheck.IsCompleted && preCheck.IsCompleted && narrowed.IsCompleted);
        Assert.Null(await baseCheck);
        Assert.Null(await preCheck);
        Assert.Null(await narrowed);
        (await stacked)!.Dispose();
    }

    [Fact]
    public async Task The_gate_runner_holds_a_second_full_suite_until_the_first_returns()
    {
        var admission = new BatchLaneAdmission { RecheckInterval = TimeSpan.FromMilliseconds(50) };
        var inner = new HoldingGate();
        var runner = new BatchLaneAdmissionGateRunner(inner, admission);
        var batch = runner.RunAsync(Request("batch-gate", batchId: "b1"), null, null, PostStepMode.Fail,
            TimeSpan.FromMinutes(5), CancellationToken.None);
        var first = runner.RunAsync(Request("batch-fallback", job: "AGT-2"), null, null, PostStepMode.Fail,
            TimeSpan.FromMinutes(5), CancellationToken.None);
        await WaitForAsync(() => inner.Started.Count == 2);
        var second = runner.RunAsync(Request("batch-fallback", job: "AGT-3"), null, null, PostStepMode.Fail,
            TimeSpan.FromMinutes(5), CancellationToken.None);
        await WaitForAsync(() => admission.Snapshot().Waiting.Count == 1);

        Assert.Equal(["batch-gate", "AGT-2"], inner.Started);
        inner.Release("AGT-2");
        await first;
        await WaitForAsync(() => inner.Started.Count == 3);
        Assert.Equal("AGT-3", inner.Started[2]);

        // A suite cancelled while it waits never reaches the gate host.
        using var cancelled = new CancellationTokenSource();
        var blocked = runner.RunAsync(Request("batch-fallback", job: "AGT-4"), null, null, PostStepMode.Fail,
            TimeSpan.FromMinutes(5), cancelled.Token);
        await WaitForAsync(() => admission.Snapshot().Waiting.Count == 1);
        cancelled.Cancel();
        Assert.Equal(BuildTestGateFailureKind.Cancellation, (await blocked).FailureKind);
        Assert.Equal(3, inner.Started.Count);

        inner.Release("AGT-3");
        inner.Release("batch-gate");
        await Task.WhenAll(batch, second);
        Assert.Equal((0, 0), (admission.Snapshot().BatchGates, admission.Snapshot().CompanionSuites));
    }

    [Fact]
    public void Daily_summary_counts_green_per_card_suites_followed_by_a_batch_gate()
    {
        var day = new[]
        {
            Row("f1", "per-task-fallback", "Ok", ["AGT-1"], Now),
            Row("f2", "per-task-fallback", "Ok", ["AGT-2"], Now),
            Row("f3", "per-task-fallback", "Fail", ["AGT-3"], Now),
            Row("g1", "per-task-gate", "Ok", ["AGT-4"], Now),
        };
        var later = day.Concat([
            Row("b1", "batch-gate", "Ok", ["AGT-1", "AGT-9"], Now.AddHours(3)),
            Row("b0", "batch-gate", "Ok", ["AGT-2"], Now.AddHours(-3)),
            Row("b2", "batch-gate", "Fail", ["AGT-3"], Now.AddHours(1)),
        ]).ToArray();

        var summary = GateRunLedger.PerCardSuites(day, later);

        // AGT-1's green suite was followed by a batch gate; AGT-2's batch gate came before it.
        Assert.Equal(new PerCardSuiteFollowUp(2, 1, .5), summary);
        Assert.Equal(new PerCardSuiteFollowUp(0, 0, null), GateRunLedger.PerCardSuites([], later));
    }

    private static PerTaskSuiteFacts Facts(int eligible = 0, int queued = 0)
        => new(null, PartnerMissingRoute: true, RefusedBefore: false, eligible, queued, MinimumSize: 2,
            Now, Now.AddMinutes(-1));

    private static BuildTestGateRequest Request(string gateId, string? job = null, string? batchId = null,
        string level = TestExecutionLevels.Full)
        => new BuildTestGateRequest("/repo", new string('a', 40), gateId == "batch-fallback" ? "batch-fallback" : gateId)
        {
            GateId = gateId, JobId = job, LedgerBatchId = batchId, Project = "project", RequiredTestLevel = level,
        };

    private static GateRunLedgerRow Row(string id, string type, string outcome, string[] cards, DateTimeOffset at)
        => new(id, type, "project", cards, null, null, at, at, null, at, at.AddMinutes(14), outcome,
            14 * 60_000, 0, [], null, 0, null, null, null);

    private static async Task WaitForAsync(Func<bool> condition)
    {
        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        while (!condition())
        {
            if (elapsed.Elapsed > TimeSpan.FromSeconds(10)) throw new TimeoutException("condition not reached");
            await Task.Delay(20);
        }
    }

    private sealed class HoldingGate : IBuildTestGateRunner
    {
        private readonly Dictionary<string, TaskCompletionSource> _holds = new(StringComparer.Ordinal);
        public List<string> Started { get; } = [];

        public void Release(string key)
        {
            lock (_holds) _holds[key].TrySetResult();
        }

        public async Task<BuildTestGateResult> RunAsync(BuildTestGateRequest request,
            IReadOnlyList<string>? changedFiles, BuildProfile? profile, PostStepMode mode,
            TimeSpan timeout, CancellationToken ct)
        {
            var key = request.JobId ?? request.GateId;
            TaskCompletionSource hold;
            lock (_holds)
            {
                Started.Add(key);
                hold = _holds[key] = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
            await hold.Task.WaitAsync(ct);
            return new BuildTestGateResult(BuildTestGateVerdict.Ok, null, 0, "", "ok", true, true);
        }
    }
}
