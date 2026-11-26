namespace AgentStudio.OrchestrationDecisions;

// DS-3 (AGT-3974 Dossier docs/dispatcher-personas): typed facts, strategy data
// and the dispatch plan of DispatchPlanPolicy. Hosts collect these facts and
// apply the returned plan; the policy reads no clock, file or service.

/// <summary>Which in-flight cards the overlap check compares a candidate with (DS-D10).</summary>
public enum DispatchOverlapWindow
{
    /// <summary>Every card of the project that started and has not integrated yet (DS-D10 B, the default).</summary>
    InFlight,
    /// <summary>Only cards that hold a slot (are coding), as today's pick-gate compares with ActiveRuns.</summary>
    Running,
}

/// <summary>How much a touch forecast can be trusted (DS-D4 B).</summary>
public enum DispatchForecastConfidence
{
    /// <summary>No forecast at all: the card's area is unknown.</summary>
    None,
    /// <summary>A forecast without a cited path; file-level personas fall back to module level.</summary>
    Low,
    /// <summary>A forecast built from cited paths (plus co-change expansion).</summary>
    High,
}

/// <summary>Model-free touch forecast of one card: files, modules and confidence (DS-1 produces it).</summary>
public sealed record DispatchForecast(
    IReadOnlyList<string> Files,
    IReadOnlyList<string> Modules,
    DispatchForecastConfidence Confidence)
{
    public static readonly DispatchForecast Unknown = new([], [], DispatchForecastConfidence.None);
}

/// <summary>Source wave of an automated card (DS-D8 B): wave id, source kind, position and size prior.</summary>
public sealed record DispatchSourceWave(
    string WaveId,
    string SourceKind,
    int Position,
    int SizePriorFiles);

/// <summary>A Ready card of the project with the facts every strategy reads.</summary>
/// <param name="Key">Card key; identity and the lane-entry tiebreaker (key descending).</param>
/// <param name="Order">Lane order field; anything other than 999 is an owner pin.</param>
/// <param name="EnteredLaneAt">When the card entered Ready (lane-entry: newest arrival first).</param>
/// <param name="DependsOn">Keys of the card's existing dependsOn edges.</param>
/// <param name="DependenciesSatisfied">False while a dependsOn target is not delivered (WaitsOn).</param>
/// <param name="DeclaredScope">Today's pick-gate scope (TaskParallelism.PredictedScope from prompt paths).</param>
/// <param name="Exclusive">The card runs alone (TaskParallelism.Exclusive, any origin).</param>
/// <param name="ReadOnly">A planning or research card that writes no files.</param>
/// <param name="PriorityClass">Priority class set by the owner or the chief of staff; lower starts first.</param>
/// <param name="Urgent">Urgent cards follow the pins of their priority class.</param>
public sealed record DispatchReadyCard(
    string Key,
    int Order,
    DateTimeOffset EnteredLaneAt,
    IReadOnlyList<string> DependsOn,
    bool DependenciesSatisfied,
    DispatchForecast Forecast,
    IReadOnlyList<string> DeclaredScope,
    bool Exclusive = false,
    bool ReadOnly = false,
    int PriorityClass = 0,
    bool Urgent = false,
    DispatchSourceWave? Source = null)
{
    /// <summary>The lane order sentinel meaning "not explicitly placed" (LaneSortStrategies.UnpinnedOrder).</summary>
    public const int UnpinnedOrder = 999;

    public bool Pinned => Order != UnpinnedOrder;
}

/// <summary>A started card that has not integrated yet; <paramref name="HoldsSlot"/> is true while it is coding.</summary>
public sealed record DispatchInFlightCard(
    string Key,
    bool HoldsSlot,
    DispatchForecast Forecast,
    IReadOnlyList<string> DeclaredScope,
    bool Exclusive = false,
    bool ReadOnly = false);

/// <summary>A module that took part in a conflict, with the time of its latest conflict.</summary>
public sealed record DispatchHotModule(string Module, DateTimeOffset LastConflictAtUtc);

/// <summary>Two cards that collided; they never run together again in their recovery rounds.</summary>
public sealed record DispatchCollidedPair(string First, string Second);

/// <summary>Mean partner count of the latest earlier wave of one source kind.</summary>
public sealed record DispatchSourceHistory(string SourceKind, double MeanPartnerCount);

