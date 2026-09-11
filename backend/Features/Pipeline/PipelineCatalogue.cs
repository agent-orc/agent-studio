

namespace AgentStudio.Pipeline;

/// <summary>
/// Static catalogue of pipeline definitions. Phase 1 ships exactly one:
/// <c>standard-task-pipeline</c>, derived from today's
/// <c>3-progress -> 4-auto-review</c> flow. Pre-steps are reserved
/// slots (no runtime today; future tasks plug requirement-clarification
/// / context-retrieval / wiki-guidance upkeep here). The Core step is the
/// CLI agent run owned by <see cref="TaskRunnerService"/>. Post-steps
/// run the four <see cref="StepKind.Aspect"/> verdicts in parallel
/// (the load-bearing behavioural change in this phase), the
/// git-commit-attribution slot (ADR "Commit-Attribution-Regel";
/// behaviour implemented in
/// <see cref="AgentStudio.Tasks.CommitAttributionService"/>
/// and run from the transition service, so the executor records it as
/// planned), a <see cref="StepKind.Tool"/> lint-scss step, and an
/// <see cref="StepKind.Orchestrator"/> decision step that reads the
/// aspect verdicts.
///
/// The catalogue is a code constant on purpose: YAML loading buys
/// nothing in Phase 1 and a YAML schema would have to be co-versioned
/// with the in-memory model. Phase 2 may externalise this when
/// per-project pipeline customisation lands.
/// </summary>
public static class PipelineCatalogue
{
    public const string StandardPipelineId = "standard-task-pipeline";

    /// <summary>
    /// The lightweight report pipeline for planning / research modes: qualify,
    /// run the agent, validate the primary report, and hand it to human review.
    /// It deliberately omits git, build, test, stylelint, aspects, quality
    /// grading, Wiki automation, and drift checks. Chosen per run by
    /// <see cref="ForMode"/>.
    /// </summary>
    public const string ReadOnlyPipelineId = "read-only-task-pipeline";
    public const string ConceptPipelineId = "concept-task-pipeline";

    /// <summary>
    /// Iterative visual-delivery pipeline used for tasks classified by the
    /// shared <c>EvidenceGate</c> UI heuristic. It ends each evidenced iteration
    /// at a durable human-review marker instead of running the standard aspect
    /// fan-out.
    /// </summary>
    public const string UiPipelineId = "ui-iteration-task-pipeline";
    public const string UiPipelineRoutingStepId = "pre-ui-pipeline-routing";
    public const string UiIterationArtifactStepId = "post-ui-iteration-artifact";
    public const string UiVisualCaptureStepId = "post-ui-visual-capture";
    public const string UiVisualVerdictStepId = "post-ui-visual-verdict";
    public const string UiHumanReviewGateStepId = "post-ui-human-review-gate";
    public const string ConceptWorkbenchPlacementStepId = "post-concept-workbench-placement";
    public const string ConceptReviewStepId = "post-concept-review";
    public const string ConceptSightReviewGateStepId = "post-concept-sight-review";
    public const string ConceptPromotionStepId = "post-concept-promotion";

    /// <summary>
    /// The four aspect step ids ship as parallel post-steps. Kept in
    /// sync with <see cref="AspectRunnerService.Catalogue"/> -
    /// <see cref="PipelineCatalogueAsserts.AspectStepsMatchAspectRunnerCatalogue"/>
    /// fails the build if they drift.
    /// </summary>
    public static readonly string[] AspectStepIds =
    {
        "aspect-requirement-fit",
        "aspect-code-quality",
        "aspect-documentation-impact",
        "aspect-tests-and-evidence",
    };

    public const string CoreAgentRunStepId = "core-agent-run";

    /// <summary>
    /// Pre-step that surfaces the auto-mode Ralph-loop guard
    /// (<c>AgentStudio.Runner.StuckLoopGuard</c>) in the
    /// pipeline table. It is the first row of <see cref="TaskPipeline.AllSteps"/>
    /// so a forming or stopped loop is visible early - before the core run and
    /// the aspect verdicts. Deterministic (no LLM); the recording lives in
    /// <c>ProjectRunner</c>: <see cref="PipelineStepStatus.Passed"/> with no
    /// verdict while healthy, <see cref="PipelineStepStatus.Passed"/> with a
    /// <c>looping</c> verdict while a loop builds under budget, and
    /// <see cref="PipelineStepStatus.Failed"/> with a <c>loop-detected</c>
    /// verdict when the circuit-breaker fires.
    /// </summary>
    public const string LoopGuardStepId = "pre-loop-guard";
    /// <summary>
    /// Deterministic, zero-token task qualification performed immediately
    /// before execution. It maps the task profile onto the selected CLI's live
    /// model and reasoning ladders. Explicit card pins remain authoritative.
    /// </summary>
    public const string ModelQualificationStepId = "pre-model-qualification";
    /// <summary>
    /// Deterministic prompt enrichment that classifies the authored task,
    /// appends bounded curated context, and persists
    /// <c>enrichment-report.json</c> before CLI dispatch. Default-on and
    /// project-disableable through the standard pipeline-step convention.
    /// </summary>
    public const string PromptEnrichmentStepId = "pre-prompt-enrichment";

