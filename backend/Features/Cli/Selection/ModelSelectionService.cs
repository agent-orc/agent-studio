using AgentStudio.Pipeline;
using AgentStudio.Projects;
using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tokens;

namespace AgentStudio.Cli;

/// <summary>
/// One consumer's selection question plus the host facts only the caller
/// knows: occupied slots, the claiming host's model acceptance and a lazy
/// correctness floor. A <paramref name="Preview"/> call plans the same route
/// but writes no ledger receipt, so a read-only forecast never changes routing.
/// </summary>
public sealed record ModelSelectionCall(
    SelectionRequest Request,
    string ExecutionPath,
    int OccupiedSlots = 0,
    Func<string, string?, bool>? HostAccepts = null,
    Func<string, string?, bool>? HostAcceptsFallback = null,
    Func<ModelRoutingTier?>? CorrectnessFloor = null,
    string? DecisionKey = null,
    bool RecordEveryCall = true,
    bool Preview = false,
    Func<string?, QuotaSnapshot?>? SnapshotOverride = null)
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
                StepId = request.StepId,
                TaskKey = request.TaskKey,
                WorkClass = request.WorkClass,
                Pins = new SelectionPins(request.CliType, request.Model, request.ThinkingLevel, request.PinMode),
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
    bool RecordExecution(string? selectionId, string? provider = null, bool providerFailure = false) => false;
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
    private readonly ProviderRoutingHealth? _health;
    private readonly object _receiptSync = new();
    private readonly Dictionary<string, string> _receiptIds = new(StringComparer.Ordinal);

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
        ITokenPricing? pricing = null,
        ProviderRoutingHealth? health = null)
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
        _health = health;
    }

    public bool RecordExecution(string? selectionId, string? provider = null, bool providerFailure = false)
    {
        var added = selectionId is not null && _ledger?.RecordExecution(selectionId, providerFailure) == true;
        if ((added || selectionId is null) && !string.IsNullOrWhiteSpace(provider)) _health?.Observe(provider, providerFailure);
        return added;
    }

    public bool IsOperatorSwitchActive(string? cliType)
        => _preference?.Get(cliType ?? CliTypes.Claude).Active == true;

    public ModelSelectionDecision Select(ModelSelectionCall call)
    {
        ArgumentNullException.ThrowIfNull(call);
        var request = call.Request;
        if (request.PipelineType is null && request.TaskMode is not null)
            request = request with { PipelineType = PipelineTypes.Resolve(request.TaskType, request.TaskMode) };
        if (request.Consumer == SelectionConsumers.Persona)
            request = request with { Required = request.Required with { SameClassOnly = true } };
        var project = string.IsNullOrWhiteSpace(request.Project)
            ? null
            : _projectSettings.Get(request.Project);
        var step = PipelineCatalogue.FindStep(request.StepId);
        var stepSettings = PipelineTypeSettings.ForType(project, request.PipelineType)?.PipelineSteps
            ?.GetValueOrDefault(request.StepId ?? "");
        request = request with
        {
            WorkClass = request.WorkClass ?? step?.WorkClass ?? SelectionWorkClasses.ForConsumer(request.Consumer),
            IsExplicitPin = request.IsExplicitPin || step is not null || !string.IsNullOrWhiteSpace(stepSettings?.Model)
                || !string.IsNullOrWhiteSpace(stepSettings?.CliType),
            Pins = stepSettings?.PinMode == "hard" ? request.Pins with { Mode = SelectionPinMode.Hard } : request.Pins,
        };
        if (step?.WorkClass is SelectionWorkClasses.Review or SelectionWorkClasses.Grade)
            request = request with
            {
                Consumer = step.WorkClass == SelectionWorkClasses.Grade ? SelectionConsumers.ReviewGrade : SelectionConsumers.ReviewAspect,
                Required = request.Required with { SameClassOnly = true },
            };
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
        if (request.Continuation is not null) configuredFallback = null;
        var routing = _fallback.RoutingFacts();
        // Automatic alternatives must be published Token Economy pairs. Legacy
        // profiles still supply primary preferences, but cannot invent equivalence.
        var profile = routing.Profile;
        routing = routing with { Profile = cli => profile(cli) is { } preferredProfile
            ? preferredProfile with { FallbackCliType = null, FallbackModel = null, FallbackThinkingLevel = null }
            : null };
        if (configuredFallback is not null)
            configuredFallback = configuredFallback.Where(candidate =>
                routing.Equivalence?.Routes.Any(pair => pair.FromCliType == request.Pins.CliType
                    && pair.FromModel == request.Pins.Model && pair.FromThinkingLevel == request.Pins.ThinkingLevel
                    && pair.ToCliType == candidate.CliType && pair.ToModel == candidate.Model
                    && pair.ToThinkingLevel == candidate.ThinkingLevel) == true).ToArray();
        var now = _timeProvider.GetUtcNow().UtcDateTime;
        var active = _ledger?.GetActive(request);
        // Freeze account evidence once per provider for this decision. Display
        // rows can contain old probe errors and are never routing facts.
        var quotaEvidence = new Dictionary<string, QuotaRoutingEvidence>(StringComparer.OrdinalIgnoreCase);
        QuotaRoutingEvidence EvidenceFor(string cli)
        {
            if (!quotaEvidence.TryGetValue(cli, out var evidence))
                quotaEvidence[cli] = evidence = _quota.GetRoutingEvidence(cli);
            return evidence;
        }
        string RecoveryAction(string cli)
            => $"No current {cli} account quota evidence. On an execution host, sign in to the current account and refresh quota";
        string? Unavailable(string cli) => _health?.UnavailableReason(cli)
            ?? (call.SnapshotOverride is null && EvidenceFor(cli).Kind == QuotaRoutingEvidenceKind.Missing
                ? RecoveryAction(cli) : null);
        var facts = new SelectionFacts
        {
            NowUtc = now,
            Caps = _caps,
            ShortStepReservePoints = workspacePolicy?.ShortStepReservePoints ?? 10,
            ProviderUnavailable = Unavailable,
            SnapshotFor = call.SnapshotOverride ?? (cli => string.IsNullOrWhiteSpace(cli) ? null : EvidenceFor(cli).Snapshot),
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
        // Missing account evidence has an explicit recovery action. It must not
        // renew a provider-unavailable wait on every pickup tick. The planner
        // still chooses any usable equivalent before this recovery is surfaced.
        if (call.SnapshotOverride is null && result.Outcome == SelectionOutcome.Wait
            && result.Rule is SelectionRules.Capacity or SelectionRules.Pin
            && EvidenceFor(result.Primary.CliType).Kind == QuotaRoutingEvidenceKind.Missing
            && _health?.UnavailableReason(result.Primary.CliType) is null)
            result = result with
            {
                Outcome = SelectionOutcome.Refused,
                Reason = $"Recovery required: {RecoveryAction(result.Primary.CliType)}; then retry selection. No permitted equivalent is available.",
                RetryAt = null,
            };
        result = result with { EquivalentRoutes = routing.Equivalence?.Routes.Where(pair =>
            pair.FromCliType == result.Primary.CliType && pair.FromModel == result.Primary.Model
            && pair.FromThinkingLevel == result.Primary.ThinkingLevel
            && (!request.Required.SameClassOnly || routing.Equivalence.SameCapabilityClass(pair.FromModel, pair.ToModel)))
            .Select(pair => new SelectionRoute(pair.ToCliType, pair.ToModel, pair.ToThinkingLevel)).Distinct().ToArray() ?? [] };
        var entry = result.Outcome == SelectionOutcome.Primary && active is not null
            && (request.Continuation is null || active.From == result.Route)
            ? new SelectionLedgerEntry(
                ModelSelectionLedger.NewId(), now,
                SelectionLedgerKinds.Returned, request.Consumer, request.Host?.HostId,
                request.Project, request.PipelineType, request.StepId, request.TaskKey,
                request.AttemptId, request.Round, call.ExecutionPath, active.To, result.Route,
                active.Rule, "Returned to the primary route after the quota return rule cleared.",
                result.Quota, result.Equivalence, null, null) { ClosesId = active.Id }
            : result.Outcome == SelectionOutcome.Primary && call.RecordEveryCall
                ? new SelectionLedgerEntry(ModelSelectionLedger.NewId(), now,
                    SelectionLedgerKinds.Selected, request.Consumer, request.Host?.HostId,
                    request.Project, request.PipelineType, request.StepId, request.TaskKey,
                    request.AttemptId, request.Round, call.ExecutionPath, result.Primary, result.Route,
                    result.Rule, result.Reason, result.Quota, result.Equivalence, null, null)
            : result.Outcome == SelectionOutcome.Fallback && active?.To == result.Route
                ? call.RecordEveryCall
                    ? new SelectionLedgerEntry(ModelSelectionLedger.NewId(), now,
                        SelectionLedgerKinds.Selected, request.Consumer, request.Host?.HostId,
                        request.Project, request.PipelineType, request.StepId, request.TaskKey,
                        request.AttemptId, request.Round, call.ExecutionPath, result.Primary, result.Route,
                        result.Rule, result.Reason, result.Quota, result.Equivalence, null, null)
                    : null
                : SelectionLedgerEntry.For(
            request, result, call.ExecutionPath,
            ModelSelectionLedger.NewId(), now);
        if (entry is not null && _ledger is not null && !call.Preview)
        {
            entry = entry with
            {
                FromPricePerMTok = facts.PricePerMTok(entry.From.Model),
                ToPricePerMTok = facts.PricePerMTok(entry.To.Model),
            };
            try
            {
                if (string.IsNullOrWhiteSpace(call.DecisionKey))
                {
                    _ledger.Record(entry);
                    result = result with { LedgerId = entry.Id };
                }
                else
                {
                    // Preflight and dispatch can select the same remote claim twice.
                    var key = string.Join("|", call.ExecutionPath, call.DecisionKey,
                        request.Project, request.TaskKey, request.Host?.HostId,
                        result.Outcome, result.Rule, result.Primary, result.Route);
                    lock (_receiptSync)
                    {
                        if (!_receiptIds.TryGetValue(key, out var id))
                        {
                            _ledger.Record(entry);
                            id = entry.Id;
                            if (_receiptIds.Count >= 4096) _receiptIds.Clear();
                            _receiptIds[key] = id;
                        }
                        result = result with { LedgerId = id };
                    }
                }
            }
            catch (Exception ex)
            {
                // Recording must not change an already planned launch route.
                SilentCatch.Note(ex, "ModelSelectionService: selection receipt write failed");
            }
        }
        else if (entry is null && result.Outcome == SelectionOutcome.Fallback
                 && active?.To == result.Route)
        {
            result = result with { LedgerId = active.Id };
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
