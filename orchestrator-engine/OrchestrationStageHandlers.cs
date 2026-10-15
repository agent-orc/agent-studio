using AgentStudio.OrchestrationDecisions;
using AgentStudio.Shared;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.OrchestratorEngine;

public sealed record OrchestrationStageDecision(
    OrchestrationAction Action,
    string OutputJson);

public interface IOrchestrationStageHandler
{
    OrchestrationStage Stage { get; }
    Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct);
}

public sealed class ReviewDecisionOrchestratorLoop : IOrchestrationStageHandler
{
    private readonly ShadowDecisionStep? _decisionStep;
    private readonly IDecisionReceiptPlane? _decisionPlane;

    public ReviewDecisionOrchestratorLoop() { }

    public ReviewDecisionOrchestratorLoop(ShadowDecisionStep decisionStep, IDecisionReceiptPlane decisionPlane)
    {
        _decisionStep = decisionStep;
        _decisionPlane = decisionPlane;
    }

    public OrchestrationStage Stage => OrchestrationStage.ReviewDecision;

    public async Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (_decisionStep is not null && _decisionPlane is not null
            && await _decisionPlane.GetGroundsInputAsync(run.RunId, ct) is { Mode: DecisionReceiptPolicy.ActiveMode })
        {
            var receipt = await _decisionStep.RecordAsync(run.RunId, ct, DecisionReceiptPolicy.ActiveMode)
                ?? throw new InvalidDataException("Active decision grounds were not found.");
            var choice = receipt.Chosen.Last(item => !item.Outcome.StartsWith("rejected:",
                StringComparison.Ordinal));
            var modelTransition = receipt.Call.Status == DecisionCallStatuses.Completed
                ? ActiveDecisionPolicy.CodingTransition(choice, run.TaskId,
                    receipt.Scope.AttemptId ?? run.RunId) : null;
            var modelAction = modelTransition is null ? OrchestrationAction.Continue
                : StageDecisionPolicy.ToStageAction(modelTransition.Action);
            return new OrchestrationStageDecision(modelAction, JsonSerializer.Serialize(new
            {
                component = nameof(ReviewDecisionOrchestratorLoop),
                activeChoice = choice,
                receiptId = receipt.ReceiptId,
                groundsDigest = receipt.GroundsDigest,
                transition = modelTransition,
                decider = $"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}:model",
            }));
        }
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var reviewOutcome = ReadString(payload.RootElement, "reviewOutcome");
        var executionOutcome = ReadString(payload.RootElement, "agentOutcome");
        var terminal = ReadString(payload.RootElement, "terminal");
        var observedOutcome = reviewOutcome ?? executionOutcome ?? terminal;
        var decision = StageDecisionPolicy.Review(run.TaskId, run.ReissueAttempts,
            JsonSerializer.Deserialize<StagePayload>(run.PayloadJson, new JsonSerializerOptions(JsonSerializerDefaults.Web))!);
        var codingTarget = ReadString(payload.RootElement, "targetState")
            ?? (StageDecisionPolicy.ReviewOutcome(null, executionOutcome, terminal)
                == OrchestrationAction.Escalate ? TaskStates.Escalated : TaskStates.AutoReview);
        var transition = decision.Transition ?? StageDecisionPolicy.Coding(run.TaskId,
            ReadString(payload.RootElement, "runAttemptId") ?? run.RunId,
            codingTarget);
        var action = StageDecisionPolicy.ToStageAction(transition.Action);
        return new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(ReviewDecisionOrchestratorLoop),
                source = reviewOutcome is null ? "execution-terminal" : "remote-review",
                runAttemptId = ReadString(payload.RootElement, "runAttemptId"),
                reviewSubjectId = ReadString(payload.RootElement, "reviewSubjectId"),
                reviewAttemptId = ReadString(payload.RootElement, "reviewAttemptId"),
                observedOutcome,
                transition,
                decider = $"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}",
                decision = action.ToString(),
            }));
    }

    private static string? ReadString(JsonElement element, string property)
        => element.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

}

