namespace AgentStudio.OrchestrationDecisions;

/// <summary>
/// DS-3 (Dossier docs/dispatcher-personas, DS-D1 B, DS-D2 B, DS-D3 B): the
/// pure dispatch plan of one dispatcher strategy for one project. The host
/// collects <see cref="DispatchFacts"/>, calls <see cref="Plan"/> for the live
/// persona and every shadow persona on the same facts, and applies only the
/// live plan. The plan orders Ready, holds, adds own dependency edges, sets
/// exclusive marks and decides Admit or Serialize for every free slot.
///
/// <para>
/// Hard constraints for every strategy: within a priority class owner pins
/// first, then urgent cards, then the rest by lane entry; a card whose
/// dependsOn target is not delivered never takes a slot; an exclusive card
/// runs alone and blocks everybody while it is in the comparison set; at most
/// the free slots are filled; a hold lasts at most 24 hours, is renewed once,
/// then becomes a decision request, and at most 25 % of Ready is held.
/// Strategies are work-conserving: when the head card must wait they look
/// further down the order.
/// </para>
///
/// <para>
/// <c>lane-entry-v1</c> (Queue keeper) reproduces today's
/// <c>LaneSortApplier</c> lane-entry order and the <c>ParallelSlotPolicy</c>
/// admission on the declared prompt-path scope. With
/// <see cref="DispatchOverlapWindow.Running"/> its admissions equal today's
/// pick-gate; with <see cref="DispatchOverlapWindow.InFlight"/> (DS-D10 B) it
/// compares with every started card that has not integrated.
/// </para>
/// </summary>
public static class DispatchPlanPolicy
{
    public static DispatchPlan Plan(DispatchFacts facts, DispatchStrategy strategy)
    {
        ArgumentNullException.ThrowIfNull(facts);
        ArgumentNullException.ThrowIfNull(strategy);
        var definition = DispatchStrategies.Find(strategy.Id)
            ?? throw new ArgumentException($"unknown dispatch strategy '{strategy.Id}'", nameof(strategy));
        var rules = new Rules(definition, strategy, facts);
        var persona = string.IsNullOrWhiteSpace(facts.Persona) ? definition.Persona : facts.Persona.Trim();

        var ordered = facts.Ready.OrderBy(card => card, Comparer<DispatchReadyCard>.Create(CompareOrder)).ToList();
        var marks = ExclusiveMarks(ordered, facts, rules);
        var marked = marks.Select(mark => mark.CardKey).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var subjects = ordered.ToDictionary(card => card.Key,
            card => Subject.Of(card, marked.Contains(card.Key), rules), StringComparer.OrdinalIgnoreCase);
        var satisfied = (facts.SatisfiedDependencies ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var retained = (facts.ActiveDependencies ?? []).Where(edge => subjects.ContainsKey(edge.CardKey)
            && !satisfied.Contains(edge.DependsOn)).ToArray();
        var edges = Chains(ordered, subjects, rules, persona, retained);
        var chained = edges.Select(edge => edge.CardKey).ToHashSet(StringComparer.OrdinalIgnoreCase);

        var order = ordered.Select((card, index) => new DispatchOrderEntry(index + 1, card.Key, card.PriorityClass,
                card.Pinned, card.Urgent,
                card.DependenciesSatisfied && !chained.Contains(card.Key)
                    ? DispatchCandidateState.Eligible
                    : DispatchCandidateState.WaitsForDependency))
            .ToArray();
        var eligible = order.Where(entry => entry.State == DispatchCandidateState.Eligible)
            .Select(entry => subjects[entry.CardKey]).ToList();
        var pickupEligible = eligible.Where(card => facts.PickupCandidates is null
            || facts.PickupCandidates.Contains(card.Key, StringComparer.OrdinalIgnoreCase)).ToList();

        var comparison = facts.InFlight
            .Where(card => facts.OverlapWindow == DispatchOverlapWindow.InFlight || card.HoldsSlot)
            .Select(card => Subject.Of(card, rules)).ToList();
        var admitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var slots = new List<DispatchSlotDecision>();
        for (var slot = 1; slot <= Math.Max(0, facts.FreeSlots); slot++)
            slots.Add(DecideSlot(slot, pickupEligible, admitted, comparison, rules));

        var (holds, releases) = Holds(facts, eligible, admitted, comparison, rules);
        return new DispatchPlan(facts.Project, definition.Id, strategy.Revision, persona,
            order, holds, releases, edges, marks, slots);
    }

    /// <summary>
    /// Plan order inside the hard constraints: priority class ascending, then
    /// owner pins by <c>order</c> ascending, then urgent cards, then the rest;
    /// unpinned cards flow by lane entry, newest first, with the card key
    /// descending as tiebreaker (LaneSortStrategies lane-entry).
    /// </summary>
    public static int CompareOrder(DispatchReadyCard a, DispatchReadyCard b)
    {
        var byClass = a.PriorityClass.CompareTo(b.PriorityClass);
        if (byClass != 0) return byClass;
        if (a.Pinned != b.Pinned) return a.Pinned ? -1 : 1;
        if (a.Pinned)
        {
            var byOrder = a.Order.CompareTo(b.Order);
            return byOrder != 0 ? byOrder : CompareKeyDescending(a.Key, b.Key);
        }
        if (a.Urgent != b.Urgent) return a.Urgent ? -1 : 1;
        var byEntry = b.EnteredLaneAt.CompareTo(a.EnteredLaneAt);
        return byEntry != 0 ? byEntry : CompareKeyDescending(a.Key, b.Key);
    }

    /// <summary>
    /// Module of a repository path (DS-1 module map): the feature folder under
    /// <c>frontend/src/app/features</c>, the folder under <c>frontend/src/app</c>
    /// and <c>frontend/e2e</c>, <c>backend/&lt;area&gt;/&lt;folder&gt;</c>, and
    /// otherwise the top folder (<c>orchestration-decisions</c>, <c>runner</c>).
    /// </summary>
    public static string ModuleOf(string path)
    {
        var parts = NormalizePath(path).Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0) return string.Empty;
        if (parts.Length == 1) return parts[0];
        var joined = string.Join('/', parts);
        var depth = joined.StartsWith("frontend/src/app/features/", StringComparison.OrdinalIgnoreCase) ? 5
            : joined.StartsWith("frontend/src/app/", StringComparison.OrdinalIgnoreCase) ? 4
            : joined.StartsWith("frontend/e2e/", StringComparison.OrdinalIgnoreCase) ? 3
            : joined.StartsWith("backend/", StringComparison.OrdinalIgnoreCase) ? 3
            : 1;
        return string.Join('/', parts.Take(Math.Min(depth, parts.Length - 1)));
    }

