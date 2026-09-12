using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct tests against <see cref="AspectRunnerService"/>. The CLI is
/// stubbed: each aspect's reply is supplied per-aspect-id so a
/// fixture can drive a deterministic mix of pass / concerns / block
/// responses without spinning up a model.
///
/// ADR-0025: an unparseable model reply must default to a Concerns
/// verdict (no silent durchwinken). The aspect runner writes one
/// <c>aspect-{id}.md</c> file per aspect into the job folder; the
/// frontmatter status token is the load-bearing field downstream
/// readers rely on.
/// </summary>
public class AspectRunnerTests : IDisposable
{
    private readonly string _jobFolder;

    public AspectRunnerTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "aspect-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task AllAspectsPass_OverallIsPass_NoConcernTags_AllMdsWritten()
    {
        var runner = BuildRunner(_ => "[[ASPECT_VERDICT: status=pass; summary=Looks fine.]]\n[[TASK_DONE]]");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Empty(report.ConcernTagIds);
        Assert.Equal(4, report.Verdicts.Count);

        foreach (var aspect in new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" })
        {
            var path = Path.Combine(_jobFolder, $"aspect-{aspect}.md");
            Assert.True(File.Exists(path), $"missing aspect MD for {aspect}");
            var content = File.ReadAllText(path);
            Assert.Equal(AspectStatus.Pass, AspectVerdictParsing.ReadStatusFromReport(content));
            Assert.Contains("Looks fine.", content);
        }
    }

    [Fact]
    public async Task EachAspect_WritesStructuredJsonTwin_AlongsideMarkdown()
    {
        // Concept doc §5: the aspect runner now writes a structured
        // `aspect-{id}.json` source of truth next to the human-readable
        // `.md`. The JSON must carry the load-bearing fields the Files tab
        // and Result head read; the markdown twin stays for existing readers.
        var runner = BuildRunner(aspect => aspect switch
        {
            "code-quality" => "The helper duplicates foo().\n[[ASPECT_VERDICT: status=concerns; summary=Dead helper left behind.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        var mdPath = Path.Combine(_jobFolder, "aspect-code-quality.md");
        var jsonPath = Path.Combine(_jobFolder, "aspect-code-quality.json");
        Assert.True(File.Exists(mdPath), "markdown twin must still be written");
        Assert.True(File.Exists(jsonPath), "structured JSON source of truth must be written");

        var doc = AspectVerdictParsing.TryParseJson(File.ReadAllText(jsonPath));
        Assert.NotNull(doc);
        Assert.Equal("code-quality", doc!.Aspect);
        Assert.Equal("concerns", doc.Status);
        Assert.Equal("Dead helper left behind.", doc.Summary);
        Assert.Equal("quality:concerns", doc.Tag);
        Assert.Equal("claude-haiku-4-5", doc.Model);
        Assert.Contains("duplicates foo()", doc.Details);
        Assert.Equal(AspectVerdictParsing.AspectDocumentSchemaVersion, doc.SchemaVersion);
    }

    [Fact]
    public async Task PassAspect_JsonTwin_HasNullTag()
    {
        var runner = BuildRunner(_ => "[[ASPECT_VERDICT: status=pass; summary=Looks fine.]]\n[[TASK_DONE]]");

        await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        var doc = AspectVerdictParsing.TryParseJson(
            File.ReadAllText(Path.Combine(_jobFolder, "aspect-requirement-fit.json")));
        Assert.NotNull(doc);
        Assert.Equal("pass", doc!.Status);
        Assert.Null(doc.Tag);
    }

    [Fact]
    public async Task OneConcernsThreePasses_OverallIsConcerns_ConcernTagAddedForThatNamespace()
    {
        var runner = BuildRunner(aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Dead helper left behind.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Contains("quality:concerns", report.ConcernTagIds);

        var qualityMd = File.ReadAllText(Path.Combine(_jobFolder, "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(qualityMd));
        Assert.Contains("quality:concerns", qualityMd);

        var docsMd = File.ReadAllText(Path.Combine(_jobFolder, "aspect-documentation-impact.md"));
        Assert.Equal(AspectStatus.Pass, AspectVerdictParsing.ReadStatusFromReport(docsMd));
    }

    [Fact]
    public async Task QualityAndTestsBothConcerns_DedupesToOneQualityConcernsTag()
    {
        // code-quality and tests-and-evidence share the `quality` namespace
        // for their concern tag. Two concerns in that namespace must
        // collapse to one chip on the card so the user does not see two
        // visually identical "quality:concerns" tags.
        var runner = BuildRunner(aspect => aspect switch
        {
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper is duplicated.]]\n[[TASK_DONE]]",
            "tests-and-evidence" => "[[ASPECT_VERDICT: status=concerns; summary=No regression test for the fix.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Equal("quality:concerns", report.ConcernTagIds[0]);
    }

    [Fact]
    public async Task OneBlockBeatsConcerns_OverallIsBlock_FollowUpListsBothFindings()
    {
        var runner = BuildRunner(aspect => aspect switch
        {
            "requirement-fit" => "[[ASPECT_VERDICT: status=block; summary=Acceptance criterion 2 not met.]]\n[[TASK_DONE]]",
            "code-quality" => "[[ASPECT_VERDICT: status=concerns; summary=Helper duplicated.]]\n[[TASK_DONE]]",
            _ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"
        });

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Block, report.Overall);
        Assert.Contains("requirement:concerns", report.ConcernTagIds);
        Assert.Contains("quality:concerns", report.ConcernTagIds);
        Assert.Contains("requirement-fit", report.FollowUpSummary);
        Assert.Contains("Acceptance criterion 2 not met.", report.FollowUpSummary);
        Assert.Contains("code-quality", report.FollowUpSummary);
        Assert.Contains("Helper duplicated.", report.FollowUpSummary);
    }

    [Fact]
    public async Task UnparseableReply_DefaultsToConcerns_WithUnparseableTag()
    {
        // The user's hard rule: a job that lands in 4-auto-review must
        // not pass through with no opinion. An unparseable model reply
        // still produces a Concerns verdict so the user sees a chip.
        // F1 (2026-05-21): the tag changed from `{namespace}:concerns`
        // to `review:unparseable` so the operator can distinguish
        // "model has a real concern" from "format violation".
        var runner = BuildRunner(_ => "I have no opinion.");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Single(report.ConcernTagIds);
        Assert.Equal("review:unparseable", report.ConcernTagIds[0]);

        var content = File.ReadAllText(Path.Combine(_jobFolder, "aspect-code-quality.md"));
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(content));
    }

    [Fact]
    public async Task Backend_restart_with_two_of_four_graded_aspects_resumes_only_remaining_aspects()
    {
        var pipeline = new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance);
        pipeline.Begin(_jobFolder, PipelineCatalogue.Standard, "demo", "test-job");
        foreach (var aspect in new[] { "requirement-fit", "code-quality" })
        {
            var verdict = new AspectVerdict(
                aspect,
                AspectStatus.Pass,
                $"{aspect} was already graded.",
                "Persisted review evidence.",
                null);
            File.WriteAllText(
                Path.Combine(_jobFolder, $"aspect-{aspect}.json"),
                AspectVerdictParsing.RenderJson(verdict, "checkpoint-model", DateTime.UtcNow));
            pipeline.RecordStep(_jobFolder, new PipelineStepExecution
            {
                StepId = $"aspect-{aspect}",
                Kind = StepKind.Aspect,
                Status = PipelineStepStatus.Passed,
                StartedAt = DateTime.UtcNow.AddSeconds(-1),
                CompletedAt = DateTime.UtcNow,
                Verdict = "pass",
            });
        }

        var calls = new List<string>();
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var replacement = new AspectRunnerService(
            prompts,
            NullLogger<AspectRunnerService>.Instance,
            pipelineLog: new PipelineExecutionLog(NullLogger<PipelineExecutionLog>.Instance));
        replacement.CliRunner = (aspect, _, _, _, _, _) =>
        {
            lock (calls) calls.Add(aspect);
            return Task.FromResult(
                $"[[ASPECT_VERDICT: status=pass; summary={aspect} completed after restart.]]\n[[TASK_DONE]]");
        };

        var report = await replacement.RunAsync(
            BuildInputs(),
            ["requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence"],
            "claude",
            "claude-haiku-4-5",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(4, report.Verdicts.Count);
        Assert.Equal(
            ["documentation-impact", "tests-and-evidence"],
            calls.OrderBy(value => value, StringComparer.Ordinal).ToArray());
        Assert.Contains(report.Verdicts, verdict =>
            verdict.Aspect == "requirement-fit"
            && verdict.Summary == "requirement-fit was already graded.");
    }

    [Fact]
    public async Task SparkReply_WithMalformedVerdict_UsesDeterministicUnparseableConcern()
    {
        var runner = BuildRunner(_ => "Analysis complete. [[ASPECT_VERDICT: status=maybe; summary=looks fine]]");

        var report = await runner.RunAsync(BuildInputs(), ["documentation-impact"],
            CliTypes.Codex, "gpt-5.6-codex-spark", TimeSpan.FromSeconds(5), CancellationToken.None,
            modelForAspect: _ => "gpt-5.6-codex-spark",
            cliForAspect: _ => CliTypes.Codex);

        var verdict = Assert.Single(report.Verdicts);
        Assert.Equal(AspectStatus.Concerns, verdict.Status);
        Assert.Equal("Aspect runner produced no parseable verdict.", verdict.Summary);
        Assert.Equal("review:unparseable", verdict.ConcernTagId);
    }

    [Theory]
    [InlineData("Looks fine.\n\nStatus: pass", AspectStatus.Pass)]
    [InlineData("**Status:** concerns\n\nNeeds review.", AspectStatus.Concerns)]
    [InlineData("Verdict says block.\nstatus = blocked\n", AspectStatus.Block)]
    public void ParseVerdict_FallsBack_When_Sentinel_Missing_But_StatusLineFound(string reply, AspectStatus expected)
    {
        // F1 tolerant fallback: when the model drops the [[ASPECT_VERDICT]]
        // sentinel but still says "Status: pass|concerns|block" on a
        // line, the parser now recovers that signal instead of bouncing
        // straight to the no-parseable-verdict fallback. Reduces false-
        // positive "concerns" tags from format violations.
        var parsed = AspectVerdictParsing.ParseVerdict(reply);
        Assert.NotNull(parsed);
        Assert.Equal(expected, parsed!.Value.Status);
    }

    [Theory]
    [InlineData("```\n[[ASPECT_VERDICT: status=pass; summary=fenced reply]]\n```")]
    [InlineData("```text\n[[ASPECT_VERDICT: status=pass; summary=fenced text]]\n```")]
    public void ParseVerdict_Recognises_Sentinel_Inside_Code_Fence(string reply)
    {
        // F1: models sometimes wrap the sentinel in triple-backtick
        // fences for visual emphasis. Strip the fence wrapper before
        // matching so this looks like a real verdict, not unparseable.
        var parsed = AspectVerdictParsing.ParseVerdict(reply);
        Assert.NotNull(parsed);
        Assert.Equal(AspectStatus.Pass, parsed!.Value.Status);
    }

    [Fact]
    public async Task UnknownAspectId_IsSkippedWithoutFailingTheRun()
    {
        var runner = BuildRunner(_ => "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality", "non-existent-aspect" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Single(report.Verdicts);
        Assert.Equal("code-quality", report.Verdicts[0].Aspect);
    }

    [Fact]
    public async Task RequirementFitPrompt_UsesFallbackEvidence_WhenTaskBodyIsEmpty()
    {
        string? capturedPrompt = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, _, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=fallback evidence is assessable.]]\n[[TASK_DONE]]");
        };

        var inputs = new AspectRunInputs(
            Project: "demo",
            JobId: "empty-prompt-job",
            JobTitle: "Convert legacy token aggregators to bus-backed shims",
            JobFolderPath: _jobFolder,
            TaskBody: "",
            RecentLog: "Agent reported the token aggregator shim conversion complete.",
            DiffSummary: "AdHocUsageService.cs, TokenSummary.cs, WorkspaceTokensTimelineService.cs changed.",
            StatusSummary: "Converted legacy token aggregator services to bus-backed shims.");

        var report = await runner.RunAsync(inputs,
            new[] { "requirement-fit" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.NotNull(capturedPrompt);
        Assert.Contains("If `prompt.md` is empty", capturedPrompt!);
        Assert.Contains("Do not flag an empty `prompt.md`", capturedPrompt);
        Assert.Contains("Convert legacy token aggregators to bus-backed shims", capturedPrompt);
        Assert.DoesNotContain("If the task body is empty or unclear, prefer `concerns` over `block`", capturedPrompt);
    }

    [Fact]
    public async Task RequirementFitPrompt_BoundedSlicePassesWithoutDemandingTheDossierWishlist()
    {
        string? capturedPrompt = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(
            config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(
            prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, _, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult(
                "[[ASPECT_VERDICT: status=pass; summary=The S1 slice meets its bounded acceptance scope.]]\n[[TASK_DONE]]");
        };
        var scope = TaskAcceptanceScopes.BoundedSlice(
            "S1: stop identical requirement-fit review loops",
            "Escalate the same requirement-fit block after two rounds.");
        var body = "Implement all recommendations in the Dossier, including S1 through S9.";
        var inputs = new AspectRunInputs(
            Project: "demo",
            JobId: "bounded-slice",
            JobTitle: "Implement Dossier S1",
            JobFolderPath: _jobFolder,
            TaskBody: body,
            RecentLog: "S1 implemented and focused tests passed.",
            DiffSummary: "Review retry policy and tests changed.",
            StatusSummary: "Delivered S1.")
        {
            AcceptanceScope = RequirementAcceptanceScope.Describe(scope, body),
        };

        var report = await runner.RunAsync(
            inputs,
            ["requirement-fit"],
            "claude",
            "claude-haiku-4-5",
            TimeSpan.FromSeconds(5),
            CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Contains("Acceptance mode: bounded slice", capturedPrompt);
        Assert.Contains("S1: stop identical requirement-fit review loops", capturedPrompt);
        Assert.Contains("Requirements outside this slice belong to the parent", capturedPrompt);
    }

    [Theory]
    [InlineData("Partial delivery is success for this card.")]
    [InlineData("Ship one slice per delivery and leave the rest in the Dossier.")]
    public void RequirementAcceptanceScope_InfersOnlyExplicitLegacySliceDeclarations(string taskBody)
    {
        var scope = RequirementAcceptanceScope.Describe(null, taskBody);

        Assert.Contains("Acceptance mode: bounded slice", scope);
        Assert.Contains("inferred", scope);
        Assert.Contains(
            "Acceptance mode: full task",
            RequirementAcceptanceScope.Describe(null, "Implement the approved change."));
    }

    [Fact]
    public async Task AspectPrompt_CarriesResultsInventoryAndCardMode_ForEvidenceCompleteness()
    {
        // AGT-2022: every aspect prompt must carry the results/ inventory and the
        // card-mode framing so a read-only / concept card is never false-BLOCKed
        // as "deliverables missing" when the deliverable lives outside the diff.
        string? capturedPrompt = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, _, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        var inputs = new AspectRunInputs(
            Project: "demo",
            JobId: "concept-job",
            JobTitle: "Analyse the pipeline and propose next steps",
            JobFolderPath: _jobFolder,
            TaskBody: "# Task\n\nWrite a plan.",
            RecentLog: "done",
            DiffSummary: "No commits attributed to this task.",
            StatusSummary: "Plan written to results/plan.md.")
        {
            ResultsInventory = "results/ folder contains 1 file(s):\n- plan.md (512 bytes)",
            CardMode = ReviewCardMode.Describe("planning"),
        };

        await runner.RunAsync(inputs,
            new[] { "requirement-fit" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("results/ folder inventory", capturedPrompt!);
        Assert.Contains("plan.md", capturedPrompt);
        Assert.Contains("read-only", capturedPrompt);
        Assert.Contains("Deliverables rule", capturedPrompt);
    }

    [Fact]
    public async Task PerAspectModel_RoutesEachAspectsCliCallToItsConfiguredModel()
    {
        // The load-bearing per-step-model-selection acceptance: when the
        // orchestrator hands RunAsync a modelForAspect resolver, each
        // aspect's CLI call must use the model that resolver returns, and
        // the recorded run-wide model must stay the fallback for the rest.
        var captured = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, model, _, _, _) =>
        {
            captured[aspectId] = model;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        // code-quality pinned to Haiku; every other aspect uses the
        // run-wide default the resolver echoes back unchanged.
        const string runWide = "claude-opus-4-1";
        Func<string, string> modelForAspect = id =>
            id == "code-quality" ? "claude-haiku-4-5" : runWide;

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit", "code-quality", "documentation-impact", "tests-and-evidence" },
            "claude", runWide, TimeSpan.FromSeconds(5), CancellationToken.None, modelForAspect);

        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Equal("claude-haiku-4-5", captured["code-quality"]);
        Assert.Equal(runWide, captured["requirement-fit"]);
        Assert.Equal(runWide, captured["documentation-impact"]);
        Assert.Equal(runWide, captured["tests-and-evidence"]);
    }

    [Fact]
    public async Task NullModelForAspect_KeepsRunWideModelForEveryAspect()
    {
        var captured = new System.Collections.Concurrent.ConcurrentDictionary<string, string>();
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, model, _, _, _) =>
        {
            captured[aspectId] = model;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        await runner.RunAsync(BuildInputs(),
            new[] { "code-quality", "tests-and-evidence" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None, modelForAspect: null);

        Assert.Equal("claude-haiku-4-5", captured["code-quality"]);
        Assert.Equal("claude-haiku-4-5", captured["tests-and-evidence"]);
    }

    [Fact]
    public async Task PerAspectCli_RoutesConfiguredCodexCliWithSparkModel()
    {
        string? capturedCli = null;
        string? capturedModel = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, cli, model, _, _, _) =>
        {
            capturedCli = cli;
            capturedModel = model;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        await runner.RunAsync(BuildInputs(), new[] { "documentation-impact" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None,
            modelForAspect: _ => "gpt-5.3-codex-spark",
            cliForAspect: _ => CliTypes.Codex);

        Assert.Equal(CliTypes.Codex, capturedCli);
        Assert.Equal("gpt-5.3-codex-spark", capturedModel);
    }

    [Fact]
    public async Task NullPerAspectCli_KeepsRunWideCli()
    {
        string? capturedCli = null;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (_, cli, _, _, _, _) =>
        {
            capturedCli = cli;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        };

        await runner.RunAsync(BuildInputs(), ["documentation-impact"],
            CliTypes.Codex, ModelIds.Gpt54Mini, TimeSpan.FromSeconds(5), CancellationToken.None,
            cliForAspect: null);

        Assert.Equal(CliTypes.Codex, capturedCli);
    }

    [Fact]
    public void AspectVerdictParsing_RoundTripsStatusToken()
    {
        var v = new AspectVerdict("code-quality", AspectStatus.Concerns, "summary text", "body", "quality:concerns");
        var rendered = AspectVerdictParsing.RenderReport(v, DateTime.UtcNow);
        Assert.Equal(AspectStatus.Concerns, AspectVerdictParsing.ReadStatusFromReport(rendered));
        Assert.Contains("aspect: code-quality", rendered);
        Assert.Contains("tag: quality:concerns", rendered);
    }

    [Theory]
    [InlineData("[[ASPECT_VERDICT: status=pass; summary=ok]]", AspectStatus.Pass, "ok")]
    [InlineData("[[ASPECT_VERDICT:status=concerns;summary=needs work]]", AspectStatus.Concerns, "needs work")]
    [InlineData("[[ASPECT_VERDICT: status=block; summary=must fix]]", AspectStatus.Block, "must fix")]
    [InlineData("[[ASPECT_VERDICT: SUMMARY=order-flip; STATUS=pass]]", AspectStatus.Pass, "order-flip")]
    public void AspectVerdictParsing_AcceptsCommonShapes(string input, AspectStatus expectedStatus, string expectedSummary)
    {
        var parsed = AspectVerdictParsing.ParseVerdict(input);
        Assert.NotNull(parsed);
        Assert.Equal(expectedStatus, parsed!.Value.Status);
        Assert.Equal(expectedSummary, parsed.Value.Summary);
    }

    [Theory]
    [InlineData("")]
    [InlineData("no sentinel here")]
    [InlineData("[[ASPECT_VERDICT: status=mystery; summary=x]]")]
    public void AspectVerdictParsing_RejectsUnparseable(string input)
    {
        Assert.Null(AspectVerdictParsing.ParseVerdict(input));
    }

    [Fact]
    public void SerializeFindings_EmitsCamelCaseTokenisedArray_ForTheFrontend()
    {
        var verdicts = new[]
        {
            new AspectVerdict("requirement-fit", AspectStatus.Concerns, "missing edge-case test", "body", "fit:concerns"),
            new AspectVerdict("code-quality", AspectStatus.Block, "helper duplicated", "body", "quality:block"),
        };

        var json = AspectVerdictParsing.SerializeFindings(verdicts);

        using var doc = System.Text.Json.JsonDocument.Parse(json);
        var items = doc.RootElement.EnumerateArray().ToList();
        Assert.Equal(2, items.Count);

        // camelCase property names + normalised status token (not the enum name).
        Assert.Equal("requirement-fit", items[0].GetProperty("aspect").GetString());
        Assert.Equal("concerns", items[0].GetProperty("verdict").GetString());
        Assert.Equal("missing edge-case test", items[0].GetProperty("reason").GetString());
        Assert.Equal("code-quality", items[1].GetProperty("aspect").GetString());
        Assert.Equal("block", items[1].GetProperty("verdict").GetString());
    }

    [Fact]
    public void SerializeFindings_EmptyInput_YieldsEmptyArray()
    {
        Assert.Equal("[]", AspectVerdictParsing.SerializeFindings(Array.Empty<AspectVerdict>()));
    }

    [Fact]
    public void RenderJson_EmitsCamelCaseStructuredDocument_AndRoundTrips()
    {
        var verdict = new AspectVerdict(
            "code-quality", AspectStatus.Concerns, "Dead helper.",
            "## Model reply\n\n```\nnarrative\n```\n", "quality:concerns");
        var now = new DateTime(2026, 7, 9, 19, 21, 3, DateTimeKind.Utc);

        var json = AspectVerdictParsing.RenderJson(verdict, "claude-haiku-4-5", now);

        using (var probe = System.Text.Json.JsonDocument.Parse(json))
        {
            var root = probe.RootElement;
            Assert.Equal("code-quality", root.GetProperty("aspect").GetString());
            Assert.Equal("concerns", root.GetProperty("status").GetString());
            Assert.Equal("Dead helper.", root.GetProperty("summary").GetString());
            Assert.Equal("quality:concerns", root.GetProperty("tag").GetString());
            Assert.Equal("claude-haiku-4-5", root.GetProperty("model").GetString());
            // Empty metrics is dropped from the wire, not emitted as {}.
            Assert.False(root.TryGetProperty("metrics", out _));
        }

        var parsed = AspectVerdictParsing.TryParseJson(json);
        Assert.NotNull(parsed);
        Assert.Equal("code-quality", parsed!.Aspect);
        Assert.Equal("concerns", parsed.Status);
        Assert.Equal("quality:concerns", parsed.Tag);
    }

    [Fact]
    public void RenderJson_PassVerdict_OmitsTag_AndKeepsMetricsWhenProvided()
    {
        var verdict = new AspectVerdict("tests-and-evidence", AspectStatus.Pass, "ok", "body", null);
        var metrics = new Dictionary<string, string> { ["filesChanged"] = "3", ["testsPassed"] = "157" };

        var json = AspectVerdictParsing.RenderJson(verdict, "claude-haiku-4-5", DateTime.UtcNow, metrics);

        using var probe = System.Text.Json.JsonDocument.Parse(json);
        var root = probe.RootElement;
        Assert.False(root.TryGetProperty("tag", out _)); // null tag dropped
        Assert.Equal("3", root.GetProperty("metrics").GetProperty("filesChanged").GetString());
        Assert.Equal("157", root.GetProperty("metrics").GetProperty("testsPassed").GetString());
    }

    [Fact]
    public void TryParseJson_RejectsMarkdownTwin_AndBlankInput()
    {
        var md = AspectVerdictParsing.RenderReport(
            new AspectVerdict("code-quality", AspectStatus.Pass, "ok", "body", null), DateTime.UtcNow);
        Assert.Null(AspectVerdictParsing.TryParseJson(md));
        Assert.Null(AspectVerdictParsing.TryParseJson(""));
        Assert.Null(AspectVerdictParsing.TryParseJson("   "));
        Assert.Null(AspectVerdictParsing.TryParseJson("{ not valid json"));
    }

    // ---- AGT-2021: environmental retry-once + InfraCrash --------------------

    [Fact]
    public async Task MissingVerdict_FromDeadReviewer_RetriesOnce_ThenRecovers()
    {
        // The backend cut kills the reviewing CLI mid-run -> the first call
        // returns nothing. This is an INFRASTRUCTURE fault, not the card's work,
        // so the aspect runner reruns the step once with the environmental
        // backoff; the retry succeeds and the aspect passes cleanly.
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var runner = BuildRunner(aspect =>
        {
            var n = calls.AddOrUpdate(aspect, 1, (_, c) => c + 1);
            return n == 1
                ? string.Empty // dead reviewer: no output
                : "[[ASPECT_VERDICT: status=pass; summary=Recovered on retry.]]\n[[TASK_DONE]]";
        });
        runner.VerdictRetryBackoff = _ => TimeSpan.Zero; // no real wait in the test

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(report.HasInfraFailure);
        Assert.Equal(AspectStatus.Pass, report.Overall);
        Assert.Empty(report.ConcernTagIds);
        Assert.Equal(2, calls["code-quality"]); // ran once, retried once
        Assert.False(report.Verdicts.Single().IsInfraFailure);
    }

    [Fact]
    public async Task MissingVerdict_Twice_RecordsInfraCrash_NotUnfinishedWork()
    {
        // The reviewer dies on both the run and the retry -> environmental
        // InfraCrash. The verdict is flagged IsInfraFailure and hangs NO
        // review:unparseable concern tag (that tag means "model replied but broke
        // the format", a different, non-infra signal).
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var runner = BuildRunner(aspect =>
        {
            calls.AddOrUpdate(aspect, 1, (_, c) => c + 1);
            return string.Empty; // dead every time
        });
        runner.VerdictRetryBackoff = _ => TimeSpan.Zero;

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(report.HasInfraFailure);
        Assert.Single(report.InfraFailures);
        Assert.Equal("code-quality", report.InfraFailures[0].Aspect);
        Assert.True(report.Verdicts.Single().IsInfraFailure);
        Assert.Null(report.Verdicts.Single().ConcernTagId);
        Assert.Empty(report.ConcernTagIds); // no review:unparseable chip leaks
        Assert.Equal(2, calls["code-quality"]); // one run + one retry, then stop
        Assert.Contains("environmental infra crash", report.Verdicts.Single().Summary);
    }

    [Fact]
    public async Task ReviewerThrows_TreatedAsInfra_RetriesThenInfraCrash()
    {
        // A CLI invocation that THROWS (not just an empty reply) is the same
        // infra class: retry once, then InfraCrash.
        var calls = 0;
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>()).Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance)
        {
            VerdictRetryBackoff = _ => TimeSpan.Zero
        };
        runner.CliRunner = (_, _, _, _, _, _) =>
        {
            Interlocked.Increment(ref calls);
            throw new InvalidOperationException("reviewing CLI died");
        };

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "requirement-fit" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.True(report.HasInfraFailure);
        Assert.Equal(2, calls); // one run + one retry
    }

    [Fact]
    public async Task NonEmptyUnparseableReply_StaysConcern_NoRetry()
    {
        // A reviewer that DID reply (even garbage) is not an infra fault: it keeps
        // the existing review:unparseable concern and is NOT retried. Guards the
        // AGT-2021 change against widening the environmental class too far.
        var calls = new System.Collections.Concurrent.ConcurrentDictionary<string, int>();
        var runner = BuildRunner(aspect =>
        {
            calls.AddOrUpdate(aspect, 1, (_, c) => c + 1);
            return "I have no opinion.";
        });
        runner.VerdictRetryBackoff = _ => TimeSpan.Zero;

        var report = await runner.RunAsync(BuildInputs(),
            new[] { "code-quality" },
            "claude", "claude-haiku-4-5", TimeSpan.FromSeconds(5), CancellationToken.None);

        Assert.False(report.HasInfraFailure);
        Assert.Equal(AspectStatus.Concerns, report.Overall);
        Assert.Equal("review:unparseable", report.ConcernTagIds[0]);
        Assert.Equal(1, calls["code-quality"]); // no retry for a real (if garbage) reply
    }

    private AspectRunInputs BuildInputs() => new(
        Project: "demo",
        JobId: "test-job",
        JobTitle: "Test job",
        JobFolderPath: _jobFolder,
        TaskBody: "# Task\n\nDo the thing.",
        RecentLog: "[12:00:00] [stdout] running\n[12:00:01] [stdout] [[TASK_DONE]]",
        DiffSummary: "Files: src/foo.ts (+10, -2)",
        StatusSummary: "# Status\n\nDone.");

    private AspectRunnerService BuildRunner(Func<string, string> stub)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var runner = new AspectRunnerService(prompts, NullLogger<AspectRunnerService>.Instance);
        runner.CliRunner = (aspectId, _, _, _, _, _) => Task.FromResult(stub(aspectId));
        return runner;
    }
}