    /// <summary>
    /// Optional, parallelisable pre-coding step that surfaces the ADR-0026
    /// orchestrator-prep pass (prompt-clarity scoring / accept-bounce-iterate)
    /// in the pipeline table. It replaces the standalone
    /// <c>1a-orchestrator-prep</c> backlog lane: prep now runs in-place on
    /// <c>1-preparation</c> cards inside
    /// <c>AgentStudio.Supervisor.OrchestratorPrepHostedService</c>
    /// and admits accepted cards straight to <c>2-ready</c>, so the active flow
    /// has prep before the coding run without a dedicated lane. It is a
    /// <see cref="StepKind.Module"/> deterministic heuristic today (no LLM) yet
    /// carries the resolved per-project model
    /// (<see cref="PipelineStepConfigResolver.ResolveModel(ProjectSettings?, PipelineStep, string)"/>)
    /// so it respects the project model selection rather than hardcoding one.
    /// It runs decoupled from the coding latch (<see cref="StepRunMode.Parallel"/>)
    /// so it never blocks throughput, and defaults
    /// <c>DefaultEnabled = false</c> because prep is an opt-in pass an operator
    /// turns on per project (mirrors the <c>Orchestrator:PrepEnabled</c> kill
    /// switch). The recording lives in <c>OrchestratorPrepHostedService</c>:
    /// <see cref="PipelineStepStatus.Passed"/> on accept,
    /// <see cref="PipelineStepStatus.Failed"/> on bounce,
    /// <see cref="PipelineStepStatus.Running"/> while it iterates.
    /// </summary>
    public const string PreOrchestratorPrepStepId = "pre-orchestrator-prep";

    /// <summary>
    /// Deterministic pre-step that runs before the core agent run on a
    /// re-issued card: it detects whether the run is a re-issue carrying open
    /// items from the previous run (the auto-review follow-up reason, unchecked
    /// checklist boxes, or aspect concern/block summaries) and, on a hit, has
    /// <c>ProjectRunner</c> foreground those items into the run prompt + post an
    /// orchestrator intervention note rather than letting the orchestrator
    /// blindly restart. The decision logic lives in the pure
    /// <see cref="AgentStudio.Runner.ReissueOpenItemsPreCheck"/>;
    /// the recording is best-effort observability, never a state-machine input.
    /// It is deterministic (no LLM) and on by default - an unfinished re-issue
    /// is a correctness signal, not an opt-in pass.
    /// </summary>
    public const string PreReissueOpenItemsStepId = "pre-reissue-open-items";

    /// <summary>
    /// The five drift dimensions ship as opt-in <see cref="StepKind.Drift"/>
    /// post-steps (DRIFT Nachtrag): four LLM dimensions plus the rule-based
    /// code-pattern check. They default <c>DefaultEnabled = false</c> because a
    /// drift run is an expensive extra pass an operator turns on per project;
    /// the trigger is wired in <c>DriftPostStepRunner</c>, which reuses the
    /// existing <c>*DriftAnalysisService</c> + <c>DriftReportStore</c>. The id
    /// suffix after <c>post-drift-</c> selects the dimension; keep it in sync
    /// with <c>DriftPostStepRunner</c>'s dispatch.
    /// </summary>
    public const string DriftAdrCodeStepId = "post-drift-adr-code";
    public const string DriftSoftwareArchitectureStepId = "post-drift-software-architecture";
    public const string DriftDocsMarketingStepId = "post-drift-docs-marketing";
    public const string DriftSpecTaskJobStepId = "post-drift-spec-task-job";
    public const string DriftCodePatternStepId = "post-drift-code-pattern";

    public static readonly string[] DriftStepIds =
    {
        DriftAdrCodeStepId,
        DriftSoftwareArchitectureStepId,
        DriftDocsMarketingStepId,
        DriftSpecTaskJobStepId,
        DriftCodePatternStepId,
    };

    public const string GitCommitAttributionStepId = "post-git-commit-attribution";
    public const string WorktreeContainmentStepId = "post-worktree-containment";
    public const string IntegrateMergeStepId = "post-integrate-merge";
    public const string ConflictResolutionStepId = "post-conflict-resolution";

    /// <summary>
    /// Deferred, operator-triggered post-step that merges the task branch
    /// (<c>task/&lt;id&gt;</c>) into the integration branch (<c>develop</c>) once a
    /// done-green task is accepted. Unlike <see cref="IntegrateMergeStepId"/>
    /// (which runs automatically during the run to keep parallel worktrees in
    /// sync, ADR-0052), this step does NOT run on its own: it carries
    /// <see cref="PipelineStep.Deferred"/> = true and stays "pending" in the
    /// pipeline view until the operator triggers the "Merge into Develop" action
    /// (the HumanReview -&gt; Delivered / <c>Completed</c> acceptance signal). On
    /// trigger it performs the real, scoped git merge; a merge conflict is made
    /// visible (recorded <see cref="PipelineStepStatus.Failed"/> with the
    /// conflicted files) rather than swallowed, and the working tree is left
    /// clean. It closes the delivery gap so accepted work actually lands on
    /// <c>develop</c>. Implemented by <c>MergeIntoDevelopRunner</c>, triggered
    /// immediately for a green fenced Remote delivery and retried from
    /// <c>TaskTransitionService</c> on human acceptance when necessary.
    /// </summary>
    public const string MergeIntoDevelopStepId = "post-merge-into-develop";

