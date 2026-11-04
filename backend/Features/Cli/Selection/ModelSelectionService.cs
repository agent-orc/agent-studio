using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

namespace AgentStudio.Cli;

/// <summary>
/// One consumer's selection question plus the host facts only the caller
/// knows: occupied slots, the claiming host's model acceptance and a lazy
/// correctness floor.
/// </summary>
public sealed record ModelSelectionCall(
    SelectionRequest Request,
    string ExecutionPath,
    int OccupiedSlots = 0,
    Func<string, string?, bool>? HostAccepts = null,
    Func<string, string?, bool>? HostAcceptsFallback = null,
    Func<ModelRoutingTier?>? CorrectnessFloor = null)
{
    /// <summary>The selection call behind a legacy admission request (adapter until MS-6).</summary>
    public static ModelSelectionCall FromAdmission(QuotaAdmissionRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return new ModelSelectionCall(
            new SelectionRequest
            {
                Consumer = ConsumerFor(request.ExecutionPath, request.SameClassOnly),
                Project = request.ProjectName,
                // A remote review claim rechecks quota for a route already frozen
                // in its ReviewAttempt. Consumer defaults must not replace it.
                IsExplicitPin = request.ExecutionPath == "remote-review-claim",
                Pins = new SelectionPins(request.CliType, request.Model, request.ThinkingLevel),
                Required = new SelectionRequirements(SameClassOnly: request.SameClassOnly),
                Host = request.ClaimingHost is null ? null : new SelectionHost(request.ClaimingHost),
            },
            request.ExecutionPath,
            request.OccupiedSlots,
            request.ModelAccepted,
            request.FallbackModelAccepted);
    }

    private static string ConsumerFor(string executionPath, bool sameClassOnly) => executionPath switch
    {
        "local-coding-launch" or "remote-coding-claim" or "remote-model-rejection" => SelectionConsumers.Coding,
        "merge-resolver" => SelectionConsumers.MergeResolver,
        "remote-project-chat" => SelectionConsumers.Chat,
        "card-creation" => SelectionConsumers.CardCreation,
        _ when sameClassOnly => SelectionConsumers.ReviewAspect,
        _ => SelectionConsumers.OneShot(executionPath),
    };
}

/// <summary>The shared planner result and its legacy admission view, which the existing launch code applies.</summary>
public sealed record ModelSelectionDecision(SelectionResult Result, QuotaAdmissionPlan Admission);

/// <summary>
/// The selection boundary every CLI launch path asks before it starts a
/// route (Dossier model-selection-and-fallback, MS-5). Tests replace it with
/// a spy to prove a consumer goes through it.
/// </summary>
public interface IModelSelectionService
{
    ModelSelectionDecision Select(ModelSelectionCall call);

    bool IsOperatorSwitchActive(string? cliType);
}

/// <summary>Collects the selection facts, asks the pure planner and writes the ledger.</summary>
public sealed class ModelSelectionService : IModelSelectionService
{
    private readonly QuotaService _quota;
    private readonly CliQuotaCapsService _caps;
    private readonly CliQuotaFallbackService _fallback;
    private readonly CliQuotaWaitPolicyService _waitPolicy;
    private readonly ProjectSettingsService _projectSettings;
    private readonly BetterCandidateService _betterCandidates;
    private readonly CliFallbackPreferenceService? _preference;
    private readonly ModelRoutingPolicyRegistry? _routing;
    private readonly IModelSelectionLedger? _ledger;
    private readonly ModelSelectionPolicyStore? _policy;
    private readonly TimeProvider _timeProvider;
    private readonly ITokenPricing? _pricing;

    public ModelSelectionService(
        QuotaService quota,
        CliQuotaCapsService caps,
        CliQuotaFallbackService fallback,
        CliQuotaWaitPolicyService waitPolicy,
        ProjectSettingsService projectSettings,
        TimeProvider? timeProvider = null,
        BetterCandidateService? betterCandidates = null,
        CliFallbackPreferenceService? preference = null,
        ModelRoutingPolicyRegistry? routing = null,
        IModelSelectionLedger? ledger = null,
        ModelSelectionPolicyStore? policy = null,
        ITokenPricing? pricing = null)
    {
        _quota = quota;
        _caps = caps;
        _fallback = fallback;
        _waitPolicy = waitPolicy;
        _projectSettings = projectSettings;
        _betterCandidates = betterCandidates ?? new BetterCandidateService();
        _preference = preference;
        _routing = routing;
        _ledger = ledger;
        _policy = policy;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _pricing = pricing;
    }

    public bool IsOperatorSwitchActive(string? cliType)
        => _preference?.Get(cliType ?? CliTypes.Claude).Active == true;

