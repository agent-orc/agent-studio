using System.Text.RegularExpressions;

namespace AgentStudio.Pipeline;

/// <summary>
/// The full-suite runs of one gate-economics window by origin (Dossier
/// AGT-3400 S2, deliverable 5). <see cref="BatchCandidates"/> are batch runs of
/// a closed manifest, infrastructure retries included; <see cref="IsolationProbes"/>
/// are runs of a diagnostic manifest split from a red batch, survivor reruns
/// included; <see cref="PerCardFallback"/> are per-task fallback gates;
/// <see cref="IntegrationGate"/> are executed integration-lane receipts at test
/// level full; <see cref="BaseChecks"/> are red-base verdicts on the batch base.
/// <see cref="ReviewFullSelections"/> counts Remote Review attempts whose test
/// commands ran without a class selection, and is null when no attempt in the
/// window carries command evidence; <see cref="ReviewAttemptsUnknown"/> counts
/// attempts that could not be classified. <see cref="Promotion"/> is null: the
/// release promotion runs on the runner host and leaves no evidence here.
/// </summary>
public sealed record FullSuiteAttribution(
    int BatchCandidates, int IsolationProbes, int PerCardFallback, int IntegrationGate,
    int BaseChecks, int? ReviewFullSelections, int ReviewAttemptsUnknown, int? Promotion = null)
{
    /// <summary>Every attributed run the Studio host can see.</summary>
    public int Known => BatchCandidates + IsolationProbes + PerCardFallback + IntegrationGate + BaseChecks
        + (ReviewFullSelections ?? 0);
}

/// <summary>Pure origin of one batch gate execution fact.</summary>
public static class FullSuiteOrigins
{
    public const string BatchCandidate = "batch-candidate";
    public const string IsolationProbe = "isolation-probe";
    public const string PerCardFallback = "per-card-fallback";
    public const string BaseCheck = "base-check";

    /// <summary>
    /// A fact without a batch is a per-task fallback gate; a batch fact without
    /// a run is the base verdict; a run of a manifest with a parent batch is an
    /// isolation probe; any other run gates a closed candidate.
    /// </summary>
    public static string Of(BatchGateExecutionFact fact, bool hasParentBatch)
        => fact.BatchId is null ? PerCardFallback
            : fact.BatchRunId is null ? BaseCheck
            : hasParentBatch ? IsolationProbe
            : BatchCandidate;

    /// <summary>
    /// True when a review attempt's executed candidate test commands ran
    /// without a test-class selection: at least one <c>dotnet test</c> command
    /// and none narrowed by a <c>FullyQualifiedName</c>, <c>ClassName</c> or
    /// <c>Name</c> filter. An attempt without a .NET test command ran no suite.
    /// </summary>
    public static bool IsFullSelection(IEnumerable<string> commandLines)
    {
        var tests = commandLines.Where(line => DotnetTest.IsMatch(line)).ToList();
        return tests.Count > 0 && tests.All(line => !Narrowed.IsMatch(line));
    }

    private static readonly Regex DotnetTest = new(@"\bdotnet(?:\.exe)?\s+test\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    // An exclusion (Name!=...) still runs the rest of the suite; ~ and = select.
    private static readonly Regex Narrowed = new(@"\b(?:FullyQualifiedName|ClassName|Name)\s*[~=]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
}
