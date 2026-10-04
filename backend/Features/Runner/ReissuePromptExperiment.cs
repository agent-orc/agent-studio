using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Runner;

public static class ReissuePromptExperiment
{
    public const string ExperimentId = "finding-first-v1";
    public const string ControlArm = "control";
    public const string TreatmentArm = "treatment";
    public const string ControlTemplateVersion = "runner-reissue-control-v1";
    public const string TreatmentTemplateVersion = "runner-reissue-treatment-v1";
    public const string AssignmentUnit = "task";

    private static readonly Regex BacktickReference = new(
        @"`(?<value>[^`\r\n]{1,160})`",
        RegexOptions.Compiled);

    private static readonly Regex BareFileReference = new(
        @"(?<![\w.-])(?<value>(?:[\w.-]+/)+[\w.-]+\.[A-Za-z0-9]{1,12}(?::\d+)?|[\w.-]+\.(?:cs|ts|tsx|js|mjs|json|md|html|scss|css|yml|yaml)(?::\d+)?)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    /// <summary>
    /// Stable 50/50 task-level assignment. A task remains in one arm for every
    /// eligible reissue, avoiding cross-arm contamination of the primary
    /// attempts-to-acceptance endpoint.
    /// </summary>
    public static ReissuePromptAssignment Assign(
        string taskAssignmentKey,
        int attempt,
        string promptFamily,
        string cause,
        int findingCount)
    {
        var assignmentInput = $"{ExperimentId}\n{taskAssignmentKey.Trim().ToLowerInvariant()}";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(assignmentInput));
        var treatment = (hash[0] & 1) == 1;
        return new ReissuePromptAssignment
        {
            ExperimentId = ExperimentId,
            Arm = treatment ? TreatmentArm : ControlArm,
            TemplateVersion = treatment ? TreatmentTemplateVersion : ControlTemplateVersion,
            AssignmentUnit = AssignmentUnit,
            AssignmentHash = Convert.ToHexString(hash.AsSpan(0, 8)).ToLowerInvariant(),
            Attempt = Math.Max(1, attempt),
            PromptFamily = NormalizeStratum(promptFamily, "open-findings"),
            Cause = NormalizeStratum(cause, "unknown"),
            FindingCount = Math.Max(0, findingCount),
        };
    }

    public static string PromptTemplate(ReissuePromptAssignment assignment)
        => assignment.Arm == TreatmentArm
            ? Prompts.RuntimePromptService.RunnerReissueTreatmentV1
            : Prompts.RuntimePromptService.RunnerReissueControlV1;

    /// <summary>
    /// The treatment changes organization only. Every deficiency is copied
    /// verbatim from the common finding payload. References are extracted from
    /// that same text and no new domain claim is invented.
    /// </summary>
    public static string BuildTreatmentFindings(IReadOnlyList<string> findings, bool escalate, string? nonce = null)
    {
        var effective = findings.Count == 0
            ? new[] { "Read the reissue evidence and resolve the open auto-review finding." }
            : findings;
        var sb = new StringBuilder();
        if (escalate)
        {
            sb.AppendLf(
                "This task has already been reissued multiple times. Resolve only the numbered findings below, or stop with `[[TASK_BLOCKED:missing-dependency-xyz]]`, replacing the example reason with the actual short reason.");
            sb.AppendLf();
        }

        // AGT-2989: the deficiency text is reviewer output, so it is quoted in
        // a nonce-fenced data block; the per-finding directions stay outside.
        var data = new List<string>();
        for (var index = 0; index < effective.Count; index++)
        {
            var finding = effective[index];
            data.Add($"{index + 1}.");
            data.Add("   - Exact deficiency: " + finding);
            data.Add("   - File, symbol, or artifact: " + ExtractReference(finding));
        }
        sb.AppendLf(AgentStudio.Review.ReviewFindingDataBlock.Render(data, nonce));
        sb.AppendLf();
        sb.AppendLf("For each numbered finding above:");
        sb.AppendLf("   - Required change: Resolve the exact deficiency without unrelated scope.");
        sb.AppendLf("   - Focused verification or acceptance evidence: Run or add the smallest focused check that proves this finding is resolved, and report the result.");

        return sb.ToString().TrimEnd();
    }

