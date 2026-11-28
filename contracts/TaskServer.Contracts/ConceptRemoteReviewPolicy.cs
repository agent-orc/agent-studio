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

    // Only the old stock prompt instructed the reviewer to judge the snapshot without tools.
    private const string LegacyPromptEnding = "End with `[[TASK_DONE]]` on its own line. Do not edit files or run tools.";

    private static readonly Regex IncompleteInput = new(
        @"(?:^|, but )(?:the )?(?:supplied diff (?:is|was) insufficient for (?:a full |the )?concept-fit review"
        + @"|(?:published Dossier snapshot|unchanged Dossier sections) (?:is|are|was|were) (?:incomplete|unavailable)"
        + @"|incomplete Dossier evidence prevents a full concept-fit review"
        + @"|Dossier content needed to judge completeness is unavailable"
        + @"|required Dossier (?:content|sections) and the (?:complete|full) (?:implementation-)?card sequence cannot be verified"
        + @"|(?:I|we|the reviewer) (?:could not|cannot) (?:read|access|see) (?:the |the full |the complete )?(?:Dossier|index\.html|workbench\.json))\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.NonBacktracking);

    public static bool IsMaterialUnavailable(ReviewVerdictDto verdict, ReviewPlanDto? plan = null)
    {
        if (!string.Equals(verdict.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase)
            || !string.Equals(verdict.Status, "concerns", StringComparison.OrdinalIgnoreCase))
            return false;
        if (string.Equals(verdict.Classification, MaterialUnavailable, StringComparison.OrdinalIgnoreCase))
            return true;
        if (verdict.UsesDossierCheckout || plan?.Commands.Any(command =>
                string.Equals(command.Aspect, "concept-fit", StringComparison.OrdinalIgnoreCase)
                && command.Prompt?.TrimEnd().EndsWith(LegacyPromptEnding, StringComparison.Ordinal) == true) != true)
            return false;
        var missing = verdict.Missing?.Trim();
        if (string.Equals(missing?.TrimEnd('.'), "Published Dossier files are marked missing, and their diffs are truncated",
                StringComparison.OrdinalIgnoreCase))
            return true;
        return IncompleteInput.IsMatch(verdict.Summary?.Trim() ?? "") &&
            (string.IsNullOrEmpty(missing)
             || missing.StartsWith("docs/", StringComparison.OrdinalIgnoreCase)
             || missing.StartsWith("Full ", StringComparison.OrdinalIgnoreCase)
             || missing.StartsWith("Complete ", StringComparison.OrdinalIgnoreCase)
             || missing.StartsWith("The complete ", StringComparison.OrdinalIgnoreCase)
             || missing.StartsWith("The published ", StringComparison.OrdinalIgnoreCase));
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
        if (IsMaterialUnavailable(verdict, plan))
            return report with
            {
                Outcome = "ReviewInfra",
                FailureClassification = MaterialUnavailable,
                Summary = MaterialFailureSummary(verdict),
                Verdicts = report.Verdicts.Where(item => !IsMaterialUnavailable(item, plan)).ToArray(),
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