/// <summary>Conflict ledger summary (DS-2) as the Historian reads it.</summary>
public sealed record DispatchLedgerSummary(
    IReadOnlyList<DispatchHotModule> HotModules,
    IReadOnlyList<DispatchCollidedPair> CollidedPairs,
    IReadOnlyList<DispatchSourceHistory> Sources)
{
    public static readonly DispatchLedgerSummary Empty = new([], [], []);
}

/// <summary>Everything one dispatch evaluation of one project knows.</summary>
/// <param name="FreeSlots">Free slots of the project (slot count minus slot holders); never raised by a plan.</param>
/// <param name="ActiveHolds">Holds of earlier plans that are still recorded.</param>
/// <param name="ActiveExclusiveMarks">Keys of cards the dispatcher marked exclusive earlier and that are still marked.</param>
/// <param name="DecisionRequestedHolds">Cards whose hold ran out after its renewal and now wait for an owner decision; never held again.</param>
/// <param name="Persona">Acting persona id, used for the edge origin <c>dispatch:&lt;persona&gt;</c>.</param>
public sealed record DispatchFacts(
    string Project,
    DateTimeOffset NowUtc,
    IReadOnlyList<DispatchReadyCard> Ready,
    IReadOnlyList<DispatchInFlightCard> InFlight,
    int FreeSlots,
    DispatchLedgerSummary Ledger,
    IReadOnlyList<string> HubFiles,
    DispatchOverlapWindow OverlapWindow = DispatchOverlapWindow.InFlight,
    IReadOnlyList<DispatchHold>? ActiveHolds = null,
    IReadOnlyList<string>? ActiveExclusiveMarks = null,
    IReadOnlyList<string>? DecisionRequestedHolds = null,
    string? Persona = null,
    IReadOnlyList<string>? PickupCandidates = null,
    IReadOnlyList<DispatchDependencyEdge>? ActiveDependencies = null,
    IReadOnlyList<string>? SatisfiedDependencies = null);

/// <summary>
/// A strategy as data (<c>dispatch-strategy.v1</c>): id, revision and string
/// parameters. A persona descriptor names one; <see cref="DispatchStrategies"/>
/// holds the five starter rule sets and their defaults.
/// </summary>
public sealed record DispatchStrategy(
    string Id,
    string Revision,
    IReadOnlyDictionary<string, string>? Parameters = null,
    string Schema = DispatchStrategy.SchemaV1)
{
    public const string SchemaV1 = "dispatch-strategy.v1";
}

/// <summary>State of a Ready card in the plan order.</summary>
public enum DispatchCandidateState
{
    /// <summary>May take a slot in this plan.</summary>
    Eligible,
    /// <summary>Waits for an undelivered dependsOn target, including an edge added by this plan.</summary>
    WaitsForDependency,
}

/// <summary>One Ready card at its plan position.</summary>
public sealed record DispatchOrderEntry(
    int Position,
    string CardKey,
    int PriorityClass,
    bool Pinned,
    bool Urgent,
    DispatchCandidateState State);

/// <summary>A hold: the card waits for <paramref name="WaitsFor"/> until it is released or expires.</summary>
public sealed record DispatchHold(
    string CardKey,
    string ReasonCode,
    string WaitsFor,
    DateTimeOffset PlacedAtUtc,
    DateTimeOffset ExpiresAtUtc,
    int Renewals = 0);

/// <summary>An earlier hold that ends; <paramref name="DecisionRequest"/> asks the owner after the renewal ran out.</summary>
public sealed record DispatchRelease(string CardKey, string ReasonCode, bool DecisionRequest);

/// <summary>A dependsOn edge the dispatcher adds: <paramref name="CardKey"/> waits for <paramref name="DependsOn"/>.</summary>
public sealed record DispatchDependencyEdge(string CardKey, string DependsOn, string Origin, string ReasonCode);

/// <summary>A run-alone mark (TaskParallelism.Exclusive) the dispatcher sets.</summary>
public sealed record DispatchExclusiveMark(string CardKey, string ReasonCode, string Detail);

/// <summary>A candidate that a slot decision passed over, with the rule that held it back.</summary>
/// <param name="With">The card it collided with, when the rule names one.</param>
/// <param name="Detail">The shared path, module or area of the collision, when there is one.</param>
public sealed record DispatchPassOver(string CardKey, string ReasonCode, string? With, string? Detail = null);

