using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.Projects;

namespace AgentStudio.Dispatch;

/// <summary>Collect once, freeze once, evaluate all active dispatchers against identical project facts.</summary>
public sealed class DispatcherShadowService(DispatchStore store, PersonaActivationStore activations,
    ProjectRegistry projects, TaskIndexCache tasks, ProjectSettingsService settings, TimeProvider time,
    ILogger<DispatcherShadowService> logger, AgentStudio.Tasks.TaskListGitProjectionCache? gitProjection = null,
    DispatchLeaseScheduler? scheduler = null)
{
    public DispatchShadowRecordV1? RecordPick(TaskInfo candidate, IReadOnlyList<RunningTask> running,
        IReadOnlyList<InFlightTask> inFlight, string path, IReadOnlyList<string>? pickupCandidates = null, string? planningWave = null)
    {
        try
        {
            var project = projects.FindByIdOrDisplayName(candidate.ProjectName);
            if (project is null || settings.Get(candidate.ProjectName).DispatchMode == "off") return null;
            if (scheduler is not null && settings.Get(candidate.ProjectName).DispatchMode == "live"
                && scheduler.Refresh(candidate.ProjectName, planningWave ?? candidate.WaveId) is null) return null;
            var active = activations.List(project.WorkspaceId).Personas.Select(p => p.Activation)
                .OfType<PersonaActivationV1>().Where(a => a.Descriptor.Kind == PersonaKinds.Dispatcher
                    && a.State is PersonaStates.Active or PersonaStates.Shadow
                    && (a.Operations.Contains("dispatch.shadow.record") || a.Operations.Contains("dispatch.plan.write"))
                    && (a.Scope.Projects.Contains("*") || a.Scope.Projects.Contains(project.Id)
                        || a.Scope.Projects.Contains(project.DisplayName))).ToArray();
            if (active.Length == 0 && store.ReadLease(candidate.ProjectName)?.Rollout is null) return null;
            var cards = tasks.GetAllResidentTasks().Where(c => !c.Fixture).ToArray();
            var referenceIndex = tasks.GetCoreReferenceIndex();
            var config = settings.Get(candidate.ProjectName);
            var now = time.GetUtcNow();
            var priors = new Dictionary<string, int>(StringComparer.Ordinal);
            var positions = cards.Where(c => c.ProjectName == candidate.ProjectName && c.WaveId is not null)
                .GroupBy(c => c.WaveId).SelectMany(g => g.OrderBy(c => c.CreatedAt).ThenBy(c => c.Key, StringComparer.Ordinal)
                    .Select((c, position) => (c.TaskKey, Position: position))).ToDictionary(c => c.TaskKey, c => c.Position);
            var integration = gitProjection?.ReadDependencyIntegration(cards);
            var ready = cards.Where(c => c.ProjectName.Equals(candidate.ProjectName, StringComparison.OrdinalIgnoreCase)
                && (c.State == TaskStates.Ready || c.Id == candidate.Id && c.State == TaskStates.Progress
                    || planningWave is not null && c.State == TaskStates.Backlog && c.WaveId == planningWave) && AgentTypes.IsAutoPickupEligible(c.Agent)
                && !c.WaitingForEngineDecision
                && (c.WaveId is null || c.WaveId == planningWave
                    || store.ReadWaveRelease(c.ProjectName, c.WaveId)?.Complete == true)).Select(c =>
                {
                    var scope = Predict(c);
                    return new DispatchReadyCard(c.Key ?? c.TaskKey, c.Order, c.EnteredLaneAt,
                        c.References?.DependsOn.Select(e => e.Key).ToArray() ?? [],
                        !referenceIndex.EvaluateWaitsOn(c, integration, config.IntegrationBranch).Blocked, Forecast(c.Key ?? c.TaskKey),
                        scope.PredictedScope, scope.Exclusive, scope.ReadOnly,
                        Urgent: c.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase),
                        Source: c.WaveId is null ? null : new(c.WaveId,
                            MonolithTransitionAdapter.DispatchSourceKind(c.CreationSource),
                            positions.GetValueOrDefault(c.TaskKey),
                            SourcePrior(c)));
                }).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
            int SourcePrior(TaskInfo card)
            {
                var kind = MonolithTransitionAdapter.DispatchSourceKind(card.CreationSource);
                if (priors.TryGetValue(kind, out var cached)) return cached;
                var delivered = cards.Where(c => c.ProjectName == card.ProjectName && c.State is TaskStates.Completed or TaskStates.Archive
                    && MonolithTransitionAdapter.DispatchSourceKind(c.CreationSource) == kind)
                    .Select(c => store.Read(c.ProjectName, c.TaskKey)?.Deliveries.Where(d => d.RecordedAt < now).OrderBy(d => d.RecordedAt).LastOrDefault()).Where(d => d is not null).Select(d => d!);
                return priors[kind] = (int)Math.Ceiling(MonolithTransitionAdapter.DispatchSizePrior(delivered
                    .Select(d => d.RealFiles.Distinct(StringComparer.Ordinal).Count(TouchForecast.IsCode)).ToArray()) ?? 0);
            }
            var exposed = cards.Select(c => integration?.TryGetValue(c.TaskKey, out var status) == true
                && TaskIntegrationBranch.Name(status.IntegrationBranch) == TaskIntegrationBranch.Name(config.IntegrationBranch)
                    ? c with { Integration = status } : c).ToArray();
            var allInFlight = OverlapComparisonSet.Collect(exposed, candidate.ProjectName,
                    running.Select(r => r.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase), Predict)
                .Concat(inFlight).GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
            var residentRunning = OverlapComparisonSet.ForProject(exposed, candidate.ProjectName, OverlapWindows.InFlight,
                Predict).Running;
            running = (path is "remote-claim" or "source-wave" ? residentRunning : Array.Empty<RunningTask>()).Concat(running).GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.Last()).ToArray();
            allInFlight = OverlapComparisonSet.For(candidate, allInFlight.Where(r => !running.Any(run => run.TaskId == r.TaskId)).ToArray()).ToArray();
            var flight = running.Select(r => Flight(r.CardKey ?? r.TaskId, true, r.Parallelism))
                .Concat(allInFlight.Select(r => Flight(r.CardKey ?? r.TaskId, false, r.Parallelism)))
                .OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
            DispatchInFlightCard Flight(string key, bool holdsSlot, TaskParallelism previous)
            {
                var scope = config.DispatchMode == "live" ? PredictScope(key, previous, candidate.ProjectName) : previous;
                return new(key, holdsSlot, Forecast(key), scope.PredictedScope, holdsSlot && scope.Exclusive, scope.ReadOnly);
            }
            var conflicts = store.ReadConflicts(candidate.ProjectName, to: now);
            var ledger = new DispatchLedgerSummary(conflicts.SelectMany(c => c.ConflictedFiles
                    .Select(file => new DispatchHotModule(TouchForecast.Module(file), c.At))).ToArray(),
                conflicts.SelectMany(c => c.PartnerCards.Select(partner => new DispatchCollidedPair(c.Card, partner))).ToArray(), []);
            var facts = new DispatchFacts(candidate.ProjectName, now, ready, flight,
                Math.Max(0, ParallelSlotPolicy.ClampMax(config.MaxParallelism) - running.Count), ledger,
                config.DispatchHubFiles ?? TouchForecast.DefaultHubFiles, PickupCandidates: pickupCandidates);
            var present = ready.Select(c => c.Key).Concat(flight.Select(c => c.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var prior = config.DispatchMode == "live" ? store.ReadLive(candidate.ProjectName) : null;
            var expired = (prior?.ExpiredHolds ?? []).Concat(prior?.Record.Plan.Releases
                .Where(r => r.DecisionRequest).Select(r => r.CardKey) ?? []).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            facts = facts with { ActiveHolds = prior?.Record.Plan.Holds,
                ActiveExclusiveMarks = prior?.Record.Plan.ExclusiveMarks.Where(m => present.Contains(m.CardKey)).Select(m => m.CardKey).ToArray(),
                DecisionRequestedHolds = expired, ActiveDependencies = prior?.Record.Plan.DependenciesToAdd,
                SatisfiedDependencies = exposed.Where(c => c.ProjectName.Equals(candidate.ProjectName, StringComparison.OrdinalIgnoreCase)
                    && (c.State is TaskStates.Completed or TaskStates.Archive
                        || c.Integration?.Status == IntegrationStatuses.Integrated
                            && TaskIntegrationBranch.Name(c.Integration.IntegrationBranch) == TaskIntegrationBranch.Name(config.IntegrationBranch)))
                    .Select(c => c.Key ?? c.TaskKey).ToArray() };
            var shadows = Record(facts, active.Select(a => a.Descriptor), path);
            if (config.DispatchMode != "live") return null;
            var lease = store.ReadLease(candidate.ProjectName);
            var owner = active.FirstOrDefault(a => a.PersonaId == lease?.Persona && a.State == PersonaStates.Active);
            var baseline = lease?.Persona == "queue-keeper" && lease.Rollout is not null;
            var dispatch = owner?.Descriptor.Dispatch;
            if (lease is null || lease.ExpiresAt <= now || dispatch is null && !baseline) return null;
            // Lease acquisition certifies the live standing grant; descriptor mode has no authority.
            if (!DispatcherCapabilities.Admits(new("dispatch.plan.write", lease.Persona, false, true,
                true, Now: now))) return null;
            facts = facts with { Persona = lease.Persona };
            var plan = MonolithTransitionAdapter.DispatchPlan(facts,
                baseline ? DispatchStrategies.Starter(DispatchStrategies.LaneEntry)
                    : new(dispatch!.Strategy.Id, dispatch.Strategy.Revision, dispatch.Parameters));
            var visibleKeys = facts.Ready.Select(c => c.Key).Concat(facts.InFlight.Select(c => c.Key)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            plan = MonolithTransitionAdapter.DispatchExclusive(plan, prior?.Record.Plan.ExclusiveMarks ?? [], visibleKeys);
            var record = new DispatchShadowRecordV1(shadows.FirstOrDefault()?.EvaluationId ?? Guid.NewGuid().ToString("N"), Guid.NewGuid().ToString("N"),
                "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(facts with { Persona = null })))), now, path, plan, false)
            { Window = lease.Window ?? MonolithTransitionAdapter.DispatchWindow(planningWave ?? candidate.WaveId, now) };
            return store.WriteLive(new(record, lease.Fence, expired), now) ? record : null;

            DispatchForecast Forecast(string key)
            {
                var f = ReadForecast(candidate.ProjectName, key);
                return f is null ? DispatchForecast.Unknown : new(f.ForecastFiles, f.Modules,
                    f.CitedFiles.Count > 0 ? DispatchForecastConfidence.High : DispatchForecastConfidence.Low);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException)
        { logger.LogWarning(ex, "dispatcher-shadow-record-failed project={Project}", candidate.ProjectName); return null; }
    }

    private TouchForecastV1? ReadForecast(string project, string identity)
    {
        var card = tasks.GetAllResidentTasks().FirstOrDefault(c => c.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)
            && (c.TaskKey.Equals(identity, StringComparison.OrdinalIgnoreCase) || c.Key?.Equals(identity, StringComparison.OrdinalIgnoreCase) == true
                || c.Id.Equals(identity, StringComparison.OrdinalIgnoreCase)));
        return store.Read(project, card?.TaskKey ?? identity);
    }

    public bool IsLive(string project) => settings.Get(project).DispatchMode == "live";

    public void RefreshLease(string project, string? wave) => scheduler?.Refresh(project, wave);

    public TaskParallelism Predict(TaskInfo card)
        => IsLive(card.ProjectName) ? PredictScope(card.Key ?? card.TaskKey, TaskScopePrediction.Predict(card), card.ProjectName)
            : TaskScopePrediction.Predict(card);

    private TaskParallelism PredictScope(string key, TaskParallelism fallback, string? project = null)
    {
        if (project is null) return fallback;
        if (fallback.ReadOnly) return fallback;
        var forecast = ReadForecast(project, key);
        var hubs = settings.Get(project).DispatchHubFiles ?? TouchForecast.DefaultHubFiles;
        var marks = store.ReadLive(project)?.Record.Plan.ExclusiveMarks;
        return fallback with { PredictedScope = (forecast?.ForecastFiles ?? []).Except(hubs, StringComparer.OrdinalIgnoreCase).ToArray(),
            Exclusive = fallback.Exclusive || marks?.Any(m => m.CardKey.Equals(key, StringComparison.OrdinalIgnoreCase)) == true };
    }

    public DispatchPickupDecision Pickup(TaskInfo card, DispatchShadowRecordV1? record)
    {
        var lease = store.ReadLease(card.ProjectName);
        var live = store.ReadLive(card.ProjectName);
        var project = projects.FindByIdOrDisplayName(card.ProjectName);
        var owner = project is null ? null : activations.List(project.WorkspaceId).Personas
            .Select(p => p.Activation).OfType<PersonaActivationV1>().FirstOrDefault(a => a.PersonaId == lease?.Persona
                && a.State == PersonaStates.Active && (a.Scope.Projects.Contains("*") || a.Scope.Projects.Contains(project.Id)
                    || a.Scope.Projects.Contains(project.DisplayName)));
        var satisfied = tasks.GetAllResidentTasks().Where(c => c.State is TaskStates.Completed or TaskStates.Archive
            || !OverlapComparisonSet.IsInFlight(c) && c.Integration?.Status == IntegrationStatuses.Integrated)
            .Select(c => c.Key ?? c.TaskKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var decision = MonolithTransitionAdapter.DispatchPickup(record?.Plan, card.Key ?? card.TaskKey, time.GetUtcNow(),
            record is not null && (owner is not null || lease is { Persona: "queue-keeper", Rollout: not null })
                && live?.Record.DecisionId == record.DecisionId && lease?.Fence == live.Fence
                && lease?.ExpiresAt > time.GetUtcNow(), satisfied);
        return MonolithTransitionAdapter.DispatchWavePickup(decision, card.WaveId,
            card.WaveId is null || store.ReadWaveRelease(card.ProjectName, card.WaveId)?.Complete == true);
    }

    public bool Validate(TaskInfo card, string? decisionId)
    {
        var record = store.ReadLive(card.ProjectName)?.Record;
        return record?.DecisionId == decisionId && Pickup(card, record).Admit;
    }

    public DispatchPickupDecision? Presentation(TaskInfo card)
    {
        if (!IsLive(card.ProjectName) || card.State != TaskStates.Ready) return null;
        return Pickup(card, store.ReadLive(card.ProjectName)?.Record);
    }

    public void BindRun(TaskInfo card, string decisionId, string? attemptId)
        => store.BindRun(card.ProjectName, card.Key ?? card.TaskKey, decisionId, attemptId);

    public void RecordAdmission(TaskInfo card, string decisionId, DispatchPickupDecision decision, string path)
        => store.AppendRunDecision(card.ProjectName, new(decisionId, card.Key ?? card.TaskKey,
            time.GetUtcNow(), path, decision.Admit, decision.Reason, decision.WaitsFor));

    public IReadOnlyList<DispatchShadowRecordV1> Record(DispatchFacts facts,
        IEnumerable<PersonaDescriptorV1> descriptors, string path)
    {
        var evaluation = Guid.NewGuid().ToString("N");
        var digest = "sha256:" + Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(
            JsonSerializer.Serialize(facts with { Persona = null }))));
        var records = new List<DispatchShadowRecordV1>();
        foreach (var descriptor in descriptors)
        {
            if (descriptor.Kind != PersonaKinds.Dispatcher || descriptor.Dispatch is not { } dispatch) continue;
            if (!DispatcherCapabilities.Admits(new("dispatch.shadow.record", descriptor.Id, true, false,
                descriptor.Operations.Contains("dispatch.shadow.record"), Now: facts.NowUtc))) continue;
            var strategy = new DispatchStrategy(dispatch.Strategy.Id, dispatch.Strategy.Revision, dispatch.Parameters);
            var plan = MonolithTransitionAdapter.DispatchPlan(facts with { Persona = descriptor.Id }, strategy);
            var record = new DispatchShadowRecordV1(evaluation, Guid.NewGuid().ToString("N"), digest,
                facts.NowUtc, path, plan);
            store.AppendShadow(record);
            records.Add(record);
        }
        // The reference never retires. Guard samples need baseline shadow on the same decision-time facts.
        if (!records.Any(r => r.Plan.Persona == "queue-keeper"))
        {
            var plan = MonolithTransitionAdapter.DispatchPlan(facts with { Persona = "queue-keeper" },
                DispatchStrategies.Starter(DispatchStrategies.LaneEntry));
            var baseline = new DispatchShadowRecordV1(evaluation, Guid.NewGuid().ToString("N"), digest, facts.NowUtc, path, plan);
            store.AppendShadow(baseline);
            records.Add(baseline);
        }
        return records;
    }
}
