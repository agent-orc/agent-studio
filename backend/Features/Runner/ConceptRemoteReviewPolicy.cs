using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Runner;

/// <summary>
/// Fail closed when a concept report omits its content aspect or judges a
/// different brief. The existing aspect runner supplies the content verdict;
/// this policy only checks coverage and brief identity at settlement.
/// </summary>
public static class ConceptRemoteReviewPolicy
{
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
                $"The concept brief changed after the ReviewSubject was created: reviewed prompt.md SHA-256 {subjectBriefSha256}, current prompt.md SHA-256 {currentBriefSha256}. A new review against the current brief is required.");
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