    /// <summary>
    /// Deferred, operator-triggered post-step that pushes the integration branch
    /// (<c>develop</c>) to <c>origin</c> after <see cref="MergeIntoDevelopStepId"/>
    /// has folded an accepted task branch into it (AGT-1999). Closes the
    /// "integration nur lokal" gap: without it the platform pushed every
    /// <c>task/*</c> branch but never the integration branch, so <c>origin/develop</c>
    /// drifted stale, develop-push CI and the remote verification host tested an
    /// old tree, and there was no remote backup. Like the merge step it is
    /// <see cref="PipelineStep.Deferred"/> (runs only on the accept trigger, never
    /// automatically) and ordered right after it. The push runs off the request
    /// path (the same offload strategy as the completed-job workspace push, via
    /// <c>IntegrationPushQueue</c> / <c>IntegrationPushWorker</c>): a transient
    /// failure retries with backoff per the AGT-1944 environmental-retry taxonomy
    /// and, once the budget is spent, is recorded as a visible
    /// <see cref="PipelineStepStatus.Failed"/> step flagged <c>environmental</c>
    /// rather than silently dropped. It is a git step (the read-only pipeline
    /// drops it) and default-on; opt out per project via the same
    /// <see cref="ProjectSettings.PipelineSteps"/> override the other steps use.
    /// Implemented by <c>MergeIntoDevelopRunner.PushIntegrationBranchAsync</c>.
    /// </summary>
    public const string MergeIntoDevelopPushStepId = "post-merge-into-develop-push";
    /// <summary>
    /// Deterministic post-step that compiles the changed repository state before
    /// the orchestrator trusts any self-reported Success. It runs after the
    /// post-core completion scan and before the expensive aspect review; a red
    /// gate reissues with the build output foregrounded. Implemented by
    /// <see cref="BuildTestGateRunner"/>.
    /// </summary>
    public const string BuildTestGateStepId = "post-build-test-gate";
    /// <summary>
    /// Deterministic delivery gate for cards linked to a Dossier through
    /// <c>references.workbenches</c> or the descriptor's
    /// <c>sourceTaskKeys</c> back edge. CORE receives the append-only authoring
    /// contract; auto review then verifies that the task's dated slice entry is
    /// present and that decision content outside the implementation log stayed
    /// byte-identical. Cards without a Dossier reference record NotApplicable.
    /// </summary>
    public const string DossierMaintenanceStepId = "post-dossier-maintenance";
    /// <summary>
    /// Post-step that runs <c>npx stylelint</c> over the frontend SCSS tree
    /// after the agent run finishes. Verdict drives the
    /// <see cref="AgentStudio.Pipeline.LintScssRunner"/> mode
    /// (off/warn/fail) and may trigger a reissue back to <c>2-ready</c>
    /// when configured to fail. See ASS-563.
    /// </summary>
    public const string LintScssStepId = "post-lint-scss";
    /// <summary>
    /// Post-step that runs the Regression Radar spec-change analysis after the
    /// agent run finishes. Deterministic (no LLM): it diffs the run's SHA range
    /// and classifies each changed spec as intended / at-risk / drift. Reporting
    /// only - the verdict surfaces in the pipeline list but never triggers a
    /// reissue. Behaviour lives in
    /// <see cref="AgentStudio.RegressionRadar.RegressionRadarService"/>.
    /// </summary>
    public const string RegressionRadarStepId = "post-regression-radar";

    public const string QualityAngularRulesStepId = "post-analysis-angular-rules";
    public const string QualityDotNetRulesStepId = "post-analysis-dotnet-rules";
    public const string QualityModelReviewStepId = "post-analysis-model-review";
    public const string QualityVisualStepId = "post-analysis-visual-quality";
    public const string QualitySecurityStepId = "post-analysis-security";
    public const string QualityRedundancyStepId = "post-analysis-redundancy";
    public const string QualityConsistencyStepId = "post-analysis-consistency";

    /// <summary>
    /// Stable Quality Studio analysis catalogue. The Angular rule pass is the
    /// first executable slice; the remaining named axes reserve their package
    /// boundary without embedding Quality Studio rule content in Agent Studio.
    /// Card-class defaults are resolved from repository changes and the
    /// repository-owned quality policy, never from per-card or environment
    /// configuration.
    /// </summary>
    public static readonly string[] QualityAnalysisStepIds =
    {
        QualityAngularRulesStepId,
        QualityDotNetRulesStepId,
        QualityModelReviewStepId,
        QualityVisualStepId,
        QualitySecurityStepId,
        QualityRedundancyStepId,
        QualityConsistencyStepId,
    };

    /// <summary>
    /// Opt-in deterministic post-step that keeps a watched project's local
    /// <c>docs/common-problems</c> library current from task outcome
    /// signals. It dedupes by slug, increments occurrence evidence, updates
    /// <c>last-seen</c>, and regenerates the common-problems index without an
    /// LLM call. Implemented by <c>WikiMaintenancePostStepRunner</c>.
    /// </summary>
    public const string WikiMaintenanceStepId = "post-wiki-maintenance";

    /// <summary>
    /// Opt-in deterministic post-step that distills the run's learnings - the
    /// derived review verdict, the per-aspect orchestrator-review findings, the
    /// agent's own close-out notes, and any typed outcome stumbling block - into
    /// a per-task page under the watched project's
    /// <c>docs/operations/learnings/&lt;task&gt;.md</c> tree, then regenerates the
    /// learnings index. It is CLI-agnostic (no model call - it reads structured
    /// run evidence the orchestrator already has) and idempotent: a re-run dedupes
    /// by run signature so it merges/augments the page rather than overwriting it,
    /// and reissues append a fresh dated run block so nothing is lost (git keeps
    /// the file history). Reporting-only - it never changes the lane decision.
    /// Implemented by <c>WikiLearningsPostStepRunner</c>; defaults
    /// <c>DefaultEnabled = false</c> because knowledge distillation is a pass an
    /// operator turns on per project (same opt-in switch the wiki-maintenance and
    /// drift steps use).
    /// </summary>
    public const string WikiLearningsStepId = "post-wiki-learnings";

