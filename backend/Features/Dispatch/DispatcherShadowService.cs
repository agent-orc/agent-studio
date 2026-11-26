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
    ILogger<DispatcherShadowService> logger, AgentStudio.Tasks.TaskListGitProjectionCache? gitProjection = null)
{
    public void RecordPick(TaskInfo candidate, IReadOnlyList<RunningTask> running,
        IReadOnlyList<InFlightTask> inFlight, string path)
    {
        try
        {
            var project = projects.FindByIdOrDisplayName(candidate.ProjectName);
            if (project is null) return;
            var active = activations.List(project.WorkspaceId).Personas.Select(p => p.Activation)
                .OfType<PersonaActivationV1>().Where(a => a.Descriptor.Kind == PersonaKinds.Dispatcher
                    && a.State is PersonaStates.Active or PersonaStates.Shadow
                    && a.Operations.Contains("dispatch.shadow.record")
                    && (a.Scope.Projects.Contains("*") || a.Scope.Projects.Contains(project.Id)
                        || a.Scope.Projects.Contains(project.DisplayName))).ToArray();
            if (active.Length == 0) return;
            var cards = tasks.GetAllResidentTasks().Where(c => !c.Fixture).ToArray();
            var referenceIndex = tasks.GetCoreReferenceIndex();
            var config = settings.Get(candidate.ProjectName);
            var integration = gitProjection?.ReadDependencyIntegration(cards);
            var ready = cards.Where(c => c.ProjectName.Equals(candidate.ProjectName, StringComparison.OrdinalIgnoreCase)
                && c.State == TaskStates.Ready).Select(c =>
                {
                    var scope = TaskScopePrediction.Predict(c);
                    return new DispatchReadyCard(c.Key ?? c.TaskKey, c.Order, c.EnteredLaneAt,
                        c.References?.DependsOn.Select(e => e.Key).ToArray() ?? [],
                        !referenceIndex.EvaluateWaitsOn(c, integration, config.IntegrationBranch).Blocked, Forecast(c.Key ?? c.TaskKey),
                        scope.PredictedScope, scope.Exclusive, scope.ReadOnly,
                        Urgent: c.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase));
                }).OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
            var exposed = cards.Select(c => integration?.TryGetValue(c.TaskKey, out var status) == true
                && TaskIntegrationBranch.Name(status.IntegrationBranch) == TaskIntegrationBranch.Name(config.IntegrationBranch)
                    ? c with { Integration = status } : c).ToArray();
            var allInFlight = OverlapComparisonSet.Collect(exposed, candidate.ProjectName,
                    running.Select(r => r.TaskId).ToHashSet(StringComparer.OrdinalIgnoreCase), TaskScopePrediction.Predict)
                .Concat(inFlight).GroupBy(r => r.TaskId, StringComparer.OrdinalIgnoreCase).Select(g => g.Last()).ToArray();
            var flight = running.Select(r => new DispatchInFlightCard(r.CardKey ?? r.TaskId, true,
                    Forecast(r.CardKey ?? r.TaskId), r.Parallelism.PredictedScope, r.Parallelism.Exclusive, r.Parallelism.ReadOnly))
                .Concat(allInFlight.Select(r => new DispatchInFlightCard(r.CardKey ?? r.TaskId, false,
                    Forecast(r.CardKey ?? r.TaskId), r.Parallelism.PredictedScope, false, r.Parallelism.ReadOnly)))
                .OrderBy(c => c.Key, StringComparer.Ordinal).ToArray();
            var now = time.GetUtcNow();
            var conflicts = store.ReadConflicts(candidate.ProjectName, to: now);
            var ledger = new DispatchLedgerSummary(conflicts.SelectMany(c => c.ConflictedFiles
                    .Select(file => new DispatchHotModule(TouchForecast.Module(file), c.At))).ToArray(),
                conflicts.SelectMany(c => c.PartnerCards.Select(partner => new DispatchCollidedPair(c.Card, partner))).ToArray(), []);
            var facts = new DispatchFacts(candidate.ProjectName, now, ready, flight,
                Math.Max(0, ParallelSlotPolicy.ClampMax(config.MaxParallelism) - running.Count), ledger,
                config.DispatchHubFiles ?? TouchForecast.DefaultHubFiles);
            Record(facts, active.Select(a => a.Descriptor), path);

            DispatchForecast Forecast(string key)
            {
                var f = store.Read(candidate.ProjectName, key);
                return f is null ? DispatchForecast.Unknown : new(f.ForecastFiles, f.Modules,
                    f.CitedFiles.Count > 0 ? DispatchForecastConfidence.High : DispatchForecastConfidence.Low);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        { logger.LogWarning(ex, "dispatcher-shadow-record-failed project={Project}", candidate.ProjectName); }
    }

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
        return records;
    }
}
