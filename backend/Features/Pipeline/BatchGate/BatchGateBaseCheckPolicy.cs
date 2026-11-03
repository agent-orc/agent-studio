namespace AgentStudio.Pipeline;

/// <summary>What answered "is the base red?" for a red batch candidate (AGT-3433).</summary>
public static class BatchGateBaseCheckSources
{
    /// <summary>The base is the tip a batch verified and published.</summary>
    public const string PublishedBatch = "published-batch";

    /// <summary>A verified ancestor, followed only by commits that change no gated input.</summary>
    public const string VerifiedAncestor = "verified-ancestor";

    /// <summary>A verdict an earlier batch recorded on the same base.</summary>
    public const string RecordedVerdict = "recorded-verdict";

    /// <summary>The whole suite on the base; the failure named nothing repeatable.</summary>
    public const string FullSuite = "full-suite";
}

/// <summary>
/// AGT-3433: pure decisions for the base check of a red batch candidate. The
/// answer only chooses whom to blame and when to split. It never releases a
/// member: a green base sends the batch to isolation, where every subset is
/// gated again, and a red base pauses the batch.
/// </summary>
public static class BatchGateBaseCheckPolicy
{
    /// <summary>
    /// A red test command with more failures than this is a broad breakage;
    /// its filter would no longer be a narrow check, so the full gate answers.
    /// </summary>
    public const int MaxNarrowTests = 50;

    /// <summary>
    /// The part of the red candidate that can be repeated on the base, or
    /// null when the failure names nothing repeatable (full-gate fallback).
    /// </summary>
    public static GateNarrowCheck? Narrow(BuildTestGateResult candidate)
    {
        if (candidate.Verdict != BuildTestGateVerdict.Fail
            || candidate.FailureKind != BuildTestGateFailureKind.Code)
            return null;
        if (candidate.GuardViolation)
            return new GateNarrowCheck(GateNarrowCheckKinds.GuardStep, []);
        if (candidate.FailedStepKind == VerifyCommandKind.Build)
            return new GateNarrowCheck(GateNarrowCheckKinds.Build, []);
        if (candidate.FailedStepKind != VerifyCommandKind.Test) return null;
        // The targeted flaky re-run is a later process; the step that turned
        // the gate red is the last failed verification process.
        var failed = candidate.Processes.LastOrDefault(process =>
            process.Phase == "verification"
            && (process.ExitCode != 0 || process.TimedOut || process.LaunchError is not null));
        if (failed is null || !GateFlakyRerunPolicy.IsTargetableDotNetTest(failed.Command)) return null;
        var tests = GateFlakyRerunPolicy.ParseFailedTests(
            failed.StandardOutput + "\n" + failed.StandardError);
        if (tests.Count == 0 || tests.Count > MaxNarrowTests
            || !tests.All(GateFlakyRerunPolicy.IsFilterable))
            return null;
        return new GateNarrowCheck(GateNarrowCheckKinds.FailedTests, tests, failed.Command);
    }

    /// <summary>
    /// A passing verdict on <paramref name="verifiedSha"/> holds for the base
    /// only when that SHA is an ancestor of the base and every path changed
    /// since is outside the gated inputs (the documentation-only rule the
    /// light gate already uses). An unknown diff fails closed.
    /// </summary>
    public static bool CarriesVerdict(bool verifiedIsAncestor, IReadOnlyList<string>? changedSinceVerified)
        => verifiedIsAncestor && DocsOnlyDeliveryPolicy.IsDocsOnly(changedSinceVerified);
}