    /// <summary>
    /// Opt-in deterministic post-step that keeps the AGENTS.md -&gt; wiki pointers
    /// for a set of designated topics consistent (no dead / missing link) and
    /// maintains a machine-owned "Current State / Progress" page per designated
    /// topic under <c>docs/concepts/designated-topics/</c>, so agents read the
    /// current state of a topic instead of re-discovering it every run ("gegen im
    /// Kreis drehen"). It is CLI-agnostic (no model call - it derives the per-topic
    /// current-state line from the task's own title / newest commit / typed outcome,
    /// and matches a task to a topic by shared tags or changed-file path prefixes)
    /// and idempotent (re-running on the same task refreshes timestamps without
    /// duplicating a progress row). Reporting-only - it never changes the lane
    /// decision. Implemented by <c>AgentsWikiSyncPostStepRunner</c>; defaults
    /// <c>DefaultEnabled = false</c> because it is a per-project opt-in pass (same
    /// switch the wiki-maintenance / wiki-learnings / drift steps use) that also
    /// self-provisions an empty designated-topics registry the operator fills in.
    /// </summary>
    public const string AgentsWikiSyncStepId = "post-agents-wiki-sync";

    /// <summary>
    /// Post-core completeness check that runs immediately after the core agent
    /// run, before the aspect verdicts. It is the deterministic
    /// <c>CompletionGate</c> scan of the run's own close-out evidence
    /// (status Open Items / Notes, the Result line, and the log tail) for
    /// unfinished-work signals: open checklist boxes, self-reported build / test
    /// failures, or a silent finish with leftover items. A hit short-circuits the
    /// accept and reissues with the items foregrounded so a task can never be
    /// accepted while its own evidence says it is unfinished. It surfaces in the
    /// Overview pipeline as the FIRST "Orchestrator-Review" row (the final
    /// <see cref="OrchestratorDecisionStepId"/> row is the second one). Recorded
    /// by <c>ReviewDecisionOrchestrator</c>.
    ///
    /// <para>
    /// <b>Placement decision (ASS-643 / ASS-744, resolved).</b> The feature spec
    /// asked for the gate "nach git commit attribution, vor/um die
    /// Auto-Review-Decision". That requirement is met: the deterministic
    /// commit-attribution (<see cref="GitCommitAttributionStepId"/>) runs at the
    /// 3-progress -&gt; 4-auto-review transition in
    /// <c>TaskTransitionService</c> - strictly BEFORE this gate, which runs in
    /// <c>ReviewDecisionOrchestrator.ProcessDoneAsync</c> once the card is in
    /// 4-auto-review - and the gate runs BEFORE the final
    /// <see cref="OrchestratorDecisionStepId"/> ruling. So at runtime the gate is
    /// after attribution and before the decision exactly as specified. It is
    /// listed as the first POST row (ahead of the aspect verdicts) on purpose,
    /// for two reasons: (1) it short-circuits an unfinished close-out before
    /// spending the expensive parallel aspect review, and (2) the Overview then
    /// reads honestly on a reissue - the aspects sit BELOW a gate that stopped
    /// before they ran, rather than showing four aspect rows stuck "pending"
    /// underneath a reissue verdict. Moving the row after the aspect/tool steps
    /// would invert that and misrepresent a gate-reissue run, so the placement is
    /// kept and pinned by <c>PipelineCatalogueTests</c>.
    /// </para>
    /// </summary>
    public const string OrchestratorReviewStepId = "post-orchestrator-review";
    public const string OrchestratorDecisionStepId = "post-orchestrator-decision";
    /// <summary>
    /// Opt-in failure-boundary step. It classifies failed run, review, gate,
    /// integration, and crash completions and raises one deduplicated
    /// orchestrator follow-up task for an actionable failure fingerprint.
    /// </summary>
    public const string FailureInterventionStepId = "post-failure-intervention";

    /// <summary>
    /// First-class automatic code-review step (ASS-1657): runs post-CORE on the
    /// task's change set and assigns a Quality-Grade A/B/C/D with a short
    /// justification, so every pipelined task carries a grade visible in the
    /// Overview pipeline (status / duration / verdict / grade) rather than only
    /// in a log. It extends the existing user-triggered
    /// <see cref="AgentStudio.Review.CodeReviewStepService"/> (a
    /// grade mode), runs after the parallel aspect verdicts and before the final
    /// orchestrator decision, and uses a quality-first model
    /// (<c>CodeReviewStep:DefaultModel</c>, the live Codex flagship by default)
    /// rather than the bounded aspect model. Default-on; the recording lives in
    /// <c>ReviewDecisionOrchestrator</c>. Reporting only - the grade never gates
    /// the lane decision, so a low grade surfaces for the human without forcing a
    /// reissue.
    /// </summary>
    public const string CodeReviewGradeStepId = "post-code-review-grade";

    /// <summary>
    /// Opt-in post-step (AGT-2028) that, after a task settles, asks the best
    /// available model whether the change set is relevant to another project
    /// (a new feature, a removed capability, ...) and, on a conservative yes,
    /// SPAWNS a follow-up card there with a generated prompt and a
    /// <c>relatedTo</c> reference back to the source task. The relevance +
    /// prompt-generation model is quality-first (defaults to the live Codex
    /// flagship at its top advertised reasoning level); the spawned card is
    /// worked by the target project's default model. Generic, not
    /// website-hardwired: the target project, relevance question, and spawn lane
    /// come from <c>ProjectSettings.TaskSpawner</c>. Reporting-only - it never
    /// gates the source task's lane decision. It is
    /// <see cref="PipelineStep.DefaultEnabled"/> = false (an operator turns it on
    /// per project, same opt-in switch the drift / wiki steps use) and, because it
    /// makes a per-task LLM judgment plus writes a card, it dedupes via the source
    /// job's <c>.metadata/spawned-tasks.jsonl</c> ledger (max 1 per source task by
    /// default). Implemented by <c>TaskSpawnerPostStepRunner</c>, driven from
    /// <c>ReviewDecisionOrchestrator</c>.
    /// </summary>
    public const string TaskSpawnerStepId = "post-task-spawner";

