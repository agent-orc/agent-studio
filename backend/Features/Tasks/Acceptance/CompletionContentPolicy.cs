namespace AgentStudio.Tasks;

/// <summary>Brief and content evidence for the delivery being accepted.</summary>
public sealed record CompletionContentFacts(
    string? Mode,
    string CurrentBriefVersion,
    string? DeliveryBriefVersion,
    string? ReviewBriefVersion,
    string RequiredAspect,
    string? ContentStatus,
    IReadOnlyList<string> RanAspects,
    IReadOnlyList<string> SkippedAspects,
    bool OperatorOverride = false,
    string? OverrideReason = null,
    string? ContentSummary = null,
    string? EvidenceChecked = null,
    string? Missing = null);

public sealed record CompletionContentDecision(bool Accepted, bool Overridden, string Message);

/// <summary>The same pure admission rule is used by manual and automatic moves.</summary>
public static class CompletionContentPolicy
{
    /// <summary>Legacy accepted cards without a bound current-brief pass need a human re-check.</summary>
    public static bool NeedsHistoricalReview(TaskCompletionClaim? claim, string? mode = null)
        => claim is null
           || !string.Equals(claim.ContentStatus, "pass", StringComparison.OrdinalIgnoreCase)
           || claim.ContentAspect is not ("requirement-fit" or "concept-fit")
           || mode is not null && !string.Equals(claim.ContentAspect,
               TaskModes.IsConcept(mode) ? "concept-fit" : "requirement-fit",
               StringComparison.OrdinalIgnoreCase)
           || string.IsNullOrWhiteSpace(claim.CurrentBriefVersion)
           || !string.Equals(claim.CurrentBriefVersion, claim.DeliveryBriefVersion,
               StringComparison.OrdinalIgnoreCase)
           || !string.Equals(claim.CurrentBriefVersion, claim.ReviewBriefVersion,
               StringComparison.OrdinalIgnoreCase);

    public static CompletionContentDecision Decide(CompletionContentFacts facts)
    {
        var gaps = new List<string>();
        var expectedAspect = TaskModes.IsConcept(facts.Mode) ? "concept-fit" : "requirement-fit";
        if (!string.Equals(facts.RequiredAspect, expectedAspect, StringComparison.OrdinalIgnoreCase))
            gaps.Add($"content aspect must be {expectedAspect}, not {facts.RequiredAspect}");
        if (string.IsNullOrWhiteSpace(facts.DeliveryBriefVersion))
            gaps.Add("delivery brief version is missing");
        else if (!Same(facts.DeliveryBriefVersion, facts.CurrentBriefVersion))
            gaps.Add($"delivered against brief {Label(facts.DeliveryBriefVersion)}, current {Label(facts.CurrentBriefVersion)}");

        if (string.IsNullOrWhiteSpace(facts.ContentStatus))
            gaps.Add($"no content verdict ({facts.RequiredAspect}); only {Aspects(facts.RanAspects)} ran");
        else if (!string.Equals(facts.ContentStatus, "pass", StringComparison.OrdinalIgnoreCase))
            gaps.Add($"{facts.RequiredAspect} verdict was {facts.ContentStatus}");

        if (string.IsNullOrWhiteSpace(facts.ReviewBriefVersion))
            gaps.Add("content verdict has no brief version");
        else if (!Same(facts.ReviewBriefVersion, facts.CurrentBriefVersion))
            gaps.Add($"content verdict was for brief {Label(facts.ReviewBriefVersion)}, current {Label(facts.CurrentBriefVersion)}");

        if (gaps.Count == 0) return new(true, false, "Content review passed for the current brief.");
        var message = "Completion refused: " + string.Join("; ", gaps) + ".";
        if (!facts.OperatorOverride) return new(false, false, message);
        if (!CompletionContractPolicy.IsUsableReason(facts.OverrideReason?.Trim()))
            return new(false, false, message + " An override needs a written reason of at least 8 characters.");
        return new(true, true, message + " Accepted with written operator override.");
    }

    private static bool Same(string left, string right)
        => string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static string Label(string? version)
        => version ?? "unknown";

    private static string Aspects(IReadOnlyList<string> aspects)
        => aspects.Count == 0 ? "no aspects" : string.Join(", ", aspects);
}