    public static string ResolvePromptFamily(string cause)
        => cause switch
        {
            "code-review-council" or "multi-aspect-block" or "solution-quality-gate"
                => "model-review-finding",
            "evidence-gate" or "completion-gate" or "build-test-gate-fail" or "lint-scss-fail"
                => "deterministic-gate",
            "no-completion-signal" or "noop-recovery" or "needs-input"
                => "execution-protocol",
            _ => "other-reissue",
        };

    private static string ExtractReference(string finding)
    {
        var backtick = BacktickReference.Match(finding);
        if (backtick.Success)
            return $"`{backtick.Groups["value"].Value.Trim()}`";

        var bare = BareFileReference.Match(finding);
        return bare.Success
            ? $"`{bare.Groups["value"].Value.Trim()}`"
            : "Not named in the source finding; use the separate evidence block to locate it.";
    }

    private static string NormalizeStratum(string? value, string fallback)
    {
        var normalized = (value ?? string.Empty).Trim().ToLowerInvariant();
        return normalized.Length == 0 ? fallback : normalized;
    }
}

public sealed record ReissuePromptAssignment
{
    public string ExperimentId { get; init; } = ReissuePromptExperiment.ExperimentId;
    public string Arm { get; init; } = ReissuePromptExperiment.ControlArm;
    public string TemplateVersion { get; init; } = ReissuePromptExperiment.ControlTemplateVersion;
    public string AssignmentUnit { get; init; } = ReissuePromptExperiment.AssignmentUnit;
    public string AssignmentHash { get; init; } = "";
    public int Attempt { get; init; }
    public string PromptFamily { get; init; } = "open-findings";
    public string Cause { get; init; } = "unknown";
    public int FindingCount { get; init; }
}

public sealed record ReissuePromptExperimentRecord
{
    public int SchemaVersion { get; init; } = 1;
    public DateTime AssignedAt { get; init; }
    public string JobId { get; init; } = "";
    public string Project { get; init; } = "";
    public string ExperimentId { get; init; } = "";
    public string Arm { get; init; } = "";
    public string TemplateVersion { get; init; } = "";
    public string AssignmentUnit { get; init; } = "";
    public string AssignmentHash { get; init; } = "";
    public int Attempt { get; init; }
    public string PromptFamily { get; init; } = "";
    public string Cause { get; init; } = "";
    public int FindingCount { get; init; }
    public string? CodingModel { get; init; }
    public string? ThinkingLevel { get; init; }
}

public static class ReissuePromptExperimentLog
{
    public const string FileName = "reissue-prompt-experiment.jsonl";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static void Append(
        string jobFolderPath,
        string project,
        string jobId,
        ReissuePromptAssignment assignment,
        DateTime assignedAt,
        string? codingModel,
        string? thinkingLevel,
        ILogger logger)
    {
        try
        {
            var logs = Path.Combine(jobFolderPath, "logs");
            Directory.CreateDirectory(logs);
            var record = new ReissuePromptExperimentRecord
            {
                AssignedAt = assignedAt,
                JobId = jobId,
                Project = project,
                ExperimentId = assignment.ExperimentId,
                Arm = assignment.Arm,
                TemplateVersion = assignment.TemplateVersion,
                AssignmentUnit = assignment.AssignmentUnit,
                AssignmentHash = assignment.AssignmentHash,
                Attempt = assignment.Attempt,
                PromptFamily = assignment.PromptFamily,
                Cause = assignment.Cause,
                FindingCount = assignment.FindingCount,
                CodingModel = codingModel,
                ThinkingLevel = thinkingLevel,
            };
            File.AppendAllText(
                Path.Combine(logs, FileName),
                JsonSerializer.Serialize(record, Json) + Environment.NewLine,
                Encoding.UTF8);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to record reissue prompt experiment assignment for {JobId}", jobId);
        }
    }
}
