using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Anthropic;
using Anthropic.Models.Messages;

namespace AgentStudio.OrchestratorEngine;

/// <summary>
/// Model call of the batch advisor, made with the engine's own provider credential.
/// One provider serves one advisor CLI type of the project settings.
/// </summary>
public interface IBatchAdvisorModel
{
    string CliType { get; }
    bool Configured { get; }
    Task<string> AskAsync(string model, string prompt, CancellationToken ct);
}

public sealed class AnthropicBatchAdvisorModel(EngineOptions options) : IBatchAdvisorModel
{
    private readonly AnthropicClient? _client = options.AdvisorAnthropicCredential is { } credential
        ? new AnthropicClient { ApiKey = credential }
        : null;

    public string CliType => "claude";
    public bool Configured => _client is not null;

    public async Task<string> AskAsync(string model, string prompt, CancellationToken ct)
    {
        if (_client is null) throw new InvalidOperationException("engine advisor credential is not configured");
        var response = await _client.Messages.Create(new MessageCreateParams
        {
            Model = model,
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

/// <summary>OpenAI Responses call for the <c>codex</c> advisor CLI type.</summary>
public sealed class OpenAiBatchAdvisorModel : IBatchAdvisorModel
{
    private static readonly Uri Endpoint = new("https://api.openai.com/v1/responses");
    private readonly string? _credential;
    private readonly HttpClient _http;

    public OpenAiBatchAdvisorModel(EngineOptions options) : this(options, new HttpClient()) { }

    internal OpenAiBatchAdvisorModel(EngineOptions options, HttpClient http)
    {
        _credential = options.AdvisorOpenAiCredential;
        _http = http;
    }

    public string CliType => "codex";
    public bool Configured => _credential is not null;

    public async Task<string> AskAsync(string model, string prompt, CancellationToken ct)
    {
        if (_credential is null) throw new InvalidOperationException("engine advisor credential is not configured");
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = JsonContent.Create(new
            {
                model, input = prompt, max_output_tokens = 2048, reasoning = new { effort = "low" },
            }),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _credential);
        using var response = await _http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidDataException($"advisor provider returned HTTP {(int)response.StatusCode}");
        using var body = await JsonDocument.ParseAsync(await response.Content.ReadAsStreamAsync(ct), cancellationToken: ct);
        if (!body.RootElement.TryGetProperty("output", out var output) || output.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("advisor provider returned no output");
        return string.Concat(output.EnumerateArray()
            .Where(item => item.TryGetProperty("content", out var content) && content.ValueKind == JsonValueKind.Array)
            .SelectMany(item => item.GetProperty("content").EnumerateArray())
            .Where(part => part.TryGetProperty("type", out var type) && type.GetString() == "output_text")
            .Select(part => part.GetProperty("text").GetString()));
    }
}

/// <summary>
/// Periodic batch planning occurrence (AGT-3055). Runs the shared library
/// decision and, when the library asks for it, the advisor model call. The
/// authority validates the intent before it closes, holds or splits a batch.
/// </summary>
public sealed class BatchPlanLoop(IEnumerable<IBatchAdvisorModel> advisors, TimeProvider? time = null)
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

    /// <summary>
    /// Calls exactly the CLI type and model the occurrence names. A provider the engine
    /// cannot serve is a recorded fallback, never a silent switch to another model.
    /// </summary>
    private async Task<BatchAdvisorReply> AskAsync(BatchAdvisorRequest request,
        BatchPlanInput snapshot, BatchPlanDecision deterministic, CancellationToken ct)
    {
        var model = request.Model ?? "unresolved";
        if (request.Model is null)
            return new(null, model, _time.GetUtcNow(), "advisor request names no model");
        var advisor = advisors.FirstOrDefault(candidate =>
            string.Equals(candidate.CliType, request.CliType, StringComparison.OrdinalIgnoreCase));
        if (advisor is null)
            return new(null, model, _time.GetUtcNow(), $"engine has no advisor provider for '{request.CliType}'");
        if (!advisor.Configured)
            return new(null, model, _time.GetUtcNow(),
                $"engine advisor credential for '{request.CliType}' is not configured");
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(request.TimeoutSeconds));
        try
        {
            var prompt = request.Prompt.Replace(BatchPlanDecisionPolicy.EngineInputMarker,
                BatchPlanDecisionPolicy.AdvisorInput(snapshot, deterministic), StringComparison.Ordinal);
            var answer = await advisor.AskAsync(request.Model, prompt, deadline.Token);
            return new(answer, model, _time.GetUtcNow());
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            return new(null, model, _time.GetUtcNow(),
                exception is OperationCanceledException ? "advisor deadline" : exception.Message);
        }
    }
}
