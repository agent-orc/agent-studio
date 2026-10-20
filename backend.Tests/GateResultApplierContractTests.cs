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

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3234: the applier contract of the GateResult profile. Shadow mode
/// never runs the applier, and on 17.10.2026 a switched profile's decisions
/// were dropped by the applier for 16 hours while its streak had matched. So
/// every GateResult decision kind runs here on an engine-held occurrence
/// through the real <see cref="MonolithOrchestrationApplier"/>, and the test
/// asserts the lane move, the park verdict and the batch effects.
/// </summary>
public sealed partial class BatchGatePilotServiceTests
{
    private const string BatchReleaseCase = "batch-release";
    private const string AlreadyIntegratedCase = "already-integrated";
    private const string BatchRedCase = "batch-red-per-task-gate";
    private const string CardGateCase = "card-gate";

    public static TheoryData<string, string> GateResultApplierCases() => new()
    {
        { BatchReleaseCase, nameof(TransitionAction.Continue) },
        { AlreadyIntegratedCase, nameof(TransitionAction.Continue) },
        { BatchRedCase, nameof(TransitionAction.Escalate) },
        { BatchRedCase, nameof(TransitionAction.ReturnToReady) },
        { CardGateCase, nameof(TransitionAction.Continue) },
        { CardGateCase, nameof(TransitionAction.ReturnToReady) },
        { CardGateCase, nameof(TransitionAction.Escalate) },
    };

    [Theory]
    [MemberData(nameof(GateResultApplierCases))]
    public async Task Gate_result_applier_applies_the_lane_park_verdict_and_batch_effects_of_each_decision(
        string gateCase, string action)
    {
        using var factory = BuildFactory(configuration: new Dictionary<string, string?>
        {
            ["Orchestration:Transitions:GateResult"] = "Engine",
        });
        _ = factory.CreateClient();
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var store = factory.Services.GetRequiredService<BatchGateStore>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        var decided = Enum.Parse<TransitionAction>(action);
        var (member, runId) = await ArrangeEngineGateResultAsync(factory, gateCase, decided);
        // The engine holds the card: the monolith applied nothing.
        AssertLane(member.Key, TaskStates.AutoReview);
        Assert.Equal(runId, EngineDecisionWaitMarker.Read(CardFolder(TaskStates.AutoReview, member.Key)));
        var gatesBefore = _gate.FallbackRuns;

        // Forced only where the real handler cannot reach the decision from these facts.
        var applied = await DecideGateResultAsEngineAsync(factory, runId,
            gateCase == BatchRedCase && decided == TransitionAction.ReturnToReady ? decided : null);
        Assert.Equal(action, applied.EngineReceipt?.Action);
        Assert.Equal("completed", applied.Run.Status);

        var lane = decided switch
        {
            TransitionAction.Escalate => TaskStates.Escalated,
            TransitionAction.ReturnToReady => TaskStates.Ready,
            _ => gateCase == CardGateCase ? TaskStates.AutoReview : TaskStates.HumanReview,
        };
        AssertLane(member.Key, lane);
        var folder = CardFolder(lane, member.Key);
        Assert.Null(EngineDecisionWaitMarker.Read(folder));
        var parked = ReviewDecisionLog.ReadAll(_workspace, ProjectName).Where(record =>
            record.JobId == member.Key && record.FailureKind == $"remote-review-park:{member.ReviewAttemptId}")
            .ToArray();
        if (lane == TaskStates.HumanReview)
            Assert.Equal(ReviewDecisionKind.AcceptAsDone, Assert.Single(parked).Kind);
        else Assert.Empty(parked);
        var prompt = File.ReadAllText(Path.Combine(folder, "prompt.md"));
        Assert.Equal(decided == TransitionAction.ReturnToReady,
            prompt.Contains($"<!-- engine-decision:{runId} -->", StringComparison.Ordinal)
            && prompt.Contains("Fix the build and test gate failure:", StringComparison.Ordinal));

        if (gateCase == CardGateCase)
        {
            Assert.Null(BatchGateOwnershipStore.Read(folder));
            return;
        }
        // The next tick runs no gate again and settles the queue entry.
        await pilot.TickAsync(CancellationToken.None);
        Assert.Equal(gatesBefore, _gate.FallbackRuns);
        Assert.Empty(store.ListPending());
        AssertLane(member.Key, lane);
        var ownership = BatchGateOwnershipStore.Read(folder);
        var ejected = IntegrationLaneEvents.Read(folder).Where(item => item.Kind == "ejected").ToArray();
        if (lane == TaskStates.HumanReview)
        {
            // A released member keeps the ownership that proves its gate.
            Assert.Equal(member.ReviewAttemptId, ownership?.ReviewAttemptId);
            Assert.Empty(ejected);
            if (gateCase == AlreadyIntegratedCase) Assert.True(ownership!.FallbackIntegrated);
            else Assert.NotNull(ownership!.BatchId);
        }
        else
        {
            // A member that is not released leaves the batch route as after
            // the monolith's red per-task gate.
            Assert.Null(ownership);
            Assert.Equal("GateFailed", Assert.Single(ejected).Outcome);
            Assert.Equal("per-task-gate-red", ResolvedPendingOutcome(member.ReviewAttemptId));
        }
    }

