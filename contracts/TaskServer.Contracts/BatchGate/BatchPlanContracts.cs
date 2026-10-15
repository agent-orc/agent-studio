namespace AgentStudio.TaskServer.Contracts.BatchGate;

public sealed record BatchPlanCard(
    string TaskKey, DateTimeOffset ReadyAtUtc, double Risk,
    IReadOnlyList<string> Paths, IReadOnlyList<string> Tags);

public sealed record BatchPlanUpcoming(
    string TaskKey, string Stage, DateTimeOffset? EtaUtc,
    double Risk, IReadOnlyList<string> Paths);

public sealed record BatchPlanInput(
    DateTimeOffset NowUtc, IReadOnlyList<BatchPlanCard> Waiting,
    IReadOnlyList<BatchPlanUpcoming> Upcoming, int MinimumSize,
    int MaximumSize, int DeadlineMinutes, int LookAheadMinutes,
    IReadOnlyList<string> UrgentTags, string RiskPolicy, string Overlap,
    bool CloseNow = false, bool IdleGateHost = false);

public sealed record BatchPlanUpcomingView(
    string TaskKey, string Stage, DateTimeOffset? EtaUtc,
    double Risk, IReadOnlyList<string> OverlapsWith);

public sealed record BatchPlanDecision(
    string Decision, string Reason, DateTimeOffset? CloseAtUtc,
    IReadOnlyList<string> ProposedMembers, int TargetSize,
    IReadOnlyList<BatchPlanUpcomingView> Upcoming,
    BatchAdvisorView? Advisor = null);

public sealed record BatchAdvisorView(string Mode, string Verdict,
    IReadOnlyList<string> Reasons, string Model, DateTimeOffset AtUtc);

public sealed record BatchAdvisorAnswer(
    string Verdict, IReadOnlyList<string> Order,
    IReadOnlyList<string> Subset, DateTimeOffset? HoldUntil,
    IReadOnlyList<string> Reasons);
