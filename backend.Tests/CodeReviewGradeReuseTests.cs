using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Decision matrix for <see cref="CodeReviewGradeReuse"/> (AGT-3081): the
/// input key encoding and every condition under which an earlier grade of the
/// open attempt may stand for the current pass.
/// </summary>
public class CodeReviewGradeReuseTests
{
    private const string JobFolder = "/jobs/demo";
    private const string ReportFile = "code-review-grade-a.md";
    private const string Finding =
        "The review report was never committed; commit the report and its screenshots.";

    [Fact]
    public void InputKey_IsStableForIdenticalRequests()
    {
        Assert.Equal(
            CodeReviewGradeReuse.ComputeInputKey(Request()),
            CodeReviewGradeReuse.ComputeInputKey(Request()));
        Assert.Matches("^sha256:[0-9a-f]{64}$", CodeReviewGradeReuse.ComputeInputKey(Request()));
    }

    [Fact]
    public void InputKey_KeepsNullDistinctFromEmpty()
    {
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = null }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = string.Empty }));
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { ThinkingLevel = null }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { ThinkingLevel = string.Empty }));
    }

    [Fact]
    public void InputKey_LengthPrefixPreventsBoundaryCollisions()
    {
        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Diff = "ab", Commit = "c" }),
            CodeReviewGradeReuse.ComputeInputKey(Request() with { Diff = "a", Commit = "bc" }));
    }

    public static TheoryData<string> KeyedFields => new()
    {
        "diff", "commit", "taskBody", "resultsInventory", "cardMode", "cliType", "model", "thinkingLevel",
    };

    [Theory]
    [MemberData(nameof(KeyedFields))]
    public void InputKey_ChangesWithEveryKeyedField(string field)
    {
        var baseline = Request();
        var changed = field switch
        {
            "diff" => baseline with { Diff = baseline.Diff + "x" },
            "commit" => baseline with { Commit = "11111111 (HEAD)" },
            "taskBody" => baseline with { TaskBody = baseline.TaskBody + "x" },
            "resultsInventory" => baseline with { ResultsInventory = baseline.ResultsInventory + "x" },
            "cardMode" => baseline with { CardMode = baseline.CardMode + "x" },
            "cliType" => baseline with { CliType = "codex" },
            "model" => baseline with { Model = "other-model" },
            "thinkingLevel" => baseline with { ThinkingLevel = "high" },
            _ => throw new ArgumentOutOfRangeException(nameof(field)),
        };

        Assert.NotEqual(
            CodeReviewGradeReuse.ComputeInputKey(baseline),
            CodeReviewGradeReuse.ComputeInputKey(changed));
    }

    [Fact]
    public void TryReuse_MatchingKey_RebuildsTheEarlierReport()
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request());

        var report = CodeReviewGradeReuse.TryReuse(Row(), key, Report(key), JobFolder);

        Assert.NotNull(report);
        Assert.Equal(ReportFile, report!.FileName);
        Assert.Equal(Path.Combine(JobFolder, ReportFile), report.FilePath);
        Assert.Equal(CodeReviewGrade.D, report.Grade);
        Assert.Equal(AspectStatus.Block, report.Status);
        Assert.Equal("code-review:grade-d", report.ConcernTagId);
        Assert.Equal("Misses the goal.", report.Summary);
        Assert.Equal("claude-opus-5-5", report.Model);
        Assert.Equal("claude", report.CliType);
        Assert.Equal("low", report.ThinkingLevel);
        Assert.Equal("10c8e370 (HEAD)", report.Commit);
        Assert.Equal(12_000, report.DurationMs);
        Assert.Equal(DateTime.UnixEpoch, report.StartedAt);
        Assert.Null(report.ExecutionError);
        Assert.Null(report.Usage);
        Assert.Equal(Finding, Assert.Single(report.Findings!));
    }

    [Fact]
    public void TryReuse_HeadPlaceholderCommit_MapsBackToNull()
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request() with { Commit = null });

        var report = CodeReviewGradeReuse.TryReuse(Row(), key, Report(key, commit: "(HEAD)"), JobFolder);

        Assert.NotNull(report);
        Assert.Null(report!.Commit);
    }

    public static TheoryData<string> RejectedCases => new()
    {
        "no-checkpoint", "no-verdict", "unknown-verdict", "no-evidence-ref", "missing-report",
        "no-frontmatter", "legacy-no-key", "other-key", "grade-mismatch", "wrong-type",
    };

    [Theory]
    [MemberData(nameof(RejectedCases))]
    public void TryReuse_GradesAfresh_WhenAnyConditionFails(string scenario)
    {
        var key = CodeReviewGradeReuse.ComputeInputKey(Request());
        var row = Row();
        var markdown = Report(key);
        switch (scenario)
        {
            case "no-checkpoint": row = null; break;
            case "no-verdict": row = row with { Verdict = null }; break;
            case "unknown-verdict": row = row with { Verdict = "?" }; break;
            case "no-evidence-ref": row = row with { EvidenceRef = null }; break;
            case "missing-report": markdown = null; break;
            case "no-frontmatter": markdown = "# Code Review - Quality Grade: D\n"; break;
            case "legacy-no-key": markdown = Report(inputKey: null); break;
            case "other-key": markdown = Report(CodeReviewGradeReuse.ComputeInputKey(Request() with { Model = "x" })); break;
            case "grade-mismatch": markdown = Report(key, grade: "B"); break;
            case "wrong-type": markdown = Report(key).Replace("type: code-review-grade", "type: code-review-step"); break;
            default: throw new ArgumentOutOfRangeException(nameof(scenario));
        }

        Assert.Null(CodeReviewGradeReuse.TryReuse(row, key, markdown, JobFolder));
    }

    private static CodeReviewStepRequest Request() => new(
        Project: "demo",
        JobId: "WEB-23",
        JobTitle: "Website review",
        JobFolderPath: JobFolder,
        TaskBody: "# Task\n\nReview the website.",
        Diff: "diff --git a/a.txt b/a.txt\n+change",
        CliType: "claude",
        Model: "claude-opus-5-5")
    {
        Mode = CodeReviewMode.Grade,
        Commit = "10c8e370 (HEAD)",
        ThinkingLevel = "low",
        ResultsInventory = "results/report.md",
        CardMode = "Implementation card.",
    };

    private static PipelineStepExecution Row() => new()
    {
        StepId = PipelineCatalogue.CodeReviewGradeStepId,
        Kind = StepKind.Orchestrator,
        Status = PipelineStepStatus.Failed,
        StartedAt = DateTime.UnixEpoch,
        CompletedAt = DateTime.UnixEpoch.AddSeconds(12),
        DurationMs = 12_000,
        Verdict = "D",
        VerdictSummary = "Misses the goal.",
        EvidenceRef = ReportFile,
        Runs = 1,
    };

    private static string Report(string? inputKey, string grade = "D", string commit = "10c8e370 (HEAD)") =>
        "---\n" +
        "type: code-review-grade\n" +
        "model: claude-opus-5-5\n" +
        "cliType: claude\n" +
        "thinkingLevel: low\n" +
        $"commit: {commit}\n" +
        $"grade: {grade}\n" +
        "verdict: block\n" +
        "summary: Misses the goal.\n" +
        $"tag: code-review:grade-{grade.ToLowerInvariant()}\n" +
        (inputKey is null ? string.Empty : $"inputKey: {inputKey}\n") +
        "---\n\n" +
        $"# Code Review - Quality Grade: {grade}\n\n" +
        "## Reviewer reply\n\n" +
        "The report is missing.\n" +
        $"[[CODE_REVIEW_FINDING: text={Finding}]]\n" +
        $"[[CODE_REVIEW_GRADE: grade={grade}; summary=Misses the goal.]]\n";
}
