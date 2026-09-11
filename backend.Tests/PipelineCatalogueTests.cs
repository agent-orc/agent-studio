

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pin the Phase-1 <c>standard-task-pipeline</c> definition: the
/// catalogue stays in sync with <see cref="AspectRunnerService.Catalogue"/>,
/// the four aspect post-steps are marked parallel, the
/// git-commit-attribution step is implemented (ADR
/// "Commit-Attribution-Regel") and ordered before the decision, and the
/// orchestrator-decision step depends on the aspect steps so a
/// DAG-resolver cannot run it ahead of its inputs.
/// </summary>
public class PipelineCatalogueTests
{
    [Fact]
    public void StandardPipeline_exposes_named_Quality_Studio_analysis_axes()
    {
        var steps = PipelineCatalogue.Standard.Post
            .Where(step => step.Kind == StepKind.Analysis)
            .ToArray();

        Assert.Equal(PipelineCatalogue.QualityAnalysisStepIds, steps.Select(step => step.Id));
        Assert.All(steps, step =>
        {
            Assert.True(step.Idempotent);
            Assert.Contains(PipelineCatalogue.BuildTestGateStepId, step.DependsOn);
        });
        Assert.False(steps.Single(step =>
            step.Id == PipelineCatalogue.QualityAngularRulesStepId).Stub);
        Assert.All(steps.Where(step =>
            step.Id != PipelineCatalogue.QualityAngularRulesStepId), step => Assert.True(step.Stub));
        Assert.All(steps, step => Assert.False(PipelineStepConfigResolver.CanDisable(step)));
    }

    [Fact]
    public void Central_project_settings_cannot_override_repository_owned_analysis_steps()
    {
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>(StringComparer.OrdinalIgnoreCase)
            {
                [PipelineCatalogue.QualityAngularRulesStepId] = new() { Enabled = false },
            },
        };
        var step = Assert.Single(PipelineCatalogue.Standard.Post, candidate =>
            candidate.Id == PipelineCatalogue.QualityAngularRulesStepId);

