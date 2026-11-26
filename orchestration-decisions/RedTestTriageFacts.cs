namespace AgentStudio.OrchestrationDecisions;

public enum RedTestCategory { Base, Routine }
public enum RedTestResult { Unknown, Pass, Fail }
public enum RedTestClassification { Flaky, Environment, Regression, WrongAttribution, Ambiguous, Inconclusive }
public enum RedTestQuarantineKind { Held, ReportOnly }
public enum RedTestAction
{
    LineStop, ResolveLineStop, StartHeld, StartReportOnly, CountFlaky, MarkInconclusive, Renew, Lift,
    AskOwner, ProposeRevert, FixForward, WithdrawAttribution, RequestClassification,
}
/// <summary>Abstract economy rungs; the host maps them to configured models within its economy dial.</summary>
public enum RedTestModelRung { Medium, High, HighPlusOne }
public enum RedTestOwnerReason { LineStopAge, RenewalLimit, RepairNotInProgress, CapExceeded, Relapse, T2Request }
public enum RedTestOwnerOption
{
    RevertAttributedMerge, KeepFixingForward, SwitchProjectToStrict, Extend24Hours,
    ReportOnlyWithCard, StopLine, RaiseCapOnce, ReholdSameCard, ReholdNewCardHigherRung,
    ReportOnly, GrantWithExpiry, Refuse,
}

/// <summary>All bounds are starting values from RT-D4, RT-D5, RT-D7 and RT-D9 B.</summary>
public static class RedTestTriageBounds
{
    public static readonly TimeSpan HeldDuration = TimeSpan.FromHours(24);
    public static readonly TimeSpan RenewalDuration = TimeSpan.FromHours(24);
    public static readonly TimeSpan HeldMaximumAge = HeldDuration + RenewalDuration;
    public static readonly TimeSpan ReportOnlyDuration = TimeSpan.FromDays(7);
    public static readonly TimeSpan FlakyWindow = TimeSpan.FromDays(14);
    public static readonly TimeSpan RelapseWindow = TimeSpan.FromDays(7);
    public static readonly TimeSpan ReevaluateAge = TimeSpan.FromHours(2);
    public static readonly TimeSpan LineStopOwnerAskAge = TimeSpan.FromMinutes(60);
    public const int MaximumRenewals = 1;
    public const int MaximumHeld = 3;
    public const int MaximumTotal = 8;
    public const int FlakyRunThreshold = 3;
    public const int MaximumCoveringFolders = 12;
}

public sealed record RedTestQuarantine(string TestId, string IncidentFingerprint, RedTestQuarantineKind Kind);
public sealed record RedTestActiveQuarantine(RedTestQuarantineKind Kind, TimeSpan Age, int Renewals);
public sealed record RedTestLiftRun(RedTestResult Result, bool StandardEnvelope, bool ContainsRepairIntegration);
public sealed record RedTestOwnerQuestion(RedTestOwnerReason Reason, IReadOnlyList<RedTestOwnerOption> Options,
    RedTestOwnerOption Recommended, string RecommendationReason);
public sealed record RedTestDecision(RedTestAction Action, string Reason, TimeSpan? Duration = null,
    RedTestModelRung? ModelRung = null, RedTestOwnerQuestion? Question = null);
public sealed record RedTestTriageDecision(RedTestClassification Classification, string? AttributedMerge,
    IReadOnlyList<RedTestDecision> Decisions);

/// <summary>
/// One frozen incident. Results refer to the same test and SHA unless explicitly named.
/// ProjectQuarantines includes all active kinds, including the curated file. DistinctFlakyRuns14Days
/// includes the current run once. LiftRuns are chronological covering runs, including failures.
/// Ages are supplied by the host; this library reads no clock. A new fingerprint is a new incident.
/// </summary>
public sealed record RedTestTriageFacts(string TestId, string SuiteId, RedTestCategory Category)
{
    public string IncidentFingerprint { get; init; } = "";
    public RedTestResult FirstResult { get; init; } = RedTestResult.Fail;
    public RedTestResult SameShaRepeat { get; init; }
    public RedTestResult EnvelopeCanary { get; init; }
    public bool? FingerprintMatchesReference { get; init; }
    public RedTestResult ReferenceEnvelopeRepeat { get; init; }
    public IReadOnlyList<RedTestResult> LastGreenControlResults { get; init; } = [];
    public IReadOnlyList<RedTestBisectPoint> BisectPoints { get; init; } = [];
    public IReadOnlyList<RedTestQuarantine> ProjectQuarantines { get; init; } = [];
    /// <summary>The quarantine for this fingerprint only; older fingerprints stay separate.</summary>
    public RedTestActiveQuarantine? ActiveQuarantine { get; init; }
    public int DistinctFlakyRuns14Days { get; init; }
    public TimeSpan? SinceLastLift { get; init; }
    public bool EarlierSuspectRevertIntegrated { get; init; }
    public bool LaterControlContradictsAttribution { get; init; }
    public string? RepairCardLane { get; init; }
    public TimeSpan IncidentAge { get; init; }
    public TimeSpan LineStopAge { get; init; }
    public bool LineStopActive { get; init; }
    public bool BuildOrPrepareFailure { get; init; }
    public int EnvironmentOccurrences { get; init; } = 1;
    public int RepairAttempt { get; init; } = 1;
    public bool AmbiguousClassificationAttempted { get; init; }
    public RedTestClassification? ModelClassification { get; init; }
    public bool RevertAppliesCleanly { get; init; }
    public bool LaterMergeCoupled { get; init; }
    public bool AreaHasNewFailure { get; init; }
    public bool T2Requested { get; init; }
    public bool BaseTestMovingToRoutine { get; init; }
    public IReadOnlyList<RedTestLiftRun> LiftRuns { get; init; } = [];
}
