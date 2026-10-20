extern alias EngineAssembly;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Shared;
using AgentStudio.Tasks;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

using Contract = AgentStudio.TaskServer.Contracts;
using GateDispatchLoop = EngineAssembly::AgentStudio.OrchestratorEngine.GateDispatchLoop;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3234: the GateResult profile stayed at streak 0 and mismatch count 0
/// on Stable from 16.10.2026. Only a release of a batch member opened a
/// GateResult occurrence; every red per-task gate escalated without one, so a
/// red batch run, whose red members end in that gate, was never compared.
/// Each real gate outcome now records exactly one shadow observation, and the
/// real engine handler decides it.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    public static TheoryData<string> GateResultOutcomes() => new()
    {
        "batch-pass", "batch-red", "per-task-pass", "per-task-red", "containment", "already-integrated",
    };

    [Fact]
    public async Task Red_per_task_gate_of_a_paused_batch_records_a_gate_result_observation()
    {
        // The reproduction: before AGT-3234 this card escalated without any
        // GateResult occurrence, so the profile counted nothing.
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        var rollout = ShadowGateResult(factory);
        var member = SeedMember(factory, "DOC-GR-PAUSED", "docs/gr-paused.md");
        _gate.Batch = _ => Infrastructure();
        _gate.Fallback = request => Red(request.ExpectedSha);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var observed = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.True(observed.Shadow);
        Assert.Equal($"batch-fallback-red:{member.ReviewAttemptId}:{member.ResultSha}", observed.IdempotencyKey);
        Assert.Equal(nameof(TransitionAction.Escalate), observed.MonolithReceipt?.Action);

        await DecideGateResultAsEngineAsync(factory, observed.Run.RunId);

        var profile = rollout.GetProfiles().Single(item => item.Profile == "GateResult");
        Assert.Equal((1, 0), (profile.Streak, profile.MismatchCount));
    }

    [Theory]
    [MemberData(nameof(GateResultOutcomes))]
    public async Task Every_gate_outcome_records_one_gate_result_observation_compared_with_the_engine(
        string outcome)
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        var rollout = ShadowGateResult(factory);
        var expected = await ArrangeGateOutcomeAsync(factory, outcome);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        // A later tick finds each outcome settled and opens nothing new.
        await factory.Services.GetRequiredService<BatchGatePilotService>().TickAsync(CancellationToken.None);

        var observations = new List<MonolithOrchestrationOccurrence>();
        foreach (var (key, lane, keyPrefix, action) in expected)
        {
            AssertLane(key, lane);
            var observed = Assert.Single(GateResultOccurrences(orchestration, key));
            Assert.True(observed.Shadow, $"{key} was not observed in shadow mode.");
            Assert.StartsWith(keyPrefix, observed.IdempotencyKey, StringComparison.Ordinal);
            Assert.Equal(action, observed.MonolithReceipt?.Action);
            observations.Add(observed);
        }
        // The engine claims in its own order, so it decides every pending occurrence.
        for (var claim = Claim(orchestration); claim.Run is not null; claim = Claim(orchestration))
            await CompleteAsEngineAsync(orchestration, claim, forced: null);

        foreach (var observed in observations)
        {
            var compared = orchestration.GetOccurrence(observed.Run.RunId)!;
            Assert.Equal(compared.MonolithReceipt!.Action, compared.EngineReceipt!.Action);
            Assert.Equal(compared.MonolithReceipt.InputsDigest, compared.EngineReceipt.InputsDigest);
        }
        var profile = rollout.GetProfiles().Single(item => item.Profile == "GateResult");
        Assert.Equal((expected.Length, 0), (profile.Streak, profile.MismatchCount));
    }

    [Fact]
    public async Task Passed_fallback_gate_waits_for_the_human_review_move_before_comparison()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var rollout = ShadowGateResult(factory);
        var member = SeedMember(factory, "CODE-GR-PASS-BLOCKED", "src/gr-pass-blocked.cs",
            settleReview: false);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        var source = authority.GetRun(member.RunAttemptId)!;
        _gate.Fallback = request => Green(request.ExpectedSha);
        var blocker = Path.Combine(_watchPath, TaskStates.HumanReview, member.Key);
        File.WriteAllText(blocker, "A file at the target lane path prevents the move.");
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();

        await pilot.RunEmergencyFallbackAsync(Card(factory, member.Key), review, source,
            _clock, CancellationToken.None);

        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal(member.ResultSha, Ownership(member.Key)?.FallbackTestedSha);
        var observed = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.Null(observed.MonolithReceipt);
        Assert.Null(observed.MonolithIntent);
        await DecideGateResultAsEngineAsync(factory, observed.Run.RunId);
        Assert.Equal(0, rollout.GetProfiles().Single(item => item.Profile == "GateResult").Streak);

        // The integration coordinator has merged locally. Publishing the
        // branch makes the gate eligible for release; the target file now
        // isolates the lane-move failure from the publication wait.
        Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
        await pilot.RunEmergencyFallbackAsync(Card(factory, member.Key), review,
            authority.GetRun(member.RunAttemptId)!,
            _clock, CancellationToken.None);
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Null(orchestration.GetOccurrence(observed.Run.RunId)!.MonolithIntent);
        Assert.Null(orchestration.GetOccurrence(observed.Run.RunId)!.MonolithReceipt);
        Assert.Equal(0, rollout.GetProfiles().Single(item => item.Profile == "GateResult").Streak);

        File.Delete(blocker);
        await pilot.RunEmergencyFallbackAsync(Card(factory, member.Key), review,
            authority.GetRun(member.RunAttemptId)!, _clock, CancellationToken.None);

        AssertLane(member.Key, TaskStates.HumanReview);
        var compared = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.Equal(observed.Run.RunId, compared.Run.RunId);
        Assert.Equal(nameof(TransitionAction.Continue), compared.MonolithReceipt?.Action);
        Assert.Equal((1, 0), (rollout.GetProfiles().Single(item => item.Profile == "GateResult").Streak,
            rollout.GetProfiles().Single(item => item.Profile == "GateResult").MismatchCount));
    }

    [Fact]
    public async Task Gate_result_mismatch_carries_the_monolith_and_the_engine_receipt()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 4);
        var rollout = ShadowGateResult(factory);
        var member = await RunEmergencyFallbackAsync(factory, "CODE-GR-MISMATCH", red: true);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var observed = Assert.Single(GateResultOccurrences(orchestration, member.Key));

        await DecideGateResultAsEngineAsync(factory, observed.Run.RunId, TransitionAction.ReturnToReady);

        var profile = rollout.GetProfiles().Single(item => item.Profile == "GateResult");
        Assert.Equal((0, 1), (profile.Streak, profile.MismatchCount));
        var mismatch = Assert.Single(rollout.Mismatches("GateResult"));
        Assert.Equal(mismatch, profile.LastMismatch);
        Assert.Equal(nameof(TransitionAction.Escalate), mismatch.Monolith.Action);
        Assert.Equal(nameof(TransitionAction.ReturnToReady), mismatch.Engine.Action);
        Assert.Equal((observed.Run.RunId, observed.Run.RunId),
            (mismatch.Monolith.Occurrence, mismatch.Engine.Occurrence));
        Assert.Equal(mismatch.Monolith.InputsDigest, mismatch.Engine.InputsDigest);
        Assert.EndsWith(":monolith", mismatch.Monolith.Decider, StringComparison.Ordinal);
        Assert.DoesNotContain(":monolith", mismatch.Engine.Decider, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Red_gate_that_runs_again_after_a_failed_escalation_reuses_its_observation()
    {
        using var factory = BuildFactory();
        _ = factory.CreateClient();
        EnableBatchGate(factory, closeSize: 1);
        ShadowGateResult(factory);
        var member = SeedMember(factory, "DOC-GR-RETRY", "docs/gr-retry.md");
        _gate.Batch = _ => Infrastructure();
        var reason = 0;
        // Each run words its failure differently, as real suites do.
        _gate.Fallback = request => Red(request.ExpectedSha) with
        {
            Reason = $"deterministic-suite-red run {Interlocked.Increment(ref reason)}",
        };
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var blocker = Path.Combine(_watchPath, TaskStates.Escalated, member.Key);
        File.WriteAllText(blocker, "A file at the target lane path makes the escalation fail.");
        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);
        AssertLane(member.Key, TaskStates.AutoReview);
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var first = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.Null(first.MonolithIntent);
        Assert.Null(first.MonolithReceipt);

        File.Delete(blocker);
        await pilot.TickAsync(CancellationToken.None);

        AssertLane(member.Key, TaskStates.Escalated);
        Assert.Equal(2, _gate.FallbackRuns);
        var observed = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.Equal(first.Run.RunId, observed.Run.RunId);
        Assert.Equal(nameof(TransitionAction.Escalate), observed.MonolithReceipt?.Action);
    }

    private static OrchestrationRolloutStore ShadowGateResult(WebApplicationFactory<Program> factory)
    {
        var rollout = factory.Services.GetRequiredService<OrchestrationRolloutStore>();
        rollout.Switch("GateResult", "shadow", "operator", "compare every gate outcome");
        return rollout;
    }

    /// <summary>The cards an outcome settles, with the lane, occurrence key and monolith action each must show.</summary>
    private async Task<(string Key, string Lane, string KeyPrefix, string Action)[]> ArrangeGateOutcomeAsync(
        WebApplicationFactory<Program> factory, string outcome)
    {
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        const string release = nameof(TransitionAction.Continue);
        const string escalate = nameof(TransitionAction.Escalate);
        switch (outcome)
        {
            case "batch-pass":
            {
                EnableBatchGate(factory, closeSize: 1);
                var member = SeedMember(factory, "DOC-GR-PASS", "docs/gr-pass.md");
                _gate.Batch = request => Green(request.ExpectedSha);
                await pilot.TickAsync(CancellationToken.None);
                return [(member.Key, TaskStates.HumanReview, "batch-result:", release)];
            }
            case "batch-red":
            {
                // A red batch run isolates its offender; the survivor is
                // published, the offender's per-task gate is red.
                EnableBatchGate(factory, closeSize: 2);
                var offender = SeedMember(factory, "DOC-GR-OFFENDER", "docs/gr-offender.md");
                var survivor = SeedMember(factory, "DOC-GR-SURVIVOR", "docs/gr-survivor.md");
                _gate.Batch = request => TreeHas(request.ExpectedSha!, "docs/gr-offender.md")
                    ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
                _gate.Fallback = request => Red(request.ExpectedSha);
                await pilot.TickAsync(CancellationToken.None);
                return
                [
                    (offender.Key, TaskStates.Escalated, "batch-fallback-red:", escalate),
                    (survivor.Key, TaskStates.HumanReview, "batch-result:", release),
                ];
            }
            case "per-task-pass":
            {
                EnableBatchGate(factory, closeSize: 4);
                var member = SeedMember(factory, "CODE-GR-PASS", "src/gr-fallback-pass.cs",
                    settleReview: false);
                var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
                var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
                _gate.Fallback = request => Green(request.ExpectedSha);
                await pilot.RunEmergencyFallbackAsync(Card(factory, member.Key), review,
                    authority.GetRun(member.RunAttemptId)!, _clock, CancellationToken.None);
                Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
                await pilot.RunEmergencyFallbackAsync(Card(factory, member.Key), review,
                    authority.GetRun(member.RunAttemptId)!, _clock, CancellationToken.None);
                return [(member.Key, TaskStates.HumanReview, "batch-fallback:", release)];
            }
            case "per-task-red":
            {
                EnableBatchGate(factory, closeSize: 4);
                var member = await RunEmergencyFallbackAsync(factory, "CODE-GR-RED", red: true);
                return [(member.Key, TaskStates.Escalated, "batch-fallback-red:", escalate)];
            }
            case "containment":
            {
                EnableBatchGate(factory, closeSize: 1);
                var member = SeedMember(factory, "DOC-GR-EQUIVALENT", "docs/gr-equivalent.md");
                var containedIn = PublishPatchEquivalent(member.ResultSha);
                await pilot.TickAsync(CancellationToken.None);
                return [(member.Key, TaskStates.HumanReview, $"batch-fallback:{member.ReviewAttemptId}:{containedIn}",
                    release)];
            }
            case "already-integrated":
            {
                EnableBatchGate(factory, closeSize: 1);
                var member = SeedMember(factory, "DOC-GR-CONTAINED", "docs/gr-contained.md");
                Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
                await pilot.TickAsync(CancellationToken.None);
                return [(member.Key, TaskStates.HumanReview, $"batch-fallback:{member.ReviewAttemptId}:{RemoteTip()}",
                    release)];
            }
            default:
                throw new ArgumentOutOfRangeException(nameof(outcome), outcome, null);
        }
    }

    /// <summary>
    /// A code card outside the documentation pilot: the batch queue refuses
    /// its admission, and the per-task emergency gate settles it, as
    /// <see cref="EngineBatchAdmission"/> does for an engine DeferToBatch.
    /// </summary>
    private async Task<SeededMember> RunEmergencyFallbackAsync(WebApplicationFactory<Program> factory,
        string key, bool red)
    {
        var member = SeedMember(factory, key, $"src/{key}.cs", settleReview: false);
        var review = Settle(factory, member, ReviewTerminalOutcome.Pass);
        _gate.Fallback = request => red ? Red(request.ExpectedSha) : Green(request.ExpectedSha);
        var authority = factory.Services.GetRequiredService<AttemptAuthorityService>();
        await factory.Services.GetRequiredService<BatchGatePilotService>().RunEmergencyFallbackAsync(
            Card(factory, key), review, authority.GetRun(member.RunAttemptId)!, _clock, CancellationToken.None);
        return member;
    }

    private static IReadOnlyList<MonolithOrchestrationOccurrence> GateResultOccurrences(
        MonolithOrchestrationStore orchestration, string taskId)
        => orchestration.List(ProjectName, null)
            .Select(run => orchestration.GetOccurrence(run.RunId)!)
            .Where(item => item.Profile == "GateResult" && item.Run.TaskId == taskId)
            .ToArray();

    /// <summary>
    /// Claims the occurrence and completes it with the real engine gate
    /// handler, or with <paramref name="forced"/> for a decision the handler
    /// does not reach from these facts.
    /// </summary>
    private static async Task<MonolithOrchestrationOccurrence> DecideGateResultAsEngineAsync(
        WebApplicationFactory<Program> factory, string runId, TransitionAction? forced = null)
    {
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        await CompleteAsEngineAsync(orchestration, ClaimShadow(orchestration, runId), forced);
        return orchestration.GetOccurrence(runId)!;
    }

    private static Contract.OrchestrationClaimResponse Claim(MonolithOrchestrationStore orchestration)
        => orchestration.Claim(new("engine", "shadow", [Contract.OrchestrationStage.GateDispatch]));

    private static async Task CompleteAsEngineAsync(MonolithOrchestrationStore orchestration,
        Contract.OrchestrationClaimResponse claim, TransitionAction? forced)
    {
        var occurrence = orchestration.GetOccurrence(claim.Run!.RunId)!;
        Contract.OrchestrationAction action;
        string output;
        if (forced is { } decided)
        {
            var transition = TransitionDecisionPolicy.Create(decided, $"The engine decided {decided}.",
                occurrence.Run.TaskId, occurrence.AuthorityAttemptId, 0, 1);
            action = StageDecisionPolicy.ToStageAction(decided);
            output = JsonSerializer.Serialize(new { transition, decision = action.ToString() });
        }
        else
        {
            var decision = await new GateDispatchLoop().ExecuteAsync(claim.Run, CancellationToken.None);
            (action, output) = (decision.Action, decision.OutputJson);
        }
        await orchestration.CompleteAsync(claim.Run.RunId, new Contract.CompleteOrchestrationStageRequest(
            "engine", "shadow", claim.Lease!.LeaseId, claim.Lease.Fence, Contract.OrchestrationStage.GateDispatch,
            action, output, $"engine:{claim.Run.RunId}"), CancellationToken.None);
    }
}
