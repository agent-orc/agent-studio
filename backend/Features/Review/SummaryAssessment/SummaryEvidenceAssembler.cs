using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentStudio.Pipeline;

namespace AgentStudio.Review;

/// <summary>
/// Builds the <see cref="SummaryEvidenceBundle"/> for one card. Assembly is a
/// pure projection of already-read records so tests can pin it without a job
/// folder; <see cref="ReadFromFolder"/> is the thin disk adapter the summary
/// service uses.
/// </summary>
public static class SummaryEvidenceAssembler
{
    private const int MaxLaneTransitions = 12;
    private const int MaxCommits = 20;
    private const int MaxSalvageRefs = 5;
    private const int MaxReasonChars = 240;

    private static readonly JsonSerializerOptions PromptJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    public static SummaryEvidenceBundle Assemble(
        TaskInfo info,
        PipelineExecutionRecord? pipeline,
        IReadOnlyList<TimelineEvent> timeline,
        IntegrationVerificationRecord? verification,
        string? runOutcome)
    {
        var steps = pipeline?.Steps ?? [];
        var executed = steps
            .Where(s => s.Status is not (PipelineStepStatus.Pending or PipelineStepStatus.Planned))
            .Where(s => !string.Equals(s.StepId, PipelineCatalogue.SummaryAssessmentStepId, StringComparison.OrdinalIgnoreCase))
            .ToList();

        return new SummaryEvidenceBundle
        {
            TaskKey = info.TaskKey,
            TaskType = info.TaskType,
            Mode = info.Mode,
            Lane = info.State,
            RunOutcome = runOutcome,
            LaneHistory = timeline
                .Where(e => e.Kind == TimelineEventKinds.LaneChanged)
                .OrderBy(e => e.Ts)
                .TakeLast(MaxLaneTransitions)
                .Select(e => new SummaryLaneTransition(e.Ts, e.Summary))
                .ToList(),
            Pipeline = pipeline is null
                ? null
                : new SummaryPipelineEvidence(
                    pipeline.Attempt,
                    pipeline.IsComplete,
                    executed.Select(s => new SummaryStepEvidence(
                        s.StepId,
                        s.Status.ToString().ToLowerInvariant(),
                        Clip(s.Reason),
                        s.FailureCode,
                        s.Verdict)).ToList()),
            Commits = info.Commits
                .TakeLast(MaxCommits)
                .Select(c => new SummaryCommitEvidence(
                    string.IsNullOrWhiteSpace(c.Sha) ? c.ShortSha : c.Sha,
                    FirstLine(c.Message),
                    c.FilesChanged,
                    !string.IsNullOrWhiteSpace(c.SupersededBySha)))
                .ToList(),
            CodeActivityDetected = info.CodeActivityDetected,
            IntegrationRecords = info.IntegrationRecords
                .Select(r => new SummaryIntegrationEvidence(
                    r.Classification,
                    r.IntegrationBranch,
                    r.IntegrationSha,
                    r.SourceSha,
                    [.. r.CommitShas],
                    Clip(r.Evidence) ?? ""))
                .ToList(),
            IntegrationVerification = verification is null
                ? null
                : new SummaryVerificationEvidence(verification.State, verification.Sha, verification.Evidence),
            SalvageRefs = timeline
                .Where(e => e.Kind == TimelineEventKinds.AgentRunFinished && e.Details is not null)
                .Select(e => new SummarySalvageEvidence(
                    e.Details!.GetValueOrDefault("salvageBranch"),
                    e.Details.GetValueOrDefault("salvageCommitSha"),
                    e.Details.GetValueOrDefault("salvageResolution"),
                    e.Details.GetValueOrDefault("status")))
                .Where(s => !string.IsNullOrWhiteSpace(s.Branch) || !string.IsNullOrWhiteSpace(s.CommitSha))
                .TakeLast(MaxSalvageRefs)
                .ToList(),
            CompletionClaim = info.CompletionClaim is { } claim
                ? new SummaryCompletionClaimEvidence(
                    claim.Basis,
                    Clip(claim.Evidence) ?? "",
                    Clip(claim.Reason),
                    claim.CommitSha,
                    claim.DeliverablePath)
                : null,
            OrchestratorVerdict = info.OrchestratorVerdict,
            ReviewVerdicts = executed
                .Where(s => !string.IsNullOrWhiteSpace(s.Verdict)
                    && !string.Equals(s.StepId, PipelineCatalogue.ConceptSightReviewGateStepId, StringComparison.OrdinalIgnoreCase))
                .Select(s => new SummaryStepVerdict(s.StepId, s.Verdict!))
                .ToList(),
            SightReviewVerdict = steps
                .FirstOrDefault(s => string.Equals(s.StepId, PipelineCatalogue.ConceptSightReviewGateStepId, StringComparison.OrdinalIgnoreCase))
                ?.Verdict,
        };
    }

    /// <summary>Reads the folder-owned records and assembles the bundle. Missing records stay empty.</summary>
    public static SummaryEvidenceBundle ReadFromFolder(
        TaskInfo info,
        PipelineExecutionLog? pipelineLog,
        TimelineLog? timeline,
        string? runOutcome)
    {
        var folder = info.FolderPath;
        return Assemble(
            info,
            pipelineLog?.Read(folder),
            timeline?.ReadAll(folder) ?? [],
            IntegrationVerificationStore.Read(folder),
            runOutcome);
    }

    /// <summary>Returns the bundle with the generated status.md header metrics attached.</summary>
    public static SummaryEvidenceBundle WithStatusHeader(SummaryEvidenceBundle bundle, string? statusMarkdown)
        => bundle with { StatusHeader = ParseStatusHeader(statusMarkdown) };

    /// <summary>
    /// Reads the <c>- Result:</c>, <c>- Files:</c> and <c>- Tests:</c> lines
    /// above the first <c>##</c> heading, the same lines the Result tab renders
    /// as header chips.
    /// </summary>
    public static SummaryStatusHeader? ParseStatusHeader(string? markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown)) return null;
        var text = markdown.Replace("\r\n", "\n");
        var firstSection = Regex.Match(text, @"(?m)^## ");
        var head = firstSection.Success ? text[..firstSection.Index] : text;
        var result = HeaderField(head, "Result");
        var filesRaw = HeaderField(head, "Files");
        int? files = filesRaw is not null && Regex.Match(filesRaw, @"\d+") is { Success: true } m
            ? int.Parse(m.Value, System.Globalization.CultureInfo.InvariantCulture)
            : null;
        var tests = HeaderField(head, "Tests");
        return result is null && files is null && tests is null ? null : new SummaryStatusHeader(result, files, tests);
    }

    public static string ToPromptJson(SummaryEvidenceBundle bundle)
        => JsonSerializer.Serialize(bundle with { StatusHeader = null }, PromptJson);

    private static string? HeaderField(string head, string name)
    {
        var match = Regex.Match(head, @"(?mi)^\s*-\s*" + name + @":[ \t]*(?<value>[^\n]*)$");
        return match.Success && !string.IsNullOrWhiteSpace(match.Groups["value"].Value)
            ? match.Groups["value"].Value.Trim()
            : null;
    }

    private static string FirstLine(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";
        var line = text.Replace("\r\n", "\n").Split('\n')[0].Trim();
        return Clip(line) ?? "";
    }

    private static string? Clip(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var trimmed = text.Trim();
        return trimmed.Length <= MaxReasonChars ? trimmed : trimmed[..MaxReasonChars] + "...";
    }
}
