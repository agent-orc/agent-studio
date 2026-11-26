using AgentStudio.OrchestrationDecisions;
using AgentStudio.Runner;

namespace AgentStudio.Dispatch;

/// <summary>Offline event simulation. All persona admission decisions come from the decision library.</summary>
public static class DispatchReplaySimulator
{
    public static DispatchReplayReport Run(DispatchReplayWave wave, DispatchReplayOptions options)
    {
        if (options.Slots < 1 || options.Durations is not ("unit" or "size")
            || !double.IsFinite(options.IntegrationLag) || options.IntegrationLag < 0
            || !Enum.IsDefined(options.Check))
            throw new ArgumentException("Use positive slots, unit or size durations, and a finite non-negative lag.");
        if (wave.Cards.Count == 0 || wave.Cards.Select(card => card.Ready.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count() != wave.Cards.Count)
            throw new ArgumentException("A wave must contain unique cards and at least one card.");
        var bound = Simulate(wave, options, null);
        var rows = DispatchStrategies.Catalogue.Select(strategy => Simulate(wave, options,
            DispatchStrategies.Starter(strategy.Id))).ToArray();
        for (var i = 0; i < rows.Length; i++)
            rows[i] = rows[i] with { Parallelism = Math.Min(1, rows[i].SlotUse / bound.SlotUse) };
        var report = new DispatchReplayReport(wave.Project, wave.Id, wave.IndexRevision, options, bound, rows)
        { WaveStart = wave.Start };
        report = report with { Limitations = report.Limitations.Concat(wave.Assumptions).ToArray() };
        foreach (var row in rows)
        {
            double Lower(Func<DispatchReplayResult, double> measure)
            {
                var low = rows.Min(measure);
                var high = rows.Max(measure);
                return high == low ? 100 : 100 * (high - measure(row)) / (high - low);
            }
            row.Scores = new Dictionary<string, double>
            {
                ["conflicts"] = Lower(r => r.ConflictsPer100),
                ["tokens"] = Lower(r => r.RoundsPerCard),
                ["rework"] = Lower(r => r.ReworkShare),
                ["forecast"] = 100 * row.ForecastF1,
                ["parallelism"] = 100 * row.Parallelism,
                ["lead"] = Lower(r => r.MedianLeadTime),
            };
            if (wave.Cards.Count < 40) continue;
            row.ValuePlain = row.Scores.Sum(score => report.Weights[score.Key] * score.Value)
                / row.Scores.Sum(score => report.Weights[score.Key]);
            row.Value = row.ValuePlain * Math.Min(1, row.Parallelism / report.Gate);
        }
        return report;
    }

    private static DispatchReplayResult Simulate(DispatchReplayWave wave, DispatchReplayOptions options, DispatchStrategy? strategy)
    {
        var cards = wave.Cards.ToDictionary(card => card.Ready.Key, StringComparer.OrdinalIgnoreCase);
        var queue = wave.Cards.Select(card => card.Ready).ToList();
        var runs = new List<DispatchReplayRun>();
        var plans = new List<DispatchPlan>();
        var exclusive = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var edges = queue.ToDictionary(card => card.Key, card => card.DependsOn.ToHashSet(StringComparer.OrdinalIgnoreCase), StringComparer.OrdinalIgnoreCase);
        var ledger = wave.Ledger;
        var hot = ledger.HotModules.ToList();
        var pairs = ledger.CollidedPairs.ToList();
        var measured = new HashSet<(string, string)>();
        var sync = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var integrated = new List<DispatchReplayRun>();
        IReadOnlyList<DispatchHold> holds = [];
        var requested = new HashSet<string>();
        var now = 0.0;
        while (queue.Count > 0 || runs.Any(run => run.IntegratedAt > now))
        {
            var active = runs.Where(run => run.IntegratedAt > now).ToArray();
            var running = active.Count(run => run.CodingEnd > now);
            var done = integrated.Select(run => run.Card).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var ready = queue.Select(card => card with
            {
                DependsOn = edges[card.Key].ToArray(),
                DependenciesSatisfied = (card.DependenciesSatisfied || card.DependsOn.All(cards.ContainsKey)) && edges[card.Key].All(key => !cards.ContainsKey(key) || done.Contains(key)),
                Exclusive = card.Exclusive || exclusive.Contains(card.Key),
            }).ToArray();
            var flight = active.Select(run => new DispatchInFlightCard(run.Card, run.CodingEnd > now,
                cards[run.Card].Ready.Forecast, cards[run.Card].Ready.DeclaredScope,
                cards[run.Card].Ready.Exclusive || exclusive.Contains(run.Card), cards[run.Card].Ready.ReadOnly)).ToArray();
            var facts = new DispatchFacts(wave.Project, wave.Start.AddHours(now), ready, flight,
                options.Slots - running, ledger, wave.HubFiles, options.Check, holds, exclusive.Where(key => active.Any(run => run.Card == key) || queue.Any(card => card.Key == key)).ToArray(), requested.ToArray());
            IReadOnlyList<string> admitted;
            if (strategy is not null)
            {
                var plan = MonolithTransitionAdapter.DispatchPlan(facts, strategy);
                plans.Add(plan);
                holds = plan.Holds;
                foreach (var release in plan.Releases.Where(release => release.DecisionRequest)) requested.Add(release.CardKey);
                foreach (var mark in plan.ExclusiveMarks) exclusive.Add(mark.CardKey);
                foreach (var edge in plan.DependenciesToAdd) edges[edge.CardKey].Add(edge.DependsOn);
                admitted = plan.Admitted;
            }
            else
            {
                // Measurement oracle uses the same policy with perfect file forecasts and no hub exclusions.
                DispatchForecast Real(string key) => new(cards[key].RealFiles, [], DispatchForecastConfidence.High);
                var oracle = facts with
                {
                    Ready = ready.Select(card => card with { Forecast = Real(card.Key), Source = null }).ToArray(),
                    InFlight = flight.Select(card => card with { Forecast = Real(card.Key) }).ToArray(),
                    HubFiles = [], Ledger = DispatchLedgerSummary.Empty,
                };
                admitted = MonolithTransitionAdapter.DispatchPlan(oracle,
                    DispatchStrategies.Starter(DispatchStrategies.FileOverlap)).Admitted;
            }
            foreach (var key in admitted)
            {
                var duration = options.Durations == "unit" ? 1 : 1 + cards[key].RealFiles.Count / 15.0;
                runs.Add(new(key, now, now + duration, now + duration + options.IntegrationLag));
                queue.RemoveAll(card => card.Key.Equals(key, StringComparison.OrdinalIgnoreCase));
            }
            var next = runs.SelectMany(run => new[] { run.CodingEnd, run.IntegratedAt }).Where(time => time > now).DefaultIfEmpty(double.PositiveInfinity).Min();
            if (!double.IsFinite(next))
            {
                if (queue.Count > 0) throw new InvalidOperationException("Replay cannot progress: unresolved dependencies or policy refusal.");
                break;
            }
            now = next;
            foreach (var run in runs.Where(run => run.IntegratedAt == now).OrderByDescending(run => run.Card, StringComparer.Ordinal))
            {
                foreach (var earlier in integrated.Where(earlier => earlier.IntegratedAt > run.Start))
                {
                    if (!Shared(cards[run.Card], cards[earlier.Card])) continue;
                    var pair = string.CompareOrdinal(run.Card, earlier.Card) < 0 ? (run.Card, earlier.Card) : (earlier.Card, run.Card);
                    measured.Add(pair);
                    sync.Add(run.Card);
                    pairs.Add(new(pair.Item1, pair.Item2));
                    hot.AddRange(TouchForecast.Modules(cards[run.Card].RealFiles.Intersect(cards[earlier.Card].RealFiles), wave.HubFiles)
                        .Select(module => new DispatchHotModule(module, wave.Start.AddHours(now))));
                }
                integrated.Add(run);
            }
            ledger = new(hot, pairs, ledger.Sources);
        }
        var n = cards.Count;
        var orderedTimes = runs.Select(run => run.IntegratedAt).Order().ToArray();
        var median = (orderedTimes[(n - 1) / 2] + orderedTimes[n / 2]) / 2;
        return new(strategy?.Id ?? "conflict-free-bound", runs.Count, measured.Count, sync.Count,
            (runs.Count + sync.Count) / (double)n, sync.Count / (double)(runs.Count + sync.Count),
            strategy is null ? 1 : PairF1(wave, strategy), runs.Sum(run => run.CodingEnd - run.Start) / (options.Slots * now),
            1, now, median, runs, plans) { ConflictsPer100 = 100.0 * sync.Count / n };
    }

    private static bool Shared(DispatchReplayCard a, DispatchReplayCard b) => a.RealFiles.Intersect(b.RealFiles, StringComparer.Ordinal).Any();

    private static double PairF1(DispatchReplayWave wave, DispatchStrategy strategy)
    {
        var truth = 0;
        var predicted = 0;
        var correct = 0;
        for (var a = 0; a < wave.Cards.Count; a++)
        for (var b = a + 1; b < wave.Cards.Count; b++)
        {
            var left = wave.Cards[a];
            var right = wave.Cards[b];
            var real = Shared(left, right);
            // Accuracy measures forecast pairs, independent of holds, exclusive marks or source chains.
            IEnumerable<string> Predicted(DispatchReplayCard card) => strategy.Id switch
            {
                DispatchStrategies.LaneEntry => [],
                DispatchStrategies.OnePerArea => card.Ready.Forecast.Modules,
                _ => card.Ready.Forecast.Files.Except(wave.HubFiles, StringComparer.Ordinal),
            };
            var guess = Predicted(left).Intersect(Predicted(right), StringComparer.Ordinal).Any();
            if (real) truth++;
            if (guess) predicted++;
            if (real && guess) correct++;
        }
        return truth + predicted == 0 ? 0 : 2.0 * correct / (truth + predicted);
    }
}
