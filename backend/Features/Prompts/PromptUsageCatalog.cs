namespace AgentStudio.Prompts;

/// <summary>
/// One recorded consumer of a runtime prompt template: the class that renders
/// it and the member that does so, plus a one-line purpose. This is the
/// "usage-ref" half of the prompt registry - it answers "which step / service
/// actually fills this template's slots" so an operator editing an override can
/// see the blast radius before they save.
/// </summary>
public sealed record PromptUsageRef(string Component, string Member, string Purpose);

/// <summary>
/// Static map from template name to the code sites that render it. Hand-curated
/// (a runtime reflection scan can't see which string literal a service passes
/// to <see cref="RuntimePromptService.Render"/>), so a newly wired template with
/// no entry here simply shows "no recorded usage" - the coverage surface, not
/// this map, is responsible for flagging gaps.
/// </summary>
internal static class PromptUsageCatalog
{
    private static readonly IReadOnlyList<PromptUsageRef> None = Array.Empty<PromptUsageRef>();

    // A class/member can remain in source while no registered runtime path can
    // reach it. Keep those audited exceptions distinct from the consumer map.
    private static readonly Dictionary<string, string> Unreachable =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["recurring-output-pattern-review.md"] =
                "The recorded RecurringOutputPatternService consumer has no DI registration, endpoint, hosted service, or scheduled runner. The prompt is unreachable.",
        };

    private static readonly Dictionary<string, IReadOnlyList<PromptUsageRef>> Map =
        new(StringComparer.OrdinalIgnoreCase)
    {
        ["runner-fresh-start.md"] = new[]
        {
            new PromptUsageRef("RunPlanner", "PickTemplate", "Selected when a task starts from scratch."),
            new PromptUsageRef("ProjectRunner", "RenderRunPrompt", "Rendered as the CORE agent's opening prompt."),
        },
        ["runner-resume-interrupted.md"] = new[]
        {
            new PromptUsageRef("RunPlanner", "PickTemplate", "Selected to resume a run interrupted mid-flight."),
        },
        ["runner-resume-restart.md"] = new[]
        {
            new PromptUsageRef("RunPlanner", "PickTemplate", "Selected to resume a task by restarting the CLI session."),
        },
        ["runner-recovery-continuation.md"] = new[]
        {
            new PromptUsageRef("RunPlanner", "BuildRecoveryPlan", "Continues a task across a crash-recovery boundary."),
        },
        ["runner-reissue-change.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildReissuePlan", "Legacy unversioned reissue prompt retained for override compatibility."),
        },
        ["runner-reissue-control-v1.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildReissuePlan", "Control arm for the finding-first reissue prompt experiment."),
        },
        ["runner-reissue-treatment-v1.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildReissuePlan", "Structured treatment arm for the finding-first reissue prompt experiment."),
        },
        ["epic-decomposition.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildEpicDecompositionPlan", "Decomposes an epic-sized task into child tasks."),
        },
        ["summary-protocol.md"] = new[]
        {
            new PromptUsageRef("SummaryGenerationService", "Generate", "Produces the run summary / review protocol."),
        },
        ["commit-message.md"] = new[]
        {
            new PromptUsageRef("GitService", "GenerateCommitMessage", "Drafts the commit message for the change set."),
        },
        ["mode-framing-readonly.md"] = new[]
        {
            new PromptUsageRef("RuntimePromptService", "RenderModeFraming", "Read-only framing injected via {{mode_framing}}."),
        },
        ["mode-framing-research.md"] = new[]
        {
            new PromptUsageRef("RuntimePromptService", "RenderModeFraming", "Research HTML-deliverable contract injected via {{mode_framing}}."),
        },
        ["mode-framing-concept.md"] = new[]
        {
            new PromptUsageRef("RuntimePromptService", "RenderModeFraming", "Docs-only Dossier framing injected for concept mode."),
        },
        ["mode-framing-dossier-maintenance.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "RenderPrompt", "Append-only Dossier delivery contract injected for referenced cards."),
            new PromptUsageRef("RuntimePromptService", "RenderDossierMaintenanceFraming", "Fills the stable card key and resolved Dossier targets."),
        },
        ["mode-framing-web.md"] = new[]
        {
            new PromptUsageRef("RuntimePromptService", "RenderModeFraming", "Web-access framing injected via {{mode_framing}}."),
        },
        ["code-review-step.md"] = new[]
        {
            new PromptUsageRef("CodeReviewStepService", "BuildPrompt", "Automated code-review pass over the diff."),
        },
        ["code-review-grade.md"] = new[]
        {
            new PromptUsageRef("CodeReviewStepService", "BuildGradePrompt", "Grades the change set A/B/C/D."),
        },
        ["review-aspect-code-quality.md"] = new[]
        {
            new PromptUsageRef("AspectRunnerService", "RenderAspectPrompt", "Code-quality review aspect."),
        },
        ["review-aspect-requirement-fit.md"] = new[]
        {
            new PromptUsageRef("AspectRunnerService", "RenderAspectPrompt", "Requirement-fit review aspect."),
        },
        ["review-aspect-tests-and-evidence.md"] = new[]
        {
            new PromptUsageRef("AspectRunnerService", "RenderAspectPrompt", "Tests-and-evidence review aspect."),
        },
        ["review-aspect-documentation-impact.md"] = new[]
        {
            new PromptUsageRef("AspectRunnerService", "RenderAspectPrompt", "Documentation-impact review aspect."),
        },
        ["post-abort-review.md"] = new[]
        {
            new PromptUsageRef("PostAbortReviewStepService", "BuildPrompt", "Verdict step after a non-clean run end."),
        },
        ["visual-qa-review.md"] = new[]
        {
            new PromptUsageRef("VisualQaService", "BuildPrompt", "Multimodal clear-defect verdict before the UI human gate."),
        },
        ["orchestrator-review-decision.md"] = new[]
        {
            new PromptUsageRef("ReviewDecisionOrchestrator", "BuildPrompt", "Primary auto-review verdict prompt."),
        },
        ["orchestrator-review-decision-fallback.md"] = new[]
        {
            new PromptUsageRef("ReviewDecisionOrchestrator", "BuildPrompt", "Resilience fallback when the primary template fails to render."),
        },
        ["orchestrator-reissue-followup.md"] = new[]
        {
            new PromptUsageRef("ReviewDecisionOrchestrator", "BuildReissueFollowUp", "Follow-up sent back to a reissued task."),
        },
        ["orchestrator-no-completion-signal.md"] = new[]
        {
            new PromptUsageRef("ReviewDecisionOrchestrator", "BuildNoCompletionSignalPrompt", "Decision when a run ends without a terminal sentinel."),
        },
        ["adr-code-drift.md"] = new[]
        {
            new PromptUsageRef("DriftPostStepRunner", "BuildAdrDriftPrompt", "ADR-vs-code drift report."),
            new PromptUsageRef("DriftReportEndpoints", "MapDriftReportEndpoints", "On-demand ADR-vs-code drift report."),
        },
        ["software-architecture-drift.md"] = new[]
        {
            new PromptUsageRef("DriftPostStepRunner", "BuildArchitectureDriftPrompt", "Software-architecture drift report."),
            new PromptUsageRef("DriftReportEndpoints", "MapDriftReportEndpoints", "On-demand architecture drift report."),
        },
        ["docs-marketing-drift.md"] = new[]
        {
            new PromptUsageRef("DriftPostStepRunner", "BuildDocsDriftPrompt", "Docs/marketing-vs-behavior drift report."),
            new PromptUsageRef("DriftReportEndpoints", "MapDriftReportEndpoints", "On-demand docs drift report."),
        },
        ["spec-task-job-drift.md"] = new[]
        {
            new PromptUsageRef("DriftPostStepRunner", "BuildSpecDriftPrompt", "Spec-vs-tasks drift report."),
            new PromptUsageRef("DriftReportEndpoints", "MapDriftReportEndpoints", "On-demand spec drift report."),
        },
        ["steering-docs-summary-and-drift.md"] = new[]
        {
            new PromptUsageRef("AnalysisReportEndpoints", "MapAnalysisReportEndpoints", "Steering-docs summary and drift report."),
        },
        ["roadmap-alignment-review.md"] = new[]
        {
            new PromptUsageRef("RoadmapAlignmentReviewService", "BuildPrompt", "Compares the task queue against the roadmap."),
            new PromptUsageRef("AnalysisReportEndpoints", "MapAnalysisReportEndpoints", "Roadmap-alignment report endpoint."),
        },
        ["recurring-output-pattern-review.md"] = new[]
        {
            new PromptUsageRef("RecurringOutputPatternService", "BuildPrompt", "Scans recent agent outputs for recurring patterns."),
        },
        ["supervisor-soft-reasoning.md"] = new[]
        {
            new PromptUsageRef("SoftReasoningHostedService", "BuildPrompt", "Layer-2 soft-reasoning second opinion."),
        },
        ["title-generate.md"] = new[]
        {
            new PromptUsageRef("TitleGenerationService", "Generate", "Generates a concise task title."),
        },
        ["prompt-enhance.md"] = new[]
        {
            new PromptUsageRef("PromptEnhancementService", "Enhance", "Expands a raw task prompt before queueing."),
        },
        ["proposal-feedback-refine.md"] = new[]
        {
            new PromptUsageRef("ProjectProposalDraftingService", "RefineFeedbackAsync", "Refines free-form proposal feedback before drafting."),
        },
        ["proposal-draft-generate.md"] = new[]
        {
            new PromptUsageRef("ProjectProposalDraftingService", "GenerateAsync", "Generates a structured project proposal draft."),
        },
        ["task-spawner-relevance.md"] = new[]
        {
            new PromptUsageRef("TaskSpawnerPostStepRunner", "BuildPrompt", "Decides whether a completed change should spawn a related task."),
        },
        ["failure-intervention-classifier.md"] = new[]
        {
            new PromptUsageRef("FailureInterventionService", "ClassifyAmbiguousAsync", "Classifies an ambiguous pipeline failure using the project's economy route."),
        },

        // --- Templates introduced by the T3a inline-migration ---
        ["code-pattern-drift-review.md"] = new[]
        {
            new PromptUsageRef("CodePatternDriftAnalysisService", "BuildLlmPrompt", "Reviews code-pattern drift hits."),
        },
        ["code-pattern-drift-canonical-sites.md"] = new[]
        {
            new PromptUsageRef("CodePatternDriftAnalysisService", "BuildLlmPrompt", "Canonical-sites sub-block of the drift review."),
        },
        ["global-orchestrator-boot.md"] = new[]
        {
            new PromptUsageRef("GlobalOrchestratorBootstrap", "BuildBootPrompt", "Global orchestrator boot prompt."),
        },
        ["global-orchestrator-self-mod-note.md"] = new[]
        {
            new PromptUsageRef("GlobalOrchestratorBootstrap", "BuildWatchedProjectsBlock", "Self-modification warning for the tool's own checkout."),
        },
        ["global-orchestrator-task-snapshot.md"] = new[]
        {
            new PromptUsageRef("GlobalOrchestratorBootstrap", "BuildTaskSnapshotBlock", "Watched-task snapshot block in the boot prompt."),
        },
        ["orchestrator-conflict-resolution.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildConflictResolutionPrompt", "Orchestrator-owned merge-conflict resolver."),
        },
        ["orchestrator-project-boot.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildOrchestratorBootPrompt", "Per-project orchestrator boot prompt."),
        },
        ["orchestrator-decision-resume.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildOrchestratorResumePrompt", "Resume-session auto-decision prompt."),
        },
        ["orchestrator-decision-attachments-resume.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildOrchestratorResumePrompt", "Attachments sub-block of the resume decision."),
        },
        ["orchestrator-decision-oneshot.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildOrchestratorPrompt", "One-shot auto-decision prompt."),
        },
        ["orchestrator-decision-attachments-oneshot.md"] = new[]
        {
            new PromptUsageRef("ProjectRunner", "BuildOrchestratorPrompt", "Attachments sub-block of the one-shot decision."),
        },
        ["wiki-search-expand.md"] = new[]
        {
            new PromptUsageRef("WikiSearchService", "TryExpandAsync", "Semantic query expansion for the wiki search (fail-open layer)."),
        },
        ["watcher-analysis.md"] = new[]
        {
            new PromptUsageRef("WatcherAnalysisService", "BuildPrompt", "Bounded strong-model analysis for a Contradiction or Drift watcher case."),
        },
    };

    public static IReadOnlyList<PromptUsageRef> For(string name) =>
        Map.TryGetValue(name, out var refs) ? refs : None;

    public static bool TryGetUnreachableReason(string name, out string reason) =>
        Unreachable.TryGetValue(name, out reason!);
}
