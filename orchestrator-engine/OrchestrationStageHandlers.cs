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
    private readonly EngineOptions? _options;
    private readonly EngineTaskServerClient? _client;

    public GateDispatchLoop() { }

    public GateDispatchLoop(EngineOptions options, EngineTaskServerClient client)
    {
        _options = options;
        _client = client;
    }

    public OrchestrationStage Stage => OrchestrationStage.GateDispatch;

    public async Task<OrchestrationStageDecision> ExecuteAsync(
        OrchestrationRunDto run,
        CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var payload = JsonDocument.Parse(run.PayloadJson);
        if (_options?.RemoteGateEnabled == true)
            return await DispatchRemoteAsync(run, payload.RootElement, ct);
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
        return new OrchestrationStageDecision(
            action,
            JsonSerializer.Serialize(new
            {
                component = nameof(GateDispatchLoop),
                failed,
                decision = action.ToString(),
            }));
    }

    private async Task<OrchestrationStageDecision> DispatchRemoteAsync(
        OrchestrationRunDto run, JsonElement payload, CancellationToken ct)
    {
        var reviewSubjectId = payload.TryGetProperty("reviewSubjectId", out var id)
            ? id.GetString() : null;
        if (string.IsNullOrWhiteSpace(reviewSubjectId))
            return new OrchestrationStageDecision(OrchestrationAction.Escalate,
                "{\"classification\":\"MissingGateSubject\"}");
        var review = await _client!.GetReviewSubjectAsync(reviewSubjectId, ct);
        var commands = review.Plan.Commands
            .Where(command => command.ExecutionKind == ReviewCommandKinds.Tool
                && command.Aspect == "build-tests")
            .Select(command => new GateCommand(command.StepId, command.FileName,
                command.Arguments, "", command.TimeoutSeconds))
            .ToArray();
        if (commands.Length == 0)
            return new OrchestrationStageDecision(OrchestrationAction.Escalate,
                "{\"classification\":\"NoCataloguedBuildTestCommands\"}");
        var required = commands.SelectMany(command =>
            command.Arguments.Concat([command.FileName]))
            .SelectMany(arg => arg.Contains("dotnet", StringComparison.OrdinalIgnoreCase)
                ? [CapabilityProtocol.DotNet]
                : arg.Contains("npm", StringComparison.OrdinalIgnoreCase)
                    || arg.Contains("node", StringComparison.OrdinalIgnoreCase)
                    ? [CapabilityProtocol.Node]
                    : Array.Empty<string>())
            .Distinct(StringComparer.Ordinal).ToArray();
        var plan = new GatePlan("post-build-test-gate", 1, commands, "",
            Math.Min(14400, commands.Sum(command => command.DeadlineSeconds) + 300),
            required, 500_000, "always");
        var planHash = Digest(JsonSerializer.Serialize(plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var auditDigest = Digest(JsonSerializer.Serialize(new
        {
            review.Plan.Commands,
            review.Plan.BuildProfileFingerprint,
        }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var subject = await _client.CreateGateSubjectAsync(new CreateGateSubjectRequest(
            review.TaskId, review.SourceRunId, review.RepositoryId,
            review.RepositoryUrl, review.ExpectedResultSha,
            review.ResultRef, review.SourceBundleArtifactId, review.SourceBundleSha256,
            planHash, review.ReviewPolicyHash,
            checked((int)run.DefinitionVersion), auditDigest, plan,
            DateTime.UtcNow.AddMinutes(15)), ct);
        var status = await _client.GetGateStatusAsync(subject.SubjectId, ct);
        var latest = status.Attempts.Last().Attempt;
        if (!GateStates.IsTerminal(latest.State))
            throw new GatePendingException(subject.SubjectId, latest.State);
        var action = latest.State switch
        {
            GateStates.Passed => OrchestrationAction.Continue,
            GateStates.ProductFailed => OrchestrationAction.Reissue,
            _ => OrchestrationAction.Escalate,
        };
        return new OrchestrationStageDecision(action,
            JsonSerializer.Serialize(new
            {
                component = nameof(GateDispatchLoop),
                gateSubjectId = subject.SubjectId,
                state = latest.State,
                outcome = latest.Outcome,
                classification = latest.FailureClassification,
                testedSha = status.TestedSha,
                decision = action.ToString(),
            }));
    }

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}

public sealed class GatePendingException(string subjectId, string phase)
    : Exception($"Gate {subjectId} is {phase}.");

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
