namespace AgentStudio.TaskServer.Contracts;

/// <summary>Evidence requirements for a code-quality verdict produced under the current prompt.</summary>
public static class CodeQualityReviewPolicy
{
    public const string ProtocolHeading = "## Code-quality verdict evidence";
    public const string Advisory = "quality-advisory";
    public const string Uncorroborated = "quality-uncorroborated";
    public const string Confirmed = "quality-confirmed";
    public const string Rechecked = "quality-rechecked";
    public const string Independent = "quality-independent";
    private const string PreviousContractHeading = "## Previous code-quality contract to recheck";
    private const string PreviousReviewLabel = "\n- Previous review: ";
    private const string PreviousEvidenceLabel = "\n- Previous evidence: ";
    private const string PreviousFindingLabel = "\n- Previous finding: ";
    private const string Unresolved = "The previous contract has no conclusive recheck recorded.";

    public static ReviewVerdictDto Normalize(ReviewVerdictDto verdict, string? prompt)
    {
        if (!IsCodeQuality(verdict.Aspect) || verdict.CarriedOverFrom is not null
            || verdict.Classification is "CommandFailed" or ReviewCommandVerdictPolicy.UnparseableClassification)
            return verdict;

        var isBlock = ReviewGradingPolicy.IsBlockingToken(verdict.Status);
        // Frozen plans and existing verdicts keep their original marker contract.
        if (!UsesProtocol(prompt))
            return isBlock && IsHumanFeedback(verdict.Classification)
                ? verdict with { Status = "concerns" }
                : verdict;

        var previous = ReadPreviousFinding(prompt!);
        var followUp = previous is not null;
        var rechecked = verdict.Classification == Rechecked && previous is not null
            && previous.Evidence is not null
            && string.Equals(verdict.EvidenceChecked?.Trim(), previous.Evidence.Trim(), StringComparison.Ordinal)
            && ReviewVerdictCitationPolicy.Meaningful(verdict.Summary);
        if (IsHumanFeedback(verdict.Classification)
            || verdict.Classification is ReviewVerdictCitationPolicy.BlockWithoutCitation
                or ReviewDiffEvidencePolicy.ConcernClassification)
        {
            if (followUp) return PreservePreviousFinding(verdict with { Status = "concerns" }, previous!);
            return isBlock ? verdict with { Status = "concerns" } : verdict;
        }

        var independent = verdict.Classification == Independent && followUp;
        var hasEvidence = ReviewVerdictCitationPolicy.HasCitation(verdict)
            && ReviewVerdictCitationPolicy.Meaningful(verdict.Summary);
        if (isBlock)
        {
            if (!followUp && !ReviewVerdictCitationPolicy.HasCitation(verdict))
                return ReviewVerdictCitationPolicy.Normalize(verdict, semanticAspect: true);
            if (hasEvidence && (followUp ? rechecked || independent : verdict.Classification == Confirmed))
                return independent ? PreservePreviousFinding(verdict, previous!, keepDisposition: true) : verdict;
        }
        else if (string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
        {
            if (!followUp || rechecked)
                return verdict;
        }
        else if (!ReviewGradingPolicy.IsConcernToken(verdict.Status))
        {
            return verdict;
        }

        var concern = verdict with
        {
            Status = "concerns",
            Classification = Uncorroborated,
        };
        return previous is not null
            ? PreservePreviousFinding(concern, previous)
            : concern with
            {
                Summary = verdict.Summary + " The verdict did not supply a confirmed shipping consequence and delivery citation.",
            };
    }

    public static bool UsesProtocol(string? prompt)
        => prompt?.Contains(ProtocolHeading, StringComparison.Ordinal) == true;

    public static bool IsHumanFeedback(string? classification)
        => classification is Advisory or Uncorroborated;

    public static bool IsAdvisoryConcern(string aspect, string? classification)
        => IsCodeQuality(aspect)
           && (IsHumanFeedback(classification)
               || classification is ReviewVerdictCitationPolicy.BlockWithoutCitation
                   or ReviewDiffEvidencePolicy.ConcernClassification or "carried-over");

    public static bool IsCodeQuality(string aspect)
        => string.Equals(aspect, "code-quality", StringComparison.OrdinalIgnoreCase);

    public static string WithPreviousFinding(string? prompt, string attemptId, ReviewVerdictDto previous)
    {
        prompt ??= string.Empty;
        if (!IsCodeQuality(previous.Aspect))
            return prompt + $"\n\n## Previous finding to verify\n\nThis aspect raised the previous finding below in review {attemptId}. Verify that it is fixed and that the fix introduced no new issue."
                + DescribePrevious(attemptId, previous);

        if (ReadPreviousFinding(prompt) is not null) return prompt;
        return prompt + $"\n\n{PreviousContractHeading}\n\n"
            + "Recheck the specific contract and evidence below on the current delivery. State what you checked and observed. "
            + "Use quality-rechecked only when you verified that same contract. Copy Previous evidence exactly into evidence_checked and describe the current result in summary. "
            + "If it still fails, the copied citation must identify material changed by the delivery. Give the shipping consequence. "
            + "If you cannot corroborate it, use concerns with quality-uncorroborated and preserve the citation and reason. "
            + "If a different defect independently justifies a block, use quality-independent and record in the narrative whether and how you rechecked the previous contract."
            + DescribePrevious(attemptId, previous);
    }

    private static string DescribePrevious(string attemptId, ReviewVerdictDto previous)
        => $"\n{PreviousReviewLabel}{attemptId}\n- Previous status: {previous.Status}\n- Previous summary: {previous.Summary}"
            + $"{PreviousEvidenceLabel}{previous.EvidenceChecked ?? "none"}{PreviousFindingLabel}{previous.Missing ?? "none"}";

    private static PreviousFinding? ReadPreviousFinding(string prompt)
    {
        var heading = prompt.LastIndexOf("\n" + PreviousContractHeading + "\n", StringComparison.Ordinal);
        if (heading < 0) return null;

        var review = prompt.IndexOf(PreviousReviewLabel, heading, StringComparison.Ordinal);
        // A prior summary can itself include the context of an older claim.
        var evidence = prompt.LastIndexOf(PreviousEvidenceLabel, StringComparison.Ordinal);
        var finding = prompt.LastIndexOf(PreviousFindingLabel, StringComparison.Ordinal);
        if (review < 0 || evidence <= review || finding <= evidence) return null;

        return new PreviousFinding(
            ReadPreviousValue(prompt[(evidence + PreviousEvidenceLabel.Length)..finding]),
            ReadPreviousValue(prompt[(finding + PreviousFindingLabel.Length)..].TrimEnd()),
            prompt[review..].TrimEnd());
    }

    private static string? ReadPreviousValue(string value)
        => ReviewVerdictCitationPolicy.Meaningful(value)
            && !string.Equals(value.Trim(), "not recorded", StringComparison.OrdinalIgnoreCase)
                ? value
                : null;

    private static ReviewVerdictDto PreservePreviousFinding(
        ReviewVerdictDto verdict, PreviousFinding previous, bool keepDisposition = false)
    {
        var context = "\n" + Unresolved + previous.Description;
        return verdict with
        {
            Summary = verdict.Summary.EndsWith(context, StringComparison.Ordinal)
                ? verdict.Summary
                : verdict.Summary + context,
            EvidenceChecked = keepDisposition || ReviewVerdictCitationPolicy.Meaningful(verdict.EvidenceChecked)
                ? verdict.EvidenceChecked : previous.Evidence,
            Missing = keepDisposition || ReviewVerdictCitationPolicy.Meaningful(verdict.Missing)
                ? verdict.Missing : previous.Finding,
        };
    }

    private sealed record PreviousFinding(string? Evidence, string? Finding, string Description);
}
