using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// Pure completion policy for successful runs whose deliverable is not a new
/// git commit. Documentation uploads, results artefacts, and verification-only
/// close-outs are legitimate terminal work. Historical failure prose in the
/// accumulated log must not turn those runs into an endless reissue cycle.
/// </summary>
public static class CompletionEvidencePolicy
{
    private static readonly Regex ApiDeliveryRegex = new(
        @"(?ix)
        \b(?:uploaded|published|created|wrote|stored)\b.{0,100}\b(?:through|via|with|using)\b.{0,30}\b(?:task\s+|application\s+)?api\b
        |
        \b(?:task\s+|application\s+)?api\b.{0,100}\b(?:uploaded|published|created|wrote|stored)\b",
        RegexOptions.Compiled);

    private static readonly Regex VerificationRegex = new(
        @"(?ix)\b(?:
            re[-\s]?verif(?:ied|ication) |
            verif(?:ied|ication)\s+(?:confirmed|passed|succeeded|complete|completed|green) |
            independent(?:ly)?\s+(?:verification\s+)?(?:verified|reproduced|confirmed) |
            \d+\s*/\s*\d+\s+(?:tests?\s+)?passed |
            (?:build|tests?|suite)\s+(?:is\s+|are\s+|now\s+)?(?:green|passed|passes|succeeded|succeeds) |
            0\s+(?:build\s+)?errors?
        )\b",
        RegexOptions.Compiled);

    public enum EvidenceKind
    {
        ApiDelivery,
        ResultsArtifact,
        DocumentedVerification,
    }

    public readonly record struct Inputs(
        bool HasTaskDoneSentinel,
        int? ExitCode,
        bool RunStatusCompleted,
        string? StatusResultToken,
        bool HasOpenItems,
        bool HasBuildFailureInStatus,
        bool HasApiDelivery,
        bool HasResultsArtifacts,
        bool HasDocumentedVerification);

    public sealed record Decision
    {
        public bool AcceptAsCompleted { get; init; }
        public IReadOnlyList<EvidenceKind> Evidence { get; init; } = [];
        public string Reason { get; init; } = "No qualifying non-commit completion evidence.";
    }

    public static Decision Decide(Inputs inputs)
    {
        if (!inputs.HasTaskDoneSentinel || inputs.ExitCode != 0 && !inputs.RunStatusCompleted)
            return new Decision { Reason = "Run did not end with TASK_DONE and a successful process terminal." };

        if (!IsSuccessResultToken(inputs.StatusResultToken) || inputs.HasOpenItems)
            return new Decision { Reason = "Status is not a clean successful close-out." };

        var evidence = new List<EvidenceKind>(3);
        if (inputs.HasApiDelivery) evidence.Add(EvidenceKind.ApiDelivery);
        if (inputs.HasResultsArtifacts) evidence.Add(EvidenceKind.ResultsArtifact);
        if (inputs.HasDocumentedVerification) evidence.Add(EvidenceKind.DocumentedVerification);
        if (evidence.Count == 0)
            return new Decision();

        // A current self-reported build failure still wins unless this same
        // close-out documents the successful re-verification that supersedes it.
        if (inputs.HasBuildFailureInStatus && !inputs.HasDocumentedVerification)
            return new Decision { Reason = "Status still reports a build/test failure without a successful re-verification." };

        return new Decision
        {
            AcceptAsCompleted = true,
            Evidence = evidence,
            Reason = $"Successful non-commit run completed with {string.Join(", ", evidence)} evidence.",
        };
    }

    public static bool IsSuccessResultToken(string? value)
        => value?.Trim().ToLowerInvariant() is "success" or "succeeded" or "done" or "complete" or "completed" or "pass" or "passed";

    public static bool DetectApiDelivery(string? text)
        => !string.IsNullOrWhiteSpace(text) && ApiDeliveryRegex.IsMatch(text);

    public static bool DetectDocumentedVerification(string? text)
        => !string.IsNullOrWhiteSpace(text) && VerificationRegex.IsMatch(text);
}
