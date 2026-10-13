using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Direct tests against <see cref="CodeReviewStepService"/>. Mirrors the
/// shape of <see cref="AspectRunnerTests"/>: the CLI is stubbed so the
/// suite runs offline and deterministically. The four invariants under
/// test are the load-bearing ones: (1) a parseable verdict produces a
/// matching MD + tag, (2) an unparseable / empty reply degrades to
/// Concerns (no silent durchwinken), (3) two runs against the same job
/// produce two distinct files (immutable history), (4) tags accumulate
/// across runs without losing earlier reviews.
/// </summary>
public class CodeReviewStepServiceTests : IDisposable
{
    private readonly string _jobFolder;

    public CodeReviewStepServiceTests()
    {
        _jobFolder = Path.Combine(Path.GetTempPath(), "code-review-step-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_jobFolder);
        File.WriteAllText(Path.Combine(_jobFolder, "task.json"), "{ \"id\": \"demo\", \"title\": \"Demo\" }");
    }

    public void Dispose()
    {
        try { Directory.Delete(_jobFolder, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public async Task PassVerdict_WritesMd_AddsPassTag_NoConcernsTag()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("looks fine\n[[ASPECT_VERDICT: status=pass; summary=Diff is clean.]]\n[[TASK_DONE]]"));

        var report = await service.RunAsync(BuildRequest("claude-opus-4-7"), CancellationToken.None);

        Assert.Equal(AspectStatus.Pass, report.Status);
        Assert.Equal("code-review:pass", report.ConcernTagId);
        Assert.True(File.Exists(report.FilePath));

        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("verdict: pass", content);
        Assert.Contains("Diff is clean.", content);
        Assert.Contains("model: claude-opus-4-7", content);

        Assert.Contains("code-review:pass", ReadTags());
    }

    [Fact]
    public async Task BlockVerdict_AddsBlockTag_AndPersistsBodyInMd()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("missing null guard\n[[ASPECT_VERDICT: status=block; summary=Null deref in lane resolver.]]\n[[TASK_DONE]]"));

        var report = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(AspectStatus.Block, report.Status);
        Assert.Equal("code-review:block", report.ConcernTagId);
        Assert.Contains("code-review:block", ReadTags());

        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("verdict: block", content);
        Assert.Contains("Null deref in lane resolver.", content);
        Assert.Contains("missing null guard", content);
    }