public enum DispatchSlotAction
{
    Admit,
    Serialize,
}

/// <summary>The decision for one free slot.</summary>
/// <param name="RunsAlone">The admitted card is exclusive and runs alone (ParallelSlotPolicy RunExclusive).</param>
public sealed record DispatchSlotDecision(
    int Slot,
    DispatchSlotAction Action,
    string? CardKey,
    string ReasonCode,
    bool RunsAlone,
    IReadOnlyList<DispatchPassOver> PassedOver);

/// <summary>The plan of one strategy for one evaluation. The live persona's plan feeds the pick-gate; shadow plans are recorded.</summary>
public sealed record DispatchPlan(
    string Project,
    string StrategyId,
    string StrategyRevision,
    string Persona,
    IReadOnlyList<DispatchOrderEntry> Order,
    IReadOnlyList<DispatchHold> Holds,
    IReadOnlyList<DispatchRelease> Releases,
    IReadOnlyList<DispatchDependencyEdge> DependenciesToAdd,
    IReadOnlyList<DispatchExclusiveMark> ExclusiveMarks,
    IReadOnlyList<DispatchSlotDecision> Slots)
{
    public IReadOnlyList<string> Admitted => Slots
        .Where(slot => slot.Action == DispatchSlotAction.Admit && slot.CardKey is not null)
        .Select(slot => slot.CardKey!).ToArray();
}

/// <summary>Reason codes of slot decisions, pass-overs, holds, releases, edges and marks.</summary>
public static class DispatchReasonCodes
{
    // Admissions.
    public const string FirstSlot = "first-slot";
    public const string ReadOnly = "read-only";
    public const string RunAlone = "run-alone";
    public const string ParallelOptimistic = "parallel-optimistic";
    public const string ParallelDisjoint = "parallel-disjoint";

    // Serialize and pass-over.
    public const string NoCandidate = "no-candidate";
    public const string NoAdmissibleCandidate = "no-admissible-candidate";
    public const string ExclusiveRunning = "exclusive-running";
    public const string ExclusiveWaitsForDrain = "exclusive-waits-for-drain";
    public const string ScopeOverlap = "scope-overlap";
    public const string AreaBusy = "area-busy";
    public const string UnknownAreaBusy = "unknown-area-busy";
    public const string FileOverlap = "file-overlap";
    public const string HotModule = "hot-module";
    public const string CollidedPair = "collided-pair";

    // Releases.
    public const string BlockerCleared = "blocker-cleared";
    public const string Admitted = "admitted";
    public const string LeftReady = "left-ready";
    public const string HoldLimitReached = "hold-limit-reached";
    public const string HoldShareCap = "hold-share-cap";

    // Edges and marks.
    public const string ChainOverlap = "chain-overlap";
    public const string ChainDenseSource = "chain-dense-source";
    public const string OverModuleLimit = "over-module-limit";
    public const string OverSizePrior = "over-size-prior";
}

/// <summary>One starter rule set: id, persona, default revision and parameters.</summary>
public sealed record DispatchStrategyDefinition(
    string Id,
    string Persona,
    string DefaultRevision,
    IReadOnlyDictionary<string, string> DefaultParameters);

/// <summary>
/// The five starter strategies of the Dossier section Starter personas, held
/// as data. Parameters only tune within the hard bounds: a hold of at most 24
/// hours, at most 25 % of Ready held, at most 2 exclusive marks per project.
/// </summary>
public static class DispatchStrategies
{
    public const string LaneEntry = "lane-entry-v1";
    public const string OnePerArea = "one-per-area-v1";
    public const string FileOverlap = "file-overlap-v1";
    public const string LedgerLearned = "ledger-learned-v1";
    public const string PackageThenChain = "package-then-chain-v1";

    public const string StarterRevision = "2026.11.1";

    public const string HoldHours = "holdHours";
    public const string MaxHeldShare = "maxHeldShare";
    public const string MaxExclusiveMarks = "maxExclusiveMarks";
    public const string ExclusiveOverModules = "exclusiveOverModules";
    public const string MinSharedFiles = "minSharedFiles";
    public const string HotWindowDays = "hotWindowDays";
    public const string ChainPartnerMean = "chainPartnerMean";
    public const string ExclusiveOverPriorFiles = "exclusiveOverPriorFiles";
    public const string PackageMinSharedFiles = "packageMinSharedFiles";
    public const string PackageMaxMembers = "packageMaxMembers";
    public const string PackageMaxPriorFiles = "packageMaxPriorFiles";
    public const string Forecast = "forecast";