public sealed class CouncilLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.Council;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var stagePayload = JsonSerializer.Deserialize<StagePayload>(run.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
        var transition = EngineTransitionAdapter.Decide(StageDecisionPolicy.Facts(run.TaskId, run.ReissueAttempts, stagePayload));
        var decision = new StagePolicyDecision(StageDecisionPolicy.ToStageAction(transition.Action), transition);
        var action = decision.Action;
        var findingCount = (stagePayload.Verdicts ?? []).Count(verdict => verdict.Status != "pass")
            + (stagePayload.ReviewFindings?.Count ?? 0);
        var blockerCount = decision.Transition?.Findings.Count(finding => ReviewGradingPolicy.IsBlockingToken(finding.Status)) ?? 0;
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CouncilLoop),
                findingCount,
                blockerCount,
                transition = decision.Transition,
                decider = $"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}",
                decision = action.ToString(),
            })));
    }
}

public sealed class PostProcessingLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.PostProcessing;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        return Task.FromResult(new OrchestrationStageDecision(
            OrchestrationAction.Continue,
            JsonSerializer.Serialize(new
            {
                component = nameof(PostProcessingLoop),
                status = "decision-chain-running-remotely",
            })));
    }
}

public sealed class GateDispatchLoop : IOrchestrationStageHandler
{
    private readonly EngineTaskServerClient? _client;
    private readonly EngineOptions? _options;

    public GateDispatchLoop() { }

    public GateDispatchLoop(EngineTaskServerClient client, EngineOptions options)
    {
        _client = client;
        _options = options;
    }

    public OrchestrationStage Stage => OrchestrationStage.GateDispatch;

