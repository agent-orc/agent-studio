using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TaskServer.Contracts.BatchGate;
using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.Pipeline;

/// <summary>
/// Single owner of the look-ahead batch decision (AGT-3055, D14 A). In
/// Monolith mode the tick runs the shared library; in Shadow mode it also
/// records an engine occurrence for comparison; in Engine mode it only
/// publishes facts and applies the intent the engine returns after the
/// authority floors in <see cref="BatchPlanDecisionPolicy.Admissible"/>.
/// </summary>
public sealed partial class BatchGatePilotService
{
    public const string BatchPlanProfile = "BatchPlan";
    private const string BatchPlanSourceState = "batch-plan";
    private static readonly TimeSpan EngineIntentLifetime = TimeSpan.FromMinutes(5);
    private static readonly JsonSerializerOptions PlanJson = new(JsonSerializerDefaults.Web);
    private readonly ConcurrentDictionary<string, EngineBatchPlan> _engineBatchPlans =
        new(StringComparer.Ordinal);

    private sealed record EngineBatchPlan(string RunId, BatchPlanDecision Decision, DateTimeOffset AtUtc);

    /// <summary>Occurrence task id of one project scope; it never names a card.</summary>
    public static string BatchPlanTaskId(string scopeKey) => "batch-plan:" + scopeKey;

    /// <summary>Records an applied engine intent. The next tick validates it against current facts.</summary>
    public void AcceptEngineBatchPlan(string runId, string taskId, BatchPlanDecision decision)
        => _engineBatchPlans[taskId] = new EngineBatchPlan(runId, decision, _time.GetUtcNow());

    private async Task<BatchPlanDecision?> PlanLookAheadAsync(string project, string scopeKey,
        BatchPlanInput snapshot, CancellationToken ct)
    {
        var settings = _settings.Get(project);
        var advisorMode = (settings.BatchGate.Advisor ?? new BatchGateAdvisorOptions()).Mode;
        var owner = _configuration is null
            ? OrchestrationExecutionMode.Monolith
            : MonolithTransitionSwitch.For(_configuration, BatchPlanProfile);
        var occurrences = _services?.GetService<MonolithOrchestrationStore>();
        var prior = _store.LatestPlanDecision(project);
        if (owner == OrchestrationExecutionMode.Engine)
        {
            if (occurrences is null)
            {
                _logger.LogWarning("batch-plan-engine-store-unavailable project={Project}", project);
                return null;
            }
            return await TakeEngineDecisionAsync(occurrences, settings,
                new BatchPlanFacts(project, scopeKey, snapshot, prior, advisorMode), ct).ConfigureAwait(false);
        }
        var facts = new BatchPlanFacts(project, scopeKey, snapshot, prior,
            _advisor is null ? "off" : advisorMode);
        var step = BatchPlanDecisionPolicy.Decide(facts);
        var decision = step.ConsultAdvisor
            ? await _advisor!.AdviseAsync(project, settings, snapshot, step.Decision, ct)
                .ConfigureAwait(false)
            : step.Decision;
        if (owner == OrchestrationExecutionMode.Shadow && occurrences is not null)
            await RecordShadowBatchPlanAsync(occurrences, settings, facts, decision, ct).ConfigureAwait(false);
        return decision;
    }

    private async Task<BatchPlanDecision?> TakeEngineDecisionAsync(MonolithOrchestrationStore occurrences,
        ProjectSettings settings, BatchPlanFacts facts, CancellationToken ct)
    {
        var taskId = BatchPlanTaskId(facts.ScopeKey);
        if (_engineBatchPlans.TryRemove(taskId, out var proposed))
        {
            if (_time.GetUtcNow() - proposed.AtUtc > EngineIntentLifetime)
                _logger.LogWarning("batch-plan-engine-intent-expired project={Project} run={RunId}",
                    facts.Project, proposed.RunId);
            else if (BatchPlanDecisionPolicy.Admissible(facts, proposed.Decision, out var error))
                return proposed.Decision;
            else
                _logger.LogWarning(
                    "batch-plan-engine-intent-rejected project={Project} run={RunId} reason={Reason}",
                    facts.Project, proposed.RunId, error);
        }
        if (occurrences.List(facts.Project, null).Any(run => run.TaskId == taskId
                && run.CurrentStage == OrchestrationStage.BatchPlan
                && run.Status is "pending" or "leased" or "applying"))
            return null;
        var payload = await OccurrencePayloadAsync(settings, facts, ct).ConfigureAwait(false);
        occurrences.Create(facts.Project, taskId, OrchestrationStage.BatchPlan, payload,
            $"{taskId}:{Digest(payload)}", BatchPlanSourceState, taskId, 0, 0,
            profile: BatchPlanProfile);
        return null;
    }

    /// <summary>
    /// Shadow receipts cover every changed decision and every effectful one;
    /// repeated identical waits would only add identical comparisons.
    /// </summary>
    private async Task RecordShadowBatchPlanAsync(MonolithOrchestrationStore occurrences,
        ProjectSettings settings, BatchPlanFacts facts, BatchPlanDecision decision, CancellationToken ct)
    {
        var signature = BatchPlanDecisionPolicy.Signature(decision);
        if (decision.Decision is not ("close" or "per-task") && facts.PriorDecision is { } prior
            && BatchPlanDecisionPolicy.Signature(prior) == signature)
            return;
        try
        {
            var taskId = BatchPlanTaskId(facts.ScopeKey);
            var payload = await OccurrencePayloadAsync(settings, facts, ct).ConfigureAwait(false);
            occurrences.Create(facts.Project, taskId, OrchestrationStage.BatchPlan, payload,
                $"{taskId}:{Digest(payload)}", BatchPlanSourceState, taskId, 0, 0,
                profile: BatchPlanProfile, shadow: true,
                monolithAction: signature, monolithReason: decision.Reason);
        }
        catch (Exception exception) when (exception is not OperationCanceledException || !ct.IsCancellationRequested)
        {
            _logger.LogWarning(exception, "batch-plan-shadow-record-failed project={Project}", facts.Project);
        }
    }

    private async Task<string> OccurrencePayloadAsync(ProjectSettings settings, BatchPlanFacts facts,
        CancellationToken ct)
    {
        var advisor = _advisor is null || facts.AdvisorMode == "off"
            ? null
            : await _advisor.EngineRequestAsync(facts.Project, settings, ct).ConfigureAwait(false);
        return JsonSerializer.Serialize(new BatchPlanOccurrencePayload(facts, advisor), PlanJson);
    }

    private static string ScopeKey<T>(T groupKey)
        => Digest(groupKey?.ToString() ?? string.Empty)[..16];

    private static string Digest(string value)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
}