    /// <summary>
    /// Display name for the post-core completeness check
    /// (<see cref="OrchestratorReviewStepId"/>): the EARLY gate that runs straight
    /// after the core run, before the aspect verdicts. It is deliberately distinct
    /// from <see cref="FinalOrchestratorReviewDisplayName"/> so the two
    /// orchestrator-review rows never read as the same step: this one is an early
    /// static scan / open-items check (verdict <c>complete</c> / <c>reissue</c> /
    /// <c>escalate</c>) and carries NO "final verdict" semantics.
    /// </summary>
    public const string PostCoreReviewDisplayName = "Post-Core Orchestrator-Review";

    /// <summary>
    /// Display name for the FINAL accept / reissue / escalate decision
    /// (<see cref="OrchestratorDecisionStepId"/>): the orchestrator's single
    /// ruling after the parallel aspects and tools. This is the ONLY row the FE
    /// tags as the "final verdict"; the post-core review row above
    /// (<see cref="PostCoreReviewDisplayName"/>) is a distinct early gate.
    /// </summary>
    public const string FinalOrchestratorReviewDisplayName = "Final Orchestrator-Review";

    /// <summary>
    /// The "Abbruch-Review" (post-abort review) step id. Unlike every other
    /// catalogue step this one is <em>abort-triggered</em>, not part of the
    /// linear post-bracket: it runs only after a non-clean CLI run end
    /// (watchdog timeout, non-zero exit, unexpected stop), so it is exposed as
    /// the standalone <see cref="AbortReviewStep"/> definition rather than
    /// inserted into <see cref="TaskPipeline.Post"/>. It defaults
    /// <see cref="PipelineStep.DefaultEnabled"/> = false because it is an extra
    /// LLM pass an operator turns on per project via the same
    /// <see cref="ProjectSettings.PipelineSteps"/> override mechanism the drift
    /// and aspect steps use. When it runs, <c>ProjectRunner</c> records a
    /// <see cref="StepKind.Orchestrator"/> step execution (verdict + reasoning)
    /// into <c>pipeline-execution.json</c> so it surfaces in the Overview
    /// pipeline view like the auto-review aspects.
    /// Implemented by <c>PostAbortReviewStepService</c>; the rule-engine
    /// decision is owned by <c>PostAbortReviewDecider</c>.
    /// </summary>
    public const string PostAbortReviewStepId = "post-abort-review";

    /// <summary>
    /// Standalone definition for the abort-triggered <see cref="PostAbortReviewStepId"/>
    /// step. Kept off the static Post list (it does not run in the normal
    /// post-bracket) but defined here so the per-project config resolver and
    /// the runtime step-execution recorder share one id, display name, default
    /// (on / opt-out), and model-resolution path.
    /// </summary>
    public static PipelineStep AbortReviewStep { get; } = new()
    {
        Id = PostAbortReviewStepId,
        DisplayName = "Abort review",
        // An orchestrator-style decision step: it consumes evidence and chooses
        // rerun / reissue / accept / human-review, the same shape as the
        // auto-review decision step, so it reuses StepKind.Orchestrator rather
        // than introducing a new kind the frontend would have to learn.
        Kind = StepKind.Orchestrator,
        RunMode = StepRunMode.Sequential,
        Idempotent = true,
        // Default-on 2026-07-05 (was opt-in/off since ADR-0032): the bounded
        // rerun budget (PostAbortReviewDecider.DefaultRerunBudget = 2) and the
        // fail-closed-to-human-review behavior on an unparseable verdict make
        // this safe to run for every project; opt out per project if undesired.
        DefaultEnabled = true,
    };

    /// <summary>
    /// Steps whose work mutates the git tree (worktree create, commit + push,
    /// merge / integration, teardown). Today the only catalogue git step is the
    /// commit-attribution slot; future worktree / merge steps add their ids here.
    /// The read-only pipeline does not include these steps, so a planning /
    /// research run does no git work. The git side effects that live outside the
    /// catalogue (auto-commit, push, completed-push) are gated separately in
    /// <c>TaskTransitionService</c> on the same <see cref="TaskModes.IsReadOnly"/>
    /// predicate.
    /// </summary>
    public static readonly HashSet<string> GitStepIds = new(StringComparer.Ordinal)
    {
        GitCommitAttributionStepId,
        WorktreeContainmentStepId,
        IntegrateMergeStepId,
        ConflictResolutionStepId,
        MergeIntoDevelopStepId,
        MergeIntoDevelopPushStepId,
    };

    private static readonly TaskPipeline StandardPipeline = BuildStandardPipeline();
    private static readonly TaskPipeline ReadOnlyPipeline = BuildReadOnlyPipeline();
    private static readonly TaskPipeline ConceptPipeline = BuildConceptPipeline();
    private static readonly TaskPipeline UiPipeline = BuildUiPipeline();

    public static TaskPipeline Standard => StandardPipeline;
    public static TaskPipeline ReadOnly => ReadOnlyPipeline;
    public static TaskPipeline Concept => ConceptPipeline;
    public static TaskPipeline UiIteration => UiPipeline;

