using System.Globalization;
using AgentStudio.OrchestrationDecisions;
using AgentStudio.Personas;
using AgentStudio.Registry;
using AgentStudio.Runner;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.Tasks;
using AgentStudio.Tokens;

namespace AgentStudio.Dispatch;

/// <summary>Collects durable evidence. Ranking has no authority to acquire or release a dispatch lease.</summary>
public sealed class DispatchReviewService(DispatchStore store, WorkspaceSettingsService settings,
    TaskScannerService scanner, TimelineLog timeline, ProjectTokenLedger tokens,
    PersonaSchedulingService occurrences, ProjectSettingsService projectSettings, TimeProvider time)
{
    public DispatchReviewProjection Review(string project, string workspace, string watchPath, string? window, string? weights)
    {
        var (from, to) = ParseWindow(window, time.GetUtcNow());
        var revisions = settings.Get(workspace).DispatchReviewWeights;
        if (revisions.Count == 0) revisions = [DispatchWeightRevision.Default];
        var revision = weights is null ? revisions[^1] : revisions.FirstOrDefault(row => row.Id == weights)
            ?? throw new ArgumentException("Unknown weight revision.");
        var raw = store.ReadReviewWindow(project, from, to);
        if (raw is null || to > time.GetUtcNow())
        {
            raw = Collect(project, workspace, watchPath, from, to);
            store.WriteReviewWindow(raw);
        }
        var projection = DispatchReviewScoring.Score(raw, revision, revisions);
        var champion = store.ReadLease(project)?.Persona ?? "queue-keeper";
        var championRow = projection.Personas.FirstOrDefault(r => r.Raw.Persona == champion);
        var evidence = store.ReadCompetitionEvidence(project, from, to);
        return projection with { Competition = projection.Personas.Where(r => r.Raw.Persona != champion).Select(row =>
        {
            var observed = evidence.FirstOrDefault(e => e.Challenger == row.Raw.Persona);
            var facts = new DispatchCompetitionFacts(championRow?.Value, row.Value,
                row.Raw.Mode == "shadow" && observed is not null ? Math.Min(row.Raw.IntegratedCards, observed.ShadowIntegratedCards) : 0,
                observed?.ShadowWaves ?? 0, observed?.AlternatingLiveWaves ?? 0,
                observed?.AheadInEveryLiveWave ?? false, observed?.GuardCards ?? 0,
                observed?.LiveConflictsPer100, observed?.BaselineShadowConflictsPer100, observed?.SameGuardCards ?? false);
            return new DispatchReviewCompetition(champion, row.Raw.Persona, facts, MonolithTransitionAdapter.DispatchCompetition(facts));
        }).ToArray() };
    }

    public static (DateTimeOffset From, DateTimeOffset To) ParseWindow(string? window, DateTimeOffset now)
    {
        if (window is null or "30d")
        {
            var day = new DateTimeOffset(now.UtcDateTime.Date, TimeSpan.Zero);
            return (day.AddDays(-29), day.AddDays(1));
        }
        var range = window.Split('/');
        if (range.Length != 2 || !DateTimeOffset.TryParse(range[0], CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal, out var from)
            || !DateTimeOffset.TryParse(range[1], CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var to)
            || from >= to || to - from > TimeSpan.FromDays(366))
            throw new ArgumentException("Use 30d or a half-open UTC start/end window of at most 366 days.");
        return (from, to);
    }

    private sealed record CardEvidence(TaskInfo Card, TouchForecastV1 Forecast, TouchDelivery Delivery,
        DateTimeOffset IntegratedAt, IReadOnlyList<TimelineEvent> Events, IReadOnlyList<RunRecord> Runs);

    private DispatchReviewWindow Collect(string project, string workspace, string watchPath, DateTimeOffset from, DateTimeOffset to)
    {
        var cards = new List<CardEvidence>();
        foreach (var card in scanner.ScanAllJobsWithArchive().Where(card => !card.Fixture
            && card.ProjectName.Equals(project, StringComparison.OrdinalIgnoreCase)))
        {
            var forecast = store.Read(project, card.TaskKey);
            if (forecast is null || forecast.Deliveries.Count == 0) continue;
            var events = timeline.ReadAll(card.FolderPath);
            var integrated = events.Where(e => e.Kind == TimelineEventKinds.IntegrationSucceeded)
                .Select(e => (DateTimeOffset?)new DateTimeOffset(DateTime.SpecifyKind(e.Ts, DateTimeKind.Utc))).Max()
                ?? card.IntegrationRecords.Where(r => r.Classification is IntegrationRecordClasses.IntegratedVerified
                        or IntegrationRecordClasses.IntegratedHistorical or IntegrationRecordClasses.CuratedMapping)
                    .Select(r => (DateTimeOffset?)new DateTimeOffset(DateTime.SpecifyKind(r.AcceptedAtUtc ?? r.RecordedAtUtc, DateTimeKind.Utc))).Max();
            if (integrated is null || integrated < from || integrated >= to) continue;
            var delivery = forecast.Deliveries.Where(d => d.RecordedAt <= integrated).LastOrDefault() ?? forecast.Deliveries[^1];
            cards.Add(new(card, delivery.Forecast ?? forecast, delivery, integrated.Value, events, TaskGitFactReads.Runs(card)));
        }
        var definitions = DispatchStrategies.Catalogue;
        if (cards.Count == 0)
            return new(project, from, to, definitions.Select(d => new DispatchReviewRaw(d.Persona, d.Id, "replay", 0, [],
                DispatchWeightRevision.Default.Weights.Keys.ToDictionary(key => key, _ => (double?)null), null, true,
                ["No integrated cards with recorded real files in this window."])).ToArray());

        var cohort = cards.Select(c => c.Card.Key ?? c.Card.TaskKey).Order(StringComparer.Ordinal).ToArray();
        var replayCards = cards.Select(c =>
        {
            var scope = TaskScopePrediction.Predict(c.Card);
            return new DispatchReplayCard(new(c.Card.Key ?? c.Card.TaskKey, c.Card.Order,
                c.Forecast.ReadyEntryAt, c.Card.References?.DependsOn.Select(edge => edge.Key).ToArray() ?? [], true,
                new(c.Forecast.ForecastFiles, c.Forecast.Modules,
                    c.Forecast.CitedFiles.Count > 0 ? DispatchForecastConfidence.High : DispatchForecastConfidence.Low),
                scope.PredictedScope, scope.Exclusive, scope.ReadOnly,
                Urgent: c.Card.Tags.Contains("urgent", StringComparer.OrdinalIgnoreCase)), c.Delivery.RealFiles);
        }).ToArray();
        var slots = Math.Max(1, projectSettings.Get(project).MaxParallelism);
        var report = DispatchReplaySimulator.Run(new(project, $"review:{from:O}/{to:O}", from, replayCards,
            projectSettings.Get(project).DispatchHubFiles ?? TouchForecast.DefaultHubFiles,
            DispatchLedgerSummary.Empty, "delivery-forecasts"), new(slots));
        var ledger = tokens.LoadSnapshot(project, watchPath);
        bool Matches(CardEvidence c, string? key) => key is not null && (key == c.Card.Id || key == c.Card.Key || key == c.Card.TaskKey);
        var accepted = cards.Where(c => c.Card.IntegrationRecords.Any(r => r.AcceptedAtUtc is { } at && at < to.UtcDateTime)
            || c.Events.Any(e => e.Kind == TimelineEventKinds.LaneChanged && e.Ts < to.UtcDateTime
                && e.Details?.GetValueOrDefault("to") is TaskStates.Completed or TaskStates.Archive)).ToArray();
        var cardTokens = cards.ToDictionary(c => c.Card.TaskKey, c => ledger.Entries.Where(e => Matches(c, e.JobId))
            .Sum(e => e.TokenUsage is { } u ? (double)AgentStudio.Quota.WorkspaceQuotaPolicy.ModelTokens(
                u.InputTokens, u.OutputTokens, u.CacheReadTokens, u.CacheCreationTokens) : 0));
        var allRounds = cards.Sum(c => c.Runs.Count);
        var roundsClassified = cards.All(c => c.Runs.Count > 0 && c.Runs.All(r => RunTriggers.IsKnown(r.Trigger)));
        var conflictHistory = store.ReadConflicts(project);
        bool IsRework(CardEvidence card, RunRecord run) => run.Trigger == RunTriggers.IntegrationRecovery
            || run.Trigger == RunTriggers.GateFailure && conflictHistory.Any(c => c.Kind == "semantic" && Matches(card, c.Card) && c.At.UtcDateTime <= run.StartedAt);
        var reworkRounds = cards.Sum(c => c.Runs.Count(r => IsRework(c, r)));
        var codingHours = cards.Sum(c => c.Runs.Where(r => r.EndedAt.HasValue)
            .Sum(r => Math.Max(0, (r.EndedAt!.Value - r.StartedAt).TotalHours)));
        var starts = cards.SelectMany(c => c.Runs.Select(r => r.StartedAt)).ToArray();
        var elapsedHours = starts.Length == 0 ? 0 : (cards.Max(c => c.IntegratedAt).UtcDateTime - starts.Min()).TotalHours;
        var observedParallelism = elapsedHours > 0 && codingHours > 0 && cards.All(c => c.Runs.Count > 0 && c.Runs.All(r => r.EndedAt.HasValue))
            ? Math.Min(1, codingHours / (slots * elapsedHours) / report.ConflictFreeBound.SlotUse) : (double?)null;
        var logs = store.ReadDispatcherLog(project).Where(e => e.At >= from && e.At < to).ToArray();
        var plans = store.ReadShadow(project).ToDictionary(r => r.DecisionId);
        var conflicts = store.ReadConflicts(project, from, to).Where(conflict => cards.Any(c => Matches(c, conflict.Card))).ToArray();
        var receipts = occurrences.Receipts(workspace).Where(r => r.Project == project && r.CreatedAtUtc >= from.UtcDateTime
            && r.CreatedAtUtc < to.UtcDateTime).ToArray();
        var admissions = store.ReadRunDecisions(project).Where(r => r.Claimed).GroupBy(r => r.Card)
            .ToDictionary(g => g.Key, g => g.OrderBy(r => r.At).First());
        var identities = definitions.Select(d => (Persona: d.Persona, Strategy: d.Id))
            .Concat(logs.Select(l => (Persona: l.PersonaId, Strategy: l.StrategyId))).Distinct().ToArray();
        var raw = new List<DispatchReviewRaw>();
        var observedRows = new List<DispatchReviewRaw>();
        foreach (var identity in identities)
        {
            var simulated = report.Strategies.FirstOrDefault(s => s.Strategy == identity.Strategy);
            if (simulated is null) continue;
            var ownLogs = logs.Where(l => l.PersonaId == identity.Persona).ToArray();
            var dispatched = ownLogs.Where(l => plans.ContainsKey(l.DecisionId))
                .SelectMany(l => plans[l.DecisionId].Plan.Admitted).Distinct(StringComparer.OrdinalIgnoreCase).Count();
            var admitted = cards.Where(c => admissions.TryGetValue(c.Card.Key ?? c.Card.TaskKey, out var a)
                && plans.TryGetValue(a.DecisionId, out var p) && p.Plan.Persona == identity.Persona).ToArray();
            // Only a fully attributed common cohort can be compared as observed live evidence.
            var live = admitted.Length == cards.Count;
            var attributionComplete = conflicts.All(c => c.PersonaId is not null
                || c.DispatchDecisionIds.TryGetValue(c.Card, out var decision) && decision is not null);
            var failed = conflicts.Where(c => c.PersonaId == identity.Persona
                || c.DispatchDecisionIds.TryGetValue(c.Card, out var id) && id is not null
                    && plans.TryGetValue(id, out var p) && p.Plan.Persona == identity.Persona)
                .Select(c => c.Card).Distinct().Count();
            var actualTokens = accepted.Sum(c => cardTokens[c.Card.TaskKey]);
            var actualConflictCards = conflicts.Select(c => c.Card).Distinct().Count();
            var avoided = Math.Max(0, actualConflictCards - simulated.CardsNeedingSync);
            var estimatedTokens = allRounds == 0 ? actualTokens : Math.Max(0, actualTokens - avoided * actualTokens / allRounds);
            var lead = cards.Select(LeadHours).OfType<double>().Order().ToArray();
            var warning = new List<string>();
            if (cards.Any(c => c.Delivery.Forecast is null)) warning.Add("Some deliveries lack a frozen forecast; accuracy uses the stored forecast and may include hindsight.");
            if (!live) warning.Add("Shadow conflicts, rework, lead time and parallelism use DS-4 replay of the common real-file cohort; tokens subtract estimated avoided rounds.");
            warning.Add("DS-4 uses unit coding durations and a greedy real-file bound, not an optimal scheduler.");
            if (admitted.Length > 0 && !live) warning.Add("Partial live attribution is excluded to preserve the common-card comparison set.");
            if (ledger.Freshness.Status != "complete") warning.Add(ledger.Freshness.Warning ?? "Token ledger is incomplete.");
            if (live && !attributionComplete) warning.Add("Some conflicts have unknown admitting decisions; observed conflict rate is unmeasured.");
            if (live && !roundsClassified) warning.Add("Some run rounds have no typed trigger; observed rework share is unmeasured.");
            if (ownLogs.Length == 0) warning.Add("No explanation log entries.");
            var rating = store.ReadExplanationRatings(project).Where(r => r.Persona == identity.Persona
                && ownLogs.Any(l => l.Id == r.EntryId)).ToArray();
            var deterministic = ownLogs.Length == 0 ? (double?)null : ownLogs.Count(ExplanationPasses) / (double)ownLogs.Length;
            var ownerRating = rating.Length == 0 ? (double?)null : rating.Average(r => r.Score) / 100;
            if (ownerRating is null) warning.Add("No owner ratings of sampled explanation entries; explanation is unmeasured.");
            var hasTokens = accepted.Length > 0 && accepted.All(c => ledger.Entries.Any(e => Matches(c, e.JobId) && e.TokenUsage is not null));
            var criteria = new Dictionary<string, double?>
            {
                ["conflicts"] = live ? attributionComplete ? 100.0 * failed / cards.Count : null : simulated.ConflictsPer100,
                ["tokens"] = hasTokens ? (live ? actualTokens : estimatedTokens) / accepted.Length : null,
                ["rework"] = live ? roundsClassified && allRounds > 0 ? reworkRounds / (double)allRounds : null : simulated.ReworkShare,
                ["forecast"] = simulated.ForecastF1,
                ["parallelism"] = live ? observedParallelism : simulated.Parallelism,
                ["lead"] = lead.Length == cards.Count ? Median(lead) * (live ? 1 : simulated.MedianLeadTime / report.Strategies.First(r => r.Strategy == DispatchStrategies.LaneEntry).MedianLeadTime) : null,
                ["ownCost"] = dispatched > 0 && receipts.Any(r => r.PersonaId == identity.Persona)
                    ? 100.0 * receipts.Where(r => r.PersonaId == identity.Persona).Sum(r => r.Tokens) / dispatched : null,
                ["explanation"] = deterministic.HasValue && ownerRating.HasValue ? 0.7 * deterministic + 0.3 * ownerRating : null,
            };
            var fileScores = cards.Select(c => F1(c.Forecast.ForecastFiles, c.Delivery.RealFiles)).ToArray();
            var row = new DispatchReviewRaw(identity.Persona, identity.Strategy, live ? "live" : ownLogs.Any(l => l.Shadow) ? "shadow" : "replay", cards.Count, cohort, criteria,
                fileScores.Average(), !live, warning)
            { AcceptedCards = accepted.Length, DeterministicExplanationPassRate = deterministic, OwnerExplanationRating = ownerRating };
            raw.Add(row);
            if (admitted.Length > 0)
            {
                var own = admitted.Select(c => c.Card.TaskKey).ToHashSet(StringComparer.Ordinal);
                var ownAccepted = accepted.Where(c => own.Contains(c.Card.TaskKey)).ToArray();
                var ownRuns = admitted.SelectMany(c => c.Runs).ToArray();
                var ownReplay = DispatchReplaySimulator.Run(new(project, "observed:" + identity.Persona, from,
                    replayCards.Where(c => admitted.Any(a => (a.Card.Key ?? a.Card.TaskKey) == c.Ready.Key)).ToArray(),
                    projectSettings.Get(project).DispatchHubFiles ?? TouchForecast.DefaultHubFiles,
                    DispatchLedgerSummary.Empty, "delivery-forecasts"), new(slots));
                var ownHours = ownRuns.Where(r => r.EndedAt.HasValue).Sum(r => Math.Max(0, (r.EndedAt!.Value - r.StartedAt).TotalHours));
                var ownElapsed = ownRuns.Length == 0 ? 0 : (admitted.Max(c => c.IntegratedAt).UtcDateTime - ownRuns.Min(r => r.StartedAt)).TotalHours;
                var ownLead = admitted.Select(LeadHours).OfType<double>().Order().ToArray();
                var observedValues = new Dictionary<string, double?>(criteria)
                {
                    ["conflicts"] = attributionComplete ? 100.0 * failed / admitted.Length : null,
                    ["tokens"] = ownAccepted.Length > 0 && ownAccepted.All(c => ledger.Entries.Any(e => Matches(c, e.JobId) && e.TokenUsage is not null))
                        ? ownAccepted.Sum(c => cardTokens[c.Card.TaskKey]) / ownAccepted.Length : null,
                    ["rework"] = ownRuns.Length > 0 && ownRuns.All(r => RunTriggers.IsKnown(r.Trigger))
                        ? admitted.Sum(c => c.Runs.Count(r => IsRework(c, r))) / (double)ownRuns.Length : null,
                    ["forecast"] = ownReplay.Strategies.First(r => r.Strategy == identity.Strategy).ForecastF1,
                    ["parallelism"] = ownElapsed > 0 && ownRuns.All(r => r.EndedAt.HasValue)
                        ? Math.Min(1, ownHours / (slots * ownElapsed) / ownReplay.ConflictFreeBound.SlotUse) : null,
                    ["lead"] = ownLead.Length == admitted.Length ? Median(ownLead) : null,
                };
                observedRows.Add(row with { Mode = "live", IntegratedCards = admitted.Length, AcceptedCards = ownAccepted.Length,
                    Cards = admitted.Select(c => c.Card.Key ?? c.Card.TaskKey).Order(StringComparer.Ordinal).ToArray(),
                    Criteria = observedValues, FileF1 = admitted.Average(c => F1(c.Forecast.ForecastFiles, c.Delivery.RealFiles)),
                    TokensEstimated = false, Warnings = warning.Where(w => !w.StartsWith("Shadow", StringComparison.Ordinal)
                            && !w.StartsWith("Partial live", StringComparison.Ordinal))
                        .Append("Observed admissions are shown separately from the ranking because their cohort can differ from the common comparison set.").ToArray() });
            }
        }
        return new DispatchReviewWindow(project, from, to, raw) { ObservedPersonas = observedRows };
    }

    private static double? LeadHours(CardEvidence card)
    {
        var ready = card.Events.Where(e => e.Kind == TimelineEventKinds.LaneChanged
                && e.Details?.GetValueOrDefault("to") == TaskStates.Ready && e.Ts <= card.IntegratedAt.UtcDateTime)
            .Select(e => (DateTime?)e.Ts).Min()
            ?? (card.Forecast.ReadyEntryAt == default ? (DateTime?)null : card.Forecast.ReadyEntryAt);
        return ready is null ? null : Math.Max(0, (card.IntegratedAt.UtcDateTime - ready.Value).TotalHours);
    }

    public static bool ExplanationPasses(DispatcherLogEntryV1 entry) => !string.IsNullOrWhiteSpace(entry.Rule)
        && !string.IsNullOrWhiteSpace(entry.FactsDigest) && entry.PassedOver.All(p => !string.IsNullOrWhiteSpace(p.ReasonCode))
        && !string.IsNullOrWhiteSpace(entry.ExpectedEffect) && !string.IsNullOrWhiteSpace(entry.Reason);
    private static double Median(double[] values) => (values[(values.Length - 1) / 2] + values[values.Length / 2]) / 2;
    private static double F1(IReadOnlyList<string> predicted, IReadOnlyList<string> real)
    {
        var p = predicted.ToHashSet(StringComparer.Ordinal);
        var r = real.ToHashSet(StringComparer.Ordinal);
        return p.Count + r.Count == 0 ? 0 : 2.0 * p.Intersect(r).Count() / (p.Count + r.Count);
    }
}