        Assert.Null(PipelineStepConfigResolver.Lookup(settings, step.Id));
        Assert.True(PipelineStepConfigResolver.IsEnabled(settings, step));
    }

    [Theory]
    [InlineData(PipelineCatalogue.UiHumanReviewGateStepId)]
    [InlineData(PipelineCatalogue.ConceptPromotionStepId)]
    [InlineData(PipelineCatalogue.PostAbortReviewStepId)]
    public void FindStep_ResolvesEveryStepExposedByTheProjectCatalogue(string stepId)
    {
        var step = PipelineCatalogue.FindStep(stepId);

        Assert.NotNull(step);
        Assert.Equal(stepId, step.Id);
    }

    [Fact]
    public void StandardPipeline_HasExpectedSections()
    {
        var p = PipelineCatalogue.Standard;
        Assert.Equal(PipelineCatalogue.StandardPipelineId, p.Id);
        Assert.Equal(1, p.Version);
        // Pre: loop guard and model qualification lead, followed by prompt
        // enrichment, opt-in orchestrator prep, and the deterministic reissue
        // open-items check.
        Assert.Equal(5, p.Pre.Count);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, p.Pre[0].Id);
        Assert.Equal(PipelineCatalogue.ModelQualificationStepId, p.Pre[1].Id);
        Assert.Equal(PipelineCatalogue.PromptEnrichmentStepId, p.Pre[2].Id);
        Assert.True(p.Pre[2].DefaultEnabled);
        Assert.Equal(PipelineCatalogue.PreOrchestratorPrepStepId, p.Pre[3].Id);
        Assert.Equal(PipelineCatalogue.PreReissueOpenItemsStepId, p.Pre[4].Id);
        Assert.Single(p.Core);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, p.Core[0].Id);
        Assert.Equal(StepKind.Core, p.Core[0].Kind);
        Assert.False(p.Core[0].Idempotent); // Core agent runs are not safe to re-run blindly.

        // Post includes the deterministic review/build gates, seven named
        // Quality Studio analyses, four aspects,
        // implemented tool steps (incl. the opt-in wiki-maintenance, wiki-learnings
        // distillation, and agents/wiki-sync steps), the deferred operator-triggered
        // "Merge into Develop" step and its integration-branch push twin, the
        // automatic code-review quality-grade step, the opt-in task-spawner step,
        // final orchestrator decision, and opt-in drift dimensions.
        Assert.Equal(34, p.Post.Count);
        var intervention = Assert.Single(p.Post.Where(step =>
            step.Id == PipelineCatalogue.FailureInterventionStepId));
        Assert.False(intervention.DefaultEnabled);
        Assert.True(intervention.Idempotent);
        Assert.Equal(StepKind.Orchestrator, intervention.Kind);
    }

    [Fact]
    public void StandardPipeline_DossierMaintenance_IsVisibleMandatoryAndGatesBuild()
    {
        var pipeline = PipelineCatalogue.Standard;
        var dossier = pipeline.Post.Single(step =>
            step.Id == PipelineCatalogue.DossierMaintenanceStepId);
        var build = pipeline.Post.Single(step =>
            step.Id == PipelineCatalogue.BuildTestGateStepId);

        Assert.Equal("Dossier maintenance", dossier.DisplayName);
        Assert.Equal(StepKind.Tool, dossier.Kind);
        Assert.Equal(StepRunMode.Sequential, dossier.RunMode);
        Assert.True(dossier.DefaultEnabled);
        Assert.True(dossier.Idempotent);
        Assert.False(PipelineStepConfigResolver.CanDisable(dossier));
        Assert.Contains(PipelineCatalogue.CoreAgentRunStepId, dossier.DependsOn);
        Assert.Contains(PipelineCatalogue.DossierMaintenanceStepId, build.DependsOn);
    }

    [Fact]
    public void PromptEnrichment_IsDefaultOnAndProjectConventionCanDisableIt()
    {
        var step = PipelineCatalogue.Standard.Pre.Single(candidate =>
            candidate.Id == PipelineCatalogue.PromptEnrichmentStepId);

        Assert.True(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        Assert.True(PipelineStepConfigResolver.CanDisable(step));

        var disabled = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.PromptEnrichmentStepId] = new() { Enabled = false },
            },
        };

        Assert.False(PipelineStepConfigResolver.IsEnabled(disabled, step));
    }

    [Fact]
    public void StandardPipeline_OrchestratorReview_IsFirstPostStep_AndHasDistinctDisplayNameFromDecision()
    {
        // The orchestrator-review completeness check is the FIRST post-step (runs
        // straight after the core run, ahead of the aspect verdicts) so an
        // unfinished close-out is caught before any expensive review pass. It is
        // the EARLY gate and the decision step is the FINAL ruling: they are two
        // distinct steps and must carry DISTINCT display names so the Overview
        // never renders the same "Orchestrator-Review" twice.
        var p = PipelineCatalogue.Standard;
        var review = p.Post[0];
        Assert.Equal(PipelineCatalogue.OrchestratorReviewStepId, review.Id);
        Assert.Equal(PipelineCatalogue.PostCoreReviewDisplayName, review.DisplayName);
        Assert.Equal(StepKind.Orchestrator, review.Kind);
        Assert.Equal(StepRunMode.Sequential, review.RunMode);
        Assert.True(review.Idempotent);
        Assert.True(review.DefaultEnabled);
        Assert.Empty(review.DependsOn);

        // It precedes every aspect so the gate runs before the verdicts.
        var reviewIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorReviewStepId);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            var aspectIndex = p.Post.FindIndex(s => s.Id == aspectId);
            Assert.True(reviewIndex < aspectIndex,
                $"orchestrator-review (idx {reviewIndex}) must precede aspect {aspectId} (idx {aspectIndex})");
        }

        // The final decision row carries its OWN distinct display name, and the
        // two orchestrator-review rows never share a label.
        var decision = p.Post.First(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(PipelineCatalogue.FinalOrchestratorReviewDisplayName, decision.DisplayName);
        Assert.NotEqual(review.DisplayName, decision.DisplayName);
    }

    [Fact]
    public void StandardPipeline_CompletionGate_RunsBeforeTheAutoReviewDecision()
    {
        // Spec ASS-643/ASS-744 requirement #1: the completion gate must be visible
        // "vor/um die Auto-Review-Decision". The gate's completeness-check row
        // (OrchestratorReviewStepId) must therefore precede the final decision row
        // (OrchestratorDecisionStepId). This is the resolved placement contract:
        // commit-attribution runs at the 3->4 transition (before this gate, see
        // the GitCommitAttributionStepId Stub), the gate runs post-core, and the
        // decision is the last orchestrator ruling - so at runtime the gate sits
        // after attribution and before the decision exactly as the spec asked.
        var p = PipelineCatalogue.Standard;
        var gateIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorReviewStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);

        Assert.True(gateIndex >= 0, "completion-gate review row must exist in the post section");
        Assert.True(decisionIndex >= 0, "orchestrator-decision row must exist in the post section");
        Assert.True(gateIndex < decisionIndex,
            $"completion gate (idx {gateIndex}) must run before the auto-review decision (idx {decisionIndex})");
    }

    [Fact]
    public void StandardPipeline_LoopGuard_IsFirstStep_Deterministic_AndDefaultOn()
    {
        // Ralph-loop early detection: the loop guard ships as the single Pre
        // step so it leads AllSteps and the Overview renders it as the first
        // row ("frueh markiert"). It is deterministic (no model) and on by
        // default - the StuckLoopGuard breaker is a safety net, not opt-in.
        var p = PipelineCatalogue.Standard;
        var guard = p.Pre[0];
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, guard.Id);
        Assert.Equal(StepKind.Module, guard.Kind);
        Assert.True(guard.Idempotent);
        Assert.True(guard.DefaultEnabled);
        Assert.Null(guard.Model);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, p.AllSteps.First().Id);
    }

    [Fact]
    public void StandardPipeline_OrchestratorPrep_IsOptInParallelPreStep_ModelResolvedPerProject()
    {
        // ARCH: orchestrator-prep moved out of the 1a-orchestrator-prep backlog
        // lane into an optional, parallelisable pre-coding pipeline step. It is
        // a deterministic Module (no LLM today) that defaults OFF (opt-in per
        // project) and carries no hardcoded model, so the runtime resolves the
        // project's selected model via PipelineStepConfigResolver.
        var p = PipelineCatalogue.Standard;
        var prep = p.Pre.First(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);
        Assert.Equal(StepKind.Module, prep.Kind);
        Assert.Equal(StepRunMode.Parallel, prep.RunMode); // must not block throughput
        Assert.True(prep.Idempotent);
        Assert.False(prep.DefaultEnabled); // opt-in
        Assert.Null(prep.Model); // resolved per project, not hardcoded

        // Project model selection flows through when the step itself sets none.
        var settings = new ProjectSettings { OrchestratorModel = "claude-sonnet-4-5" };
        Assert.Equal(
            "claude-sonnet-4-5",
            PipelineStepConfigResolver.ResolveModel(settings, prep, "fallback-model"));
        // A per-step override still wins over the project model.
        var withOverride = new ProjectSettings
        {
            OrchestratorModel = "claude-sonnet-4-5",
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.PreOrchestratorPrepStepId] = new() { Enabled = true, Model = "claude-opus-4-1" },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(withOverride, prep));
        Assert.Equal(
            "claude-opus-4-1",
            PipelineStepConfigResolver.ResolveModel(withOverride, prep, "fallback-model"));
    }

    [Fact]
    public void StandardPipeline_ReissueOpenItems_IsDeterministicDefaultOnPreStep_AfterPrep()
    {
        // The reissue open-items check ships as a deterministic (no model)
        // Module pre-step that defaults ON - an unfinished re-issue is a
        // correctness signal, not an opt-in pass. It runs after the loop guard
        // and orchestrator-prep so it leads into the core run.
        var p = PipelineCatalogue.Standard;
        var step = p.Pre.First(s => s.Id == PipelineCatalogue.PreReissueOpenItemsStepId);
        Assert.Equal(StepKind.Module, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.True(step.Idempotent);
        Assert.True(step.DefaultEnabled);
        Assert.Null(step.Model);

        var prepIndex = p.Pre.FindIndex(s => s.Id == PipelineCatalogue.PreOrchestratorPrepStepId);
        var reissueIndex = p.Pre.FindIndex(s => s.Id == PipelineCatalogue.PreReissueOpenItemsStepId);
        Assert.True(reissueIndex > prepIndex,
            $"reissue open-items (idx {reissueIndex}) must come after orchestrator-prep (idx {prepIndex})");
    }

    [Fact]
    public void StandardPipeline_DriftSteps_AreOptInPostSteps_DefaultOff_AfterTheDecision()
    {
        // DRIFT Nachtrag: the five drift dimensions ship as Kind=Drift
        // post-steps that default OFF (an opt-in expensive pass) and run after
        // the auto-review decision so the task's own work is settled before
        // drift is measured.
        var p = PipelineCatalogue.Standard;
        var driftSteps = p.Post.Where(s => s.Kind == StepKind.Drift).ToList();
        Assert.Equal(5, driftSteps.Count);

        // The ids on the pipeline match the catalogue constants one-to-one.
        Assert.Equal(
            PipelineCatalogue.DriftStepIds.OrderBy(x => x, StringComparer.Ordinal),
            driftSteps.Select(s => s.Id).OrderBy(x => x, StringComparer.Ordinal));

        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        foreach (var step in driftSteps)
        {
            Assert.False(step.DefaultEnabled, $"{step.Id} drift step must default OFF (opt-in)");
            Assert.True(step.Idempotent, $"{step.Id} drift step must be idempotent");
            Assert.Contains(PipelineCatalogue.OrchestratorDecisionStepId, step.DependsOn);
            var stepIndex = p.Post.FindIndex(s => s.Id == step.Id);
            Assert.True(stepIndex > decisionIndex,
                $"{step.Id} (idx {stepIndex}) must come after the orchestrator-decision (idx {decisionIndex})");
        }
    }

    [Fact]
    public void StandardPipeline_AllFourAspectStepsRunParallel()
    {
        var p = PipelineCatalogue.Standard;
        var aspectSteps = p.Post.Where(s => s.Kind == StepKind.Aspect).ToList();
        Assert.Equal(4, aspectSteps.Count);
        foreach (var step in aspectSteps)
        {
            Assert.Equal(StepRunMode.Parallel, step.RunMode);
            Assert.True(step.Idempotent, $"{step.Id} aspect must be idempotent");
            // Aspect step ids on the pipeline are namespaced with the
            // `aspect-` prefix so they cannot collide with non-aspect ids;
            // strip the prefix to look up the underlying AspectDefinition.
            var bareId = step.Id.Substring("aspect-".Length);
            Assert.True(AspectRunnerService.Catalogue.ContainsKey(bareId),
                $"pipeline aspect step '{step.Id}' has no entry in AspectRunnerService.Catalogue");
        }
    }

    [Fact]
    public void StandardPipeline_GitCommitAttributionIsStub_WithAspectDependencies()
    {
        // ADR "Commit-Attribution-Regel": the deterministic attribution
        // behaviour is implemented in CommitAttributionService and runs from
        // the transition service on the 3-progress -> 4-auto-review move -
        // ahead of this executor's bracket. So within the executor the slot
        // stays a reserved record (Stub) that surfaces as "planned".
        var p = PipelineCatalogue.Standard;
        var commitStep = p.Post.First(s => s.Id == PipelineCatalogue.GitCommitAttributionStepId);
        Assert.Equal(StepKind.Tool, commitStep.Kind);
        Assert.True(commitStep.Stub);
        Assert.True(commitStep.Idempotent);
        // Commit attribution reads aspect MDs, so it depends on every aspect.
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, commitStep.DependsOn);
        }
    }

    [Fact]
    public void StandardPipeline_MergeIntoDevelop_IsDeferredGitStep_RightAfterCommitCollection()
    {
        // ASS-1721: the "Merge into Develop" step ships as a deferred,
        // operator-triggered Tool post-step placed right after the
        // commit-collection slot (GitCommitAttribution). It is Deferred (not a
        // Stub): the implementation exists (MergeIntoDevelopRunner) but only runs
        // when the operator accepts the task, so the pipeline view shows it as a
        // not-yet-run (pending) step until then. It is a git step, so the
        // read-only pipeline drops it.
        var p = PipelineCatalogue.Standard;
        var merge = p.Post.First(s => s.Id == PipelineCatalogue.MergeIntoDevelopStepId);

        Assert.Equal("post-merge-into-develop", merge.Id);
        Assert.Equal("Merge into Develop", merge.DisplayName);
        Assert.Equal(StepKind.Tool, merge.Kind);
        Assert.Equal(StepRunMode.Sequential, merge.RunMode);
        Assert.True(merge.Deferred, "merge-into-develop must be a deferred (operator-triggered) step");
        Assert.False(merge.Stub, "merge-into-develop is implemented, not a stub");
        Assert.True(merge.Idempotent);
        Assert.True(merge.DefaultEnabled);
        Assert.Contains(PipelineCatalogue.GitCommitAttributionStepId, merge.DependsOn);

        // Ordered immediately after the commit-collection slot.
        var commitIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.GitCommitAttributionStepId);
        var mergeIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.MergeIntoDevelopStepId);
        Assert.True(commitIndex >= 0 && mergeIndex >= 0);
        Assert.Equal(commitIndex + 1, mergeIndex);

        // It is a git step (read-only pipeline drops it).
        Assert.Contains(PipelineCatalogue.MergeIntoDevelopStepId, PipelineCatalogue.GitStepIds);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.Post, s => s.Id == PipelineCatalogue.MergeIntoDevelopStepId);
    }

    [Fact]
    public void StandardPipeline_MergeIntoDevelopPush_IsDeferredGitStep_RightAfterTheMerge()
    {
        // AGT-1999: the integration-branch push ships as a deferred,
        // operator-triggered Tool post-step placed immediately after the
        // "Merge into Develop" step - the push only makes sense once the merge
        // has landed. Like the merge it is Deferred (runs on the accept trigger,
        // off the request path) and default-on, and it is a git step so the
        // read-only pipeline drops it.
        var p = PipelineCatalogue.Standard;
        var push = p.Post.First(s => s.Id == PipelineCatalogue.MergeIntoDevelopPushStepId);

        Assert.Equal("post-merge-into-develop-push", push.Id);
        Assert.Equal("Push develop to origin", push.DisplayName);
        Assert.Equal(StepKind.Tool, push.Kind);
        Assert.Equal(StepRunMode.Sequential, push.RunMode);
        Assert.True(push.Deferred, "merge-into-develop push must be a deferred (operator-triggered) step");
        Assert.False(push.Stub, "merge-into-develop push is implemented, not a stub");
        Assert.True(push.Idempotent);
        Assert.True(push.DefaultEnabled);
        Assert.Contains(PipelineCatalogue.MergeIntoDevelopStepId, push.DependsOn);

        // Ordered immediately after the merge step.
        var mergeIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.MergeIntoDevelopStepId);
        var pushIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.MergeIntoDevelopPushStepId);
        Assert.True(mergeIndex >= 0 && pushIndex >= 0);
        Assert.Equal(mergeIndex + 1, pushIndex);

        // It is a git step (read-only pipeline drops it).
        Assert.Contains(PipelineCatalogue.MergeIntoDevelopPushStepId, PipelineCatalogue.GitStepIds);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.Post, s => s.Id == PipelineCatalogue.MergeIntoDevelopPushStepId);

        // Default-on, but an operator can disable it per project.
        Assert.True(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, PipelineCatalogue.MergeIntoDevelopPushStepId));
        var disabled = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.MergeIntoDevelopPushStepId] = new() { Enabled = false },
            },
        };
        Assert.False(PipelineStepConfigResolver.IsEnabled(disabled, PipelineCatalogue.MergeIntoDevelopPushStepId));
    }

    [Fact]
    public void StandardPipeline_WorktreeIntegrationSteps_AreVisibleGitSteps()
    {
        var p = PipelineCatalogue.Standard;
        var containment = p.Post.First(s => s.Id == PipelineCatalogue.WorktreeContainmentStepId);
        var integrate = p.Post.First(s => s.Id == PipelineCatalogue.IntegrateMergeStepId);
        var resolution = p.Post.First(s => s.Id == PipelineCatalogue.ConflictResolutionStepId);

        Assert.Equal("Worktree containment", containment.DisplayName);
        Assert.Equal(StepKind.Tool, containment.Kind);
        Assert.False(containment.Stub);

        Assert.Equal("Integrate merge", integrate.DisplayName);
        Assert.Equal(StepKind.Tool, integrate.Kind);
        Assert.False(integrate.Stub);
        Assert.Contains(PipelineCatalogue.WorktreeContainmentStepId, integrate.DependsOn);

        Assert.Equal("Conflict resolution", resolution.DisplayName);
        Assert.Equal(StepKind.Orchestrator, resolution.Kind);
        Assert.False(resolution.Stub);
        Assert.False(resolution.Idempotent);
        Assert.Contains(PipelineCatalogue.IntegrateMergeStepId, resolution.DependsOn);

        var containmentIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.WorktreeContainmentStepId);
        var integrateIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.IntegrateMergeStepId);
        var resolutionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.ConflictResolutionStepId);
        Assert.True(containmentIndex < integrateIndex);
        Assert.True(integrateIndex < resolutionIndex);

        Assert.Contains(PipelineCatalogue.WorktreeContainmentStepId, PipelineCatalogue.GitStepIds);
        Assert.Contains(PipelineCatalogue.IntegrateMergeStepId, PipelineCatalogue.GitStepIds);
        Assert.Contains(PipelineCatalogue.ConflictResolutionStepId, PipelineCatalogue.GitStepIds);
    }

    [Fact]
    public void StandardPipeline_LintScss_IsToolStep_WithAspectDependencies_AndNotAStub()
    {
        // ASS-563: lint-scss must run after the aspect verdicts (so the
        // pre-existing aspect work is not wasted by an early lint reissue)
        // but before the orchestrator decision (so a fail verdict can
        // route through the reissue path instead of accept-as-done).
        // Unlike commit-attribution, this step is implemented today.
        var p = PipelineCatalogue.Standard;
        var lintStep = p.Post.First(s => s.Id == PipelineCatalogue.LintScssStepId);
        Assert.Equal(StepKind.Tool, lintStep.Kind);
        Assert.False(lintStep.Stub);
        Assert.True(lintStep.Idempotent);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, lintStep.DependsOn);
        }

        // The orchestrator-decision step must come AFTER lint-scss in the
        // Post list, so a deterministic executor runs lint first and the
        // decision step sees its verdict.
        var lintIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.LintScssStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(lintIndex < decisionIndex,
            $"lint-scss (idx {lintIndex}) must precede orchestrator-decision (idx {decisionIndex}) in Post list");
    }

    [Fact]
    public void StandardPipeline_BuildTestGate_IsToolStep_BeforeAspects_AndNotAStub()
    {
        // Deterministic build/test gate: runs after the post-core completion
        // scan and before aspect review so a broken compile short-circuits
        // before LLM review or accept-as-done can trust self-reported Success.
        var p = PipelineCatalogue.Standard;
        var step = p.Post.First(s => s.Id == PipelineCatalogue.BuildTestGateStepId);
        Assert.Equal(StepKind.Tool, step.Kind);
        Assert.False(step.Stub);
        Assert.True(step.Idempotent);
        Assert.Contains(PipelineCatalogue.CoreAgentRunStepId, step.DependsOn);

        var reviewIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorReviewStepId);
        var buildIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.BuildTestGateStepId);
        Assert.True(reviewIndex < buildIndex,
            $"orchestrator-review (idx {reviewIndex}) must precede build/test gate (idx {buildIndex})");
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            var aspectIndex = p.Post.FindIndex(s => s.Id == aspectId);
            Assert.True(buildIndex < aspectIndex,
                $"build/test gate (idx {buildIndex}) must precede aspect {aspectId} (idx {aspectIndex})");
        }
    }

    [Fact]
    public void StandardPipeline_RegressionRadar_IsToolStep_WithAspectDependencies_AndNotAStub()
    {
        // Regression radar runs as a deterministic Tool post-step after the
        // aspect verdicts and before the orchestrator decision, mirroring the
        // lint-scss slot. Unlike commit-attribution it is implemented today,
        // so it is not a stub. It is reporting-only (no reissue), but it still
        // depends on the aspects so a DAG-resolver schedules it after them.
        var p = PipelineCatalogue.Standard;
        var radarStep = p.Post.First(s => s.Id == PipelineCatalogue.RegressionRadarStepId);
        Assert.Equal(StepKind.Tool, radarStep.Kind);
        Assert.False(radarStep.Stub);
        Assert.True(radarStep.Idempotent);
        Assert.True(radarStep.DefaultEnabled); // default-on, configurable off per project
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, radarStep.DependsOn);
        }

        // Ordered between lint-scss and the orchestrator decision.
        var lintIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.LintScssStepId);
        var radarIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.RegressionRadarStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(lintIndex < radarIndex,
            $"lint-scss (idx {lintIndex}) must precede regression-radar (idx {radarIndex})");
        Assert.True(radarIndex < decisionIndex,
            $"regression-radar (idx {radarIndex}) must precede orchestrator-decision (idx {decisionIndex})");
    }

    [Fact]
    public void StandardPipeline_WikiLearnings_IsOptInToolStep_AfterAspects_BeforeDecision()
    {
        // ASS-1694: the wiki-learnings distillation ships as a deterministic
        // (no model) Tool post-step that defaults OFF - knowledge distillation is
        // an opt-in pass an operator turns on per project, like wiki-maintenance.
        // It reads the aspect verdicts it distills, so it depends on every aspect
        // and is ordered after them but before the final orchestrator decision.
        var p = PipelineCatalogue.Standard;
        var step = p.Post.First(s => s.Id == PipelineCatalogue.WikiLearningsStepId);
        Assert.Equal("post-wiki-learnings", step.Id);
        Assert.Equal(StepKind.Tool, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.False(step.Stub);
        Assert.True(step.Idempotent);
        Assert.False(step.DefaultEnabled); // opt-in
        Assert.Null(step.Model); // deterministic, no LLM
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, step.DependsOn);
        }

        var learningsIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.WikiLearningsStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(learningsIndex < decisionIndex,
            $"wiki-learnings (idx {learningsIndex}) must precede orchestrator-decision (idx {decisionIndex})");
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            var aspectIndex = p.Post.FindIndex(s => s.Id == aspectId);
            Assert.True(aspectIndex < learningsIndex,
                $"aspect {aspectId} (idx {aspectIndex}) must precede wiki-learnings (idx {learningsIndex})");
        }

        // Opt-in gate: default off, but a per-project override turns it on.
        Assert.False(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.WikiLearningsStepId] = new() { Enabled = true },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(settings, step));
    }

    [Fact]
    public void StandardPipeline_AgentsWikiSync_IsOptInToolStep_AfterWikiLearnings_BeforeDecision()
    {
        // AGT-1782: the AGENTS/wiki-sync step ships as a deterministic (no model)
        // Tool post-step that defaults OFF - keeping the designated-topic pointers
        // consistent and collecting their current state is an opt-in per-project
        // pass, like wiki-maintenance and wiki-learnings. It is keyed off the
        // task's own change set, so it depends on the core run (not the aspect
        // verdicts) and sits with the sibling wiki steps before the final decision.
        var p = PipelineCatalogue.Standard;
        var step = p.Post.First(s => s.Id == PipelineCatalogue.AgentsWikiSyncStepId);
        Assert.Equal("post-agents-wiki-sync", step.Id);
        Assert.Equal(StepKind.Tool, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.False(step.Stub);
        Assert.False(step.Deferred);
        Assert.True(step.Idempotent);
        Assert.False(step.DefaultEnabled); // opt-in
        Assert.Null(step.Model); // deterministic, no LLM
        Assert.Contains(PipelineCatalogue.CoreAgentRunStepId, step.DependsOn);

        var syncIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.AgentsWikiSyncStepId);
        var learningsIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.WikiLearningsStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(learningsIndex < syncIndex,
            $"wiki-learnings (idx {learningsIndex}) must precede agents/wiki-sync (idx {syncIndex})");
        Assert.True(syncIndex < decisionIndex,
            $"agents/wiki-sync (idx {syncIndex}) must precede orchestrator-decision (idx {decisionIndex})");

        // Document-only planning/research runs do not run Wiki automation.
        Assert.DoesNotContain(PipelineCatalogue.AgentsWikiSyncStepId, PipelineCatalogue.GitStepIds);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.Post, s => s.Id == PipelineCatalogue.AgentsWikiSyncStepId);

        // Opt-in gate: default off, but a per-project override turns it on.
        Assert.False(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.AgentsWikiSyncStepId] = new() { Enabled = true },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(settings, step));
    }

    [Fact]
    public void FrontendStylelint_IsMarkedAngularSpecific()
    {
        var step = PipelineCatalogue.Standard.Post.Single(candidate =>
            candidate.Id == PipelineCatalogue.LintScssStepId);

        Assert.Equal(PipelineStepStacks.Angular, step.AppliesTo);
    }

    [Fact]
    public void StandardPipeline_ModelQualification_IsVisibleDefaultOnPreStepBeforeExecution()
    {
        var p = PipelineCatalogue.Standard;
        var step = p.Pre.Single(s => s.Id == PipelineCatalogue.ModelQualificationStepId);
        Assert.Equal("Model qualification", step.DisplayName);
        Assert.Equal(StepKind.Module, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.True(step.DefaultEnabled);
        Assert.True(step.Idempotent);
        Assert.Null(step.Model);
        Assert.True(p.Pre.FindIndex(s => s.Id == step.Id) < p.AllSteps.ToList().FindIndex(s => s.Kind == StepKind.Core));
    }

    [Fact]
    public void StandardPipeline_TaskSpawner_IsOptInOrchestratorStep_AfterAspects_BeforeDecision()
    {
        // AGT-2028: the task-spawner ships as an opt-in Orchestrator post-step
        // (an LLM relevance judgment, like the grade/decision steps) that defaults
        // OFF - spawning a follow-up card in another project is an explicit
        // per-project activation. It reads the settled change set, so it depends
        // on every aspect and is ordered after them but before the final decision.
        var p = PipelineCatalogue.Standard;
        var step = p.Post.First(s => s.Id == PipelineCatalogue.TaskSpawnerStepId);
        Assert.Equal("post-task-spawner", step.Id);
        Assert.Equal(StepKind.Orchestrator, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.False(step.Stub);
        Assert.False(step.Deferred);
        Assert.True(step.Idempotent);
        Assert.False(step.DefaultEnabled); // opt-in (Default aus)
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, step.DependsOn);
        }

        var spawnerIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.TaskSpawnerStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(spawnerIndex < decisionIndex,
            $"task-spawner (idx {spawnerIndex}) must precede orchestrator-decision (idx {decisionIndex})");
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            var aspectIndex = p.Post.FindIndex(s => s.Id == aspectId);
            Assert.True(aspectIndex < spawnerIndex,
                $"aspect {aspectId} (idx {aspectIndex}) must precede task-spawner (idx {spawnerIndex})");
        }

        // Document-only planning/research runs do not spawn follow-up tasks.
        Assert.DoesNotContain(PipelineCatalogue.TaskSpawnerStepId, PipelineCatalogue.GitStepIds);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.Post, s => s.Id == PipelineCatalogue.TaskSpawnerStepId);

        // Opt-in gate: default off, but a per-project override turns it on.
        Assert.False(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        var settings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.TaskSpawnerStepId] = new() { Enabled = true },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(settings, step));
    }

    [Fact]
    public void StandardPipeline_CodeReviewGrade_IsDefaultOnReviewStep_AfterAspects_BeforeDecision()
    {
        // ASS-1657: the automatic code-review quality-grade step ships as a
        // first-class post-step that runs after the parallel aspect verdicts
        // and before the final orchestrator decision. It is default-on (every
        // pipelined task carries a grade), reuses the Orchestrator kind (an LLM
        // ruling, like the decision step), and depends on every aspect so a
        // DAG-resolver schedules it after the verdicts.
        var p = PipelineCatalogue.Standard;
        var step = p.Post.First(s => s.Id == PipelineCatalogue.CodeReviewGradeStepId);
        Assert.Equal("post-code-review-grade", step.Id);
        Assert.Equal(StepKind.Orchestrator, step.Kind);
        Assert.Equal(StepRunMode.Sequential, step.RunMode);
        Assert.True(step.Idempotent);
        Assert.True(step.DefaultEnabled); // every task gets a grade
        Assert.False(step.Stub);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, step.DependsOn);
        }

        var gradeIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.CodeReviewGradeStepId);
        var decisionIndex = p.Post.FindIndex(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.True(gradeIndex < decisionIndex,
            $"code-review grade (idx {gradeIndex}) must precede orchestrator-decision (idx {decisionIndex})");
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            var aspectIndex = p.Post.FindIndex(s => s.Id == aspectId);
            Assert.True(aspectIndex < gradeIndex,
                $"aspect {aspectId} (idx {aspectIndex}) must precede code-review grade (idx {gradeIndex})");
        }

        // Document-only planning/research runs do not receive a code grade.
        Assert.DoesNotContain(PipelineCatalogue.CodeReviewGradeStepId, PipelineCatalogue.GitStepIds);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.Post, s => s.Id == PipelineCatalogue.CodeReviewGradeStepId);

        // Default-on, but an operator can disable it per project.
        Assert.True(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        var disabled = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.CodeReviewGradeStepId] = new() { Enabled = false },
            },
        };
        Assert.False(PipelineStepConfigResolver.IsEnabled(disabled, step));
    }

    [Fact]
    public void StandardPipeline_OrchestratorDecision_DependsOnAllAspects()
    {
        var p = PipelineCatalogue.Standard;
        var decisionStep = p.Post.First(s => s.Id == PipelineCatalogue.OrchestratorDecisionStepId);
        Assert.Equal(StepKind.Orchestrator, decisionStep.Kind);
        Assert.True(decisionStep.Idempotent);
        foreach (var aspectId in PipelineCatalogue.AspectStepIds)
        {
            Assert.Contains(aspectId, decisionStep.DependsOn);
        }
    }

    [Fact]
    public void AbortReviewStep_IsOptOutOrchestratorStep_NotInLinearPostBracket()
    {
        // The "Abbruch-Review" step is abort-triggered, so it must NOT sit in
        // the always-runs Post bracket (otherwise a generic post-step executor
        // would run it on every clean completion). It is exposed as a
        // standalone definition that defaults ON (opt-out per project, since
        // 2026-07-05 - was opt-in/off under ADR-0032) and is model-resolvable
        // like the other LLM steps.
        var step = PipelineCatalogue.AbortReviewStep;
        Assert.Equal(PipelineCatalogue.PostAbortReviewStepId, step.Id);
        Assert.Equal(StepKind.Orchestrator, step.Kind);
        Assert.True(step.Idempotent);
        Assert.True(step.DefaultEnabled); // opt-out

        // Absent from every section of both pipelines.
        Assert.DoesNotContain(PipelineCatalogue.Standard.AllSteps, s => s.Id == PipelineCatalogue.PostAbortReviewStepId);
        Assert.DoesNotContain(PipelineCatalogue.ReadOnly.AllSteps, s => s.Id == PipelineCatalogue.PostAbortReviewStepId);

        // Opt-out gate + per-project model resolution use the same resolver as
        // the other steps: default on, but a project override can turn it off
        // (or just override the model while staying enabled).
        Assert.True(PipelineStepConfigResolver.IsEnabled((ProjectSettings?)null, step));
        var disabledSettings = new ProjectSettings
        {
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.PostAbortReviewStepId] = new() { Enabled = false },
            },
        };
        Assert.False(PipelineStepConfigResolver.IsEnabled(disabledSettings, step));

        var settings = new ProjectSettings
        {
            OrchestratorModel = "claude-sonnet-4-5",
            PipelineSteps = new Dictionary<string, PipelineStepSetting>
            {
                [PipelineCatalogue.PostAbortReviewStepId] = new() { Enabled = true, Model = "claude-opus-4-1" },
            },
        };
        Assert.True(PipelineStepConfigResolver.IsEnabled(settings, step));
        Assert.Equal("claude-opus-4-1", PipelineStepConfigResolver.ResolveModel(settings, step, "fallback"));
    }

    [Fact]
    public void Get_ReturnsStandardForCanonicalId_NullForUnknown()
    {
        Assert.NotNull(PipelineCatalogue.Get(PipelineCatalogue.StandardPipelineId));
        Assert.NotNull(PipelineCatalogue.Get("STANDARD-TASK-PIPELINE")); // case-insensitive
        Assert.NotNull(PipelineCatalogue.Get(PipelineCatalogue.ReadOnlyPipelineId));
        Assert.NotNull(PipelineCatalogue.Get(PipelineCatalogue.ConceptPipelineId));
        Assert.Null(PipelineCatalogue.Get("does-not-exist"));
    }

    [Fact]
    public void ReadOnlyPipeline_IsLightweightAndOmitsCodeGates()
    {
        var ro = PipelineCatalogue.ReadOnly;

        Assert.Equal(PipelineCatalogue.ReadOnlyPipelineId, ro.Id);
        Assert.Single(ro.Core);
        Assert.Equal(PipelineCatalogue.CoreAgentRunStepId, ro.Core[0].Id);
        Assert.Equal(5, ro.Pre.Count);
        Assert.Equal(PipelineCatalogue.LoopGuardStepId, ro.Pre[0].Id);
        Assert.Equal(PipelineCatalogue.ModelQualificationStepId, ro.Pre[1].Id);
        Assert.Equal(PipelineCatalogue.PromptEnrichmentStepId, ro.Pre[2].Id);
        Assert.Equal(PipelineCatalogue.PreOrchestratorPrepStepId, ro.Pre[3].Id);
        Assert.Equal(PipelineCatalogue.PreReissueOpenItemsStepId, ro.Pre[4].Id);
        Assert.Equal(
            [PipelineCatalogue.OrchestratorReviewStepId, PipelineCatalogue.OrchestratorDecisionStepId],
            ro.Post.Select(step => step.Id));

        Assert.DoesNotContain(ro.AllSteps, step =>
            PipelineCatalogue.GitStepIds.Contains(step.Id)
            || PipelineCatalogue.AspectStepIds.Contains(step.Id)
            || step.Id == PipelineCatalogue.BuildTestGateStepId
            || step.Id == PipelineCatalogue.LintScssStepId
            || step.Id == PipelineCatalogue.CodeReviewGradeStepId
            || step.Id == PipelineCatalogue.RegressionRadarStepId
            || step.Id.Contains("drift", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void ForMode_SelectsReadOnlyForPlanningAndResearch_StandardOtherwise()
    {
        Assert.Same(PipelineCatalogue.ReadOnly, PipelineCatalogue.ForMode("planning"));
        Assert.Same(PipelineCatalogue.ReadOnly, PipelineCatalogue.ForMode("research"));
        Assert.Same(PipelineCatalogue.Concept, PipelineCatalogue.ForMode("concept"));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode("coding"));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode(null));
        Assert.Same(PipelineCatalogue.Standard, PipelineCatalogue.ForMode("anything-else"));
    }

    [Fact]
    public void ForTask_SelectsByCardTypeAndKeepsPlanningReadOnly()
    {
        Assert.Same(
            PipelineCatalogue.Standard,
            PipelineCatalogue.ForTask(TaskTypes.Bug, TaskModes.Coding));
        Assert.Same(
            PipelineCatalogue.Standard,
            PipelineCatalogue.ForTask(TaskTypes.Feature, TaskModes.Coding));
        Assert.Same(
            PipelineCatalogue.ReadOnly,
            PipelineCatalogue.ForTask(TaskTypes.Feature, TaskModes.Planning));
        Assert.Same(
            PipelineCatalogue.ReadOnly,
            PipelineCatalogue.ForTask(TaskTypes.Bug, TaskModes.Research));
        Assert.Same(
            PipelineCatalogue.Concept,
            PipelineCatalogue.ForTask(TaskTypes.Feature, TaskModes.Concept));
    }

    [Fact]
    public void ConceptPipeline_IsDocumentReviewThenSightGateThenPromotion()
    {
        var concept = PipelineCatalogue.Concept;

        Assert.Empty(concept.Pre);
        Assert.Equal(
            [
                PipelineCatalogue.CoreAgentRunStepId,
                PipelineCatalogue.ConceptWorkbenchPlacementStepId,
                PipelineCatalogue.ConceptReviewStepId,
                PipelineCatalogue.ConceptSightReviewGateStepId,
                PipelineCatalogue.ConceptPromotionStepId,
            ],
            concept.AllSteps.Select(step => step.Id));
        Assert.DoesNotContain(concept.AllSteps, step =>
            step.Kind == StepKind.Aspect
            || step.Id.Contains("build", StringComparison.OrdinalIgnoreCase)
            || PipelineCatalogue.GitStepIds.Contains(step.Id));
        Assert.True(concept.Post[^1].Deferred);
    }

    [Fact]
    public void ReadOnlyPipeline_HasNoDanglingDependsOnEdges()
    {
        // The deliberately small document DAG must not depend on omitted code
        // gates or aspect steps.
        var ro = PipelineCatalogue.ReadOnly;
        var presentIds = ro.AllSteps.Select(s => s.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var step in ro.AllSteps)
            foreach (var dep in step.DependsOn)
                Assert.Contains(dep, presentIds);
    }

    [Fact]
    public void AspectStepIds_MatchAspectRunnerCatalogueOneToOne()
    {
        // The four pipeline aspect step ids must each map to an entry in
        // AspectRunnerService.Catalogue (with the "aspect-" prefix stripped).
        // Without this guard the pipeline could list aspects the runner
        // does not know how to execute (silent skip + missing aspect MD).
        foreach (var stepId in PipelineCatalogue.AspectStepIds)
        {
            Assert.StartsWith("aspect-", stepId);
            var bareId = stepId.Substring("aspect-".Length);
            Assert.True(AspectRunnerService.Catalogue.ContainsKey(bareId),
                $"AspectStepIds entry '{stepId}' has no underlying AspectDefinition");
        }
        // The reverse direction: every aspect definition the runner knows
        // about should appear on the pipeline. Drift here means the
        // pipeline view would miss an aspect that actually runs.
        foreach (var bareId in AspectRunnerService.Catalogue.Keys)
        {
            var pipelineStepId = $"aspect-{bareId}";
            Assert.Contains(pipelineStepId, PipelineCatalogue.AspectStepIds);
        }
    }
}