    public async Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var failed = 0;
        if (payload.RootElement.TryGetProperty("gates", out var gates)
            && gates.ValueKind == JsonValueKind.Array)
        {
            failed = gates.EnumerateArray().Count(gate =>
                gate.TryGetProperty("status", out var status)
                && status.ValueKind == JsonValueKind.String
                && status.GetString() is "failed" or "fail");
        }
        var action = StageDecisionPolicy.Gate(failed);
        if (payload.RootElement.TryGetProperty("monolithGateResult", out var monolithGate)
            && monolithGate.ValueKind == JsonValueKind.True)
        {
            var roundsUsed = payload.RootElement.TryGetProperty("automaticRoundsUsed", out var used)
                && used.TryGetInt32(out var u) ? u : 0;
            var roundsAllowed = payload.RootElement.TryGetProperty("maximumCardRounds", out var allowed)
                && allowed.TryGetInt32(out var a) ? a : 4;
            var attemptId = payload.RootElement.TryGetProperty("gateAttemptId", out var attempt)
                ? attempt.GetString() ?? run.RunId : run.RunId;
            var infrastructureFailure = payload.RootElement.TryGetProperty("infrastructureFailure", out var infrastructure)
                && infrastructure.ValueKind == JsonValueKind.True;
            var interventionRequired = payload.RootElement.TryGetProperty("interventionRequired", out var intervention)
                && intervention.ValueKind == JsonValueKind.True;
            var decision = StageDecisionPolicy.GateTransition(run.TaskId, attemptId,
                failed, roundsUsed, roundsAllowed, infrastructureFailure, interventionRequired);
            return new OrchestrationStageDecision(decision.Action, JsonSerializer.Serialize(new
            {
                component = nameof(GateDispatchLoop),
                transition = decision.Transition,
                decider = $"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}",
                decision = decision.Action.ToString(),
            }));
        }
        if (_options?.RemotePostBuildTestEnabled == true && _client is not null && failed == 0
            && payload.RootElement.TryGetProperty("reviewSubjectId", out var reviewId)
            && reviewId.ValueKind == JsonValueKind.String)
        {
            var review = await _client.GetReviewSubjectAsync(reviewId.GetString()!, ct);
            var verify = review.Plan.Commands.Where(command =>
                command.ExecutionKind == ReviewCommandKinds.Tool
                && (command.Aspect is "build-tests" or "lint")).ToArray();
            if (verify.Length > 0)
            {
                var commands = (review.Plan.Preparation ?? [])
                    .Select(command => new GateCommand(command.StepId, command.FileName,
                        command.Arguments, Math.Clamp(command.TimeoutSeconds, 1, 7200), command.WorkingSubdir))
                    .Concat(verify.Select(command => new GateCommand(command.StepId,
                        command.FileName, command.Arguments,
                        Math.Clamp(command.TimeoutSeconds, 1, 7200), command.WorkingSubdir)))
                    .ToArray();
                var plan = new GatePlan("post-build-test-gate", 1, commands, "", 21600,
                    GateRules.RequiredCapabilities(commands), 32768, "always");
                static string Digest(string value) => Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
                var planHash = GateRules.PlanHash(plan);
                var selectionAuditDigest = Digest(JsonSerializer.Serialize(new
                {
                    gateId = plan.GateId,
                    sourceReviewSubjectId = review.SubjectId,
                    selectedSteps = verify.Select(command => command.StepId).ToArray(),
                    preparationSteps = (review.Plan.Preparation ?? [])
                        .Select(command => command.StepId).ToArray(),
                    review.Plan.BuildProfileFingerprint,
                }));
                var gate = await _client.CreateGateSubjectAsync(new CreateGateSubjectRequest(
                    run.TaskId, review.SourceRunId, review.RepositoryId,
                    review.RepositoryUrl, review.ExpectedResultSha, review.ResultRef,
                    review.SourceBundleArtifactId, review.SourceBundleSha256,
                    planHash, review.ReviewPolicyHash, run.DefinitionVersion.ToString(),
                    selectionAuditDigest, plan,
                    (run.StageResults?.LastOrDefault()?.CompletedAt ?? run.CreatedAt).AddMinutes(15)), ct);
                while (!GateStates.IsTerminal(gate.Phase))
                {
                    await Task.Delay(TimeSpan.FromSeconds(Math.Max(1, _options.PollSeconds)), ct);
                    gate = await _client.GetGateStatusAsync(gate.Subject.SubjectId, ct);
                }
                var remoteAction = StageDecisionPolicy.RemoteGate(gate.Phase);
                return new OrchestrationStageDecision(remoteAction, JsonSerializer.Serialize(new
                {
                    component = nameof(GateDispatchLoop),
                    gateSubjectId = gate.Subject.SubjectId,
                    gateAttemptCount = gate.AttemptCount,
                    gateOutcome = gate.TerminalOutcome,
                    testedSha = gate.TestedSha,
                    decision = remoteAction.ToString(),
                }));
            }
        }
        return new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(GateDispatchLoop),
                failed,
                decision = action.ToString(),
            }));
    }
}

public sealed class CompletionJudgeLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.CompletionJudge;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var priorFailure = run.StageResults?.Any(result =>
            result.Action is OrchestrationAction.Fail or OrchestrationAction.Escalate) == true;
        var action = StageDecisionPolicy.Completion(priorFailure);
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CompletionJudgeLoop),
                decision = action.ToString(),
            })));
    }
}

public sealed class OperatorSweepLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.OperatorSweep;

    public Task<OrchestrationStageDecision> ExecuteAsync(OrchestrationRunDto run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Deserialize<OperatorSweepStagePayload>(run.PayloadJson,
            new JsonSerializerOptions(JsonSerializerDefaults.Web))
            ?? throw new InvalidDataException("Sweep occurrence payload is missing.");
        var decision = OperatorSweepPolicy.Decide(payload.Sweep, payload.Trigger, payload.Guard);
        return Task.FromResult(new OrchestrationStageDecision(
            decision.Action == OperatorSweepAction.Act ? OrchestrationAction.Reissue
                : decision.Action == OperatorSweepAction.WaitForPerson ? OrchestrationAction.Escalate
                : OrchestrationAction.Continue,
            JsonSerializer.Serialize(new { sweepDecision = decision })));
    }
}
