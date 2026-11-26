using System.Globalization;
using AgentStudio.Pipeline;
using AgentStudio.Tasks;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Personas;

/// <summary>Weekly UTC cohort from retained incident, quarantine and guardian receipts.</summary>
public sealed class GuardianReviewService(LineGuardianStore guardian, PostMergeRunStore runs,
    TestQuarantineStore quarantines, TimeProvider clock, TaskScannerService? scanner = null, PipelineExecutionLog? executions = null, TimelineLog? timeline = null)
{
    public GuardianReview Review(string project, DateTimeOffset? week = null, string? displayName = null)
    {
        var now = clock.GetUtcNow();
        var start = week ?? new DateTimeOffset(now.UtcDateTime.Date.AddDays(-((int)now.UtcDateTime.DayOfWeek + 6) % 7 - 7), TimeSpan.Zero);
        if (start.Offset != TimeSpan.Zero || start.TimeOfDay != TimeSpan.Zero || start.DayOfWeek != DayOfWeek.Monday)
            throw new ArgumentException("Week must start on Monday at 00:00 UTC.");
        var state = guardian.Read(project);
        var post = runs.Read(project);
        var quarantine = quarantines.Read(project);
        var end = start.AddDays(7);
        var observedTo = now < end ? now : end;
        bool InWeek(DateTimeOffset at) => at >= start && at < end && at <= now;
        var incidents = (post.Incidents ?? []).Where(i => InWeek(i.FirstRedAtUtc)).ToArray();
        var cards = scanner?.ScanAllJobsWithArchive()
            .Where(c => c.ProjectName == project || c.ProjectName == displayName).ToArray() ?? [];
        var refusalEvidence = new List<GuardianReviewEvidence>();
        var unknownRefusals = false;
        if (scanner is not null && timeline is not null)
            foreach (var card in cards)
            {
                var events = timeline.ReadIntegrationWaitHistory(card.FolderPath).OrderBy(e => e.Ts).ToArray();
                AgentStudio.Shared.TimelineEvent? opened = null;
                foreach (var entry in events)
                {
                    if (entry.Kind == TimelineEventKinds.IntegrationQuarantineWait && entry.Details?.GetValueOrDefault("state") == "blocked")
                        opened ??= entry;
                    else if (opened is not null && (entry.Kind == TimelineEventKinds.IntegrationSucceeded
                        || entry.Kind == TimelineEventKinds.IntegrationQuarantineWait && entry.Details?.GetValueOrDefault("state") == "released"
                        || entry.Kind == TimelineEventKinds.LaneChanged && entry.Details?.GetValueOrDefault("to") == TaskStates.Archive))
                    { AddWait(opened, Utc(entry.Ts)); opened = null; }
                }
                if (opened is not null) AddWait(opened, now);
                void AddWait(AgentStudio.Shared.TimelineEvent entry, DateTimeOffset until)
                {
                    if (entry.Details?.GetValueOrDefault("scope") != "held-area")
                    { unknownRefusals |= Utc(entry.Ts) < end && until > start; return; }
                    refusalEvidence.Add(new($"wait:{card.TaskKey}:{entry.Ts:O}", card.TaskKey, Utc(entry.Ts),
                        "held-wait", $"timeline:{card.TaskKey}:{entry.Ts:O}", End: until, Card: card.TaskKey));
                }
            }
        var annotations = state.ReviewEvidence.Concat(refusalEvidence).Where(e => e.At <= now).ToArray();
        var values = GuardianReviewProjection.DefaultWeights.Keys.ToDictionary(k => k, _ => (double?)null);
        var sources = GuardianReviewProjection.DefaultWeights.Keys.ToDictionary(k => k, _ => (IReadOnlyList<string>)[]);
        var notes = new Dictionary<string, string>();
        var count = incidents.Length;
        var falseBlames = annotations.Where(e => InWeek(e.At) && e.Kind is "false-blame" or "unrelated-hold").ToArray();
        var withdrawn = state.Decisions.GroupBy(d => (d.Log.Test, d.Log.IncidentFingerprint)).SelectMany(group =>
        {
            var ordered = group.OrderBy(d => d.Log.AtUtc).ToArray();
            return ordered.Skip(1).Where((d, index) => InWeek(d.Log.AtUtc)
                && ordered[index].Applied.AttributedMerge is { } blamed && blamed != d.Applied.AttributedMerge);
        }).ToArray();
        var blameCount = withdrawn.Length + falseBlames.Where(e => e.Kind == "unrelated-hold" || !withdrawn.Any(d => e.Incident == d.Log.IncidentFingerprint)).Sum(e => e.Value);
        values["false-blames"] = count == 0 ? null : blameCount * 10 / count;
        sources["false-blames"] = falseBlames.Select(e => e.Source).Concat(withdrawn.Select(d => "guardian-decision:" + d.Log.DecisionId))
            .Concat(incidents.Select(i => "incident:" + i.Fingerprint)).ToArray();
        notes["false-blames"] = $"{blameCount:0.##} withdrawn attributions or unrelated held cards / {count} incidents × 10. Only evidenced withdrawals count.";
        var expiries = quarantine.Entries.Where(q => q.ExpiresAtUtc <= now && InWeek(q.ExpiresAtUtc)
            && (q.LiftedAtUtc is null || q.LiftedAtUtc > q.ExpiresAtUtc)).ToArray();
        var escapes = annotations.Where(e => InWeek(e.At) && e.Kind == "escape").ToArray();
        var relapses = quarantine.Entries.Where(q => InWeek(q.StartedAtUtc) && quarantine.Entries.Any(previous =>
            previous.Test == q.Test && previous.Id != q.Id && previous.LiftedAtUtc is { } lifted
            && q.StartedAtUtc >= lifted && q.StartedAtUtc - lifted <= TimeSpan.FromDays(7))).ToArray();
        var misclassified = state.Decisions.GroupBy(d => (d.Log.Test, d.Log.IncidentFingerprint)).Select(group =>
        {
            var ordered = group.OrderBy(d => d.Log.AtUtc).ToArray();
            var firstRegression = ordered.FirstOrDefault(d => d.Applied.Classification == AgentStudio.OrchestrationDecisions.RedTestClassification.Regression);
            return (ordered[0].Applied.Classification is AgentStudio.OrchestrationDecisions.RedTestClassification.Flaky
                or AgentStudio.OrchestrationDecisions.RedTestClassification.Environment)
                && firstRegression is not null && InWeek(firstRegression.Log.AtUtc) ? firstRegression : null;
        }).OfType<GuardianShadowRecord>().ToArray();
        values["escapes"] = expiries.Length + relapses.Length + misclassified.Length
            + escapes.Where(e => !expiries.Concat(relapses).Any(q => q.IncidentFingerprint == e.Incident)
                && !misclassified.Any(d => d.Log.IncidentFingerprint == e.Incident)).Sum(e => e.Value);
        sources["escapes"] = expiries.Concat(relapses).Select(e => "quarantine:" + e.Id)
            .Concat(misclassified.Select(d => "guardian-decision:" + d.Log.DecisionId)).Concat(escapes.Select(e => e.Source)).Distinct().ToArray();
        notes["escapes"] = "Unresolved expiries, 7-day relapses and regressions first classified flaky or environment. Explicit evidence supplements retained receipts without repeating the same incident.";
        var isolation = new List<double>();
        var isolationSources = new List<string>();
        foreach (var incident in incidents)
        {
            var log = quarantine.IncidentLog.Where(l => l.IncidentFingerprint == incident.Fingerprint && l.Test == incident.TestId
                && l.AtUtc >= incident.FirstRedAtUtc && l.AtUtc <= now
                && l.Actions.Any(a => a is "StartHeld" or "StartReportOnly" or "LineStop")).MinBy(l => l.AtUtc);
            if (log is null) continue;
            isolation.Add((log.AtUtc - incident.FirstRedAtUtc).TotalMinutes);
            isolationSources.Add("incident-log:" + log.DecisionId);
        }
        var p50 = GuardianReviewProjection.Percentile(isolation, .5);
        var p95 = GuardianReviewProjection.Percentile(isolation, .95);
        values["isolation-minutes"] = isolation.Count == count ? p50 : null;
        sources["isolation-minutes"] = isolationSources;
        notes["isolation-minutes"] = $"p50 {p50?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} min / p95 {p95?.ToString("0.##", CultureInfo.InvariantCulture) ?? "unavailable"} min; {isolation.Count}/{count} terminal red receipts isolated. Score uses p50.";
        // Intersect intervals with the week and union line stops to avoid double-counting simultaneous incidents.
        var stops = (post.Incidents ?? []).Where(i => i.LineStopReason is not null)
            .Select(i => (From: i.FirstRedAtUtc < start ? start : i.FirstRedAtUtc,
                To: i.ResolvedAtUtc is { } at && at < observedTo ? at : observedTo)).Where(i => i.To > i.From).ToArray();
        var unrelated = annotations.Where(e => e.Kind == "unrelated-refusal" && e.End is not null).ToArray();
        values["unusable-minutes"] = UnionMinutes(stops) + UnionMinutes(unrelated.Select(e => (e.At < start ? start : e.At,
            e.End!.Value < observedTo ? e.End.Value : observedTo)));
        sources["unusable-minutes"] = (post.Incidents ?? []).Where(i => i.LineStopReason is not null)
            .Select(i => "incident:" + i.Fingerprint).Concat(unrelated.Select(e => e.Source)).ToArray();
        notes["unusable-minutes"] = "Line-stop minutes plus outside-held-area refusal minutes, clipped to the week; simultaneous intervals within each category count once. Refusals need explicit path evidence.";
        var waits = annotations.Where(e => e.Kind == "held-wait" && e.End is not null && e.At < end && e.End > start).ToArray();
        values["held-wait"] = waits.Length == 0 || unknownRefusals ? null : waits.GroupBy(e => e.Card ?? e.Incident).Sum(group =>
            UnionMinutes(group.Select(e => (e.At > start ? e.At : start, e.End!.Value < observedTo ? e.End.Value : observedTo))));
        sources["held-wait"] = waits.Select(e => e.Source).ToArray();
        notes["held-wait"] = "Card-minutes from integration refusal to release, clipped to the week. Missing duration or unknown path scope means unavailable, not zero.";
        var repairCards = (post.Incidents ?? []).Select(i => i.RepairCardId).Distinct().ToHashSet(StringComparer.Ordinal);
        var steps = new List<(string Source, DateTimeOffset At, double Tokens, double CoreMinutes)>();
        var missingRepair = repairCards.ToHashSet(StringComparer.Ordinal);
        if (scanner is not null && executions is not null)
        {
            foreach (var card in cards)
            {
                if (!repairCards.Contains(card.Id) && !repairCards.Contains(card.TaskKey) && !repairCards.Contains(card.Key ?? "")) continue;
                var execution = executions.Read(card.FolderPath);
                if (execution is null) continue;
                missingRepair.Remove(card.Id); missingRepair.Remove(card.TaskKey); missingRepair.Remove(card.Key ?? "");
                foreach (var attempt in new[] { execution }.Concat(execution.PreviousAttempts))
                foreach (var step in attempt.Steps)
                {
                    if (step.CompletedAt is { } completed)
                        steps.Add(($"repair-receipt:{card.TaskKey}:{attempt.Attempt}:{step.StepId}", Utc(completed),
                            step.InputTokens + step.OutputTokens + (step.InputIncludesCached == true ? 0 : step.CacheReadTokens + step.CacheCreationTokens),
                            Math.Max(0, step.DurationMs) * Math.Max(1, step.GrantedCores ?? 1) / 60000d));
                    foreach (var earlier in step.EarlierRuns ?? [])
                        if (earlier.CompletedAt is { } prior)
                            steps.Add(($"repair-receipt:{card.TaskKey}:{attempt.Attempt}:{step.StepId}:{prior:O}", Utc(prior),
                                earlier.InputTokens + earlier.OutputTokens + (earlier.InputIncludesCached == true ? 0 : earlier.CacheReadTokens + earlier.CacheCreationTokens),
                                Math.Max(0, earlier.DurationMs) * Math.Max(1, earlier.GrantedCores ?? 1) / 60000d));
                }
            }
        }

        (double Tokens, double Cores, int Count, string[] Sources, bool Complete) Cost(DateTimeOffset from, DateTimeOffset to)
        {
            var receipts = state.Receipts.Where(e => e.At >= from && e.At < to && e.At <= now)
                .GroupBy(e => e.RunId).Select(g => g.MaxBy(e => e.CoreMinutes)!).ToArray();
            var models = state.Models.Where(e => e.At >= from && e.At < to && e.At <= now).ToArray();
            var repairs = steps.Where(e => e.At >= from && e.At < to && e.At <= now).ToArray();
            var n = (post.Incidents ?? []).Count(i => i.FirstRedAtUtc >= from && i.FirstRedAtUtc < to);
            return (models.Sum(e => e.TokensUsed ?? 0) + repairs.Sum(e => e.Tokens),
                receipts.Sum(e => e.CoreMinutes) + repairs.Sum(e => e.CoreMinutes), n,
                receipts.Select(e => "receipt:" + e.RunId).Concat(models.Select(e => "classification:" + e.Incident))
                    .Concat(repairs.Select(e => e.Source)).ToArray(), missingRepair.Count == 0 && receipts.Length > 0 && models.All(e => e.TokensUsed is not null));
        }
        var currentCost = Cost(start, end); var previousCost = Cost(start.AddDays(-7), start);
        double? Ratio(double current, double previous) => previous > 0 ? current / previous : current == 0 ? 1 : null;
        var tokensRatio = currentCost.Count > 0 && previousCost.Count > 0
            ? Ratio(currentCost.Tokens / currentCost.Count, previousCost.Tokens / previousCost.Count) : null;
        var coresRatio = currentCost.Count > 0 && previousCost.Count > 0
            ? Ratio(currentCost.Cores / currentCost.Count, previousCost.Cores / previousCost.Count) : null;
        values["repair-cost"] = currentCost.Complete && previousCost.Complete && tokensRatio is not null && coresRatio is not null
            ? (tokensRatio + coresRatio) / 2 : null;
        sources["repair-cost"] = currentCost.Sources.Concat(previousCost.Sources).ToArray();
        notes["repair-cost"] = $"This week: {currentCost.Tokens:0.##} tokens, {currentCost.Cores:0.##} core-min / {currentCost.Count} incidents. Previous week: {previousCost.Tokens:0.##} tokens, {previousCost.Cores:0.##} core-min / {previousCost.Count} incidents. Score input is the mean of per-incident token and compute ratios. Legacy compute uses one core. {missingRepair.Count} repair cards lack receipts.";
        var resolvedOutcomes = state.Decisions.Where(d => d.Shadow).Select(d =>
        {
            var incident = (post.Incidents ?? []).Where(i => i.Fingerprint == d.Log.IncidentFingerprint
                && i.TestId == d.Log.Test && i.FirstRedAtUtc <= d.Log.AtUtc && i.ResolvedAtUtc is not null)
                .OrderByDescending(i => i.FirstRedAtUtc).FirstOrDefault();
            if (incident?.ResolvedAtUtc is not { } resolved || resolved > now) return null;
            var last = quarantine.IncidentLog.Where(l => l.Test == d.Log.Test && l.IncidentFingerprint == d.Log.IncidentFingerprint
                && l.AtUtc <= resolved).MaxBy(l => l.AtUtc);
            return new GuardianReviewEvidence("resolved:" + d.Log.DecisionId, incident.Fingerprint, resolved, "outcome",
                "incident:" + incident.Fingerprint + (last is null ? "" : ";incident-log:" + last.DecisionId),
                DecisionId: d.Log.DecisionId, Outcome: $"Resolved {resolved:O}; final applied classification {last?.Class ?? "unavailable"}.");
        }).OfType<GuardianReviewEvidence>().ToArray();
        state = state with { ReviewEvidence = state.ReviewEvidence.Concat(resolvedOutcomes).ToArray() };
        var applied = new GuardianMeasurements(values, sources);
        // Observed decisions can share measurements; a shadow alternative has no invented runtime outcome.
        var same = state.Decisions.Where(d => InWeek(d.Log.AtUtc)).ToArray();
        var cohortCovered = incidents.Length > 0 && incidents.All(incident => same.Any(d =>
            d.Log.Test == incident.TestId && d.Log.IncidentFingerprint == incident.Fingerprint));
        var shadowValues = values.ToDictionary(pair => pair.Key, pair => !cohortCovered || same.Any(d =>
            GuardianReviewProjection.Signature(d.Guardian) != GuardianReviewProjection.Signature(d.Applied)) ? (double?)null : pair.Value);
        return GuardianReviewProjection.Build(project, start, state, applied, new(shadowValues, sources), now,
            [.. values["repair-cost"] is null ? new[] { "Complete repair receipts and previous-week cost comparison" } : Array.Empty<string>(),
                .. !cohortCovered ? new[] { "Guardian decisions for every incident in the common cohort" }
                    : same.Any(d => GuardianReviewProjection.Signature(d.Guardian) != GuardianReviewProjection.Signature(d.Applied))
                        ? new[] { "Counterfactual shadow runtime outcomes" } : Array.Empty<string>(),
                .. waits.Length == 0 || unknownRefusals ? new[] { "Integration refusal duration or scope receipts" } : Array.Empty<string>()], notes);
    }
    private static DateTimeOffset Utc(DateTime at) => new(DateTime.SpecifyKind(at, DateTimeKind.Utc));
    internal static double UnionMinutes(IEnumerable<(DateTimeOffset From, DateTimeOffset To)> intervals)
    {
        DateTimeOffset? left = null, right = null;
        double total = 0;
        foreach (var span in intervals.Where(i => i.To > i.From).OrderBy(i => i.From))
        {
            if (right is null || span.From > right)
            {
                if (left is not null) total += (right!.Value - left.Value).TotalMinutes;
                left = span.From; right = span.To;
            }
            else if (span.To > right) right = span.To;
        }
        return total + (left is null ? 0 : (right!.Value - left.Value).TotalMinutes);
    }
}
