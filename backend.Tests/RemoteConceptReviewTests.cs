using System.Diagnostics;
using AgentStudio.Prompts;
using AgentStudio.Runner;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentStudio.Tests;

public sealed class RemoteConceptReviewTests : IDisposable
{
    private readonly string _folder = Path.Combine(Path.GetTempPath(), "concept-review-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public void Contradictory_dossier_cannot_pass_without_concept_fit_review()
    {
        Directory.CreateDirectory(_folder);
        const string brief = "Use one implementation card per independently reviewable slice. Include a recommendation for every open decision.";
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), brief);
        var plan = BuildPlan();

        var aspect = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit");
        Assert.Equal(ReviewCommandKinds.AgentAspect, aspect.ExecutionKind);
        Assert.Equal(PipelineStepModelDefaults.SupportModel, aspect.Model);
        Assert.Equal(PipelineStepModelDefaults.SupportThinkingLevel, aspect.ThinkingLevel);
        Assert.Contains(brief, aspect.Prompt);
        Assert.Contains("contradict", aspect.Prompt, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("concept-fit", plan.RequiredAspects);
        Assert.Equal(TaskModes.Concept, plan.TaskMode);

        // The delivered Dossier says to combine every slice into one card, in
        // direct conflict with the current brief. Build and lint can pass, but
        // a report with no content verdict must not be accepted as a pass.
        var report = EmptyPassingReport();
        var checkedReport = ConceptRemoteReviewPolicy.Enforce(
            plan, plan.BriefSha256, plan.BriefSha256, report);
        Assert.NotEqual("Pass", checkedReport.Outcome);
        Assert.Contains("concept-fit", checkedReport.Summary);
    }

    [Fact]
    public void Review_against_superseded_brief_fails_with_both_versions()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Old operator direction.");
        var plan = BuildPlan();
        var current = AttemptAuthorityService.Hash("New operator direction.");

        var report = ConceptRemoteReviewPolicy.Enforce(plan, plan.BriefSha256, current, EmptyPassingReport());

