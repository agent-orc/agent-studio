using System.Security.Cryptography;
using System.Text;

namespace AgentStudio.Review;

/// <summary>
/// Pure reuse rule for the automatic quality grade (AGT-3081). A deferred
/// review re-drives the same open pipeline attempt on every tick; when the
/// grade's inputs are byte-identical to the graded report of that attempt, the
/// orchestrator rebuilds the earlier report instead of paying for another
/// model call. The operator-triggered grade never consults this rule.
/// </summary>
public static class CodeReviewGradeReuse
{
    /// <summary>Frontmatter key that carries the input key in a grade report.</summary>
    public const string InputKeyField = "inputKey";

    private const string InputKeyPrefix = "sha256:";
    private const string ReplyHeading = "## Reviewer reply";

    /// <summary>
    /// SHA-256 over every request field that reaches the grade prompt or picks
    /// its reviewer. Each field is length-prefixed and null stays distinct from
    /// empty, so no two field combinations can collide by concatenation.
    /// </summary>
    public static string ComputeInputKey(CodeReviewStepRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        AppendField(hash, request.Diff);
        AppendField(hash, request.Commit);
        AppendField(hash, request.TaskBody);
        AppendField(hash, request.ResultsInventory);
        AppendField(hash, request.CardMode);
        AppendField(hash, request.CliType);
        AppendField(hash, request.Model);
        AppendField(hash, request.ThinkingLevel);
        return InputKeyPrefix + Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
    }

    /// <summary>Short form for log lines: the first 12 hex characters.</summary>
    public static string ShortKey(string inputKey)
    {
        var hex = inputKey.StartsWith(InputKeyPrefix, StringComparison.Ordinal)
            ? inputKey[InputKeyPrefix.Length..]
            : inputKey;
        return hex.Length <= 12 ? hex : hex[..12];
    }

    /// <summary>
    /// Rebuild the earlier grade report when it may stand for this pass, or
    /// return null to grade afresh. <paramref name="checkpoint"/> is the
    /// terminal grade row of the still-open attempt (null when the attempt is
    /// complete, a new attempt started, or the row is still Running);
    /// <paramref name="reportMarkdown"/> is the text of the file named by its
    /// <c>EvidenceRef</c> (null when missing or unreadable).
    /// </summary>
    public static CodeReviewStepReport? TryReuse(
        PipelineStepExecution? checkpoint,
        string inputKey,
        string? reportMarkdown,
        string jobFolderPath)
    {
        if (checkpoint is null || string.IsNullOrWhiteSpace(inputKey)) return null;

        // A row without a grade verdict is an execution error, a cancellation
        // or an exception: no authoritative grade exists to reuse.
        var grade = CodeReviewGradeParsing.TokenToGrade(checkpoint.Verdict);
        if (grade is null || string.IsNullOrWhiteSpace(checkpoint.EvidenceRef)) return null;
        if (string.IsNullOrWhiteSpace(reportMarkdown)) return null;

        var frontmatter = FrontmatterParser.Parse(reportMarkdown);
        if (!frontmatter.Ok) return null;
        var fields = frontmatter.Fields;
        if (!string.Equals(Field(fields, "type"), "code-review-grade", StringComparison.Ordinal)) return null;
        // A legacy report without a key gets one fresh grade.
        if (!string.Equals(Field(fields, InputKeyField), inputKey, StringComparison.Ordinal)) return null;
        if (CodeReviewGradeParsing.TokenToGrade(Field(fields, "grade")) != grade) return null;

        var model = Field(fields, "model");
        var cliType = Field(fields, "cliType");
        if (string.IsNullOrWhiteSpace(model) || string.IsNullOrWhiteSpace(cliType)) return null;

        // RenderReport writes a null commit label as "(HEAD)".
        var commit = Field(fields, "commit");
        if (string.IsNullOrWhiteSpace(commit) || commit == "(HEAD)") commit = null;

        return new CodeReviewStepReport(
            FileName: checkpoint.EvidenceRef,
            FilePath: Path.Combine(jobFolderPath, checkpoint.EvidenceRef),
            Status: CodeReviewGradeParsing.ToAspectStatus(grade.Value),
            Summary: checkpoint.VerdictSummary ?? string.Empty,
            Model: model,
            CliType: cliType,
            ThinkingLevel: string.IsNullOrWhiteSpace(Field(fields, "thinkingLevel")) ? null : Field(fields, "thinkingLevel"),
            Commit: commit,
            ConcernTagId: CodeReviewGradeParsing.TagFor(grade.Value),
            DurationMs: checkpoint.DurationMs,
            StartedAt: checkpoint.StartedAt ?? default,
            Grade: grade,
            ExecutionError: null,
            Findings: CodeReviewFindingParsing.Parse(ReviewerReply(frontmatter.Body)),
            Usage: null);
    }

    /// <summary>Everything after the report's <c>## Reviewer reply</c> heading.</summary>
    private static string ReviewerReply(string body)
    {
        var normalized = body.Replace("\r\n", "\n");
        var index = normalized.StartsWith(ReplyHeading + "\n", StringComparison.Ordinal)
            ? 0
            : normalized.IndexOf("\n" + ReplyHeading + "\n", StringComparison.Ordinal);
        if (index < 0) return string.Empty;
        var start = normalized.IndexOf(ReplyHeading, index, StringComparison.Ordinal) + ReplyHeading.Length;
        return normalized[start..];
    }

    private static string? Field(IReadOnlyDictionary<string, string> fields, string key)
        => fields.TryGetValue(key, out var value) ? value : null;

    private static void AppendField(IncrementalHash hash, string? value)
    {
        Span<byte> header = stackalloc byte[5];
        if (value is null)
        {
            header[0] = 0;
            hash.AppendData(header[..1]);
            return;
        }

        var bytes = Encoding.UTF8.GetBytes(value);
        header[0] = 1;
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header[1..], bytes.Length);
        hash.AppendData(header);
        hash.AppendData(bytes);
    }
}
