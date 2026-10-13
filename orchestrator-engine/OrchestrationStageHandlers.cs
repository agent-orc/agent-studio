using AgentStudio.Orchestration.Decisions;
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

internal static class ReviewDecisionPolicy
{
    internal static OrchestrationAction Decide(string? reviewOutcome, string? executionOutcome, string? terminal)
        => StageTransitionPolicy.Review(reviewOutcome, executionOutcome, terminal);
}

public sealed class ReviewDecisionOrchestratorLoop : IOrchestrationStageHandler
{
    public OrchestrationStage Stage => OrchestrationStage.ReviewDecision;

    public Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        var reviewOutcome = ReadString(payload.RootElement, "reviewOutcome");
        var executionOutcome = ReadString(payload.RootElement, "agentOutcome");
        var terminal = ReadString(payload.RootElement, "terminal");
        var observedOutcome = reviewOutcome ?? executionOutcome ?? terminal;
        var decisionFacts = payload.RootElement.TryGetProperty("decisionFacts", out var factsElement)
            ? factsElement.Deserialize<TransitionFacts>(new JsonSerializerOptions(JsonSerializerDefaults.Web)) : null;
        var transition = decisionFacts is null ? null : EngineDecisionAdapter.Review(decisionFacts);
        var action = transition is null
            ? ReviewDecisionPolicy.Decide(reviewOutcome, executionOutcome, terminal)
            : StageTransitionPolicy.FromDecision(transition);
        return Task.FromResult(new OrchestrationStageDecision(
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
                decision = action.ToString(),
            })));
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
        var severities = new List<string>();
        var findingCount = 0;
        var reviewFindings = new List<ReviewFollowUpFinding>();
        if (payload.RootElement.TryGetProperty("reviewFindings", out var findings)
            && findings.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in findings.EnumerateArray())
            {
                findingCount++;
                if (finding.TryGetProperty("severity", out var severity)
                    && severity.ValueKind == JsonValueKind.String
                    && severity.GetString() is { } value)
                    severities.Add(value);
            }
        }
        if (payload.RootElement.TryGetProperty("verdicts", out var verdicts)
            && verdicts.ValueKind == JsonValueKind.Array)
        {
            foreach (var verdict in verdicts.EnumerateArray())
            {
                if (!verdict.TryGetProperty("status", out var status)
                    || status.ValueKind != JsonValueKind.String
                    || status.GetString() is not { } value
                    || value == "pass")
                    continue;
                findingCount++;
                reviewFindings.Add(new ReviewFollowUpFinding(
                    verdict.TryGetProperty("aspect", out var aspect) ? aspect.GetString() ?? "unknown" : "unknown",
                    value,
                    verdict.TryGetProperty("summary", out var summary) ? summary.GetString() : null,
                    Finding: verdict.TryGetProperty("missing", out var missing) ? missing.GetString() : null,
                    Classification: verdict.TryGetProperty("classification", out var classification)
                        ? classification.GetString() : null));
            }
        }
        var priorReview = run.StageResults?.LastOrDefault(result => result.Stage == OrchestrationStage.ReviewDecision);
        using var priorOutput = JsonDocument.Parse(priorReview?.OutputJson ?? "{}");
        var alreadyDecided = priorOutput.RootElement.TryGetProperty("transition", out var transition)
                             && transition.ValueKind == JsonValueKind.Object;
        var blockerCount = StageTransitionPolicy.CriticalFindingCount(severities);
        var action = StageTransitionPolicy.Council(reviewFindings, blockerCount, 0, 1, alreadyDecided);
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CouncilLoop),
                findingCount,
                blockerCount,
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
        var action = StageTransitionPolicy.Gate(failed);
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
                var commandText = string.Join(' ', commands.SelectMany(command =>
                    new[] { command.FileName }.Concat(command.Arguments))).ToLowerInvariant();
                var capabilities = new HashSet<string>(StringComparer.Ordinal)
                {
                    CapabilityProtocol.GitFetch, CapabilityProtocol.RepositoryAccess,
                };
                if (commandText.Contains("dotnet", StringComparison.Ordinal))
                    capabilities.Add(CapabilityProtocol.DotNet);
                if (commandText.Contains("npm", StringComparison.Ordinal)
                    || commandText.Contains("node", StringComparison.Ordinal)
                    || commandText.Contains("npx", StringComparison.Ordinal))
                    capabilities.Add(CapabilityProtocol.Node);
                if (commandText.Contains("playwright", StringComparison.Ordinal))
                    capabilities.Add(CapabilityProtocol.Playwright);
                // AGT-2981: a Compose-render step routes to a gate host that
                // can run `docker compose config`; the scripts also need node.
                if (ComposeRenderGatePolicy.IsRenderInvocation(commandText))
                {
                    capabilities.Add(CapabilityProtocol.ComposeRender);
                    capabilities.Add(CapabilityProtocol.Node);
                }
                var plan = new GatePlan("post-build-test-gate", 1, commands, "", 21600,
                    capabilities.Order(StringComparer.Ordinal).ToArray(),
                    32768, "always");
                static string Digest(string value) => Convert.ToHexString(
                    SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
                var planHash = Digest(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
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
                var remoteAction = StageTransitionPolicy.GateTerminal(gate.Phase);
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
        var action = StageTransitionPolicy.Completion(priorFailure);
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CompletionJudgeLoop),
                decision = action.ToString(),
            })));
    }
}
