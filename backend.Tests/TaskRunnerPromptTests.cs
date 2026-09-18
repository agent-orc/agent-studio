using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Locks in the runner prompt contract without embedding the full prompt text
/// in C# code. The planner chooses templates; <see cref="RuntimePromptService"/>
/// loads the Markdown files and renders variables.
/// </summary>
public class TaskRunnerPromptTests
{
    [Fact]
    public void RunnerFreshStartTemplate_PointsAtPromptFileAndKeepsAppInControl()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook"
        });

        Assert.Contains(@"C:\jobs\fix-bug\prompt.md", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("Do not scan for or pick up other tasks", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("application owns pickup", p);
        Assert.Contains("git status and git diff", p);

        // Structural property: the user task header must come before the
        // run-context framing. With user content second, Claude treats the
        // bootstrap as a system stub and replies "I'll wait for your actual
        // request." This guards against the regression that broke production.
        var taskHeader = p.IndexOf("# ", StringComparison.Ordinal);
        var contextHeader = p.IndexOf("Context for this run", StringComparison.Ordinal);
        Assert.InRange(taskHeader, 0, contextHeader);
    }

    [Fact]
    public void RunnerResumeTemplate_RestoresJobContextWithoutMovingState()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerResumeInterrupted, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "(body)"
        });

        Assert.Contains("Resume this interrupted task", p);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("job.json", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("status.md", p);
        Assert.Contains("logs", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunnerResumeRestartTemplate_TellsAgentPreviousRunIsDoneAndToActOnDelta()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerResumeRestart, new Dictionary<string, string?>
        {
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "(updated body)"
        });

        // The whole reason this template exists: tell Claude the previous
        // run already finished, so re-issuing the same task body does not
        // get the "I'll wait for your request" no-op response.
        Assert.Contains("previous run", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("re-started", p, StringComparison.OrdinalIgnoreCase);
        // Must instruct the agent to re-read prompt.md and diff against
        // what it remembers, otherwise the delta is invisible.
        Assert.Contains("Re-read", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("prompt.md", p);
        Assert.Contains("git status", p);
        Assert.Contains("git diff", p);
        // Standard guardrails still apply.
        Assert.Contains("do not scan", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);
        // Variables are expanded.
        Assert.Contains("Improve the layout of the taskbar.", p);
        Assert.Contains("(updated body)", p);
    }

    [Fact]
    public void RunnerRecoveryTemplate_CarriesOriginalTaskAndFollowupBeforeFraming()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerRecoveryContinuation, new Dictionary<string, string?>
        {
            ["title"] = "Improve the layout of the taskbar.",
            ["prompt_text"] = "The header is cramped; rearrange model and CLI selectors.",
            ["prompt_path"] = @"C:\jobs\fix-bug\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-bug",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["user_followup"] = "Please continue with adding the chat compose box."
        });

        Assert.Contains("previous CLI session", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains(@"C:\jobs\fix-bug", p);
        Assert.Contains(@"C:\Projects\Runbook\App", p);
        Assert.Contains(@"C:\Projects\Runbook", p);
        Assert.Contains("prompt.md", p);
        Assert.Contains("[[TASK_BLOCKED", p);
        Assert.Contains("do not move the job folder", p, StringComparison.OrdinalIgnoreCase);

        // Both the original task body AND the follow-up must appear in the
        // rendered prompt. When the session is unrecoverable, the agent
        // must still see the task it was hired for, not just the latest
        // direction. Without this, the agent says "no context, please
        // tell me what to do" - the symptom the user reported.
        Assert.Contains("Improve the layout of the taskbar.", p);
        Assert.Contains("The header is cramped; rearrange model and CLI selectors.", p);
        Assert.Contains("Please continue with adding the chat compose box.", p);

        // Structural property: original task body comes BEFORE the
        // follow-up which comes BEFORE the framing block. The previous
        // arrangement labelled the original task "reference only" and the
        // agent treated it as ignorable.
        var taskBodyIndex = p.IndexOf("The header is cramped", StringComparison.Ordinal);
        var followupIndex = p.IndexOf("Please continue with adding the chat compose box.", StringComparison.Ordinal);
        var framingIndex = p.IndexOf("previous CLI session", StringComparison.OrdinalIgnoreCase);
        Assert.InRange(taskBodyIndex, 0, followupIndex);
        Assert.InRange(followupIndex, 0, framingIndex);

        // The "reference only" framing is gone. The original task is the
        // authoritative starting point, not a footnote.
        Assert.DoesNotContain("reference only", p, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void RunnerReissueChangeTemplate_ForegroundsReviewFindingsBeforeOriginalTask()
    {
        var p = Prompts().Render(RuntimePromptService.RunnerReissueChange, new Dictionary<string, string?>
        {
            ["title"] = "Fix the toolbar spacing",
            ["prompt_text"] = "Original task: polish the toolbar.",
            ["reissue_findings"] = "- [ ] Code review found the save button still wraps on mobile.\n- [ ] Add a regression test for the toolbar.",
            ["reissue_followup"] = "# Orchestrator follow-up\n\nAuto-review found blocking review findings.",
            ["prompt_path"] = @"C:\jobs\fix-toolbar\prompt.md",
            ["job_folder"] = @"C:\jobs\fix-toolbar",
            ["working_directory"] = @"C:\Projects\Runbook\App",
            ["repository_path"] = @"C:\Projects\Runbook",
            ["attachments_list"] = "(none)",
            ["mode_framing"] = ""
        });

        Assert.Contains("Reissue change prompt", p);
        Assert.Contains("review findings below are the primary task", p, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Code review found the save button still wraps on mobile.", p);
        Assert.Contains("Add a regression test for the toolbar.", p);
        Assert.Contains("Original task: polish the toolbar.", p);
        Assert.Contains("code-review-*.md", p);
        Assert.Contains("aspect-*.md", p);

        var findingsIndex = p.IndexOf("Code review found", StringComparison.Ordinal);
        var originalIndex = p.IndexOf("Original task: polish", StringComparison.Ordinal);
        Assert.InRange(findingsIndex, 0, originalIndex);
    }

    [Fact]
    public void VersionedReissueExperimentTemplates_PreserveControlAndSeparateTreatmentEvidence()
    {
        var values = new Dictionary<string, string?>
        {
            ["title"] = "Fix the toolbar spacing",
            ["prompt_text"] = "Original task context.",
            ["reissue_findings"] = ReissuePromptExperiment.BuildTreatmentFindings(
                new[] { "Fix wrapping in `toolbar.component.scss`." },
                escalate: false),
            ["reissue_followup"] = "RAW-REVIEW-EVIDENCE",
            ["reissue_evidence"] = "RAW-REVIEW-EVIDENCE",
            ["prompt_path"] = "prompt.md",
            ["job_folder"] = "job",
            ["working_directory"] = "work",
            ["repository_path"] = "repo",
            ["attachments_list"] = "(none)",
            ["mode_framing"] = "",
        };
        var prompts = Prompts();
        var control = prompts.Render(RuntimePromptService.RunnerReissueControlV1, values);
        var treatment = prompts.Render(RuntimePromptService.RunnerReissueTreatmentV1, values);

        Assert.Contains("Full reissue context", control);
        Assert.Contains("Numbered findings to resolve", treatment);
        Assert.Contains("1.", treatment);
        Assert.Contains("Exact deficiency:", treatment);
        Assert.Contains("File, symbol, or artifact:", treatment);
        Assert.Contains("Required change:", treatment);
        Assert.Contains("Focused verification or acceptance evidence:", treatment);
        Assert.Contains("Evidence block", treatment);
        Assert.True(
            treatment.IndexOf("Focused verification or acceptance evidence:", StringComparison.Ordinal)
            < treatment.IndexOf("RAW-REVIEW-EVIDENCE", StringComparison.Ordinal));
        Assert.Contains("[[TASK_DONE]]", control);
        Assert.Contains("[[TASK_DONE]]", treatment);
        Assert.Contains("[[TASK_BLOCKED:missing-dependency-xyz]]", control);
        Assert.Contains("[[TASK_BLOCKED:missing-dependency-xyz]]", treatment);
    }

    [Fact]
    public void AllRunnerTemplates_SpellOutTheOutputContract()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["title"] = "title",
                ["prompt_text"] = "body",
                ["reissue_findings"] = "- [ ] finding",
                ["reissue_followup"] = "follow up",
                ["attachments_list"] = "(none)",
                ["mode_framing"] = ""
            });
            Assert.Contains("[[TASK_DONE]]", rendered);
            Assert.Contains("[[TASK_BLOCKED:missing-dependency-xyz]]", rendered);
            Assert.Contains("[[TASK_NEEDS_INPUT:choose-primary-column]]", rendered);
            Assert.Contains("Replace the example reason", rendered);
            Assert.DoesNotContain("<short reason>", rendered);
        }
    }

    [Fact]
    public void AgentFacingRuntimeTemplates_ReferenceCanonicalModelRoutingPolicy()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange,
            RuntimePromptService.EpicDecomposition,
            "global-orchestrator-boot.md",
            "orchestrator-project-boot.md",
            "orchestrator-reissue-followup.md",
            "orchestrator-conflict-resolution.md",
            "orchestrator-decision-oneshot.md",
            "orchestrator-decision-resume.md",
            "orchestrator-no-completion-signal.md",
            "orchestrator-review-decision-fallback.md",
            "orchestrator-review-decision.md"
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>());

            Assert.Contains("docs/system/domains/model-routing-policy.md", rendered);
            Assert.Contains("authoritative source", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("correctness-risk floors", rendered);
        }
    }

    [Fact]
    public void AllRunnerTemplates_MentionBuildTimeObservabilityWithoutDominating()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["title"] = "title",
                ["prompt_text"] = "body",
                ["reissue_findings"] = "- [ ] finding",
                ["reissue_followup"] = "follow up",
                ["attachments_list"] = "(none)",
                ["mode_framing"] = ""
            });

            // Each runner template carries the small observability nudge so
            // coding agents preserve and extend structured logging when the
            // change introduces meaningful product behavior.
            Assert.Contains("observability", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("structured log", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("stable event name", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("error context", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("timing", rendered, StringComparison.OrdinalIgnoreCase);

            // The opt-out clause must be present so trivial changes are not
            // padded with logging just to satisfy the guidance.
            Assert.Contains("Skip instrumentation", rendered, StringComparison.OrdinalIgnoreCase);

            // Guidance must stay short. The block should not dominate the
            // prompt; cap it at a small fraction of total characters so a
            // future edit cannot quietly turn it into a checklist.
            var blockStart = rendered.IndexOf("Build-time observability", StringComparison.OrdinalIgnoreCase);
            Assert.True(blockStart >= 0, "observability block must be present");
            var blockEnd = rendered.IndexOf("When you finish", blockStart, StringComparison.OrdinalIgnoreCase);
            if (blockEnd < 0) blockEnd = rendered.Length;
            var blockLength = blockEnd - blockStart;
            Assert.InRange(blockLength, 1, 1200);
        }
    }

    [Fact]
    public void RunnerAndOrchestratorTemplates_PointToCanonicalContributionGuide()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange,
            RuntimePromptService.EpicDecomposition,
            "global-orchestrator-boot.md",
            "orchestrator-conflict-resolution.md",
            "orchestrator-decision-oneshot.md",
            "orchestrator-decision-resume.md",
            "orchestrator-no-completion-signal.md",
            "orchestrator-project-boot.md",
            "orchestrator-reissue-followup.md",
            "orchestrator-review-decision-fallback.md",
            "orchestrator-review-decision.md"
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["title"] = "title",
                ["prompt_text"] = "body",
                ["reissue_findings"] = "- [ ] finding",
                ["reissue_followup"] = "follow up",
                ["attachments_list"] = "(none)",
                ["mode_framing"] = "",
                ["project_name"] = "project",
                ["decision"] = "continue",
                ["task_title"] = "task title",
                ["task_id"] = "AGT-1",
                ["task_description"] = "task body",
                ["attachments_block"] = "",
                ["last_agent_text"] = "question",
                ["project"] = "project",
                ["job_id"] = "AGT-1",
                ["job_title"] = "task title",
                ["needs_input_reason"] = "question",
                ["task_body"] = "task body",
                ["recent_log"] = "log",
                ["roadmap_excerpt"] = "roadmap",
                ["adr_titles"] = "ADR",
                ["previous_decisions"] = "none",
                ["task_branch"] = "task/agt-1",
                ["integration_branch"] = "main",
                ["worktree"] = "worktree",
                ["conflicted_files"] = "file.cs"
            });

            Assert.Contains("docs/start/contribution-and-style-guide.html", rendered);
            Assert.Contains("authoritative source", rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void AllRunnerTemplates_UseCalmPlatformCommitOwnership()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["title"] = "title",
                ["prompt_text"] = "body",
                ["reissue_findings"] = "- [ ] finding",
                ["reissue_followup"] = "follow up",
                ["attachments_list"] = "(none)",
                ["mode_framing"] = ""
            });

            // Fresh/recovery/reissue use the calm wording, while the two resume
            // templates still carry the earlier direct form. Both state the
            // current platform-owned commit boundary without punitive language.
            var usesCalmWording = rendered.Contains(
                "Please do not commit or push yourself",
                StringComparison.Ordinal);
            var usesDirectWording = rendered.Contains(
                "Do not run `git commit`",
                StringComparison.Ordinal);
            Assert.True(usesCalmWording || usesDirectWording);
            Assert.Contains("push", rendered, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("process violation", rendered, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public void SummaryProtocolTemplate_PinsImageSectionContract()
    {
        var rendered = Prompts().Render(RuntimePromptService.SummaryProtocol, new Dictionary<string, string?>
        {
            ["log"] = string.Join('\n',
                "Captured result screenshot at results/run-proof.png",
                "Used supplied reference image attachments/input-wireframe.png"),
            ["taskTitle"] = "Image task",
            ["taskPrompt"] = "Capture proof.",
            ["rounds"] = "Run 1, initial.",
            ["agentStatus"] = "Not provided.",
            ["delivery"] = "No structured delivery facts were recorded.",
        });

        Assert.Contains("## Images", rendered);
        Assert.Contains("list every unique hit as `![](<path>)`", rendered);
        Assert.Contains("Prefer `results/<name>` for screenshots produced during the run.", rendered);
        Assert.Contains("Prefer `attachments/<name>` for images supplied in the task prompt.", rendered);
        Assert.Contains("Omit this section when no images appear.", rendered);
        Assert.Contains("Images do not count.", rendered);
        Assert.Contains("results/run-proof.png", rendered);
        Assert.Contains("attachments/input-wireframe.png", rendered);
        Assert.DoesNotContain("{{log}}", rendered);
    }

    /// <summary>
    /// The Result redesign (Protocol -> Result) feeds a case-based, overview-first
    /// view. The frontend parses a `## Overview` section (`- Problem:` / `- Solution:`)
    /// and an optional `- Case:` hint out of `status.md`; this test pins that the
    /// summarizer prompt actually asks for both, plus the full case vocabulary the
    /// client classifier understands. If a template refactor drops these, the
    /// Result head silently falls back to synthesized overviews and heuristic
    /// cases for every new run, so keep this contract sharp.
    /// </summary>
    [Fact]
    public void SummaryProtocolTemplate_PinsOverviewAndCaseContract()
    {
        var rendered = Prompts().Render(RuntimePromptService.SummaryProtocol, new Dictionary<string, string?>
        {
            ["log"] = "did some work",
            ["taskTitle"] = "Example task",
            ["taskPrompt"] = "Deliver the requested change.",
            ["rounds"] = "Run 1, initial.",
            ["agentStatus"] = "Not provided.",
            ["delivery"] = "No structured delivery facts were recorded.",
        });

        Assert.Contains("## Overview", rendered);
        Assert.Contains("- Problem:", rendered);
        Assert.Contains("- Solution:", rendered);
        Assert.Contains("- Case:", rendered);
        // The two quality-head metrics (Teil 2): the client parses these optional
        // header lines into the Files / Tests metric chips. If a template refactor
        // drops them, the chips silently disappear for every new run.
        Assert.Contains("- Files:", rendered);
        Assert.Contains("- Tests:", rendered);
        // The eight cases the client classifier (result-case.ts) understands.
        foreach (var caseId in new[] { "bugfix", "feature", "refactor", "docs", "forensics", "ui-cleanup", "blocked", "generic" })
        {
            Assert.Contains(caseId, rendered);
        }
        // Overview must be asked for before the detail section, so status.md
        // leads with the shareable summary.
        Assert.True(rendered.IndexOf("## Overview", StringComparison.Ordinal)
            < rendered.IndexOf("## What Was Done", StringComparison.Ordinal));
    }

    /// <summary>
    /// The summarizer sees task intent before round and log evidence. This pins
    /// the task-level placeholder wiring without a billable one-shot call.
    /// </summary>
    [Fact]
    public void SummarySlots_CarryTaskMetadataAndOutcomeIntoRenderedPrompt()
    {
        var info = new TaskInfo
        {
            Id = "slots-test",
            TaskKey = "::slots-test",
            Title = "Slots test",
            State = "4-auto-review",
            FolderPath = "folder",
            WatchPath = "",
            ProjectName = "test",
            TaskType = TaskTypes.Bug,
            Mode = TaskModes.Research,
        };

        var inputs = new SummaryInputs(
            "Slots test",
            "TASK-PROMPT-MARKER with acceptance criteria",
            "ROUND-LEDGER-MARKER",
            "AGENT-STATUS-MARKER",
            "DELIVERY-MARKER",
            "LOG-BODY-MARKER");
        var slots = SummaryGenerationService.BuildSummarySlots(info, inputs, "Blocked");
        var rendered = Prompts().Render(RuntimePromptService.SummaryProtocol, slots);

        Assert.Contains("Slots test", rendered);
        Assert.Contains("TASK-PROMPT-MARKER", rendered);
        Assert.Contains("ROUND-LEDGER-MARKER", rendered);
        Assert.Contains("AGENT-STATUS-MARKER", rendered);
        Assert.Contains("DELIVERY-MARKER", rendered);
        Assert.Contains("LOG-BODY-MARKER", rendered);
        Assert.True(rendered.IndexOf("TASK-PROMPT-MARKER", StringComparison.Ordinal)
            < rendered.IndexOf("LOG-BODY-MARKER", StringComparison.Ordinal));
        foreach (var placeholder in new[] { "taskTitle", "taskPrompt", "rounds", "agentStatus", "delivery", "log" })
            Assert.DoesNotContain("{{" + placeholder + "}}", rendered);
    }

    [Fact]
    public void RunnerTemplates_DoNotContainEmDashes()
    {
        var prompts = Prompts();
        foreach (var template in new[]
        {
            RuntimePromptService.RunnerFreshStart,
            RuntimePromptService.RunnerResumeInterrupted,
            RuntimePromptService.RunnerResumeRestart,
            RuntimePromptService.RunnerRecoveryContinuation,
            RuntimePromptService.RunnerReissueChange,
            RuntimePromptService.SummaryProtocol,
            RuntimePromptService.CommitMessage
        })
        {
            var rendered = prompts.Render(template, new Dictionary<string, string?>
            {
                ["prompt_path"] = "prompt.md",
                ["job_folder"] = "job",
                ["working_directory"] = "work",
                ["repository_path"] = "repo",
                ["user_followup"] = "follow up",
                ["log"] = "log",
                ["diff"] = "diff",
                ["task_title"] = "title",
                ["task_prompt_first_paragraph"] = "first paragraph",
                ["last_user_continue"] = "follow up",
                ["reissue_findings"] = "- [ ] finding",
                ["reissue_followup"] = "follow up",
                ["attachments_list"] = "(none)",
                ["mode_framing"] = ""
            });

            Assert.DoesNotContain("\u2014", rendered);
        }
    }

    /// <summary>
    /// Slice B of the git-problem task: the commit-message template must anchor
    /// on the task's stated intent (title, first prompt paragraph, last user
    /// follow-up) so the generated subject reflects *why* the change is being
    /// recorded, not just *what* the diff touches. Pinning this here so a
    /// future template refactor cannot silently drop the intent placeholders
    /// and fall back to a diff-only summary.
    /// </summary>
    [Fact]
    public void CommitMessageTemplate_AnchorsOnTaskIntent()
    {
        var rendered = Prompts().Render(RuntimePromptService.CommitMessage, new Dictionary<string, string?>
        {
            ["diff"] = "diff --git a/x b/x",
            ["diff_summary"] = "1 file changed",
            ["candidate_manifest"] = "[{\"Path\":\"x\",\"Included\":true}]",
            ["task_title"] = "Git-Problem",
            ["task_prompt_first_paragraph"] = "Orchestrator should commit and push when a task finishes.",
            ["last_user_continue"] = "Please also enrich the commit message."
        });

        // Intent anchors must reach the LLM, not just the diff.
        Assert.Contains("Git-Problem", rendered);
        Assert.Contains("Orchestrator should commit and push", rendered);
        Assert.Contains("Please also enrich the commit message.", rendered);
        Assert.Contains("diff --git a/x b/x", rendered);
        Assert.Contains("candidate manifest", rendered, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("COMMIT_REVIEW: ALLOW", rendered);
        Assert.Contains("COMMIT_REVIEW: SUSPICIOUS", rendered);
        Assert.Contains("Never reproduce a credential", rendered);

        // Structural guard: the template must instruct the model to prefer
        // intent over a literal diff summary, otherwise enriching the prompt
        // is pointless.
        Assert.Contains("intent", rendered, StringComparison.OrdinalIgnoreCase);

        // The Conventional Commit shape must survive the prompt rewrite.
        Assert.Contains("Conventional Commit", rendered);
        Assert.Contains("72 characters", rendered);
    }

    /// <summary>
    /// Empty intent fields (legacy jobs without a title, or no Extend prompt)
    /// must render without leaking the literal placeholder back into the LLM
    /// input. The renderer substitutes the empty string, so the headings stay
    /// but the bodies collapse to a blank line.
    /// </summary>
    [Fact]
    public void CommitMessageTemplate_TolerantToMissingIntentFields()
    {
        var rendered = Prompts().Render(RuntimePromptService.CommitMessage, new Dictionary<string, string?>
        {
            ["diff"] = "diff --git a/x b/x",
            ["diff_summary"] = "1 file changed",
            ["candidate_manifest"] = "[]",
            ["task_title"] = "",
            ["task_prompt_first_paragraph"] = "",
            ["last_user_continue"] = ""
        });

        Assert.DoesNotContain("{{task_title}}", rendered);
        Assert.DoesNotContain("{{task_prompt_first_paragraph}}", rendered);
        Assert.DoesNotContain("{{last_user_continue}}", rendered);
        Assert.DoesNotContain("{{candidate_manifest}}", rendered);
        Assert.DoesNotContain("{{diff_summary}}", rendered);
        Assert.Contains("diff --git a/x b/x", rendered);
    }

    /// <summary>
    /// First-paragraph extraction must trim leading whitespace, stop at the
    /// first blank line, and bound the result so a wall-of-text prompt does
    /// not dominate the LLM input. Pinned here so a future helper rewrite
    /// cannot silently regress to "send the whole prompt body".
    /// </summary>
    [Theory]
    [InlineData("", "")]
    [InlineData("   \n  \n", "")]
    [InlineData("Hello world.", "Hello world.")]
    [InlineData("Hello world.\n\nSecond paragraph.", "Hello world.")]
    [InlineData("\n\nLeading blank lines.\n\nThen more.", "Leading blank lines.")]
    [InlineData("Line one.\nLine two of the same paragraph.\n\nNext block.",
                "Line one.\nLine two of the same paragraph.")]
    public void ExtractFirstParagraph_TrimsAndStopsAtBlankLine(string input, string expected)
    {
        Assert.Equal(expected, AgentStudio.Git.GitService.ExtractFirstParagraph(input));
    }

    [Fact]
    public void ExtractFirstParagraph_BoundsLongParagraphs()
    {
        var body = new string('a', 2000);
        var result = AgentStudio.Git.GitService.ExtractFirstParagraph(body);

        // Bounded to 1500 chars + ellipsis suffix, so the LLM call stays cheap.
        Assert.True(result.Length <= 1503, $"unexpected length: {result.Length}");
        Assert.EndsWith("...", result);
    }

    /// <summary>
    /// Pre-generated slugs in the <c>taskboard-{jobId}-{yyyyMMddHHmm}</c> shape are
    /// placeholders we wrote ourselves before a real session UUID was captured.
    /// They must be recognised so the cross-CLI guard drops them silently.
    /// </summary>
    [Theory]
    [InlineData("taskboard-verbessere-den-task-log-202604282114", true)]
    [InlineData("taskboard-action-failed-202604282112", true)]
    [InlineData("taskboard-x-202604282114", true)]
    [InlineData("a1b2c3d4-e5f6-7890-abcd-ef0123456789", false)]
    [InlineData("rollover_2025-04-01T12:00:00Z", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("taskboard-no-timestamp", false)]
    public void IsPlaceholderSessionSlug_RecognisesGeneratedShape(string? input, bool expected)
    {
        Assert.Equal(expected, RunPlanner.IsPlaceholderSessionSlug(input));
    }

    /// <summary>
    /// The resume-continuation prompt only pays off when there is something to
    /// reconstruct: a real session to load, or a dropped foreign-CLI session
    /// whose files we can re-read.
    /// </summary>
    [Theory]
    [InlineData("2-ready", false, false, false)]
    [InlineData("2-ready", true, false, false)]
    [InlineData("3-progress", false, false, false)]
    [InlineData("3-progress", true, false, true)]
    [InlineData("3-progress", false, true, true)]
    [InlineData("2-ready", false, true, true)]
    public void ShouldUseResumePrompt_OnlyFiresWhenContextExists(
        string initialState, bool resume, bool sessionDropped, bool expected)
    {
        Assert.Equal(expected, RunPlanner.ShouldUseResumePrompt(initialState, resume, sessionDropped));
    }

    // ---- Per-mode prompt framing (planning/research read-only + web hint) ----

    [Fact]
    public void RenderModeFraming_CodingWithoutWeb_IsEmpty()
    {
        // Coding with web off is the legacy default; framing must stay empty so
        // the rendered runner prompt is byte-identical to the pre-mode output.
        Assert.Equal(string.Empty, Prompts().RenderModeFraming("coding", allowWebAccess: false));
    }

    [Theory]
    [InlineData("planning")]
    [InlineData("research")]
    public void RenderModeFraming_ReadOnlyModes_CarryReadOnlyBlock(string mode)
    {
        var framing = Prompts().RenderModeFraming(mode, allowWebAccess: false);

        Assert.Contains("Read-only run", framing);
        Assert.Contains("do not write or modify source", framing, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("commit", framing, StringComparison.OrdinalIgnoreCase);
        // Web hint must NOT leak in when web access is off.
        Assert.DoesNotContain("Web access is enabled", framing);
    }

    [Fact]
    public void RenderModeFraming_Research_AddsBothReadOnlyAndWebHint()
    {
        // Research gets the read-only block, its HTML result contract, and the
        // web hint in that order.
        var framing = Prompts().RenderModeFraming("research", allowWebAccess: true);

        Assert.Contains("Read-only run", framing);
        Assert.Contains("**Research:**", framing);
        Assert.Contains("results/report.html", framing);
        Assert.Contains("self-contained HTML", framing);
        Assert.Contains("link every supporting document", framing);
        Assert.Contains("Web access is enabled", framing);
        var readOnlyIndex = framing.IndexOf("Read-only run", StringComparison.Ordinal);
        var researchIndex = framing.IndexOf("**Research:**", StringComparison.Ordinal);
        var webIndex = framing.IndexOf("Web access is enabled", StringComparison.Ordinal);
        Assert.InRange(readOnlyIndex, 0, researchIndex);
        Assert.InRange(researchIndex, readOnlyIndex, webIndex);
    }

    [Fact]
    public void RenderModeFraming_Planning_DoesNotInheritResearchHtmlContract()
    {
        var framing = Prompts().RenderModeFraming("planning", allowWebAccess: false);

        Assert.Contains("Read-only run", framing);
        Assert.DoesNotContain("**Research:**", framing);
        Assert.DoesNotContain("results/report.html", framing);
    }

    [Fact]
    public void RenderModeFraming_Concept_CarriesBoundedWorkbenchContract()
    {
        var framing = Prompts().RenderModeFraming("concept", allowWebAccess: false);

        Assert.Contains("repository Dossier delivery", framing);
        Assert.Contains("docs/<slug>/", framing);
        Assert.Contains("workbench.json", framing);
        Assert.Contains("index.html", framing);
        Assert.Contains("status: \"decision-pending\"", framing);
        Assert.Contains("sourceTaskKeys", framing);
        Assert.Contains("results/deliverables.md", framing);
        Assert.Contains("status.md", framing);
        Assert.Contains("docs/app/templates/article-document-v2.html", framing);
        Assert.Contains("docs/operations/haertung-verteilte-ausfuehrung/index.html", framing);
        Assert.Contains("plain nouns", framing);
        Assert.Contains("light and dark themes", framing);
        Assert.Contains("CSS variables", framing);
        Assert.Contains("inline SVG", framing);
        Assert.Contains("colour system or font family", framing);
        Assert.Contains("full-bleed evidence figure", framing);
        Assert.Contains("docs/operations/<slug>/assets/", framing);
        Assert.Contains("docs/operations/setup/presentation-capture.md", framing);
        Assert.Contains("frontend/e2e/visual-evidence/presentation-capture.spec.ts", framing);
        Assert.Contains("scripts/stable-frontend-boot-probe.mjs", framing);
        Assert.Contains("frontend/e2e/fixtures/dev-backend.ts", framing);
        Assert.Contains("[[TASK_NEEDS_INPUT:", framing);
        Assert.DoesNotContain("Read-only run", framing);
    }

    [Fact]
    public void RenderDossierMaintenanceFraming_CarriesTargetsAndAppendOnlyContract()
    {
        var framing = Prompts().RenderDossierMaintenanceFraming(
            "AGT-2580",
            "- `docs/operations/context/index.html` (ASW-W8, Context chat)");

        Assert.Contains("Dossier implementation update", framing);
        Assert.Contains("post-dossier-maintenance", framing);
        Assert.Contains("AGT-2580", framing);
        Assert.Contains("docs/operations/context/index.html", framing);
        Assert.Contains(DossierImplementationContract.SectionStartMarker, framing);
        Assert.Contains(DossierImplementationContract.LogStartMarker, framing);
        Assert.Contains("append-only", framing);
        Assert.Contains("YYYY-MM-DD", framing);
        Assert.DoesNotContain('\u2014', framing);
    }

    [Fact]
    public void RenderModeFraming_CodingWithWeb_AddsWebHintOnly()
    {
        // Decision 2: the web toggle is independent of the mode. A coding task
        // with web opted in gets the web hint but no read-only constraint.
        var framing = Prompts().RenderModeFraming("coding", allowWebAccess: true);

        Assert.Contains("Web access is enabled", framing);
        Assert.DoesNotContain("Read-only run", framing);
    }

    [Fact]
    public void FreshStartTemplate_ReadOnlyMode_InjectsFramingAndKeepsGuardrails()
    {
        var prompts = Prompts();
        var p = prompts.Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = "prompt.md",
            ["job_folder"] = "job",
            ["working_directory"] = "work",
            ["repository_path"] = "repo",
            ["title"] = "Investigate the slow board load",
            ["prompt_text"] = "Profile the board and propose fixes.",
            ["mode_framing"] = prompts.RenderModeFraming("planning", allowWebAccess: false)
        });

        Assert.Contains("Read-only run", p);
        Assert.DoesNotContain("{{mode_framing}}", p);
        // Standard guardrails survive the injection.
        Assert.Contains("Do not scan for or pick up other tasks", p);
        Assert.Contains("[[TASK_DONE]]", p);
        // Read-only framing sits after the task body, before the run context.
        var bodyIndex = p.IndexOf("Profile the board", StringComparison.Ordinal);
        var framingIndex = p.IndexOf("Read-only run", StringComparison.Ordinal);
        var contextIndex = p.IndexOf("Context for this run", StringComparison.Ordinal);
        Assert.InRange(framingIndex, bodyIndex, contextIndex);
    }

    [Fact]
    public void FreshStartTemplate_CodingMode_OmitsFramingWithoutLeftoverPlaceholder()
    {
        var prompts = Prompts();
        var p = prompts.Render(RuntimePromptService.RunnerFreshStart, new Dictionary<string, string?>
        {
            ["prompt_path"] = "prompt.md",
            ["job_folder"] = "job",
            ["working_directory"] = "work",
            ["repository_path"] = "repo",
            ["title"] = "Implement the widget",
            ["prompt_text"] = "Add the widget to the toolbar.",
            ["mode_framing"] = prompts.RenderModeFraming("coding", allowWebAccess: false)
        });

        Assert.DoesNotContain("Read-only run", p);
        Assert.DoesNotContain("Web access is enabled", p);
        Assert.DoesNotContain("{{mode_framing}}", p);
    }

    [Fact]
    public void ModeFramingSnippets_DoNotContainEmDashes()
    {
        var framing =
            Prompts().RenderModeFraming("research", allowWebAccess: true)
            + Prompts().RenderModeFraming("concept", allowWebAccess: false);
        Assert.DoesNotContain("—", framing);
    }

    private static RuntimePromptService Prompts()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["PromptTemplates:RuntimePath"] = FindPromptRoot()
            })
            .Build();
        return new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
    }

    private static string FindPromptRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null)
        {
            var candidate = Path.Combine(dir.FullName, "prompts", "runtime");
            if (Directory.Exists(candidate)) return candidate;
            dir = dir.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate prompts/runtime from test base directory.");
    }
}
