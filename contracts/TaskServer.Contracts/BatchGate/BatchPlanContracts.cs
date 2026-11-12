namespace AgentStudio.TaskServer.Contracts.BatchGate;

public sealed record BatchPlanCard(
    string TaskKey, DateTimeOffset ReadyAtUtc, double Risk,
    IReadOnlyList<string> Paths, IReadOnlyList<string> Tags);

public sealed record BatchPlanUpcoming(
    string TaskKey, string Stage, DateTimeOffset? EtaUtc,
    double Risk, IReadOnlyList<string> Paths);

/// <param name="QueuedPartners">
/// Queued cards of the scope that a next batch can take but that are not in
/// <see cref="Waiting"/> yet, because they wait for their batch pre-check
/// (AGT-3466). They count as batch partners, not as eligible members.
/// </param>
public sealed record BatchPlanInput(
    DateTimeOffset NowUtc, IReadOnlyList<BatchPlanCard> Waiting,
    IReadOnlyList<BatchPlanUpcoming> Upcoming, int MinimumSize,
    int MaximumSize, int DeadlineMinutes, int LookAheadMinutes,
    IReadOnlyList<string> UrgentTags, string RiskPolicy, string Overlap,
    bool CloseNow = false, bool IdleGateHost = false, bool GateBacklog = false,
    int QueuedPartners = 0,
    string Calibration = "legacy",
    IReadOnlyList<BatchPlanObservation>? Observations = null);

/// <summary>One original batch candidate. Risk is a relative score, not a probability.</summary>
public sealed record BatchPlanObservation(
    string BatchId, DateTimeOffset AtUtc, int Size, double MaxRisk,
    IReadOnlyList<string> Areas, string OverlapGroup, bool Red,
    int IsolationRuns = 0, double IsolationMinutes = 0,
    string Classification = "pass");

public sealed record BatchPlanUpcomingView(
    string TaskKey, string Stage, DateTimeOffset? EtaUtc,
    double Risk, IReadOnlyList<string> OverlapsWith);

/// <param name="Considered">
/// Waiting cards of the snapshot the decision was computed from. The
/// authority compares it with its current facts before it applies a decision
/// it did not compute itself; null on decisions of an older decider.
/// </param>
public sealed record BatchPlanDecision(
    string Decision, string Reason, DateTimeOffset? CloseAtUtc,
    IReadOnlyList<string> ProposedMembers, int TargetSize,
    IReadOnlyList<BatchPlanUpcomingView> Upcoming,
    BatchAdvisorView? Advisor = null,
    IReadOnlyList<string>? Considered = null,
    IReadOnlyList<BatchPlanExcludedMember>? ExcludedMembers = null);

public sealed record BatchPlanExcludedMember(string TaskKey, string Rule);

public sealed record BatchAdvisorView(string Mode, string Verdict,
    IReadOnlyList<string> Reasons, string Model, DateTimeOffset AtUtc);

public sealed record BatchAdvisorAnswer(
    string Verdict, IReadOnlyList<string> Order,
    IReadOnlyList<string> Subset, DateTimeOffset? HoldUntil,
    IReadOnlyList<string> Reasons);
