using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>
/// Fail closed when a concept report omits its content aspect or judges a
/// different brief. Distinguish unavailable review inputs from content findings.
/// </summary>
public static class ConceptRemoteReviewPolicy
{
    public const string MaterialUnavailable = "ReviewMaterialUnavailable";

    public const string WorkspaceInstructions =
        "Read the complete Dossier in the read-only Result-SHA checkout. Inspect the index, workbench.json, "
        + "local files they link or include, and other Dossier files changed by this delivery. Read large files in chunks. "
        + "The diff and any snapshot in the prompt are excerpts. Use the checkout even if an excerpt marks a file missing. "
        + "Use only tracked files inside this repository. If a required file is absent from the Git tree, cite that delivery gap. "
        + "If review material cannot be read, emit the aspect sentinel with status=concerns and classification="
        + MaterialUnavailable + ". Name the inaccessible paths and read failure in missing.";

    // Older prompts asked for an ordinary concern when the input was incomplete.
    // Recognize evidence-access statements, leaving findings about missing sections alone.
    private static readonly Regex IncompleteInput = new(
        @"\b(?:supplied diff|(?:published |Dossier )snapshot|review (?:input|material)|Dossier evidence|unchanged Dossier sections)"
        + @" (?:is |are |was |were )?(?:unavailable|incomplete|missing|truncated|insufficient)\b"
        + @"|\bDossier files (?:are |were )?marked missing\b|\bincomplete Dossier evidence\b"
        + @"|\bDossier content needed to judge completeness is unavailable\b"
        + @"|\b(?:I|we|the reviewer) (?:could not|cannot) (?:read|access|see) (?:the |the full |the complete )?(?:Dossier|index\.html|workbench\.json)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsMaterialUnavailable(ReviewVerdictDto verdict)
    {
        if (!string.Equals(verdict.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(verdict.Classification, MaterialUnavailable, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.Equals(verdict.Status, "concerns", StringComparison.OrdinalIgnoreCase))
            return false;
        var summary = verdict.Summary ?? string.Empty;
        return IncompleteInput.IsMatch(summary) || IncompleteInput.IsMatch(verdict.Missing ?? "")
            || (summary.Contains("cannot be verified", StringComparison.OrdinalIgnoreCase)
                && verdict.Missing?.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase) == true
                && verdict.Missing.Contains("index.html", StringComparison.OrdinalIgnoreCase));
    }

    public static string MaterialFailureSummary(ReviewVerdictDto verdict)
        => $"Concept-fit review material unavailable. {verdict.Summary} {verdict.Missing}".TrimEnd();

    public static bool AppliesTo(ReviewPlanDto plan)
        => string.Equals(plan.TaskMode, "concept", StringComparison.OrdinalIgnoreCase)
           || plan.BriefSha256 is not null
           || plan.Commands.Any(command => string.Equals(command.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase))
           || plan.RequiredAspects.Contains("concept-fit", StringComparer.OrdinalIgnoreCase)
           || plan.SkippedAspects?.Any(item => string.Equals(item.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase)) == true;

    public static ReviewReportRequest Enforce(
        ReviewPlanDto plan,
        string? subjectBriefSha256,
        string? currentBriefSha256,
        ReviewReportRequest report)
    {
        report = report with
        {
            BriefSha256 = subjectBriefSha256,
            SkippedAspects = plan.SkippedAspects,
        };
        if (string.IsNullOrWhiteSpace(subjectBriefSha256)
            || string.IsNullOrWhiteSpace(currentBriefSha256))
            return Fail(report, "The concept brief version is unavailable; concept-fit cannot establish requirement fit.");
        if (!string.Equals(plan.BriefSha256, subjectBriefSha256, StringComparison.OrdinalIgnoreCase))
            return Fail(report,
                $"The frozen concept-fit prompt does not match the ReviewSubject brief version: plan {plan.BriefSha256 ?? "missing"}, subject {subjectBriefSha256}. A new review is required.");
        if (!string.Equals(subjectBriefSha256, currentBriefSha256, StringComparison.OrdinalIgnoreCase))
            return Fail(report,
                $"The concept brief changed after the ReviewSubject was created: reviewed authored version {subjectBriefSha256}, current authored version {currentBriefSha256}. A new review against the current brief is required.");
        if (report.Outcome is "ReviewInfra" or "Cancellation")
            return report;
        if (!plan.RequiredAspects.Contains("concept-fit", StringComparer.OrdinalIgnoreCase)
            || !plan.Commands.Any(command =>
                string.Equals(command.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase)
                && ReviewCommandKinds.IsAgent(command.ExecutionKind)))
            return Fail(report, "Applicable concept-fit was skipped: " +
                (plan.SkippedAspects?.FirstOrDefault(item => item.Aspect == "concept-fit")?.Reason
                 ?? "the frozen review plan did not include it."));
        var verdict = report.Verdicts.FirstOrDefault(item =>
            string.Equals(item.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase));
        if (verdict is null)
            return Fail(report, "The concept-fit aspect did not run or supplied no verdict; build and lint alone cannot pass this concept delivery.");
        if (IsMaterialUnavailable(verdict))
            return report with
            {
                Outcome = "ReviewInfra",
                FailureClassification = MaterialUnavailable,
                Summary = MaterialFailureSummary(verdict),
                Verdicts = report.Verdicts.Where(item => !IsMaterialUnavailable(item)).ToArray(),
            };
        if (ReviewGradingPolicy.IsBlockingToken(verdict.Status)
            && string.Equals(report.Outcome, "Pass", StringComparison.Ordinal))
            return report with
            {
                Outcome = "ProductFailure",
                FailureClassification = "ReviewFinding",
                Summary = verdict.Summary,
            };
        return report;
    }

    private static ReviewReportRequest Fail(ReviewReportRequest report, string reason)
        => report with
        {
            Outcome = "Inconclusive",
            FailureClassification = "ConceptReviewIncomplete",
            Summary = reason,
        };
}
