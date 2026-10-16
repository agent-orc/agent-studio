using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.Host;
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

    private int _batchPlanRefusalLogged;

    /// <summary>
    /// Effective owner of the <c>BatchPlan</c> transition. Engine and Shadow
    /// ownership are opt-in through <see cref="MonolithTransitionSwitch"/>. With
    /// <c>TaskServer:BaseUrl</c> set, <c>/api/v1/orchestration</c> is proxied to
    /// the Task Server, where the engine claims occurrences; batch-plan
    /// occurrences live in the monolith store and would never be claimed, so
    /// engine ownership is refused and the monolith keeps the decision.
    /// </summary>
    public static OrchestrationExecutionMode OwnerOfBatchPlan(IConfiguration? configuration, out string? refusal)
    {
        refusal = null;
        if (configuration is null) return OrchestrationExecutionMode.Monolith;
        var requested = MonolithTransitionSwitch.For(configuration, BatchPlanProfile);
        if (requested == OrchestrationExecutionMode.Monolith || !TaskServerPlaneProxy.IsConfigured(configuration))
            return requested;
        refusal = BatchPlanProxyRefusal(requested);
        return OrchestrationExecutionMode.Monolith;
    }

    /// <summary>Operator-facing reason the batch decision stays in the monolith behind the Task Server proxy.</summary>
    public static string BatchPlanProxyRefusal(OrchestrationExecutionMode requested)
        => $"{BatchPlanProfile} {requested} ownership is refused because TaskServer:BaseUrl proxies "
           + "/api/v1/orchestration to the Task Server, where the engine cannot claim batch-plan occurrences. "
           + "The monolith keeps the batch decision; unset TaskServer:BaseUrl or set "
           + $"Orchestration:Transitions:{BatchPlanProfile}=Monolith.";

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
        var owner = OwnerOfBatchPlan(_configuration, out var refusal);
        if (refusal is not null && Interlocked.Exchange(ref _batchPlanRefusalLogged, 1) == 0)
            _logger.LogError("batch-plan-engine-ownership-refused: {Refusal}", refusal);
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
        var step = AgentStudio.Runner.MonolithTransitionAdapter.BatchPlan(facts);
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
            else if (MonolithTransitionAdapter.BatchPlanDecisionPolicyAdmissible(facts, proposed.Decision, out var error))
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
        var signature = MonolithTransitionAdapter.BatchPlanDecisionPolicySignature(decision);
        if (decision.Decision is not ("close" or "per-task") && facts.PriorDecision is { } prior
            && MonolithTransitionAdapter.BatchPlanDecisionPolicySignature(prior) == signature)
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
