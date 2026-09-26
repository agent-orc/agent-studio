using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Outcome-fixture coverage for <see cref="IntakeRunner"/>. The pure
/// <see cref="IntakeRunner.Evaluate"/> surface is the regression boundary
/// for the typed verdict matrix promised by
/// <c>ready-orchestrator-intake-lane</c>: pass / needs-clarification /
/// duplicate-candidate / needs-split / blocked. Each outcome is pinned by
/// a fixture so a future swap from heuristic to model-based intake cannot
/// silently change the public contract that the runner pickup gate, the
/// frontend lane projection, and the activity log all key off.
///
/// <para>
/// The integration test at the bottom drives <see cref="IntakeRunner.RunForJob"/>
/// against a temp watch path so the state-transition contract is also
/// covered end-to-end: phase moves human-ready -> intake-passed on Pass
/// and human-ready -> intake-blocked on a non-Pass verdict, plus a
/// <c>lifecycle.json</c> sidecar lands next to <c>task.json</c>.
/// </para>
/// </summary>
public class IntakeRunnerTests : IDisposable
{
    private readonly string _watchPath;

    public IntakeRunnerTests()
    {
        _watchPath = Path.Combine(Path.GetTempPath(), "rdo-intake-tests-" + Guid.NewGuid().ToString("N"));
        foreach (var state in TaskStates.All)
        {
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_watchPath, recursive: true); } catch { /* best-effort */ }
    }

    // ---- Pure Evaluate matrix ------------------------------------------------

    [Fact]
    public void Evaluate_HealthyPrompt_Passes()
    {
        var target = new TaskInfo { Id = "alpha", Title = "Add a daily token rollup card", State = TaskStates.Ready };
        var prompt = "Add a daily token rollup section to the project header. " +
                     "Acceptance: chip shows total tokens for the last 24 hours.";

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Pass, v.Outcome);
    }

    [Fact]
    public void Evaluate_TooShortPrompt_NeedsClarification()
    {
        var target = new TaskInfo { Id = "thin", Title = "fix it", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, "fix it", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsClarification, v.Outcome);
        Assert.Contains("short", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_NearDuplicateTitle_FlagsAsDuplicateCandidate()
    {
        var target = new TaskInfo
        {
            Id = "dup-new",
            Title = "Add daily token rollup card to project header",
            State = TaskStates.Ready
        };
        var peer = new TaskInfo
        {
            Id = "dup-old",
            Title = "Add daily token rollup card project header",
            State = TaskStates.Ready
        };

        var v = IntakeRunner.Evaluate(target, "Add daily token rollup card to project header. Done when the chip is visible.", new[] { peer });

        Assert.Equal(IntakeOutcome.DuplicateCandidate, v.Outcome);
        Assert.Contains("dup-old", v.Details ?? Array.Empty<string>());
    }

    [Fact]
    public void Evaluate_ParallelCodingPrompt_NoLongerBlocked()
    {
        // ADR-0052 reversed the intra-project-parallelism non-goal: bounded
        // parallel execution via worktrees + per-task branches is now an opt-in
        // capability, so prompts about it must NOT be hard-blocked at intake.
        // Regression guard for the reversal.
        var target = new TaskInfo { Id = "para", Title = "Add intra-project parallelism to the runner", State = TaskStates.Ready };
        var prompt = "Add bounded intra-project parallel execution via git worktrees and per-task branches. Acceptance: maxParallelism is configurable per project and the orchestrator decides parallelizability.";

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.NotEqual(IntakeOutcome.Blocked, v.Outcome);
    }

    [Fact]
    public void Evaluate_PromptWithSixLevel2Headings_NeedsSplit()
    {
        var target = new TaskInfo { Id = "fan-out", Title = "Refactor the runner across many surfaces", State = TaskStates.Ready };
        var prompt = string.Join('\n', new[]
        {
            "Top-level rewrite of the runner pickup loop with several independent deliverables.",
            "## Add metric A",
            "Body for metric A so the section is not empty.",
            "## Add metric B",
            "Body B.",
            "## Add metric C",
            "Body C.",
            "## Add metric D",
            "Body D.",
            "## Add metric E",
            "Body E.",
            "## Add metric F",
            "Body F."
        });

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.NeedsSplit, v.Outcome);
    }

    // (Removed Evaluate_BlockedTakesPriorityOverDuplicate: it tested the
    //  priority of the hard non-goal block over duplicate detection. ADR-0052
    //  removed the intra-project-parallelism block, so there is no longer a
    //  hard-block outcome to prioritise here.)

    // ---- Done-precheck matrix -----------------------------------------------

    [Theory]
    [InlineData("This feature is already implemented on main; nothing to build.")]
    [InlineData("Heads up: the rollup card has been shipped in the last release.")]
    [InlineData("Das Token-Rollup wurde bereits umgesetzt und ist live.")]
    public void Evaluate_PromptDeclaringWorkDone_FlagsAlreadyDone(string prompt)
    {
        var target = new TaskInfo { Id = "done-1", Title = "Add daily token rollup card", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.AlreadyDone, v.Outcome);
    }

    [Theory]
    [InlineData("Make sure this is already implemented and add a test if it is not.")] // 'make sure' + 'not' guard
    [InlineData("Verify the endpoint already done before wiring the UI.")] // 'verify' guard
    [InlineData("This is not yet implemented, so build it.")] // negation; also no 'already implemented' bigram
    [InlineData("Add a daily token rollup card. Acceptance: chip shows totals for the last 24h.")] // no done-signal at all
    public void Evaluate_AmbiguousOrNegatedDoneLanguage_DoesNotFlagAlreadyDone(string prompt)
    {
        var target = new TaskInfo { Id = "guarded", Title = "Add daily token rollup card", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, prompt, Array.Empty<TaskInfo>());

        Assert.NotEqual(IntakeOutcome.AlreadyDone, v.Outcome);
    }

    [Fact]
    public void Evaluate_AlreadyDoneBeatsClarityOnAShortPrompt()
    {
        // A done card may have a terse prompt; the done-precheck must win over
        // the clarity probe so the card is surfaced as already-done, not as
        // needs-clarification.
        var target = new TaskInfo { Id = "short-done", Title = "Rollup", State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(target, "already done.", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.AlreadyDone, v.Outcome);
    }

    // ---- Consistency-check matrix (requirement 3) ----------------------------

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("Untitled")]
    [InlineData("TBD")]
    [InlineData("new task")]
    public void Evaluate_PlaceholderOrEmptyTitle_FlagsInconsistent(string title)
    {
        var target = new TaskInfo { Id = "no-goal", Title = title, State = TaskStates.Ready };

        var v = IntakeRunner.Evaluate(
            target, "A prompt long enough to clear the clarity probe comfortably.", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Inconsistent, v.Outcome);
        Assert.Contains("title", v.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Evaluate_SelfReferentialReference_FlagsInconsistent()
    {
        var target = new TaskInfo
        {
            Id = "self-ref",
            TaskKey = "ATP-7",
            Title = "Add the rollup card",
            State = TaskStates.Ready,
            References = new TaskReferences { RelatedTo = ["ATP-7"] }
        };

        var v = IntakeRunner.Evaluate(
            target, "Add the rollup card to the header. Done when the chip is visible.", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Inconsistent, v.Outcome);
        Assert.Contains("ATP-7", v.Reason);
    }

    [Fact]
    public void Evaluate_BlockedByWhileQueuedReady_FlagsInconsistent()
    {
        // A card sitting in 2-ready (pickup queue) that still declares it is
        // blockedBy something is contradictory: it should not be runnable yet.
        var target = new TaskInfo
        {
            Id = "blocked-ready",
            TaskKey = "ATP-8",
            Title = "Wire the export button",
            State = TaskStates.Ready,
            References = new TaskReferences { BlockedBy = ["ATP-3"] }
        };

        var v = IntakeRunner.Evaluate(
            target, "Wire the export button to the report endpoint. Done when a CSV downloads.", Array.Empty<TaskInfo>());

        Assert.Equal(IntakeOutcome.Inconsistent, v.Outcome);
        Assert.Contains("ATP-3", v.Reason);
    }

    [Fact]
    public void Evaluate_CoherentCardWithNonBlockingReferences_PassesConsistency()
    {
        // dependsOn / relatedTo / supersedes edges to OTHER tasks are normal and
        // must not trip the consistency check — only self-reference and
        // blockedBy-while-ready do.
        var target = new TaskInfo
        {
            Id = "coherent",
            TaskKey = "ATP-9",
            Title = "Add the rollup card",
            State = TaskStates.Ready,
            References = new TaskReferences { DependsOn = ["ATP-1"], RelatedTo = ["ATP-2"] }
        };

        var v = IntakeRunner.Evaluate(
            target, "Add the rollup card to the header. Done when the chip shows totals.", Array.Empty<TaskInfo>());

        Assert.NotEqual(IntakeOutcome.Inconsistent, v.Outcome);
        Assert.Equal(IntakeOutcome.Pass, v.Outcome);
    }

    // ---- Context-load matrix (requirement 4) ---------------------------------

    [Fact]
    public void BuildContextManifest_ResolvesKnownReferences_AndFlagsMissing()
    {
        var target = new TaskInfo
        {
            Id = "ctx",
            TaskKey = "ATP-20",
            Title = "Add the rollup card",
            Tags = ["ui", "metrics"],
            References = new TaskReferences { DependsOn = ["ATP-1"], RelatedTo = ["ATP-404"] }
        };
        var known = new List<TaskInfo>
        {
            new() { Id = "dep", TaskKey = "ATP-1", Title = "the dependency" }
        };

        var manifest = IntakeRunner.BuildContextManifest(target, "no attachments here", known, Array.Empty<string>());

        Assert.Contains("dependsOn:ATP-1", manifest.ResolvedReferences);
        Assert.Contains("relatedTo:ATP-404", manifest.MissingReferences);
        Assert.Equal(new[] { "ui", "metrics" }, manifest.Tags);
        Assert.False(manifest.IsComplete);
    }

    [Fact]
    public void BuildContextManifest_ClassifiesAttachmentsByDiskPresence()
    {
        var target = new TaskInfo { Id = "att", Title = "Wire the importer" };
        var prompt = "Follow the design in attachments/spec.md and the icon attachments/missing.png.";

        var manifest = IntakeRunner.BuildContextManifest(
            target, prompt, Array.Empty<TaskInfo>(), new[] { "spec.md" });

        Assert.Contains("attachments/spec.md", manifest.ResolvedAttachments);
        Assert.Contains("attachments/missing.png", manifest.MissingAttachments);
        Assert.False(manifest.IsComplete);
    }

    // ---- Constraint/context enrichment --------------------------------------

    [Fact]
    public void BuildEnrichmentManifest_GitRunnerTask_IncludesGitHandlingConstraint()
    {
        var target = new TaskInfo
        {
            Id = "git-runner",
            Title = "Move git handling out of the CLI runner",
            State = TaskStates.Ready,
            Tags = ["runner"]
        };
        var prompt = "Change the backend runner so worktree branch, commit, and merge handling is orchestrated by the API instead of worker agents.";

        var manifest = IntakeRunner.BuildEnrichmentManifest(target, prompt);

        Assert.Contains("git", manifest.Areas);
        Assert.Contains("runner", manifest.Areas);
        Assert.Contains(manifest.Constraints, c => c.Id == "git-handling-api-not-cli");
        Assert.Contains(manifest.Omissions,
            omission => omission.Id == "orchestrator-state-machine-authority"
                        && omission.Reason == "optional-block-limit");
    }

    [Fact]
    public void BuildEnrichmentManifest_FrontendTask_IncludesDesignTokenConstraint()
    {
        var target = new TaskInfo
        {
            Id = "frontend-layout",
            Title = "Polish the task card layout",
            State = TaskStates.Ready,
            Tags = ["frontend"]
        };
        var prompt = "Update the Angular component SCSS so the card spacing and badge colors follow the shared UI design.";

        var manifest = IntakeRunner.BuildEnrichmentManifest(target, prompt);

        Assert.Contains("frontend", manifest.Areas);
        Assert.Contains(manifest.Constraints, c => c.Id == "frontend-design-tokens-components");
        Assert.DoesNotContain(manifest.Constraints, c => c.Id == "git-handling-api-not-cli");
    }

    [Fact]
    public void BuildEnrichmentManifest_CodingTask_IncludesApplicableRepositoryStyleGuide()
    {
        var target = new TaskInfo
        {
            Id = "angular-card",
            Title = "Add Angular usage metric component",
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Tags = ["frontend"]
        };
        var guide = new ProjectStyleGuide(
            "angular-components",
            "Angular component guide",
            "quality/angular-components.md",
            "Angular rules",
            "Use OnPush, stable tracking, semantic tokens, and tabular numbers.",
            "1",
            new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"]));

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            target,
            "Build the Angular component and SCSS for a frontend usage card.",
            [guide],
            "snapshot-1");

        var selected = Assert.Single(manifest.Constraints,
            constraint => constraint.Id == "style-guide:angular-components");
        Assert.Equal("docs/quality/angular-components.md", selected.Source);
        Assert.Contains("OnPush", selected.Text);
        Assert.Equal(IntakeRunner.EnrichmentSelector, manifest.Selector);
        Assert.Equal("snapshot-1", manifest.StyleGuideSnapshotId);
        Assert.Contains("Style-guide snapshot: `snapshot-1`", IntakeRunner.RenderEnrichedContextMarkdown(manifest));
    }

    [Fact]
    public void BuildEnrichmentManifest_ReadOnlyTask_DoesNotInjectCodingStyleGuide()
    {
        var target = new TaskInfo
        {
            Id = "research-card",
            Title = "Research Angular rendering",
            State = TaskStates.Ready,
            Mode = TaskModes.Research,
            Tags = ["frontend"]
        };
        var guide = new ProjectStyleGuide(
            "angular-components", "Angular component guide", "quality/angular-components.md",
            "Angular rules", "Use OnPush.", "1",
            new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"]));

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            target,
            "Research the Angular component rendering behavior and write a report.",
            [guide]);

        Assert.DoesNotContain(manifest.Constraints,
            constraint => constraint.Id == "style-guide:angular-components");
    }

    [Fact]
    public void BuildEnrichmentManifest_StylingCardInjectsLivingStylingContext()
    {
        var target = new TaskInfo
        {
            Id = "styling-card",
            Title = "Unify action styling",
            State = TaskStates.Ready,
            Mode = TaskModes.Coding
        };
        var guide = new ProjectStyleGuide(
            "frontend-styling", "Frontend styling context", "quality/frontend-styling.md",
            "Compact styling rules",
            "Apply the living guide. Labels never imitate button chrome.",
            "1",
            new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"]));

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            target,
            "Fix inconsistent styling and typography in the decision bar.",
            [guide]);

        Assert.Contains("frontend", manifest.Areas);
        var selected = Assert.Single(manifest.Constraints,
            constraint => constraint.Id == "style-guide:frontend-styling");
        Assert.Equal("docs/quality/frontend-styling.md", selected.Source);
        Assert.Contains("Labels never imitate button chrome", selected.Text);
    }

    [Fact]
    public void BuildEnrichmentManifest_TaskAreaWildcardMatchesCodingTaskButEmptyTaskAreasDoNot()
    {
        var target = new TaskInfo
        {
            Id = "general-coding",
            Title = "Apply the accepted change",
            State = TaskStates.Ready,
            Mode = TaskModes.Coding
        };
        var wildcard = new ProjectStyleGuide(
            "global", "Global guide", "quality/global.md", "Global", "Follow the global rule.", "1",
            new StyleGuideAppliesTo(["*"], ["dotnet"], ["*"]));
        var empty = new ProjectStyleGuide(
            "empty", "Empty guide", "quality/empty.md", "Empty", "Never selected.", "1",
            new StyleGuideAppliesTo(["*"], ["dotnet"], []));

        var manifest = IntakeRunner.BuildEnrichmentManifest(target, "Apply it.", [empty, wildcard]);

        var selected = Assert.Single(manifest.Constraints,
            constraint => constraint.Id == "style-guide:global");
        Assert.Equal(["general"], selected.Areas);
        Assert.DoesNotContain(manifest.Constraints, constraint => constraint.Id == "style-guide:empty");
    }

    [Fact]
    public void BuildEnrichmentManifest_EnforcesHardBudgetWithDeterministicOmissionManifest()
    {
        var target = new TaskInfo
        {
            Id = "large-style-guide-set",
            Title = "Build Angular frontend components",
            State = TaskStates.Ready,
            Mode = TaskModes.Coding,
            Tags = ["frontend"]
        };
        var guides = Enumerable.Range(0, 40)
            .Select(index => new ProjectStyleGuide(
                $"guide-{index:00}",
                $"Guide {index:00}",
                $"quality/guide-{index:00}.md",
                "Summary",
                new string((char)('a' + index % 26), 600),
                "1",
                new StyleGuideAppliesTo(["*"], ["angular"], ["frontend"])))
            .ToList();

        var first = IntakeRunner.BuildEnrichmentManifest(target, "Build the Angular frontend component.", guides);
        var second = IntakeRunner.BuildEnrichmentManifest(target, "Build the Angular frontend component.", guides.AsEnumerable().Reverse().ToList());
        var rendered = IntakeRunner.RenderEnrichedContextMarkdown(first);

        Assert.True(rendered.Length <= IntakeRunner.MaxEnrichmentContextCharacters);
        Assert.Equal(rendered.Length, first.UsedCharacters);
        Assert.True(first.EstimatedTokens <= IntakeRunner.MaxEnrichmentEstimatedTokens);
        Assert.NotEmpty(first.Omissions);
        Assert.True(first.Omissions.Count <= 16);
        Assert.True(first.Omissions.Count + first.AdditionalOmissionCount > 0);
        Assert.Equal(first.Constraints.Select(constraint => constraint.Id), second.Constraints.Select(constraint => constraint.Id));
        Assert.Equal(first.Omissions.Select(omission => omission.Id), second.Omissions.Select(omission => omission.Id));
        Assert.Equal(first.AdditionalOmissionCount, second.AdditionalOmissionCount);
        Assert.Contains("Omitted relevant constraints", rendered);
        Assert.True(first.Constraints.Count(constraint => !constraint.Mandatory)
                    <= IntakeRunner.MaxOptionalEnrichmentBlocks);
        Assert.Equal(1_500, first.EstimatedTokenBudget);
        Assert.Equal(2, first.OptionalBlockLimit);
    }

    [Fact]
    public void BuildEnrichmentManifest_DelegationTask_IncludesDelegationEconomy()
    {
        var target = new TaskInfo
        {
            Id = "delegation-card",
            Title = "Delegate independent analysis to sub-agents",
            State = TaskStates.Ready,
            Tags = ["runner", "multi-agent"]
        };

        var manifest = IntakeRunner.BuildEnrichmentManifest(
            target,
            "Use bounded delegation for independent research, then merge the evidence in one orchestrator.");

        Assert.Contains("delegation", manifest.Areas);
        Assert.Contains(manifest.Constraints, constraint => constraint.Id == "delegation-economy");
        Assert.Contains(manifest.Constraints, constraint =>
            constraint.Id == "repo-instructions-source"
            && constraint.Mandatory);
    }

    // ---- RunForJob integration: phase transitions + sidecar ------------------

    [Fact]
    public void RunForJob_PassingCard_StampsIntakePassedAndWritesSidecar()
    {
        WriteJob(TaskStates.Ready, "happy", phase: LifecyclePhases.HumanReady,
            promptMd: "Add a daily token rollup card to the project header. Acceptance: chip shows totals.");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("happy");

        Assert.Equal(IntakeOutcome.Pass, verdict.Outcome);

        // Phase moved to intake-passed.
        var info = BuildScanner().FindJob("happy");
        Assert.NotNull(info);
        Assert.Equal(LifecyclePhases.IntakePassed, info!.Phase);

        // lifecycle.json is next to task.json, status reflects the verdict.
        var sidecar = ReadLifecycleJson("happy");
        Assert.NotNull(sidecar);
        Assert.Equal(LifecyclePhases.IntakePassed, sidecar!.Phase);
        Assert.Null(sidecar.BlockingReason);
        Assert.Single(sidecar.IntakeChecks);
        Assert.Equal("passed", sidecar.IntakeChecks[0].Status);
    }

    // (Removed RunForJob_BlockedCard_StampsIntakeBlockedAndCarriesReason: it
    //  drove a parallel-coding card to IntakeBlocked via the hard non-goal
    //  block that ADR-0052 removed. The passing-card path is covered by
    //  RunForJob_PassingCard_StampsIntakePassedAndWritesSidecar; clarification-
    //  driven IntakeBlocked is covered by RunForJob_NeedsClarification_*.)

    [Fact]
    public void RunForJob_NeedsClarification_StampsIntakeBlockedSoRunnerStaysOff()
    {
        // V1 maps every non-pass outcome onto intake-blocked at the phase
        // level. The verdict still carries the typed outcome and reason so
        // the chat / sidecar can render a needs-clarification UI; the
        // pickup gate just needs a single "not approved" signal.
        WriteJob(TaskStates.Ready, "thin", phase: LifecyclePhases.HumanReady, promptMd: "fix it");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("thin");

        Assert.Equal(IntakeOutcome.NeedsClarification, verdict.Outcome);
        var info = BuildScanner().FindJob("thin");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);
    }

    [Fact]
    public void RunForJob_DuplicateCandidate_PinsPeerInDetails()
    {
        WriteJob(TaskStates.Ready, "dup-old", phase: LifecyclePhases.HumanReady,
            promptMd: "Add daily token rollup card to project header. Acceptance: chip shows totals.",
            title: "Add daily token rollup card to project header");
        WriteJob(TaskStates.Ready, "dup-new", phase: LifecyclePhases.HumanReady,
            promptMd: "Add daily token rollup card to project header. Done when chip is visible.",
            title: "Add daily token rollup card project header");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("dup-new");

        Assert.Equal(IntakeOutcome.DuplicateCandidate, verdict.Outcome);
        Assert.Contains("dup-old", verdict.Details ?? Array.Empty<string>());

        var info = BuildScanner().FindJob("dup-new");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);
    }

    [Fact]
    public void RunForJob_AlreadyDoneCard_StampsIntakeBlockedSoRunnerStaysOff()
    {
        // Done-precheck routes through the same intake-blocked phase as every
        // other non-pass outcome: the pickup gate keeps the coding runner off
        // the card, and the typed AlreadyDone verdict + reason let the human
        // confirm-and-complete instead of running redundant work.
        WriteJob(TaskStates.Ready, "done-card", phase: LifecyclePhases.HumanReady,
            promptMd: "This rollup card is already implemented on main and merged. No work needed.");

        var runner = BuildRunner();
        var verdict = runner.RunForJob("done-card");

        Assert.Equal(IntakeOutcome.AlreadyDone, verdict.Outcome);

        var info = BuildScanner().FindJob("done-card");
        Assert.Equal(LifecyclePhases.IntakeBlocked, info!.Phase);

        var sidecar = ReadLifecycleJson("done-card");
        Assert.NotNull(sidecar);
        Assert.Equal("failed", sidecar!.IntakeChecks[0].Status);
        Assert.NotNull(sidecar.BlockingReason);
    }

    [Fact]
    public void RunForJob_RecordsContextManifestInSidecar()
    {
        // Context-load runs as part of RunForJob and records the resolved /
        // missing attachments into the lifecycle sidecar so the board can see
        // what context the card carries.
        WriteJob(TaskStates.Ready, "ctx-card", phase: LifecyclePhases.HumanReady,
            promptMd: "Implement the importer per attachments/spec.md; the icon is attachments/missing.png. Done when imports succeed.");
        var attachmentsDir = Path.Combine(_watchPath, TaskStates.Ready, "ctx-card", "attachments");
        Directory.CreateDirectory(attachmentsDir);
        File.WriteAllText(Path.Combine(attachmentsDir, "spec.md"), "# Spec");

        var runner = BuildRunner();
        runner.RunForJob("ctx-card");

        var sidecar = ReadLifecycleJson("ctx-card");
        Assert.NotNull(sidecar);
        Assert.NotNull(sidecar!.Context);
        Assert.Contains("attachments/spec.md", sidecar.Context!.ResolvedAttachments);
        Assert.Contains("attachments/missing.png", sidecar.Context.MissingAttachments);
    }

    [Fact]
    public void RunForJob_WritesEnrichedContextArtifactAndSidecarManifest()
    {
        WriteJob(TaskStates.Ready, "git-card", phase: LifecyclePhases.HumanReady,
            promptMd: "Update backend runner git handling so worktree branch, commit, and merge steps live in the API layer. Done when worker agents no longer own git lifecycle decisions.");

        var runner = BuildRunner();
        runner.RunForJob("git-card");

        var artifactPath = Path.Combine(
            _watchPath,
            TaskStates.Ready,
            "git-card",
            IntakeRunner.EnrichedContextRelativePath.Replace('/', Path.DirectorySeparatorChar));
        Assert.True(File.Exists(artifactPath));
        var artifact = File.ReadAllText(artifactPath);
        Assert.Contains("Prompt enrichment", artifact);
        Assert.Contains("No task-specific constraints were selected", artifact);
        Assert.Contains("source-missing", artifact);

        var sidecar = ReadLifecycleJson("git-card");
        Assert.NotNull(sidecar);
        Assert.NotNull(sidecar!.Enrichment);
        Assert.Equal(IntakeRunner.EnrichedContextRelativePath, sidecar.Enrichment!.ArtifactPath);
        Assert.Empty(sidecar.Enrichment.Constraints);
        Assert.Contains(sidecar.Enrichment.Omissions, omission =>
            omission.Id == "git-handling-api-not-cli"
            && omission.Reason == "source-missing");
    }

    [Fact]
    public void RunForJob_RejectsNonReadyJobs()
    {
        WriteJob(TaskStates.Preparation, "draft", phase: null, promptMd: "Anything goes.");

        var runner = BuildRunner();

        Assert.Throws<InvalidOperationException>(() => runner.RunForJob("draft"));
    }

    // ---- helpers -------------------------------------------------------------

    private void WriteJob(string state, string slug, string? phase, string promptMd, string? title = null)
    {
        var dir = Path.Combine(_watchPath, state, slug);
        Directory.CreateDirectory(dir);
        var phaseField = phase is null ? "" : $",\"phase\":\"{phase}\"";
        File.WriteAllText(Path.Combine(dir, "task.json"),
            $"{{\"id\":\"{slug}\",\"title\":\"{title ?? slug + " title"}\",\"state\":\"{state}\",\"order\":1,\"agent\":\"claude\",\"ownerClientId\":\"default\"{phaseField}}}");
        File.WriteAllText(Path.Combine(dir, "prompt.md"), promptMd);
    }

    private LifecycleSnapshot? ReadLifecycleJson(string slug)
    {
        var path = Path.Combine(_watchPath, TaskStates.Ready, slug, "lifecycle.json");
        if (!File.Exists(path)) return null;
        var raw = File.ReadAllText(path);
        return JsonSerializer.Deserialize<LifecycleSnapshot>(raw,
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }

    private TaskScannerService BuildScanner()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var summary = new SummaryGenerationService(NullLogger<SummaryGenerationService>.Instance, config);
        return new TaskScannerService(config, NullLogger<TaskScannerService>.Instance, summary);
    }

    private IntakeRunner BuildRunner()
    {
        var scanner = BuildScanner();
        var cfg = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WatchPaths:0:Name"] = "test",
                ["WatchPaths:0:Path"] = _watchPath
            })
            .Build();
        var mutations = new TaskMutationService(scanner, new ClientIdentityStore(cfg, NullLogger<ClientIdentityStore>.Instance), new ProjectRegistry(cfg, NullLogger<ProjectRegistry>.Instance), new TaskChangeNotifier(NullLogger<TaskChangeNotifier>.Instance), NullLogger<TaskMutationService>.Instance);
        var chatLog = new OrchestratorChatLog(NullLogger<OrchestratorChatLog>.Instance);
        return new IntakeRunner(scanner, mutations, chatLog, NullLogger<IntakeRunner>.Instance);
    }
}