    /// <summary>
    /// Select the default definition for an explicit pipeline type. Task, bug,
    /// and feature currently share the established coding chain; planning owns
    /// the lightweight git-free definition. Their settings remain independent
    /// even where catalogue defaults match.
    /// </summary>
    public static TaskPipeline ForType(string? pipelineType) =>
        PipelineTypes.Normalize(pipelineType) == PipelineTypes.Planning
            ? ReadOnlyPipeline
            : StandardPipeline;

    /// <summary>
    /// Resolve the default pipeline from the card's structural type and mode.
    /// Concept keeps its dedicated document-first chain.
    /// </summary>
    public static TaskPipeline ForTask(string? taskType, string? mode) =>
        TaskModes.IsConcept(mode)
            ? ConceptPipeline
            : ForType(PipelineTypes.Resolve(taskType, mode));

    public static TaskPipeline ForTask(TaskInfo task) => ForTask(task.TaskType, task.Mode);

    /// <summary>Compatibility wrapper for callers that only have an execution mode.</summary>
    public static TaskPipeline ForMode(string? mode) => ForTask(null, mode);

    public static TaskPipeline? Get(string id) =>
        string.Equals(id, StandardPipelineId, StringComparison.OrdinalIgnoreCase) ? StandardPipeline
        : string.Equals(id, ReadOnlyPipelineId, StringComparison.OrdinalIgnoreCase) ? ReadOnlyPipeline
        : string.Equals(id, ConceptPipelineId, StringComparison.OrdinalIgnoreCase) ? ConceptPipeline
        : string.Equals(id, UiPipelineId, StringComparison.OrdinalIgnoreCase) ? UiPipeline
        : null;

    public static IReadOnlyList<TaskPipeline> All { get; } =
        [StandardPipeline, ReadOnlyPipeline, ConceptPipeline, UiPipeline];

    public static PipelineStep? FindStep(string? id) =>
        All.SelectMany(pipeline => pipeline.AllSteps)
            .Append(AbortReviewStep)
            .FirstOrDefault(step => string.Equals(step.Id, id, StringComparison.OrdinalIgnoreCase));

