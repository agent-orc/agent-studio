namespace AgentStudio.Runner;

/// <summary>Projects the local journal onto the shared recurrence policy.</summary>
public static class RepeatedAspectBlockPolicy
{
    public const string FailureKind = RepeatedAspectDecisionPolicy.FailureKind;

    public static RepeatedAspectBlockDiagnosis? Diagnose(
        AspectRunReport report, IEnumerable<ReviewDecisionRecord> records, string jobId,
        int attemptEpoch, int maximumRounds)
        => RepeatedAspectDecisionPolicy.Diagnose(
            report.Verdicts.Select(verdict => new AgentStudio.TaskServer.Contracts.ReviewFollowUpFinding(
                verdict.Aspect, verdict.Status.ToString(), verdict.Summary)).ToArray(),
            records.Select(record => new RepeatedAspectRoundFact(record.JobId, record.Kind.ToString(),
                record.FailureKind, record.FailureFingerprint, record.FailureFingerprints)),
            jobId, attemptEpoch, maximumRounds);

    internal static string Fingerprint(string value) => RepeatedAspectDecisionPolicy.Fingerprint(value);
}
