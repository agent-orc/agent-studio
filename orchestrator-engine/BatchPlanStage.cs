using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Anthropic;
using Anthropic.Models.Messages;

namespace AgentStudio.OrchestratorEngine;

/// <summary>Model call of the batch advisor, made with the engine's own provider credential.</summary>
public interface IBatchAdvisorModel
{
    string Model { get; }
    bool Configured { get; }
    Task<string> AskAsync(string prompt, CancellationToken ct);
}

public sealed class AnthropicBatchAdvisorModel(EngineOptions options) : IBatchAdvisorModel
{
    private readonly AnthropicClient? _client = options.AdvisorCredential is { } credential
        ? new AnthropicClient { ApiKey = credential }
        : null;

    public string Model => options.AdvisorModel;
    public bool Configured => _client is not null;

    public async Task<string> AskAsync(string prompt, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("engine advisor credential is not configured");
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = options.AdvisorModel,
            MaxTokens = 2048,
            OutputConfig = new OutputConfig { Effort = Effort.Low },
            Messages = [new() { Role = Role.User, Content = prompt }],
        }, ct);
        if (response.StopReason == "refusal")
            throw new InvalidDataException("advisor model declined the request");
        return string.Concat(response.Content.Select(block => block.Value).OfType<TextBlock>()
            .Select(block => block.Text));
    }
}

/// <summary>
/// Periodic batch planning occurrence (AGT-3055). Runs the shared library
/// decision and, when the library asks for it, the advisor model call. The
/// authority validates the intent before it closes, holds or splits a batch.
/// </summary>
public sealed class BatchPlanLoop(IBatchAdvisorModel advisor, TimeProvider? time = null)
    : IOrchestrationStageHandler
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    public OrchestrationStage Stage => OrchestrationStage.BatchPlan;

    public async Task<OrchestrationStageDecision> ExecuteAsync(OrchestrationRunDto run, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        var payload = JsonSerializer.Deserialize<BatchPlanOccurrencePayload>(run.PayloadJson, Json)
            ?? throw new InvalidDataException("Batch plan occurrence payload is missing.");
        var step = BatchPlanDecisionPolicy.Decide(payload.Facts);
        var decision = step.Decision;
        if (step.ConsultAdvisor && payload.Advisor is { } request)
            decision = BatchPlanDecisionPolicy.Advise(payload.Facts, decision,
                await AskAsync(request, payload.Facts.Snapshot, decision, ct));
        return new OrchestrationStageDecision(OrchestrationAction.Continue, JsonSerializer.Serialize(new
        {
            component = nameof(BatchPlanLoop),
            batchPlan = decision,
            rule = step.Rule,
            decider = $"orchestration-decisions:{EngineVersion.DecisionLibraryVersion}:engine",
            decision = BatchPlanDecisionPolicy.Signature(decision),
        }, Json));
    }

    private async Task<BatchAdvisorReply> AskAsync(BatchAdvisorRequest request,
        BatchPlanInput snapshot, BatchPlanDecision deterministic, CancellationToken ct)
    {
        if (!advisor.Configured)
            return new(null, advisor.Model, _time.GetUtcNow(), "engine advisor credential is not configured");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            var prompt = request.Prompt.Replace(BatchPlanDecisionPolicy.EngineInputMarker,
                BatchPlanDecisionPolicy.AdvisorInput(snapshot, deterministic), StringComparison.Ordinal);
            var answer = await advisor.AskAsync(prompt, deadline.Token);
            return new(answer, advisor.Model, _time.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(null, advisor.Model, _time.GetUtcNow(),
                exception is OperationCanceledException ? "advisor deadline" : exception.Message);
        }
    }
}
