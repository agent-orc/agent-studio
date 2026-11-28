using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>A semantic reviewer block needs a citation to material changed by this delivery.</summary>
public static class ReviewDiffEvidencePolicy
{
    public const string ConcernClassification = "review-concern-no-diff-evidence";

    public static ReviewVerdictDto Normalize(
        ReviewVerdictDto verdict, IReadOnlyList<string> changedPaths)
    {
        if (!ReviewGradingPolicy.IsBlockingToken(verdict.Status))
            return verdict.Classification == ReviewVerdictCitationPolicy.BlockWithoutCitation
                ? verdict with
                {
                    Diagnosis = new DeliveryFailureDiagnosisResult(
                        DeliveryFailureDiagnosis.Concern, 1,
                        ["reviewer block lacked a complete review-material citation"]),
                }
                : verdict;
        if (CitesChangedPath(verdict.EvidenceChecked, changedPaths))
            return verdict with
            {
                Diagnosis = new DeliveryFailureDiagnosisResult(
                    DeliveryFailureDiagnosis.Product, 1,
                    ["reviewer cited a path changed by the delivery", verdict.EvidenceChecked!]),
            };
        return verdict with
        {
            Status = "concerns",
            Classification = ConcernClassification,
            Summary = verdict.Summary + " No changed file was cited as evidence against the delivery diff.",
            Diagnosis = new DeliveryFailureDiagnosisResult(
                DeliveryFailureDiagnosis.Concern, 1,
                ["reviewer block has no evidence against a changed path"]),
        };
    }

    internal static bool CitesChangedPath(string? evidence, IReadOnlyList<string> changedPaths)
        => changedPaths.Any(path =>
            !string.IsNullOrWhiteSpace(path)
            && evidence is not null
            && Regex.IsMatch(evidence,
                @"(?<![\w./\\-])" + Regex.Escape(path) + @"(?![\w./\\-])",
                RegexOptions.IgnoreCase | RegexOptions.CultureInvariant));
}