    /// <summary>
    /// First overlapping declared path of two scopes, else null. Same rule as
    /// the pick-gate (<c>ParallelSlotPolicy.FirstScopeConflict</c>): an empty
    /// scope is unknown and never conflicts; paths overlap by prefix.
    /// </summary>
    public static string? FirstScopeConflict(IReadOnlyList<string> a, IReadOnlyList<string> b)
    {
        if (!HasDeclaredScope(a) || !HasDeclaredScope(b)) return null;
        foreach (var pa in a)
        foreach (var pb in b)
        {
            if (string.IsNullOrWhiteSpace(pa) || string.IsNullOrWhiteSpace(pb)) continue;
            var na = ScopePath(pa);
            var nb = ScopePath(pb);
            if (na.Length == 0 || nb.Length == 0) continue;
            if (na == nb || na.StartsWith(nb + "/", StringComparison.Ordinal) || nb.StartsWith(na + "/", StringComparison.Ordinal))
                return na.Length <= nb.Length ? na : nb;
        }
        return null;
    }

    private static DispatchSlotDecision DecideSlot(int slot, IReadOnlyList<Subject> eligible,
        HashSet<string> admitted, List<Subject> comparison, Rules rules)
    {
        var passedOver = new List<DispatchPassOver>();
        foreach (var candidate in eligible)
        {
            if (admitted.Contains(candidate.Key)) continue;
            var check = Check(candidate, comparison, rules);
            if (!check.Admit)
            {
                passedOver.Add(new DispatchPassOver(candidate.Key, check.Code, check.With, check.Detail));
                continue;
            }
            admitted.Add(candidate.Key);
            comparison.Add(candidate);
            return new DispatchSlotDecision(slot, DispatchSlotAction.Admit, candidate.Key, check.Code,
                check.Code == DispatchReasonCodes.RunAlone, passedOver);
        }
        var code = passedOver.Count == 0 ? DispatchReasonCodes.NoCandidate
            : passedOver.Select(item => item.ReasonCode).Distinct().Count() == 1 ? passedOver[0].ReasonCode
            : DispatchReasonCodes.NoAdmissibleCandidate;
        return new DispatchSlotDecision(slot, DispatchSlotAction.Serialize, null, code, false, passedOver);
    }