    [Fact]
    public async Task Engine_held_red_per_task_gate_waits_without_escalating_or_running_again()
    {
        using var factory = BuildFactory(configuration: new Dictionary<string, string?>
        {
            ["Orchestration:Transitions:GateResult"] = "Engine",
        });
        _ = factory.CreateClient();
        var (member, runId) = await ArrangeEngineGateResultAsync(factory, BatchRedCase, TransitionAction.Escalate);
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();

        await pilot.TickAsync(CancellationToken.None);
        await pilot.TickAsync(CancellationToken.None);

        Assert.Equal(1, _gate.FallbackRuns);
        AssertLane(member.Key, TaskStates.AutoReview);
        var held = factory.Services.GetRequiredService<MonolithOrchestrationStore>().GetOccurrence(runId)!;
        Assert.False(held.Shadow);
        Assert.Equal("pending", held.Run.Status);
        using var payload = JsonDocument.Parse(held.Run.PayloadJson);
        Assert.Equal(0, payload.RootElement.GetProperty("maximumCardRounds").GetInt32());
        Assert.Equal("failed", payload.RootElement.GetProperty("gates")[0].GetProperty("status").GetString());
        Assert.Equal(member.ReviewAttemptId, payload.RootElement.GetProperty("reviewAttemptId").GetString());
    }

    /// <summary>
    /// Arranges an engine-held GateResult occurrence. The batch cases reach it
    /// through the pilot; the card gate case opens it with the payload of the
    /// per-task gate handler, whose verdict selects the decision.
    /// </summary>
    private async Task<(SeededMember Member, string RunId)> ArrangeEngineGateResultAsync(
        WebApplicationFactory<Program> factory, string gateCase, TransitionAction decided)
    {
        var pilot = factory.Services.GetRequiredService<BatchGatePilotService>();
        var orchestration = factory.Services.GetRequiredService<MonolithOrchestrationStore>();
        SeededMember member;
        switch (gateCase)
        {
            case BatchReleaseCase:
                EnableBatchGate(factory, closeSize: 1);
                member = SeedMember(factory, "DOC-GRA-RELEASE", "docs/gra-release.md");
                _gate.Batch = request => Green(request.ExpectedSha);
                await pilot.TickAsync(CancellationToken.None);
                break;
            case AlreadyIntegratedCase:
                EnableBatchGate(factory, closeSize: 1);
                member = SeedMember(factory, "DOC-GRA-CONTAINED", "docs/gra-contained.md");
                Git(_repo, "push", "-q", "origin", $"{member.ResultSha}:refs/heads/develop");
                await pilot.TickAsync(CancellationToken.None);
                break;
            case BatchRedCase:
                EnableBatchGate(factory, closeSize: 1);
                member = SeedMember(factory, "DOC-GRA-RED", "docs/gra-red.md");
                _gate.Batch = _ => Infrastructure();
                _gate.Fallback = request => Red(request.ExpectedSha);
                await pilot.TickAsync(CancellationToken.None);
                await pilot.TickAsync(CancellationToken.None);
                break;
            case CardGateCase:
                member = SeedMember(factory, "CARD-GRA-" + decided.ToString().ToUpperInvariant(),
                    "src/card-gate.cs", settleReview: false);
                var source = factory.Services.GetRequiredService<AttemptAuthorityService>()
                    .GetTaskProjection(member.Key).CurrentRunAttempt!;
                var passed = decided == TransitionAction.Continue;
                var run = orchestration.Create(ProjectName, member.Key, Contract.OrchestrationStage.GateDispatch,
                    JsonSerializer.Serialize(new
                    {
                        monolithGateResult = true,
                        gateAttemptId = source.AttemptId,
                        automaticRoundsUsed = 0,
                        maximumCardRounds = 2,
                        gateReason = passed ? "ok" : "dotnet test failed in CardGateTests.",
                        infrastructureFailure = decided == TransitionAction.Escalate,
                        interventionRequired = false,
                        gates = new[] { new { status = passed ? "passed" : "failed" } },
                    }),
                    $"gate-result:{member.Key}:{source.AttemptId}:{member.ResultSha}:{decided}",
                    TaskStates.AutoReview, source.AttemptId, source.LastFence, source.AuthorityEpoch,
                    profile: "GateResult");
                EngineDecisionWaitMarker.Write(CardFolder(TaskStates.AutoReview, member.Key), run.RunId);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(gateCase), gateCase, null);
        }
        var held = Assert.Single(GateResultOccurrences(orchestration, member.Key));
        Assert.False(held.Shadow);
        return (member, held.Run.RunId);
    }

    private string CardFolder(string lane, string key) => Path.Combine(_watchPath, lane, key);

    /// <summary>The queue entry's resolution; the store names entries by the SHA-256 of the review id.</summary>
    private string? ResolvedPendingOutcome(string reviewAttemptId)
    {
        var name = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(
            System.Text.Encoding.UTF8.GetBytes(reviewAttemptId)));
        var resolved = Path.Combine(_workspace, "batch-gates", "pending", name + ".json.resolved");
        if (!File.Exists(resolved)) return null;
        using var document = JsonDocument.Parse(File.ReadAllText(resolved));
        return document.RootElement.GetProperty("outcome").GetString();
    }
}
