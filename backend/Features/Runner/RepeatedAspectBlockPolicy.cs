namespace AgentStudio.Runner;

public static class RepeatedAspectBlockPolicy
{
    public const string FailureKind = RepeatedAspectBlockRules.FailureKind;

    public static RepeatedAspectBlockDiagnosis? Diagnose(AspectRunReport report,
        IEnumerable<ReviewDecisionRecord> records, string jobId, int attemptEpoch, int maximumRounds)
        => RepeatedAspectBlockRules.Diagnose(
            report.Verdicts.Select(v => new AspectBlockFact(v.Aspect, v.Status.ToString(), v.Summary)).ToArray(),
            records.Select(r => new AspectBlockHistoryFact(r.JobId, r.Kind.ToString(), r.FailureKind,
                r.FailureFingerprint, r.FailureFingerprints)), jobId, attemptEpoch, maximumRounds);

    internal static string Fingerprint(string value) => RepeatedAspectBlockRules.Fingerprint(value);
}
