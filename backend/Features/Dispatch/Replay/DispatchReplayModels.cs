using AgentStudio.OrchestrationDecisions;

namespace AgentStudio.Dispatch;

public sealed record DispatchReplayOptions(int Slots = 4, string Durations = "unit",
    double IntegrationLag = 0, DispatchOverlapWindow Check = DispatchOverlapWindow.InFlight);

public sealed record DispatchReplayCard(DispatchReadyCard Ready, IReadOnlyList<string> RealFiles);

public sealed record DispatchReplayWave(string Project, string Id, DateTimeOffset Start,
    IReadOnlyList<DispatchReplayCard> Cards, IReadOnlyList<string> HubFiles,
    DispatchLedgerSummary Ledger, string IndexRevision)
{
    public IReadOnlyList<string> Assumptions { get; init; } = [];
}

/// <summary>Exported historical card facts from the task API, captured before the wave starts.</summary>
public sealed record DispatchReplayRecording(string Project, IReadOnlyList<DispatchReplayRecordedCard> Cards)
{
    public IReadOnlyList<string> HubFiles { get; init; } = TouchForecast.DefaultHubFiles;
}
public sealed record DispatchReplayRecordedCard(string WaveId, DispatchReadyCard Ready,
    string ForecastText, DateTimeOffset CapturedAt, string BaseSha, string ResultSha);

public sealed record DispatchReplayRun(string Card, double Start, double CodingEnd, double IntegratedAt);
public sealed record DispatchReplayResult(string Strategy, int Runs, int ExposedPairs, int CardsNeedingSync,
    double RoundsPerCard, double ReworkShare, double ForecastF1, double SlotUse, double Parallelism,
    double Makespan, double MedianLeadTime, IReadOnlyList<DispatchReplayRun> Schedule,
    IReadOnlyList<DispatchPlan> Plans)
{
    public double ConflictsPer100 { get; init; }
    public double? ValuePlain { get; set; }
    public double? Value { get; set; }
    public IReadOnlyDictionary<string, double> Scores { get; set; } = new Dictionary<string, double>();
}

public sealed record DispatchReplayReport(string Project, string WaveId, string IndexRevision,
    DispatchReplayOptions Options, DispatchReplayResult ConflictFreeBound, IReadOnlyList<DispatchReplayResult> Strategies)
{
    public DateTimeOffset WaveStart { get; init; }
    public string Schema { get; init; } = "dispatch-replay.v1";
    public string Mode { get; init; } = "replay";
    public double Gate { get; init; } = DispatchWeightRevision.Default.Gate;
    public IReadOnlyDictionary<string, int> Weights { get; init; } = DispatchWeightRevision.Default.Weights;
    public string ValueStatus => Strategies.First().Runs >= 40 ? "scored" : "too few cards";
    public IReadOnlyList<string> Limitations { get; init; } =
    [
        "Shared real files are an upper bound for conflicts, including hub files.",
        "Tokens are estimated by rounds per card; own cost and explanation are unmeasured and excluded from the weighted denominator.",
        "Packer runs the DS-3 dispatch policy only; no DS-9 recut is simulated.",
        "The conflict-free bound is a greedy real-file schedule, not an optimal scheduler.",
        "Replay time is in card-duration units, not observed hours. The whole cohort is Ready at time zero; lane timestamps determine order. Lead time uses the median simulated integration time.",
    ];
}
