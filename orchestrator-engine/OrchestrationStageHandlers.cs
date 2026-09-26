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
    internal static OrchestrationAction Decide(
        string? reviewOutcome,
        string? executionOutcome,
        string? terminal)
    {
        if (!string.IsNullOrWhiteSpace(reviewOutcome))
        {
            return Normalize(reviewOutcome) switch
            {
                "pass" => OrchestrationAction.Continue,
                "productfailure" => OrchestrationAction.Reissue,
                "reviewinfra" => OrchestrationAction.Escalate,
                _ => OrchestrationAction.Escalate,
            };
        }

        return Normalize(executionOutcome ?? terminal) switch
        {
            "blocked" => OrchestrationAction.Escalate,
            "needsinput" => OrchestrationAction.Reissue,
            _ => OrchestrationAction.Continue,
        };
    }

    private static string Normalize(string? value)
        => new((value ?? string.Empty)
            .Where(char.IsLetterOrDigit)
            .Select(char.ToLowerInvariant)
            .ToArray());
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
        var action = ReviewDecisionPolicy.Decide(reviewOutcome, executionOutcome, terminal);
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
        var blockerCount = 0;
        var findingCount = 0;
        if (payload.RootElement.TryGetProperty("reviewFindings", out var findings)
            && findings.ValueKind == JsonValueKind.Array)
        {
            foreach (var finding in findings.EnumerateArray())
            {
                findingCount++;
                if (finding.TryGetProperty("severity", out var severity)
                    && severity.ValueKind == JsonValueKind.String
                    && severity.GetString() is { } value
                    && value is "blocker" or "critical")
                    blockerCount++;
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
                var uncitedBlock = verdict.TryGetProperty("classification", out var classification)
                    && classification.ValueKind == JsonValueKind.String
                    && string.Equals(
                        classification.GetString(),
                        ReviewVerdictCitationPolicy.BlockWithoutCitation,
                        StringComparison.OrdinalIgnoreCase);
                if (uncitedBlock) continue;
                if (value is "concerns" or "block" or "fail")
                    blockerCount++;
            }
        }
        var action = blockerCount > 0
            ? OrchestrationAction.Reissue
            : OrchestrationAction.Continue;
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
        var action = failed > 0
            ? OrchestrationAction.Reissue
            : OrchestrationAction.Continue;
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
                var remoteAction = gate.Phase switch
                {
                    GateStates.Passed => OrchestrationAction.Continue,
                    GateStates.ProductFailed => OrchestrationAction.Reissue,
                    _ => OrchestrationAction.Escalate,
                };
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
        var action = priorFailure
            ? OrchestrationAction.Escalate
            : OrchestrationAction.Complete;
        return Task.FromResult(new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(CompletionJudgeLoop),
                decision = action.ToString(),
            })));
    }
}