        Assert.NotEqual("Pass", report.Outcome);
        Assert.Contains(plan.BriefSha256!, report.Summary);
        Assert.Contains(current, report.Summary);
    }

    [Fact]
    public void AGT_3573_two_follow_up_rounds_keep_authored_brief_and_pass_completion()
    {
        Directory.CreateDirectory(_folder);
        const string authored = "Deliver a persona team Dossier with implementation cards.";
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), authored);
        var version = BriefVersionStore.Record(_folder, authored);
        File.AppendAllText(Path.Combine(_folder, "prompt.md"), "\nFollow-up round 1: clarify personas.");
        var first = BuildPlan();
        File.AppendAllText(Path.Combine(_folder, "prompt.md"), "\nFollow-up round 2: finish cards.");
        var second = BuildPlan();

        Assert.Equal(version, first.BriefSha256);
        Assert.Equal(version, second.BriefSha256);
        var task = new TaskInfo { Id = "AGT-3573", Mode = TaskModes.Concept, FolderPath = _folder };
        var legacyWholeFileHash = AttemptAuthorityService.Hash(
            File.ReadAllText(Path.Combine(_folder, "prompt.md")));
        Assert.NotEqual(version, legacyWholeFileHash);
        var repairedReviewVersion = CompletionContentEvidence.NormalizeLegacyConceptReviewVersion(
            task, version, version, legacyWholeFileHash);
        Assert.Equal(version, repairedReviewVersion);

        var completion = CompletionContentPolicy.Decide(new CompletionContentFacts(
            TaskModes.Concept, version, version, repairedReviewVersion,
            "concept-fit", "pass", ["concept-fit"], []));
        Assert.True(completion.Accepted, completion.Message);
    }

    [Fact]
    public void Direct_authored_edit_keeps_an_older_concept_verdict_stale()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "First authored brief.");
        var first = BriefVersionStore.Record(_folder, "First authored brief.");
        var plan = BuildPlan();
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Revised authored brief.");
        var current = BriefVersionStore.Record(_folder, "Revised authored brief.");
        var task = new TaskInfo { Id = "AGT-3573", Mode = TaskModes.Concept, FolderPath = _folder };

        Assert.Equal(first, plan.BriefSha256);
        Assert.NotEqual(current, first);
        Assert.Equal(first, CompletionContentEvidence.NormalizeLegacyConceptReviewVersion(
            task, current, current, first));
        Assert.False(CompletionContentPolicy.Decide(new CompletionContentFacts(
            TaskModes.Concept, current, current, plan.BriefSha256,
            "concept-fit", "pass", ["concept-fit"], [])).Accepted);
    }

    [Fact]
    public void Follow_up_concept_fit_reads_published_index_and_linked_files()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Review the whole Dossier.");
        var version = BriefVersionStore.Record(_folder, "Review the whole Dossier.");
        var repo = Path.Combine(_folder, "repository");
        var dossier = Path.Combine(repo, "docs", "persona-team");
        Directory.CreateDirectory(dossier);
        File.WriteAllText(Path.Combine(dossier, "index.html"),
            "<h1>Persona team decisions</h1><a href=\"cards.md\">Cards</a>");
        File.WriteAllText(Path.Combine(dossier, "cards.md"), "# Implementation cards\nOne independently reviewable card.");
        File.WriteAllText(Path.Combine(dossier, "workbench.json"), "{\"implementationTasks\":[\"AGT-3573-A\"]}");
        ConceptWorkbenchStore.Write(_folder, new ConceptWorkbenchRecord
        {
            RepoRelativeDirectory = "docs/persona-team",
            RepoRelativeEntrypoint = "docs/persona-team/index.html",
        });
        File.AppendAllText(Path.Combine(_folder, "prompt.md"), "\nFollow-up: improve summary only.");

        var plan = BuildPlan(repositoryPath: repo);
        var prompt = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit").Prompt!;
        Assert.Equal(version, plan.BriefSha256);
        Assert.Contains("docs/persona-team/index.html", prompt);
        Assert.Contains("Persona team decisions", prompt);
        Assert.Contains("docs/persona-team/cards.md", prompt);
        Assert.Contains("independently reviewable card", prompt);
        Assert.Contains("implementationTasks", prompt);
    }

    [Fact]
    public void Project_step_setting_controls_concept_model_thinking_and_skip_reason()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Write a Concept Dossier.");
        var configured = BuildPlan(new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting
                {
                    Model = "gpt-5.6-sol", ThinkingLevel = "medium",
                },
            },
        });
        var command = Assert.Single(configured.Commands, item => item.Aspect == "concept-fit");
        Assert.Equal("gpt-5.6-sol", command.Model);
        Assert.Equal("medium", command.ThinkingLevel);

        var disabled = BuildPlan(new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting { Enabled = false },
            },
        });
        Assert.DoesNotContain(disabled.Commands, item => item.Aspect == "concept-fit");
        Assert.Contains("Disabled", Assert.Single(disabled.SkippedAspects!).Reason);
        Assert.NotEqual("Pass", ConceptRemoteReviewPolicy.Enforce(
            disabled, disabled.BriefSha256, disabled.BriefSha256, EmptyPassingReport()).Outcome);
    }

    [Fact]
    public void Concept_brief_input_is_bounded_while_version_covers_complete_file()
    {
        Directory.CreateDirectory(_folder);
        var brief = new string('x', 70_000) + "Follow the final operator direction.";
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), brief);

        var plan = BuildPlan();
        var prompt = Assert.Single(plan.Commands, command => command.Aspect == "concept-fit").Prompt!;

        Assert.Equal(AttemptAuthorityService.Hash(brief), plan.BriefSha256);
        Assert.Contains("Follow the final operator direction.", prompt);
        Assert.DoesNotContain(new string('x', 70_000), prompt);
    }

    [Fact]
    public void Project_host_requirement_is_sealed_into_review_plan()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Review the concept.");

        Assert.False(BuildPlan().RequireDifferentHostFailureDomain);
        Assert.True(BuildPlan(new ProjectSettings
        {
            RequireDifferentReviewHost = true,
        }).RequireDifferentHostFailureDomain);
    }

    // The results inventory excerpts the head of each report, so only text past
    // that excerpt shows whether the status section carries the whole report.
    private static readonly string AgentReportBeyondTheExcerpt =
        new string('x', 1_000) + "\nTimings beyond the excerpt.";

    [Fact]
    public void Aspect_reads_the_agent_report_while_the_card_status_is_a_scaffold()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Brief.");
        File.WriteAllText(Path.Combine(_folder, "status.md"),
            AgentStudio.Tasks.TaskTransitionService.ResultScaffoldMarker + "\n# Status\nScaffold text.");
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReportBeyondTheExcerpt);

        var aspect = Assert.Single(BuildPlan().Commands, command => command.Aspect == "concept-fit");

        Assert.Contains("Timings beyond the excerpt.", aspect.Prompt);
        Assert.DoesNotContain("Scaffold text.", aspect.Prompt);
    }

    [Fact]
    public void Aspect_prefers_the_generated_summary_over_the_agent_report()
    {
        Directory.CreateDirectory(Path.Combine(_folder, "results"));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Brief.");
        File.WriteAllText(Path.Combine(_folder, "status.md"), "# Status\nGenerated summary.");
        File.WriteAllText(Path.Combine(_folder, "results", "status.md"), AgentReportBeyondTheExcerpt);

        var aspect = Assert.Single(BuildPlan().Commands, command => command.Aspect == "concept-fit");

        Assert.Contains("Generated summary.", aspect.Prompt);
        Assert.DoesNotContain("Timings beyond the excerpt.", aspect.Prompt);
    }

    [Fact]
    public void Aspect_keeps_the_scaffold_when_the_agent_wrote_no_report()
    {
        Directory.CreateDirectory(_folder);
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Brief.");
        File.WriteAllText(Path.Combine(_folder, "status.md"),
            AgentStudio.Tasks.TaskTransitionService.ResultScaffoldMarker + "\n# Status\nScaffold text.");

        var aspect = Assert.Single(BuildPlan().Commands, command => command.Aspect == "concept-fit");

        Assert.Contains("Scaffold text.", aspect.Prompt);
    }

    [Fact]
    public void Dossier_only_concept_review_plans_concept_fit_without_preparation_or_build_tests()
    {
        var repo = NodeRepository();
        var control = BuildPlan(repositoryPath: repo, changedFiles: ["docs/topic/index.html", "src/index.js"]);
        Assert.Contains(control.Commands, command => command.Aspect == "build-tests");
        Assert.NotEmpty(control.Preparation!);

        var plan = BuildPlan(repositoryPath: repo,
            changedFiles: ["docs/topic/index.html", "docs/topic/workbench.json"]);

        var command = Assert.Single(plan.Commands);
        Assert.Equal("concept-fit", command.Aspect);
        Assert.Equal(ReviewCommandKinds.AgentAspect, command.ExecutionKind);
        Assert.Equal(["concept-fit"], plan.RequiredAspects);
        Assert.Empty(plan.Preparation!);
        Assert.DoesNotContain(plan.Commands, item => item.CompareToBaseline);
        var skipped = Assert.Single(plan.SkippedAspects!, item => item.Aspect == "build-tests");
        Assert.Equal(RemoteReviewPlanBuilder.ConceptDossierSkipReason("docs/topic"), skipped.Reason);
        Assert.False(plan.BuildTestDeferredToBatch);
        Assert.Equal(skipped.Reason, plan.BuildTestDeferralDisabledReason);

        // The grade carries the skipped build-tests with its reason, and a red
        // integration branch has no verification step through which it could
        // block the Dossier: the concept-fit verdict alone settles the review.
        var report = ConceptRemoteReviewPolicy.Enforce(plan, plan.BriefSha256, plan.BriefSha256,
            EmptyPassingReport() with
            {
                Verdicts = [new ReviewVerdictDto("concept-fit", "pass", "content", "Dossier fits the brief.")],
            });
        Assert.Equal("Pass", report.Outcome);
        Assert.Contains(report.SkippedAspects!, item => item.Aspect == "build-tests"
            && item.Reason == RemoteReviewPlanBuilder.ConceptDossierSkipReason("docs/topic"));
    }

    [Fact]
    public void Concept_review_keeps_tool_gates_when_the_diff_is_not_one_dossier()
    {
        var repo = NodeRepository();

        foreach (var changed in new IReadOnlyList<string>?[]
                 {
                     null, ["docs/one/index.html", "docs/two/index.html"], ["docs/index.html"],
                 })
        {
            var plan = BuildPlan(repositoryPath: repo, changedFiles: changed);
            Assert.Contains(plan.Commands, command => command.Aspect == "build-tests");
            Assert.Contains(plan.Commands, command => command.Aspect == "concept-fit");
            Assert.DoesNotContain(plan.SkippedAspects!, item => item.Aspect == "build-tests");
        }
    }

    [Fact]
    public void Disabled_concept_fit_on_a_dossier_keeps_an_admissible_plan_that_cannot_pass()
    {
        var plan = BuildPlan(new ProjectSettings
        {
            PipelineSteps = new()
            {
                [PipelineCatalogue.RemoteConceptFitStepId] = new PipelineStepSetting { Enabled = false },
            },
        }, NodeRepository(), ["docs/topic/index.html"]);

        Assert.Equal("completion", Assert.Single(plan.Commands).Aspect);
        Assert.Empty(plan.Preparation!);
        Assert.NotEqual("Pass", ConceptRemoteReviewPolicy.Enforce(
            plan, plan.BriefSha256, plan.BriefSha256, EmptyPassingReport()).Outcome);
    }

    [Fact]
    public void Single_dossier_directory_requires_every_path_inside_one_topic()
    {
        Assert.Equal("docs/topic", ConceptWorkbenchContract.SingleDossierDirectory(
            ["docs/topic/index.html", "docs\\topic\\workbench.json", "/docs/topic/assets/a.png"]));
        Assert.Null(ConceptWorkbenchContract.SingleDossierDirectory(null));
        Assert.Null(ConceptWorkbenchContract.SingleDossierDirectory([]));
        Assert.Null(ConceptWorkbenchContract.SingleDossierDirectory(["docs/topic/index.html", "README.md"]));
        Assert.Null(ConceptWorkbenchContract.SingleDossierDirectory(["docs/a/index.html", "docs/b/index.html"]));
        Assert.Null(ConceptWorkbenchContract.SingleDossierDirectory(["docs/index.html"]));
    }

    // The playthrough's small Node repository: npm test auto-discovered, npm ci
    // as preparation.
    private string NodeRepository()
    {
        var repo = Path.Combine(_folder, "repo");
        Directory.CreateDirectory(Path.Combine(repo, "src"));
        File.WriteAllText(Path.Combine(_folder, "prompt.md"), "Write a Concept Dossier.");
        File.WriteAllText(Path.Combine(repo, "package.json"),
            "{\"name\":\"small\",\"scripts\":{\"test\":\"node --test\"}}\n");
        File.WriteAllText(Path.Combine(repo, "package-lock.json"),
            "{\"name\":\"small\",\"lockfileVersion\":3,\"packages\":{}}\n");
        File.WriteAllText(Path.Combine(repo, "src", "index.js"), "module.exports = 1;\n");
        Git(repo, "init", "-q");
        Git(repo, "add", ".");
        return repo;
    }

    private static void Git(string repo, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = repo,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        process.StandardOutput.ReadToEnd();
        var error = process.StandardError.ReadToEnd();
        process.WaitForExit();
        Assert.True(process.ExitCode == 0, $"git {string.Join(' ', arguments)}: {error}");
    }

    private ReviewPlanDto BuildPlan(ProjectSettings? settings = null, string? repositoryPath = null,
        IReadOnlyList<string>? changedFiles = null)
    {
        var config = new ConfigurationBuilder().Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var aspects = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        var task = new TaskInfo
        {
            Id = "CON-1", Title = "Concept", Mode = TaskModes.Concept,
            FolderPath = _folder, ProjectName = "Test",
        };
        return new RemoteReviewPlanBuilder(aspects, config).Build(task, repositoryPath, settings,
            "refs/heads/main", changedFiles: changedFiles);
    }

    private static ReviewReportRequest EmptyPassingReport() => new(
        "executor", "instance", "lease", 1, "key", "Pass", null,
        "All applicable remote review aspects passed.", null!, null!, [], [], []);

    public void Dispose()
    {
        try { Directory.Delete(_folder, recursive: true); } catch { }
    }
}