    private static TaskPipeline BuildStandardPipeline()
    {
        var aspects = new List<PipelineStep>();
        foreach (var aspectId in AspectStepIds)
        {
            var bareId = aspectId.StartsWith("aspect-", StringComparison.Ordinal)
                ? aspectId.Substring("aspect-".Length)
                : aspectId;
            if (!AspectRunnerService.Catalogue.TryGetValue(bareId, out var def))
            {
                // Catalogue mismatch would trip PipelineCatalogueTests.cs;
                // keep going so the runtime still has a step record.
                aspects.Add(new PipelineStep
                {
                    Id = aspectId,
                    DisplayName = aspectId,
                    Kind = StepKind.Aspect,
                    RunMode = StepRunMode.Parallel,
                    Idempotent = true,
                });
                continue;
            }
            aspects.Add(new PipelineStep
            {
                Id = aspectId,
                DisplayName = def.Title,
                Kind = StepKind.Aspect,
                RunMode = StepRunMode.Parallel,
                Idempotent = true,
                PromptTemplate = def.PromptTemplate,
            });
        }

        return new TaskPipeline
        {
            Id = StandardPipelineId,
            DisplayName = "Standard task pipeline",
            Version = 1,
            Pre =
            [
                new PipelineStep
                {
                    Id = LoopGuardStepId,
                    DisplayName = "Loop check",
                    Kind = StepKind.Module,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = ModelQualificationStepId,
                    DisplayName = "Model qualification",
                    Kind = StepKind.Module,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = PromptEnrichmentStepId,
                    DisplayName = "Prompt enrichment",
                    Kind = StepKind.Module,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = PreOrchestratorPrepStepId,
                    DisplayName = "Orchestrator prep",
                    Kind = StepKind.Module,
                    // Decoupled from the coding latch (runs on 1-preparation
                    // cards in OrchestratorPrepHostedService), so it never
                    // blocks the active flow into 3-progress.
                    RunMode = StepRunMode.Parallel,
                    Idempotent = true,
                    // Opt-in per project: prep is an extra pre-coding pass an
                    // operator turns on, so an absent override leaves it off.
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = PreReissueOpenItemsStepId,
                    DisplayName = "Reissue open-items check",
                    Kind = StepKind.Module,
                    // Runs deterministically in the pre-bracket ahead of the core
                    // run; foregrounds leftover open items on a re-issue.
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    // Default-on: a re-issue that still has open items is a
                    // correctness signal the orchestrator should not skip.
                    DefaultEnabled = true,
                },
            ],
            Core =
            [
                new PipelineStep
                {
                    Id = CoreAgentRunStepId,
                    DisplayName = "Agent execution",
                    Kind = StepKind.Core,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = false,
                    PromptTemplate = "prompt.md",
                },
            ],
            Post =
            [
                new PipelineStep
                {
                    Id = OrchestratorReviewStepId,
                    DisplayName = PostCoreReviewDisplayName,
                    Kind = StepKind.Orchestrator,
                    // Runs straight after the core run, ahead of the aspect
                    // verdicts, so an unfinished close-out is caught before any
                    // expensive review pass. No intra-section dependency.
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = DossierMaintenanceStepId,
                    DisplayName = "Dossier maintenance",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = BuildTestGateStepId,
                    DisplayName = "Build/test gate",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId, DossierMaintenanceStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = QualityAngularRulesStepId,
                    DisplayName = "Quality Studio Angular rules",
                    Kind = StepKind.Analysis,
                    AppliesTo = PipelineStepStacks.Angular,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = QualityDotNetRulesStepId,
                    DisplayName = "Quality Studio C# rules",
                    Kind = StepKind.Analysis,
                    AppliesTo = PipelineStepStacks.DotNet,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = QualityModelReviewStepId,
                    DisplayName = "Quality Studio model review",
                    Kind = StepKind.Analysis,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = QualityVisualStepId,
                    DisplayName = "Quality Studio visual quality",
                    Kind = StepKind.Analysis,
                    AppliesTo = PipelineStepStacks.Angular,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = QualitySecurityStepId,
                    DisplayName = "Quality Studio security",
                    Kind = StepKind.Analysis,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = QualityRedundancyStepId,
                    DisplayName = "Quality Studio redundancy",
                    Kind = StepKind.Analysis,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = QualityConsistencyStepId,
                    DisplayName = "Quality Studio consistency",
                    Kind = StepKind.Analysis,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [BuildTestGateStepId],
                    Idempotent = true,
                    Stub = true,
                },
                .. aspects,
                new PipelineStep
                {
                    Id = WorktreeContainmentStepId,
                    DisplayName = "Worktree containment",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = IntegrateMergeStepId,
                    DisplayName = "Integrate merge",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [WorktreeContainmentStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = ConflictResolutionStepId,
                    DisplayName = "Conflict resolution",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [IntegrateMergeStepId],
                    Idempotent = false,
                },
                new PipelineStep
                {
                    Id = GitCommitAttributionStepId,
                    DisplayName = "Git commit attribution",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                    // The deterministic attribution behaviour IS implemented
                    // (CommitAttributionService, run from TaskTransitionService on
                    // the 3-progress -> 4-auto-review transition). It runs ahead
                    // of this executor bracket, so within the executor the slot
                    // stays a reserved record that surfaces as "planned".
                    Stub = true,
                },
                new PipelineStep
                {
                    Id = MergeIntoDevelopStepId,
                    DisplayName = "Merge into Develop",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    // Ordered right after the commit-collection slot: the merge
                    // only makes sense once the task's commits are attributed.
                    DependsOn = [GitCommitAttributionStepId],
                    // Re-runnable: an already-merged branch is a no-op (ancestor
                    // check), a conflict aborts cleanly and can be retried.
                    Idempotent = true,
                    // Implemented but operator-triggered (the "Merge into Develop"
                    // acceptance action), so it stays "pending" until then rather
                    // than running automatically in the post-bracket.
                    Deferred = true,
                },
                new PipelineStep
                {
                    Id = MergeIntoDevelopPushStepId,
                    DisplayName = "Push develop to origin",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    // Runs only once the merge into develop has actually landed.
                    DependsOn = [MergeIntoDevelopStepId],
                    // Re-runnable: an already-pushed branch is an ancestor no-op.
                    Idempotent = true,
                    // Same acceptance trigger as the merge, off the request path:
                    // stays "pending" until the operator accepts the task.
                    Deferred = true,
                },
                new PipelineStep
                {
                    Id = LintScssStepId,
                    DisplayName = "Frontend stylelint",
                    Kind = StepKind.Tool,
                    AppliesTo = PipelineStepStacks.Angular,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = RegressionRadarStepId,
                    DisplayName = "Regression radar",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = WikiMaintenanceStepId,
                    DisplayName = "Wiki maintenance",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId],
                    Idempotent = true,
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = WikiLearningsStepId,
                    DisplayName = "Wiki learnings",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    // Reads the aspect verdicts (the orchestrator-review findings)
                    // it distills, so it must schedule after the aspects.
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = AgentsWikiSyncStepId,
                    DisplayName = "Agent skills / AGENTS wiki sync",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    // Deterministic wiki upkeep keyed off the task's own evidence
                    // (tags / changed files / commit), independent of the aspect
                    // verdicts, so it only needs the core run to have produced the
                    // change set - mirrors the wiki-maintenance dependency.
                    DependsOn = [CoreAgentRunStepId],
                    Idempotent = true,
                    // Opt-in per project: an operator turns on the designated-topic
                    // sync (and fills in the seeded registry), same as the sibling
                    // wiki steps.
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = CodeReviewGradeStepId,
                    DisplayName = "Code-review quality grade",
                    // An LLM review pass that produces a single A/B/C/D ruling,
                    // same shape as the orchestrator decision (consumes the diff,
                    // emits a verdict), so it reuses StepKind.Orchestrator rather
                    // than the Aspect kind (which is pinned to the four
                    // aspect-runner ids).
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    // Reads the full change set after the aspects have run; depends
                    // on them so a DAG-resolver schedules it after the verdicts and
                    // before the final decision.
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                    // Every pipelined task carries a grade (ASS-1657), so on by
                    // default; an operator can still disable it per project.
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = TaskSpawnerStepId,
                    DisplayName = "Task spawner",
                    // An LLM relevance judgment that consumes the change set and
                    // emits a spawn/no-spawn verdict, the same shape as the grade
                    // step, so it reuses StepKind.Orchestrator rather than the
                    // Aspect kind (pinned to the four aspect-runner ids).
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    // Reads the full settled change set after the aspects have run.
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                    // Opt-in: a project turns it on only when it wants a follow-up
                    // card auto-created in another project (Default aus). No spam.
                    DefaultEnabled = false,
                },
                new PipelineStep
                {
                    Id = FailureInterventionStepId,
                    DisplayName = "Failure intervention",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    DefaultEnabled = false,
                    DependsOn = [OrchestratorReviewStepId],
                },
                new PipelineStep
                {
                    Id = OrchestratorDecisionStepId,
                    DisplayName = FinalOrchestratorReviewDisplayName,
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [.. AspectStepIds],
                    Idempotent = true,
                },
                .. BuildDriftSteps(),
            ],
        };
    }