    private readonly record struct Verdict(bool Admit, string Code, string? With = null, string? Detail = null);

    /// <summary>
    /// Admission of one candidate against the comparison set, in the order of
    /// the pick-gate: an exclusive card in the set, a read-only candidate, an
    /// exclusive candidate, then the strategy's overlap rule.
    /// </summary>
    private static Verdict Check(Subject candidate, IReadOnlyList<Subject> others, Rules rules)
    {
        others = others.Where(other => !other.Key.Equals(candidate.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
        var exclusive = others.FirstOrDefault(other => other.Exclusive);
        if (exclusive is not null)
            return new Verdict(false, DispatchReasonCodes.ExclusiveRunning, exclusive.Key);
        if (candidate.ReadOnly)
            return new Verdict(true, DispatchReasonCodes.ReadOnly);
        if (candidate.Exclusive)
            return others.Count == 0
                ? new Verdict(true, DispatchReasonCodes.RunAlone)
                : new Verdict(false, DispatchReasonCodes.ExclusiveWaitsForDrain);

        var optimistic = rules.Unknown(candidate);
        foreach (var other in others)
        {
            optimistic |= rules.Unknown(other);
            if (rules.Conflict(candidate, other) is { } conflict)
                return conflict;
        }
        return new Verdict(true, others.Count == 0 ? DispatchReasonCodes.FirstSlot
            : optimistic ? DispatchReasonCodes.ParallelOptimistic
            : DispatchReasonCodes.ParallelDisjoint);
    }

    private static IReadOnlyList<DispatchExclusiveMark> ExclusiveMarks(
        IReadOnlyList<DispatchReadyCard> ordered, DispatchFacts facts, Rules rules)
    {
        if (!rules.MarksExclusive) return [];
        var free = rules.MaxExclusiveMarks - (facts.ActiveExclusiveMarks ?? [])
            .Distinct(StringComparer.OrdinalIgnoreCase).Count();
        var marks = new List<DispatchExclusiveMark>();
        foreach (var card in ordered)
        {
            if (marks.Count >= free) break;
            if (card.Exclusive || card.ReadOnly) continue;
            var modules = rules.ModulesOf(card.Forecast);
            if (modules.Count > rules.ExclusiveOverModules)
                marks.Add(new DispatchExclusiveMark(card.Key, DispatchReasonCodes.OverModuleLimit,
                    $"{modules.Count} modules, more than {rules.ExclusiveOverModules}"));
            else if (rules.ExclusiveOverPriorFiles is { } prior && card.Source is { } source && source.SizePriorFiles > prior)
                marks.Add(new DispatchExclusiveMark(card.Key, DispatchReasonCodes.OverSizePrior,
                    $"size prior {source.SizePriorFiles} code files, more than {prior}"));
        }
        return marks;
    }

    /// <summary>
    /// Chains (Packer; Historian for dense sources): inside one source wave,
    /// each card waits for the nearest earlier card in source order whose
    /// forecast overlaps it. Pinned and urgent cards never wait for an added
    /// edge; an existing edge or a cycle with existing edges skips the edge.
    /// </summary>
    private static IReadOnlyList<DispatchDependencyEdge> Chains(IReadOnlyList<DispatchReadyCard> ordered,
        IReadOnlyDictionary<string, Subject> subjects, Rules rules, string persona, IReadOnlyList<DispatchDependencyEdge> retained)
    {
        var edges = retained.ToList();
        var graph = ordered.ToDictionary(card => card.Key,
            card => new HashSet<string>(card.DependsOn ?? [], StringComparer.OrdinalIgnoreCase),
            StringComparer.OrdinalIgnoreCase);
        foreach (var edge in retained) graph[edge.CardKey].Add(edge.DependsOn);
        var waves = ordered.Where(card => card.Source is not null && !string.IsNullOrWhiteSpace(card.Source.WaveId))
            .GroupBy(card => card.Source!.WaveId.Trim(), StringComparer.OrdinalIgnoreCase)
            .OrderBy(wave => wave.Key, StringComparer.Ordinal);
        foreach (var wave in waves)
        {
            var code = rules.ChainCode(wave.First().Source!.SourceKind);
            if (code is null) continue;
            var members = wave.OrderBy(card => card.Source!.Position)
                .ThenBy(card => card.Key, Comparer<string>.Create(CompareKeyAscending)).ToList();
            for (var index = 1; index < members.Count; index++)
            {
                var card = members[index];
                if (card.Pinned || card.Urgent || card.ReadOnly) continue;
                var before = members.Take(index).Reverse()
                    .FirstOrDefault(earlier => !earlier.ReadOnly
                        && rules.ForecastOverlap(subjects[card.Key], subjects[earlier.Key]) is not null);
                if (before is null || graph[card.Key].Contains(before.Key) || Reaches(graph, before.Key, card.Key))
                    continue;
                graph[card.Key].Add(before.Key);
                edges.Add(new DispatchDependencyEdge(card.Key, before.Key, "dispatch:" + persona, code));
            }
        }
        return edges;
    }

    private static bool Reaches(IReadOnlyDictionary<string, HashSet<string>> graph, string from, string to)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<string>([from]);
        while (stack.Count > 0)
        {
            var key = stack.Pop();
            if (string.Equals(key, to, StringComparison.OrdinalIgnoreCase)) return true;
            if (!seen.Add(key) || !graph.TryGetValue(key, out var next)) continue;
            foreach (var target in next) stack.Push(target);
        }
        return false;
    }

    private static readonly HashSet<string> HoldCodes =
    [
        DispatchReasonCodes.AreaBusy, DispatchReasonCodes.UnknownAreaBusy, DispatchReasonCodes.FileOverlap,
        DispatchReasonCodes.HotModule, DispatchReasonCodes.CollidedPair,
    ];

    /// <summary>
    /// Holds for eligible cards that a forecast rule keeps behind a named
    /// in-flight or just admitted card, in plan order up to 25 % of Ready. A
    /// hold lasts the hold hours, is renewed once, then is released with a
    /// decision request. Holds whose card no longer waits are released.
    /// </summary>
    private static (IReadOnlyList<DispatchHold>, IReadOnlyList<DispatchRelease>) Holds(DispatchFacts facts,
        IReadOnlyList<Subject> eligible, HashSet<string> admitted, IReadOnlyList<Subject> comparison, Rules rules)
    {
        var active = (facts.ActiveHolds ?? [])
            .GroupBy(hold => hold.CardKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var requested = (facts.DecisionRequestedHolds ?? []).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var cap = (int)Math.Floor(facts.Ready.Count * rules.MaxHeldShare + 1e-9);
        var holds = new List<DispatchHold>();
        var releases = new List<DispatchRelease>();
        var handled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (rules.Holds)
        {
            foreach (var candidate in eligible)
            {
                if (admitted.Contains(candidate.Key) || requested.Contains(candidate.Key)) continue;
                var check = Check(candidate, comparison, rules);
                if (check.Admit || !HoldCodes.Contains(check.Code) || check.With is null) continue;
                handled.Add(candidate.Key);
                active.TryGetValue(candidate.Key, out var existing);
                if (holds.Count >= cap)
                {
                    if (existing is not null)
                        releases.Add(new DispatchRelease(candidate.Key, DispatchReasonCodes.HoldShareCap, false));
                    continue;
                }
                if (existing is null)
                {
                    holds.Add(new DispatchHold(candidate.Key, check.Code, check.With, facts.NowUtc,
                        facts.NowUtc.Add(rules.HoldDuration)));
                }
                else if (facts.NowUtc < existing.ExpiresAtUtc)
                {
                    holds.Add(existing with { ReasonCode = check.Code, WaitsFor = check.With });
                }
                else if (existing.Renewals < 1)
                {
                    holds.Add(existing with
                    {
                        ReasonCode = check.Code, WaitsFor = check.With,
                        ExpiresAtUtc = facts.NowUtc.Add(rules.HoldDuration), Renewals = existing.Renewals + 1,
                    });
                }
                else
                {
                    releases.Add(new DispatchRelease(candidate.Key, DispatchReasonCodes.HoldLimitReached, true));
                }
            }
        }

        var ready = facts.Ready.Select(card => card.Key).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var hold in active.Values.OrderBy(hold => hold.CardKey, StringComparer.Ordinal))
        {
            if (handled.Contains(hold.CardKey)) continue;
            var code = !ready.Contains(hold.CardKey) ? DispatchReasonCodes.LeftReady
                : admitted.Contains(hold.CardKey) ? DispatchReasonCodes.Admitted
                : DispatchReasonCodes.BlockerCleared;
            releases.Add(new DispatchRelease(hold.CardKey, code, false));
        }
        return (holds, releases);
    }

    /// <summary>The normalized facts of one card that the admission rules compare.</summary>
    private sealed record Subject(
        string Key,
        bool Exclusive,
        bool ReadOnly,
        IReadOnlyList<string> DeclaredScope,
        IReadOnlySet<string> Files,
        IReadOnlySet<string> Modules,
        DispatchForecastConfidence Confidence)
    {
        public static Subject Of(DispatchReadyCard card, bool markedExclusive, Rules rules)
            => Create(card.Key, card.Exclusive || markedExclusive, card.ReadOnly, card.DeclaredScope, card.Forecast, rules);

        public static Subject Of(DispatchInFlightCard card, Rules rules)
            => Create(card.Key, card.Exclusive, card.ReadOnly, card.DeclaredScope, card.Forecast, rules);

        private static Subject Create(string key, bool exclusive, bool readOnly, IReadOnlyList<string>? scope,
            DispatchForecast? forecast, Rules rules)
        {
            forecast ??= DispatchForecast.Unknown;
            return new Subject(key, exclusive, readOnly, scope ?? [], rules.FilesOf(forecast),
                rules.ModulesOf(forecast), forecast.Confidence);
        }
    }

    /// <summary>The rule set of one strategy with its parameters clamped to the hard bounds.</summary>
    private sealed class Rules
    {
        private readonly string _id;
        private readonly HashSet<string> _hubs;
        private readonly HashSet<string> _hotModules;
        private readonly HashSet<(string, string)> _collided;
        private readonly HashSet<string> _denseSources;
        private readonly int _minSharedFiles;

        public Rules(DispatchStrategyDefinition definition, DispatchStrategy strategy, DispatchFacts facts)
        {
            _id = definition.Id;
            var parameters = strategy.Parameters ?? new Dictionary<string, string>();
            double Number(string name, double bound = double.MaxValue)
            {
                var text = parameters.TryGetValue(name, out var own) ? own
                    : definition.DefaultParameters.TryGetValue(name, out var fallback) ? fallback : null;
                var value = double.TryParse(text, System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out var parsed) && parsed >= 0 ? parsed : 0;
                return Math.Min(value, bound);
            }

            HoldDuration = TimeSpan.FromHours(Number(DispatchStrategies.HoldHours, DispatchStrategies.HoldHoursBound));
            MaxHeldShare = Number(DispatchStrategies.MaxHeldShare, DispatchStrategies.HeldShareBound);
            MaxExclusiveMarks = (int)Number(DispatchStrategies.MaxExclusiveMarks, DispatchStrategies.ExclusiveMarksBound);
            ExclusiveOverModules = (int)Number(DispatchStrategies.ExclusiveOverModules);
            ExclusiveOverPriorFiles = _id == DispatchStrategies.PackageThenChain
                ? (int)Number(DispatchStrategies.ExclusiveOverPriorFiles) : null;
            _minSharedFiles = Math.Max(1, (int)Number(DispatchStrategies.MinSharedFiles));
            _hubs = (facts.HubFiles ?? []).Select(FileKey).Where(path => path.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            var ledger = facts.Ledger ?? DispatchLedgerSummary.Empty;
            var learned = _id == DispatchStrategies.LedgerLearned;
            var since = facts.NowUtc.AddDays(-Number(DispatchStrategies.HotWindowDays));
            _hotModules = learned
                ? ledger.HotModules.Where(hot => hot.LastConflictAtUtc >= since)
                    .Select(hot => ModuleKey(hot.Module)).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
            _collided = learned
                ? ledger.CollidedPairs.SelectMany(pair => new[]
                    {
                        (pair.First.ToUpperInvariant(), pair.Second.ToUpperInvariant()),
                        (pair.Second.ToUpperInvariant(), pair.First.ToUpperInvariant()),
                    }).ToHashSet()
                : [];
            var partnerMean = Number(DispatchStrategies.ChainPartnerMean);
            _denseSources = learned
                ? ledger.Sources.Where(source => source.MeanPartnerCount > partnerMean)
                    .Select(source => source.SourceKind.Trim()).ToHashSet(StringComparer.OrdinalIgnoreCase)
                : [];
        }

        public TimeSpan HoldDuration { get; }
        public double MaxHeldShare { get; }
        public int MaxExclusiveMarks { get; }
        public int ExclusiveOverModules { get; }
        public int? ExclusiveOverPriorFiles { get; }

        /// <summary>Queue keeper: no holds, no chains, no recut.</summary>
        public bool Holds => _id != DispatchStrategies.LaneEntry;

        public bool MarksExclusive => _id is DispatchStrategies.OnePerArea or DispatchStrategies.PackageThenChain;

        public string? ChainCode(string? sourceKind) => _id switch
        {
            DispatchStrategies.PackageThenChain => DispatchReasonCodes.ChainOverlap,
            DispatchStrategies.LedgerLearned when sourceKind is not null && _denseSources.Contains(sourceKind.Trim())
                => DispatchReasonCodes.ChainDenseSource,
            _ => null,
        };

        public IReadOnlySet<string> FilesOf(DispatchForecast forecast)
            => (forecast.Files ?? []).Select(FileKey).Where(path => path.Length > 0 && !_hubs.Contains(path))
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

        /// <summary>Forecast modules; derived from the non-hub files when the forecast names none. Hubs define no area.</summary>
        public IReadOnlySet<string> ModulesOf(DispatchForecast forecast)
        {
            var named = (forecast.Modules ?? []).Select(ModuleKey).Where(module => module.Length > 0)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);
            return named.Count > 0 ? named
                : FilesOf(forecast).Select(ModuleOf).Where(module => module.Length > 0)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
        }

        /// <summary>Whether a card's touch set is unknown, so an admission beside it is optimistic.</summary>
        public bool Unknown(Subject card) => _id switch
        {
            DispatchStrategies.LaneEntry => !HasDeclaredScope(card.DeclaredScope),
            DispatchStrategies.OnePerArea => !card.ReadOnly && card.Modules.Count == 0,
            _ => !card.ReadOnly && card.Files.Count == 0 && card.Modules.Count == 0,
        };

        public Verdict? Conflict(Subject candidate, Subject other)
        {
            if (_id == DispatchStrategies.LaneEntry)
            {
                var path = FirstScopeConflict(candidate.DeclaredScope, other.DeclaredScope);
                return path is null ? null : new Verdict(false, DispatchReasonCodes.ScopeOverlap, other.Key, path);
            }
            if (other.ReadOnly) return null;
            if (_id == DispatchStrategies.OnePerArea)
            {
                if (candidate.Modules.Count == 0)
                    return other.Modules.Count == 0
                        ? new Verdict(false, DispatchReasonCodes.UnknownAreaBusy, other.Key, "unknown")
                        : null;
                var module = candidate.Modules.Where(other.Modules.Contains).Order(StringComparer.Ordinal).FirstOrDefault();
                return module is null ? null : new Verdict(false, DispatchReasonCodes.AreaBusy, other.Key, module);
            }
            if (ForecastOverlap(candidate, other) is { } overlap) return overlap;
            if (_id != DispatchStrategies.LedgerLearned) return null;
            var hot = candidate.Modules.Where(module => _hotModules.Contains(module) && other.Modules.Contains(module))
                .Order(StringComparer.Ordinal).FirstOrDefault();
            if (hot is not null) return new Verdict(false, DispatchReasonCodes.HotModule, other.Key, hot);
            return _collided.Contains((candidate.Key.ToUpperInvariant(), other.Key.ToUpperInvariant()))
                ? new Verdict(false, DispatchReasonCodes.CollidedPair, other.Key)
                : null;
        }

        /// <summary>
        /// Surveyor overlap: at least the minimum of shared non-hub forecast
        /// files; a low-confidence forecast (no cited path) on either side
        /// compares modules instead. An unknown forecast never conflicts.
        /// </summary>
        public Verdict? ForecastOverlap(Subject candidate, Subject other)
        {
            if (candidate.Confidence == DispatchForecastConfidence.Low || other.Confidence == DispatchForecastConfidence.Low)
            {
                var module = candidate.Modules.Where(other.Modules.Contains).Order(StringComparer.Ordinal).FirstOrDefault();
                return module is null ? null : new Verdict(false, DispatchReasonCodes.AreaBusy, other.Key, module);
            }
            var shared = candidate.Files.Where(other.Files.Contains).Order(StringComparer.Ordinal).ToArray();
            return shared.Length >= _minSharedFiles
                ? new Verdict(false, DispatchReasonCodes.FileOverlap, other.Key, shared[0])
                : null;
        }

        private static string FileKey(string path) => NormalizePath(path);

        private static string ModuleKey(string module) => NormalizePath(module);
    }

    private static bool HasDeclaredScope(IReadOnlyList<string>? scope)
        => scope != null && scope.Any(path => !string.IsNullOrWhiteSpace(path));

    private static string ScopePath(string path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/').Trim('/').ToLowerInvariant();

    private static string NormalizePath(string? path)
        => string.IsNullOrWhiteSpace(path) ? string.Empty : path.Trim().Replace('\\', '/').Trim('/');

    /// <summary>Lane-entry key order: prefix, then numeric suffix; an empty key sorts last in both directions.</summary>
    private static int CompareKeyDescending(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
        if (string.IsNullOrEmpty(a)) return 1;
        if (string.IsNullOrEmpty(b)) return -1;
        return -CompareKeys(a, b);
    }

    private static int CompareKeyAscending(string? a, string? b)
    {
        if (string.IsNullOrEmpty(a) && string.IsNullOrEmpty(b)) return 0;
        if (string.IsNullOrEmpty(a)) return 1;
        if (string.IsNullOrEmpty(b)) return -1;
        return CompareKeys(a, b);
    }

    private static int CompareKeys(string a, string b)
    {
        var dashA = a.LastIndexOf('-');
        var dashB = b.LastIndexOf('-');
        if (dashA > 0 && dashB > 0)
        {
            var byPrefix = a.AsSpan(0, dashA).CompareTo(b.AsSpan(0, dashB), StringComparison.OrdinalIgnoreCase);
            if (byPrefix != 0) return byPrefix;
            if (int.TryParse(a.AsSpan(dashA + 1), out var numberA) && int.TryParse(b.AsSpan(dashB + 1), out var numberB))
                return numberA.CompareTo(numberB);
        }
        return string.Compare(a, b, StringComparison.OrdinalIgnoreCase);
    }
}
