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

    public static ReviewVerdictDto Normalize(ReviewVerdictDto verdict, string? prompt)
    {
        if (!IsCodeQuality(verdict.Aspect) || verdict.CarriedOverFrom is not null
            || verdict.Classification is "CommandFailed" or ReviewCommandVerdictPolicy.UnparseableClassification)
            return verdict;

        var isBlock = ReviewGradingPolicy.IsBlockingToken(verdict.Status);
        if (IsHumanFeedback(verdict.Classification))
            return isBlock ? verdict with { Status = "concerns" } : verdict;

        // Frozen plans and existing verdicts keep their original marker contract.
        if (!UsesProtocol(prompt)
            || verdict.Classification is ReviewVerdictCitationPolicy.BlockWithoutCitation
                or ReviewDiffEvidencePolicy.ConcernClassification)
            return verdict;

        var followUp = prompt!.Contains(PreviousContractHeading, StringComparison.Ordinal);
        var rechecked = verdict.Classification == Rechecked;
        var independent = verdict.Classification == Independent && followUp;
        var hasEvidence = ReviewVerdictCitationPolicy.HasCitation(verdict)
            && ReviewVerdictCitationPolicy.Meaningful(verdict.Summary);
        if (isBlock)
        {
            if (!ReviewVerdictCitationPolicy.HasCitation(verdict))
                return ReviewVerdictCitationPolicy.Normalize(verdict, semanticAspect: true);
            if (hasEvidence && (followUp ? rechecked || independent : verdict.Classification == Confirmed))
                return verdict;
        }
        else if (string.Equals(verdict.Status, "pass", StringComparison.OrdinalIgnoreCase))
        {
            if (!followUp || (rechecked && ReviewVerdictCitationPolicy.Meaningful(verdict.EvidenceChecked)
                                          && ReviewVerdictCitationPolicy.Meaningful(verdict.Summary)))
                return verdict;
        }
        else if (!ReviewGradingPolicy.IsConcernToken(verdict.Status))
        {
            return verdict;
        }

        return verdict with
        {
            Status = "concerns",
            Classification = Uncorroborated,
            Summary = verdict.Summary + (followUp
                ? " The previous contract has no conclusive recheck recorded."
                : " The verdict did not supply a confirmed shipping consequence and delivery citation."),
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

        if (prompt.Contains(PreviousContractHeading, StringComparison.Ordinal)) return prompt;
        return prompt + $"\n\n{PreviousContractHeading}\n\n"
            + "Recheck the specific contract and evidence below on the current delivery. State what you checked and observed. "
            + "Use quality-rechecked only when you verified that same contract. If it still fails, cite the current delivery and give the shipping consequence. "
            + "If you cannot corroborate it, use concerns with quality-uncorroborated and preserve the citation and reason. "
            + "If a different defect independently justifies a block, use quality-independent and record in the narrative whether and how you rechecked the previous contract."
            + DescribePrevious(attemptId, previous);
    }

    private static string DescribePrevious(string attemptId, ReviewVerdictDto previous)
        => $"\n\n- Previous review: {attemptId}\n- Previous status: {previous.Status}\n- Previous summary: {previous.Summary}\n"
            + $"- Previous evidence: {previous.EvidenceChecked ?? "not recorded"}\n- Previous finding: {previous.Missing ?? "not recorded"}";
}