    // Planning and research are document runs, not code runs. Keep the useful
    // deterministic preflight, one core authoring step, and two bounded handoff
    // checks. New standard post-steps must never leak into this list
    // automatically: build, test, lint, aspects, grading, Wiki automation, and
    // drift work are intentionally out of scope.
    private static TaskPipeline BuildReadOnlyPipeline()
    {
        var review = StandardPipeline.Post.Single(s => s.Id == OrchestratorReviewStepId) with
        {
            DependsOn = [CoreAgentRunStepId],
        };
        var decision = StandardPipeline.Post.Single(s => s.Id == OrchestratorDecisionStepId) with
        {
            DependsOn = [OrchestratorReviewStepId],
        };

        return StandardPipeline with
        {
            Id = ReadOnlyPipelineId,
            DisplayName = "Lightweight report pipeline",
            Pre = [.. StandardPipeline.Pre],
            Core = [.. StandardPipeline.Core],
            Post = [review, decision],
        };
    }

    private static TaskPipeline BuildConceptPipeline()
    {
        return new TaskPipeline
        {
            Id = ConceptPipelineId,
            DisplayName = "Concept Dossier pipeline",
            Version = 1,
            Core =
            [
                new PipelineStep
                {
                    Id = CoreAgentRunStepId,
                    DisplayName = "Concept authoring",
                    Kind = StepKind.Core,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = false,
                    PromptTemplate = "prompt.md",
                },
            ],
            Post =
            [
                new PipelineStep
                {
                    Id = ConceptWorkbenchPlacementStepId,
                    DisplayName = "Dossier placement",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = ConceptReviewStepId,
                    DisplayName = "Concept review",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [ConceptWorkbenchPlacementStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = ConceptSightReviewGateStepId,
                    DisplayName = "Sight review",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [ConceptReviewStepId],
                    Idempotent = true,
                },
                new PipelineStep
                {
                    Id = ConceptPromotionStepId,
                    DisplayName = "Promote implementation cards",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [ConceptSightReviewGateStepId],
                    Idempotent = true,
                    Deferred = true,
                },
            ],
        };
    }

    private static TaskPipeline BuildUiPipeline()
    {
        return new TaskPipeline
        {
            Id = UiPipelineId,
            DisplayName = "UI iteration task pipeline",
            Version = 2,
            Pre =
            [
                .. StandardPipeline.Pre.Select(step => step with { }),
                new PipelineStep
                {
                    Id = UiPipelineRoutingStepId,
                    DisplayName = "UI pipeline routing",
                    Kind = StepKind.Module,
                    RunMode = StepRunMode.Sequential,
                    Idempotent = true,
                    DefaultEnabled = true,
                },
            ],
            Core = StandardPipeline.Core.Select(step => step with { }).ToList(),
            Post =
            [
                StandardPipeline.Post.Single(step =>
                    step.Id == DossierMaintenanceStepId) with
                {
                    DependsOn = [CoreAgentRunStepId],
                },
                new PipelineStep
                {
                    Id = UiIterationArtifactStepId,
                    DisplayName = "UI iteration evidence",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [CoreAgentRunStepId, DossierMaintenanceStepId],
                    Idempotent = true,
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = UiVisualCaptureStepId,
                    DisplayName = "Stable-equivalent visual capture",
                    Kind = StepKind.Tool,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [UiIterationArtifactStepId],
                    Idempotent = true,
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = UiVisualVerdictStepId,
                    DisplayName = "Multimodal visual verdict",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [UiVisualCaptureStepId],
                    Idempotent = true,
                    DefaultEnabled = true,
                },
                new PipelineStep
                {
                    Id = UiHumanReviewGateStepId,
                    DisplayName = "Human iteration review",
                    Kind = StepKind.Orchestrator,
                    RunMode = StepRunMode.Sequential,
                    DependsOn = [UiVisualVerdictStepId],
                    Idempotent = true,
                    DefaultEnabled = true,
                },
            ],
        };
    }

    // The opt-in drift post-steps. They run after the auto-review decision (so
    // the task's own work is settled before drift is measured) and default off
    // - an absent override leaves them disabled. Four are LLM dimensions that
    // accept a per-step model; code-pattern is rule-based but still carries a
    // model so an operator can opt into the optional LLM verdict-enrichment pass.
    private static IEnumerable<PipelineStep> BuildDriftSteps()
    {
        var dependsOnReview = new[] { OrchestratorDecisionStepId };
        (string Id, string Name)[] dims =
        {
            (DriftAdrCodeStepId, "Drift: ADR / Code"),
            (DriftSoftwareArchitectureStepId, "Drift: Software / Architecture"),
            (DriftDocsMarketingStepId, "Drift: Docs / Marketing"),
            (DriftSpecTaskJobStepId, "Drift: Spec / Task / Job"),
            (DriftCodePatternStepId, "Drift: Code-Pattern (rule-based)"),
        };
        foreach (var (id, name) in dims)
        {
            yield return new PipelineStep
            {
                Id = id,
                DisplayName = name,
                Kind = StepKind.Drift,
                RunMode = StepRunMode.Sequential,
                DependsOn = [.. dependsOnReview],
                Idempotent = true,
                DefaultEnabled = false,
            };
        }
    }
}