    [Fact]
    public async Task UnparseableReply_DefaultsToConcerns_NoSilentDurchwinken()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("I don't really have an opinion."));

        var report = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Status);
        Assert.Equal("code-review:concerns", report.ConcernTagId);
        Assert.Contains("code-review:concerns", ReadTags());

        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("verdict: concerns", content);
        Assert.Contains("Code review produced no parseable verdict sentinel.", content);
    }

    [Fact]
    public async Task EmptyReply_DefaultsToConcerns_WithDistinctSummary()
    {
        var service = BuildService((_, _, _, _, _) => Task.FromResult(string.Empty));

        var report = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Status);
        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("Code review produced no parseable reply.", content);
    }

    [Fact]
    public async Task CliThrows_TreatedAsConcerns_TagAndMdStillProduced()
    {
        var service = BuildService((_, _, _, _, _) => throw new InvalidOperationException("CLI exploded"));

        var report = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.Equal(AspectStatus.Concerns, report.Status);
        Assert.True(File.Exists(report.FilePath));
        Assert.Contains("code-review:concerns", ReadTags());
    }

    [Fact]
    public async Task TwoRuns_ProduceTwoDistinctFiles_HistoryIsImmutable()
    {
        var replies = new Queue<string>(new[]
        {
            "[[ASPECT_VERDICT: status=concerns; summary=First pass concern.]]\n[[TASK_DONE]]",
            "[[ASPECT_VERDICT: status=pass; summary=Second pass clean.]]\n[[TASK_DONE]]"
        });
        var service = BuildService((_, _, _, _, _) => Task.FromResult(replies.Dequeue()));

        var first = await service.RunAsync(BuildRequest(), CancellationToken.None);
        // Force a different filename without sleeping: rename the first file
        // so the timestamp-based slot is free even on fast machines (the
        // production timestamp resolution is one second).
        var rotated = first.FilePath + ".bak";
        File.Move(first.FilePath, rotated);
        var second = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.True(File.Exists(rotated), "first review file must be preserved");
        Assert.True(File.Exists(second.FilePath), "second review file must be written");
        Assert.NotEqual(rotated, second.FilePath);

        Assert.Contains("First pass concern.", File.ReadAllText(rotated));
        Assert.Contains("Second pass clean.", File.ReadAllText(second.FilePath));

        // Tag merge is idempotent: Concerns + Pass leaves both tags on the job.
        var tags = ReadTags();
        Assert.Contains("code-review:concerns", tags);
        Assert.Contains("code-review:pass", tags);
    }

    [Fact]
    public async Task FileNameContainsTimestamp_AndIsValidOnWindows()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]"));

        var report = await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.StartsWith("code-review-", report.FileName);
        Assert.EndsWith(".md", report.FileName);
        // Windows-illegal characters must not be in the filename.
        Assert.DoesNotContain(":", report.FileName);
        Assert.DoesNotContain("?", report.FileName);
        Assert.DoesNotContain("*", report.FileName);
    }

    [Fact]
    public async Task RunAsync_RegistersGeneratedFileMetadata_WhenIndexIsAvailable()
    {
        var index = new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance);
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("""
                {"type":"result","result":"[[ASPECT_VERDICT: status=pass; summary=ok]]","model":"claude-haiku-4-5","usage":{"input_tokens":100,"output_tokens":25,"cache_read_input_tokens":40,"cache_creation_input_tokens":10}}
                """), index);

        var report = await service.RunAsync(BuildRequest("claude-haiku-4-5"), CancellationToken.None);

        var entries = index.ReadForJob(_jobFolder, cacheLegacy: false);
        var generation = Assert.Single(entries.Values);
        Assert.Equal(report.FileName, generation.File);
        Assert.Equal("code-review", generation.Kind);
        Assert.Equal("claude-haiku-4-5", generation.Model);
        Assert.Equal("claude", generation.Cli);
        Assert.Equal("code-review-step", generation.StepId);
        Assert.Equal("0aa4c5d", generation.HeadShaAfter);
        Assert.Equal(100, generation.TokensIn);
        Assert.Equal(25, generation.TokensOut);
        Assert.Equal(40, generation.CacheReadTokens);
        Assert.Equal(10, generation.CacheCreationTokens);
        Assert.Equal(175, generation.TokensTotal);
    }

    [Fact]
    public async Task RunAsync_PromptNamesGoalMissRedundantAndHalfFinishedReviewRisks()
    {
        string? capturedPrompt = null;
        var service = BuildService((_, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult("[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        });

        await service.RunAsync(BuildRequest(), CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("do what the task asks", capturedPrompt);
        Assert.Contains("redundant work", capturedPrompt);
        Assert.Contains("half-finished", capturedPrompt);
        Assert.Contains("does not solve the task", capturedPrompt);
    }

    [Theory]
    [InlineData(CodeReviewMode.Verdict)]
    [InlineData(CodeReviewMode.Grade)]
    public async Task Prompt_CarriesResultsInventoryAndCardMode_ForEvidenceCompleteness(CodeReviewMode mode)
    {
        // AGT-2022: the code-review verdict AND grade prompts must both carry the
        // results/ inventory and the card-mode framing so a read-only / concept
        // card is never false-BLOCKed (or graded D) as "deliverables missing"
        // when the deliverable lives outside the git diff. This pins the
        // CodeReviewStepService half of the evidence contract that the AspectRunner
        // test pins for the aspect prompts.
        string? capturedPrompt = null;
        var service = BuildService((_, _, prompt, _, _) =>
        {
            capturedPrompt = prompt;
            return Task.FromResult(mode == CodeReviewMode.Grade
                ? "[[CODE_REVIEW_GRADE: grade=A; summary=ok]]\n[[TASK_DONE]]"
                : "[[ASPECT_VERDICT: status=pass; summary=ok]]\n[[TASK_DONE]]");
        });

        var request = BuildRequest() with
        {
            Mode = mode,
            ResultsInventory = "results/ folder contains 1 file(s):\n- plan.md (512 bytes)",
            CardMode = ReviewCardMode.Describe("planning"),
        };

        await service.RunAsync(request, CancellationToken.None);

        Assert.NotNull(capturedPrompt);
        Assert.Contains("results/ folder inventory", capturedPrompt!);
        Assert.Contains("plan.md", capturedPrompt);
        // Card-mode framing: a planning card legitimately ships no code diff.
        Assert.Contains("read-only", capturedPrompt);
        // The deliverables-missing rule is stated so an empty diff with results/
        // artefacts is not treated as a gap.
        Assert.Contains("results/ artefact", capturedPrompt);
    }

    [Fact]
    public void TagFor_MapsAllVerdicts()
    {
        Assert.Equal("code-review:pass", CodeReviewStepService.TagFor(AspectStatus.Pass));
        Assert.Equal("code-review:concerns", CodeReviewStepService.TagFor(AspectStatus.Concerns));
        Assert.Equal("code-review:block", CodeReviewStepService.TagFor(AspectStatus.Block));
    }

    [Fact]
    public async Task GradeMode_ParsesGradeSentinel_WritesGradeMd_AddsGradeTag()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("Complete and tested.\n[[CODE_REVIEW_GRADE: grade=A; summary=Solves the goal with tests.]]\n[[TASK_DONE]]"));

        var report = await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        Assert.Equal(CodeReviewGrade.A, report.Grade);
        Assert.Equal(AspectStatus.Pass, report.Status); // A maps to pass
        Assert.Equal("code-review:grade-a", report.ConcernTagId);
        Assert.StartsWith("code-review-grade-", report.FileName);

        var content = File.ReadAllText(report.FilePath);
        Assert.Contains("type: code-review-grade", content);
        Assert.Contains("grade: A", content);
        Assert.Contains("Quality Grade: A", content);
        Assert.Contains("Solves the goal with tests.", content);

        Assert.Contains("code-review:grade-a", ReadTags());
    }

    [Fact]
    public async Task GradeMode_UnparseableReply_DefaultsToGradeC_NoSilentA()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("I have some thoughts but no clear grade."));

        var report = await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        Assert.Equal(CodeReviewGrade.C, report.Grade);
        Assert.Equal(AspectStatus.Concerns, report.Status);
        Assert.Equal("code-review:grade-c", report.ConcernTagId);
        Assert.Contains("code-review:grade-c", ReadTags());
    }

    [Fact]
    public async Task GradeMode_CliThrows_ReportsExecutionError_WithoutAuthoritativeGradeTag()
    {
        File.WriteAllText(
            Path.Combine(_jobFolder, "task.json"),
            "{ \"id\": \"demo\", \"title\": \"Demo\", \"tags\": [\"keep-me\", \"code-review:grade-a\"] }");
        var service = BuildService((_, _, _, _, _) =>
            throw new InvalidOperationException("Codex grade process unavailable"));

        var report = await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        Assert.Equal("Codex grade process unavailable", report.ExecutionError);
        Assert.Null(report.ConcernTagId);
        Assert.DoesNotContain(ReadTags(), tag => tag.StartsWith("code-review:grade-"));
        Assert.Contains("keep-me", ReadTags());
        Assert.True(File.Exists(report.FilePath));
    }

    [Fact]
    public async Task GradeMode_DGrade_MapsToBlock_AndTags()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("Reimplements existing behaviour.\n[[CODE_REVIEW_GRADE: grade=D; summary=Redundant, not wired.]]"));

        var report = await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        Assert.Equal(CodeReviewGrade.D, report.Grade);
        Assert.Equal(AspectStatus.Block, report.Status);
        Assert.Contains("code-review:grade-d", ReadTags());
    }

    [Fact]
    public async Task GradeMode_ReGrade_ReplacesStaleGradeTag_KeepsExactlyOne()
    {
        var replies = new Queue<string>(new[]
        {
            "[[CODE_REVIEW_GRADE: grade=C; summary=First, half-done.]]",
            "[[CODE_REVIEW_GRADE: grade=A; summary=Now complete.]]",
        });
        var service = BuildService((_, _, _, _, _) => Task.FromResult(replies.Dequeue()));

        var first = await service.RunAsync(BuildGradeRequest(), CancellationToken.None);
        File.Move(first.FilePath, first.FilePath + ".bak");
        await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        var tags = ReadTags();
        Assert.Contains("code-review:grade-a", tags);
        Assert.DoesNotContain("code-review:grade-c", tags);
        Assert.Single(tags, t => t.StartsWith("code-review:grade-"));
    }

    [Fact]
    public async Task GradeMode_RegistersGeneratedFileMetadata_WithGradeKind()
    {
        var index = new FileGenerationIndex(NullLogger<FileGenerationIndex>.Instance);
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("[[CODE_REVIEW_GRADE: grade=B; summary=ok]]"), index);

        await service.RunAsync(BuildGradeRequest(), CancellationToken.None);

        var entries = index.ReadForJob(_jobFolder, cacheLegacy: false);
        var generation = Assert.Single(entries.Values);
        Assert.Equal("code-review-grade", generation.Kind);
    }

    [Fact]
    public async Task GradeMode_IdenticalRequestWithSameInputKey_GradesAfreshEveryCall()
    {
        // The operator-triggered grade calls the service directly, so reuse of
        // an unchanged grade lives only in the orchestrator, never here.
        var cliCalls = 0;
        var service = BuildService((_, _, _, _, _) =>
        {
            Interlocked.Increment(ref cliCalls);
            return Task.FromResult("[[CODE_REVIEW_GRADE: grade=D; summary=Misses the goal.]]");
        });
        var request = BuildGradeRequest();
        request = request with { InputKey = CodeReviewGradeReuse.ComputeInputKey(request) };

        var first = await service.RunAsync(request, CancellationToken.None);
        // File names have millisecond resolution; free the slot without sleeping.
        var rotated = first.FilePath + ".bak";
        File.Move(first.FilePath, rotated);
        var second = await service.RunAsync(request, CancellationToken.None);

        Assert.Equal(2, cliCalls);
        Assert.True(File.Exists(rotated));
        Assert.True(File.Exists(second.FilePath));
        Assert.NotEqual(rotated, second.FilePath);
    }

    [Fact]
    public async Task GradeMode_PersistsInputKeyInFrontmatter_OnlyWhenProvided()
    {
        var service = BuildService((_, _, _, _, _) =>
            Task.FromResult("[[CODE_REVIEW_GRADE: grade=B; summary=ok]]"));
        var request = BuildGradeRequest();
        var inputKey = CodeReviewGradeReuse.ComputeInputKey(request);

        var keyed = await service.RunAsync(request with { InputKey = inputKey }, CancellationToken.None);
        File.Move(keyed.FilePath, keyed.FilePath + ".bak");
        var unkeyed = await service.RunAsync(request, CancellationToken.None);

        var keyedFields = FrontmatterParser.Parse(File.ReadAllText(keyed.FilePath + ".bak")).Fields;
        Assert.Equal(inputKey, keyedFields[CodeReviewGradeReuse.InputKeyField]);
        Assert.StartsWith("sha256:", inputKey);
        Assert.DoesNotContain("inputKey:", File.ReadAllText(unkeyed.FilePath));
    }

    private CodeReviewStepRequest BuildGradeRequest(string model = "claude-opus-4-8") => BuildRequest(model) with
    {
        Mode = CodeReviewMode.Grade,
    };

    private CodeReviewStepRequest BuildRequest(string model = "claude-opus-4-7") => new(
        Project: "demo",
        JobId: "test-job",
        JobTitle: "Test job",
        JobFolderPath: _jobFolder,
        TaskBody: "# Task\n\nDo the thing.",
        Diff: "diff --git a/foo.ts b/foo.ts\n+ // new line",
        CliType: "claude",
        Model: model)
    {
        Commit = "0aa4c5d",
        Timeout = TimeSpan.FromSeconds(5),
    };

    private CodeReviewStepService BuildService(
        Func<string, string, string, TimeSpan, CancellationToken, Task<string>> stub,
        FileGenerationIndex? fileGenerationIndex = null)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>())
            .Build();
        var prompts = new RuntimePromptService(config, NullLogger<RuntimePromptService>.Instance);
        var service = new CodeReviewStepService(
            prompts,
            NullLogger<CodeReviewStepService>.Instance,
            fileGenerationIndex: fileGenerationIndex);
        service.CliRunner = stub;
        return service;
    }

    private List<string> ReadTags()
    {
        var jobJsonPath = Path.Combine(_jobFolder, "task.json");
        if (!File.Exists(jobJsonPath)) return new List<string>();
        var json = File.ReadAllText(jobJsonPath);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("tags", out var tagsEl)) return new List<string>();
        if (tagsEl.ValueKind != JsonValueKind.Array) return new List<string>();
        var list = new List<string>();
        foreach (var t in tagsEl.EnumerateArray())
        {
            if (t.ValueKind == JsonValueKind.String)
            {
                var s = t.GetString();
                if (!string.IsNullOrWhiteSpace(s)) list.Add(s);
            }
        }
        return list;
    }
}