    public ModelSelectionDecision Select(ModelSelectionCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var request = call.Request;
        var project = string.IsNullOrWhiteSpace(request.Project)
            ? null
            : _projectSettings.Get(request.Project);
        var consumer = request.Consumer.StartsWith("one-shot:", StringComparison.Ordinal)
            ? "one-shot" : request.Consumer;
        var workspacePolicy = _policy?.Get();
        var consumerPolicy = workspacePolicy?.Consumers.GetValueOrDefault(consumer);
        var projectOverride = project?.ModelSelectionOverrides?.GetValueOrDefault(consumer);
        projectOverride = projectOverride?.ByPipelineType?.GetValueOrDefault(
            request.PipelineType ?? PipelineTypes.Task) ?? projectOverride;
        if (request.StepId is not null)
            projectOverride = projectOverride?.ByStep?.GetValueOrDefault(request.StepId) ?? projectOverride;
        var preferred = projectOverride?.Primary ?? consumerPolicy?.Primary?.ToSelectionRoute();
        if (!request.IsExplicitPin && preferred is not null)
            request = request with { Pins = new SelectionPins(preferred.CliType, preferred.Model,
                preferred.ThinkingLevel, request.Pins.Mode) };
        if (projectOverride?.PinMode == "hard")
            request = request with { Pins = request.Pins with { Mode = SelectionPinMode.Hard } };
        IReadOnlyList<SelectionRoute>? configuredFallback = projectOverride?.Fallbacks
            ?? (request.IsExplicitPin ? null
                : consumerPolicy?.Fallbacks?.Routes.Select(route => route.ToSelectionRoute()).ToArray());
        var explicitKey = $"{request.Pins.Model}:{request.Pins.ThinkingLevel}";
        var modelFallbacks = _policy?.Get().ModelFallbacks;
        if (modelFallbacks is not null && modelFallbacks.TryGetValue(explicitKey, out var explicitFallback))
            configuredFallback = [explicitFallback];
        var routing = _fallback.RoutingFacts();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var active = _ledger?.GetActive(request);
        var facts = new SelectionFacts
        {
            NowUtc = now,
            Caps = _caps,
            SnapshotFor = cli => string.IsNullOrWhiteSpace(cli) ? null : _quota.GetCachedFor(cli),
            Routing = routing,
            ConfiguredFallbacks = configuredFallback,
            OccupiedSlots = call.OccupiedSlots,
            WaitPolicy = _waitPolicy.Resolve(project),
            OperatorSwitch = _preference?.Get(request.Pins.CliType ?? CliTypes.Claude),
            FallbackProviderAllowed = cli => _preference?.Get(cli).Active != true,
            HostAccepts = call.HostAccepts,
            HostAcceptsFallback = call.HostAcceptsFallback,
            Tiers = _routing?.Policy.Tiers ?? [],
            NormalizeModelId = ModelMetadataRegistry.NormalizeId,
            CorrectnessFloor = call.CorrectnessFloor,
            ActiveSwitch = active,
            ReturnBelowCapPoints = workspacePolicy?.Hysteresis.ReturnBelowCapPoints ?? 10,
            MinDwellMinutes = workspacePolicy?.Hysteresis.MinDwellMinutes ?? 30,
            CostGuardFactor = consumerPolicy?.CostGuardFactor ?? workspacePolicy?.CostGuardFactor ?? 2.0,
            PricePerMTok = model => string.IsNullOrWhiteSpace(model) ? null
                : _pricing?.Estimate(model, 0, 0, 0, 0).PriceBasis is { } price
                    ? price.InputPerMillion + price.OutputPerMillion : null,
        };

        var result = ModelSelectionPlanner.Plan(request, facts);
        var entry = result.Outcome == SelectionOutcome.Primary && active is not null
            ? new SelectionLedgerEntry(
                Guid.CreateVersion7(new DateTimeOffset(now, TimeSpan.Zero)).ToString("N"), now,
                SelectionLedgerKinds.Returned, request.Consumer, request.Host?.HostId,
                request.Project, request.PipelineType, request.StepId, request.TaskKey,
                request.AttemptId, request.Round, call.ExecutionPath, active.To, result.Route,
                active.Rule, "Returned to the primary route after the quota return rule cleared.",
                result.Quota, result.Equivalence, null, null) { ClosesId = active.Id }
            : result.Outcome == SelectionOutcome.Fallback && active?.To == result.Route
                ? null
                : SelectionLedgerEntry.For(
            request, result, call.ExecutionPath,
            Guid.CreateVersion7(new DateTimeOffset(now, TimeSpan.Zero)).ToString("N"), now);
        if (entry is not null && _ledger is not null)
        {
            _ledger.Record(entry);
            result = result with { LedgerId = entry.Id };
        }

        var admission = QuotaAdmissionPlan.From(result) with
        {
            BetterCandidates = _betterCandidates.FindRoute(
                result.Route.Model,
                result.Route.ThinkingLevel,
                project?.BenchmarkCapabilityClass),
        };
        return new ModelSelectionDecision(result, admission);
    }
}
