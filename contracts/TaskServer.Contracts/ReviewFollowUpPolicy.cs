using System.Text.RegularExpressions;

namespace AgentStudio.TaskServer.Contracts;

/// <summary>The bounded action selected after a complete set of review verdicts.</summary>
public enum ReviewFollowUpAction
{
    Accept,
    ReviewFindingRound,
    ReviewConcernRound,
    RetryAspect,
}

/// <summary>Plane-neutral input used by both local and remote review paths.</summary>
public sealed record ReviewFollowUpFinding(
    string Aspect,
    string? Status,
    string? Summary,
    string? EvidenceChecked = null,
    string? Finding = null,
    string? Classification = null,
    bool InfrastructureFailure = false,
    string? ReportBody = null);

public sealed record ReviewFollowUpDecision(
    ReviewGrade Grade,
    ReviewFollowUpAction Action,
    IReadOnlyList<ReviewFollowUpFinding> Findings,
    string Reason)
{
    public bool StartsCodingRound =>
        Action is ReviewFollowUpAction.ReviewFindingRound or ReviewFollowUpAction.ReviewConcernRound;
}

/// <summary>Facts used to decide whether a prior aspect verdict may be carried over.</summary>
public sealed record ScopedReviewAspect(
    string Aspect,
    string Status,
    IReadOnlyList<string> EvidenceChecked,
    bool RaisedFinding);

public sealed record ScopedReviewFacts(
    bool Enabled,
    bool ReviewedTreeIdentityMatches,
    int DeltaFileCount,
    int MaximumDeltaFiles,
    IReadOnlyList<string> ChangedFiles,
    IReadOnlyList<string> RemovedFiles,
    IReadOnlyList<ScopedReviewAspect> PreviousAspects);

public sealed record ScopedReviewAspectDecision(string Aspect, bool Run, string Reason);

/// <summary>Pure scoped re-review planner. Deterministic gates are outside this list and always run.</summary>
public static class ScopedReviewPolicy
{
    public static IReadOnlyList<ScopedReviewAspectDecision> Plan(ScopedReviewFacts facts)
    {
        var full = !facts.Enabled
                   || !facts.ReviewedTreeIdentityMatches
                   || facts.DeltaFileCount < 0
                   || facts.DeltaFileCount > Math.Max(0, facts.MaximumDeltaFiles);
        return facts.PreviousAspects.Select(aspect =>
        {
            if (full) return new ScopedReviewAspectDecision(aspect.Aspect, true, "full review fallback");
            if (aspect.RaisedFinding) return new ScopedReviewAspectDecision(aspect.Aspect, true, "raised the finding");
            if (TouchesEvidence(facts.ChangedFiles, aspect.EvidenceChecked))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "delta touches cited evidence");
            if (string.Equals(aspect.Aspect, "documentation-impact", StringComparison.OrdinalIgnoreCase)
                && facts.ChangedFiles.Any(IsPublicContractOrSchema))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "public contract or schema changed");
            if (string.Equals(aspect.Aspect, "tests-and-evidence", StringComparison.OrdinalIgnoreCase)
                && facts.RemovedFiles.Any(IsTestFile))
                return new ScopedReviewAspectDecision(aspect.Aspect, true, "test file removed");
            return new ScopedReviewAspectDecision(aspect.Aspect, false, "carried over from the prior review");
        }).ToArray();
    }

    private static bool TouchesEvidence(IEnumerable<string> changed, IEnumerable<string> evidence)
        => changed.Any(file => evidence.Any(cited =>
            string.Equals(Normalize(file), Normalize(cited), StringComparison.OrdinalIgnoreCase)));

    private static bool IsPublicContractOrSchema(string file)
    {
        var path = Normalize(file);
        return path.StartsWith("contracts/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/contracts/", StringComparison.OrdinalIgnoreCase)
               || path.StartsWith("schemas/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/schemas/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".schema.json", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith("openapi.json", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsTestFile(string file)
    {
        var path = Normalize(file);
        return path.StartsWith("tests/", StringComparison.OrdinalIgnoreCase)
               || path.Contains(".tests/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/test/", StringComparison.OrdinalIgnoreCase)
               || path.Contains("/tests/", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".spec.ts", StringComparison.OrdinalIgnoreCase)
               || path.EndsWith(".test.ts", StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string value) => value.Replace('\\', '/').TrimStart('.', '/');
}
