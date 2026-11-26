using AgentStudio.Shared;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

public sealed record RoutingDrillCase(string Scenario, string WorkClass, SelectionResult Selection, bool Passed);
public sealed record RoutingDrillGap(string Consumer, string? StepId, EffectiveModelRoute Preferred, string Reason);
public sealed record RoutingDrillReport(bool DryRun, bool Passed, string CatalogueVersion, int ShortStepReservePoints,
    IReadOnlyList<RoutingDrillCase> Cases, IReadOnlyList<RoutingDrillGap> MissingEquivalence);

/// <summary>Exercises the installation planner with fake quotas, without dispatch or ledger writes.</summary>
public sealed class ModelRoutingDrill(IModelSelectionService selection, IModelEquivalenceCatalog catalogue,
    ModelSelectionPolicyStore policy, ModelSelectionEffectiveService effective, TimeProvider? timeProvider = null)
{
    public RoutingDrillReport Run(string? project = null, string? pipelineType = null)
    {
        var now = (timeProvider ?? TimeProvider.System).GetUtcNow().UtcDateTime;
        var cases = new List<RoutingDrillCase>();
        foreach (var provider in new[] { CliTypes.Claude, CliTypes.Codex })
        {
            var pair = catalogue.Routes.FirstOrDefault(route => route.FromCliType == provider
                && catalogue.SameCapabilityClass(route.FromModel, route.ToModel));
            if (pair is null) continue;
            foreach (var (work, stepId) in new (string Work, string? Step)[]
            {
                (SelectionWorkClasses.Coding, null),
                (SelectionWorkClasses.Review, "aspect-requirement-fit"),
                (SelectionWorkClasses.Review, "aspect-code-quality"),
                (SelectionWorkClasses.Review, "aspect-documentation-impact"),
                (SelectionWorkClasses.Review, "aspect-tests-and-evidence"),
                (SelectionWorkClasses.Grade, "post-code-review-grade"),
                (SelectionWorkClasses.Summary, "post-summary-assessment"),
                (SelectionWorkClasses.Enrichment, "pre-prompt-enrichment"),
                (SelectionWorkClasses.Conflict, "post-conflict-resolution"),
                (SelectionWorkClasses.Concept, null),
            })
            foreach (var scenario in new[] { "primary-exhausted", "both-exhausted", "follow-up-exhausted" })
            {
                var request = new SelectionRequest { Consumer = SelectionConsumers.Coding, WorkClass = work,
                    Project = project, PipelineType = pipelineType, StepId = stepId,
                    Pins = new(provider, pair.FromModel, pair.FromThinkingLevel), IsExplicitPin = true,
                    Continuation = scenario == "follow-up-exhausted" ? new(provider, pair.FromModel) : null };
                var result = selection.Select(new ModelSelectionCall(request, "routing-drill", Preview: true,
                    SnapshotOverride: cli => new QuotaSnapshot { CliType = cli!, FetchedAt = now,
                        Windows = [new() { Label = "Weekly", UsedPct = scenario == "both-exhausted" || cli == provider ? 100 : 0,
                            ResetAt = now.AddDays(1) }] })).Result;
                cases.Add(new(provider + ":" + scenario + ":" + (stepId ?? work), work, result,
                    scenario == "both-exhausted" ? result.Outcome == SelectionOutcome.Wait
                        : result.IsFallback && result.Route.CliType != provider));
            }
        }
        var gaps = effective.Get(project, pipelineType).Where(route => !catalogue.Routes.Any(pair =>
                pair.FromCliType == route.Configured.CliType && pair.FromModel == route.Configured.Model
                && pair.FromThinkingLevel == route.Configured.ThinkingLevel))
            .Select(route => new RoutingDrillGap(route.Consumer, route.StepId, route.Configured,
                "Token Economy has no published equivalent for this model and reasoning pair; propose and qualify an upstream entry."))
            .ToArray();
        return new(true, cases.Count > 0 && cases.All(item => item.Passed), catalogue.Version,
            policy.Get().ShortStepReservePoints, cases, gaps);
    }
}