    public const double HoldHoursBound = 24;
    public const double HeldShareBound = 0.25;
    public const double ExclusiveMarksBound = 2;

    private static readonly IReadOnlyDictionary<string, string> Bounded = new Dictionary<string, string>
    {
        [HoldHours] = "24",
        [MaxHeldShare] = "0.25",
        [MaxExclusiveMarks] = "2",
        [Forecast] = "model-free",
    };

    public static readonly IReadOnlyList<DispatchStrategyDefinition> Catalogue =
    [
        new(LaneEntry, "queue-keeper", StarterRevision, new Dictionary<string, string>()),
        new(OnePerArea, "area-warden", StarterRevision, With(new()
        {
            [ExclusiveOverModules] = "3",
        })),
        new(FileOverlap, "surveyor", StarterRevision, With(new()
        {
            [MinSharedFiles] = "1",
        })),
        new(LedgerLearned, "historian", StarterRevision, With(new()
        {
            [MinSharedFiles] = "1",
            [HotWindowDays] = "14",
            [ChainPartnerMean] = "5",
        })),
        new(PackageThenChain, "packer", StarterRevision, With(new()
        {
            [MinSharedFiles] = "1",
            [ExclusiveOverModules] = "3",
            [ExclusiveOverPriorFiles] = "25",
            // Read by the recut of DS-9; the dispatch part does not package.
            [PackageMinSharedFiles] = "2",
            [PackageMaxMembers] = "3",
            [PackageMaxPriorFiles] = "25",
        })),
    ];

    /// <summary>The starter strategy <paramref name="id"/> at its default revision and parameters.</summary>
    public static DispatchStrategy Starter(string id)
    {
        var definition = Find(id) ?? throw new ArgumentException($"unknown dispatch strategy '{id}'", nameof(id));
        return new DispatchStrategy(definition.Id, definition.DefaultRevision, definition.DefaultParameters);
    }

    public static DispatchStrategyDefinition? Find(string? id)
        => Catalogue.FirstOrDefault(definition => string.Equals(definition.Id, id?.Trim(), StringComparison.Ordinal));

    /// <summary>Problems of a strategy as data; empty when the plan can use it unchanged.</summary>
    public static IReadOnlyList<string> Validate(DispatchStrategy strategy)
    {
        var problems = new List<string>();
        if (!string.Equals(strategy.Schema, DispatchStrategy.SchemaV1, StringComparison.Ordinal))
            problems.Add($"schema '{strategy.Schema}' is not {DispatchStrategy.SchemaV1}");
        var definition = Find(strategy.Id);
        if (definition is null)
        {
            problems.Add($"unknown strategy id '{strategy.Id}'");
            return problems;
        }
        if (string.IsNullOrWhiteSpace(strategy.Revision))
            problems.Add("revision is empty");
        foreach (var (name, value) in strategy.Parameters ?? new Dictionary<string, string>())
        {
            if (!definition.DefaultParameters.ContainsKey(name))
            {
                problems.Add($"parameter '{name}' is not defined for {definition.Id}");
                continue;
            }
            if (name == Forecast)
            {
                if (value is not ("model-free" or "model-assisted"))
                    problems.Add($"parameter '{Forecast}' must be model-free or model-assisted");
                continue;
            }
            if (!double.TryParse(value, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var number) || number < 0)
            {
                problems.Add($"parameter '{name}' must be a non-negative number");
                continue;
            }
            var bound = name switch
            {
                HoldHours => HoldHoursBound,
                MaxHeldShare => HeldShareBound,
                MaxExclusiveMarks => ExclusiveMarksBound,
                _ => double.MaxValue,
            };
            if (number > bound)
                problems.Add($"parameter '{name}' exceeds its hard bound {bound.ToString(System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return problems;
    }

    private static IReadOnlyDictionary<string, string> With(Dictionary<string, string> own)
    {
        foreach (var (name, value) in Bounded) own.TryAdd(name, value);
        return own;
    }
}
