using System.Globalization;
using AgentStudio.Pipeline;
using AgentStudio.Runner;
using AgentStudio.Review;
using AgentStudio.Shared;
using AgentStudio.Watcher;

namespace AgentStudio.Operator;

/// <summary>
/// Pure projections from the records Studio already keeps to decision facts.
/// Each projection reads a state another feature decided; it adds no
/// threshold of its own. Stall kinds come from the watcher's
/// <see cref="OperationalStallDetector"/>, the push age alert from
/// <see cref="BatchIntegrationReservationService"/>, and lanes, decision
/// cards, quota waits and fallbacks from the card records.
/// </summary>
public static class OperatorDecisionFacts
{
    /// <summary>Watcher stall kinds that mean the execution backend stopped making progress.</summary>
    public static readonly IReadOnlySet<string> BackendStallKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "host-offline", "host-overload", "memory-pressure", "deploy-drift", "claim-refusal", "batch-gate-stuck",
    };

    /// <summary>Watcher stall kinds that mean capacity is idle while cards wait.</summary>
    public static readonly IReadOnlySet<string> IdleWithReadyKinds = new HashSet<string>(StringComparer.Ordinal)
    {
        "idle-capacity", "fleet-claim-stall",
    };

    private static readonly IReadOnlySet<BatchPhase> RunningPhases = new HashSet<BatchPhase>
    {
        BatchPhase.Formed, BatchPhase.Assembling, BatchPhase.Assembled, BatchPhase.Running,
    };

    public static IReadOnlyList<OperatorDecisionFact> FromTasks(
        IEnumerable<TaskInfo> tasks, Func<TaskInfo, QuotaFallbackStatus?> fallbackOf)
    {
        var facts = new List<OperatorDecisionFact>();
        foreach (var task in tasks)
        {
            var card = CardKey(task);
            var evidence = new[] { new OperatorEvidenceLink("card", TaskHref(task)) };
            if (task.State == TaskStates.Escalated)
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.EscalationOpened,
                    Key(OperatorDecisionTypes.EscalationOpened, task.ProjectName, card, Stamp(task.EnteredLaneAt)),
                    task.ProjectName, card,
                    FirstText(task.ParkedBlocker?.Reason, task.ParkedBlocker?.ConditionDescription)
                        ?? $"{card} entered the escalated lane.",
                    Utc(task.EnteredLaneAt), evidence, Title: task.Title));
            if (task.State == TaskStates.HumanReview)
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.HumanReviewReady,
                    Key(OperatorDecisionTypes.HumanReviewReady, task.ProjectName, card, Stamp(task.EnteredLaneAt)),
                    task.ProjectName, card, ReviewReason(task),
                    Utc(task.EnteredLaneAt), evidence, Title: task.Title));
            if (TaskKinds.IsDecision(task.Kind) && task.Decision is { } decision
                && DecisionStatuses.IsOpen(decision.Status)
                && task.State is not (TaskStates.Completed or TaskStates.Archive))
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.DecisionCardOpened,
                    Key(OperatorDecisionTypes.DecisionCardOpened, task.ProjectName, card,
                        decision.History.Count.ToString(CultureInfo.InvariantCulture)),
                    task.ProjectName, card,
                    FirstText(decision.Question) ?? $"Decision card {card} is open.",
                    Utc(task.CreatedAt), evidence, Title: task.Title));
            if (task.QuotaWait is { } wait)
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.QuotaExhausted,
                    Key(OperatorDecisionTypes.QuotaExhausted, "card", task.ProjectName, card, Stamp(wait.StartedAt)),
                    task.ProjectName, card,
                    $"{card} waits for the {wait.CliType} {wait.Scope} reset at {Stamp(wait.ResetAt)}: {wait.Reason}",
                    Utc(wait.StartedAt), [.. evidence, new OperatorEvidenceLink("quota", "/api/cli/quota")]));
            if (task.State == TaskStates.Progress && fallbackOf(task) is { } fallback)
                facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.QuotaFallbackActive,
                    Key(OperatorDecisionTypes.QuotaFallbackActive, task.ProjectName, card, fallback.CliType,
                        fallback.Model ?? "", fallback.ActivatedAt is { } at ? Stamp(at) : ""),
                    task.ProjectName, card,
                    $"{card} runs on the {fallback.CliType} fallback"
                        + (fallback.Model is { Length: > 0 } model ? $" ({model})" : "")
                        + (fallback.Reason is { Length: > 0 } reason ? $": {reason}" : "."),
                    fallback.ActivatedAt is { } activated ? Utc(activated) : Utc(task.EnteredLaneAt),
                    [.. evidence, new OperatorEvidenceLink("quota", "/api/cli/quota")]));
        }
        return facts;
    }

    public static IReadOnlyList<OperatorDecisionFact> FromWatcherCases(IEnumerable<WatcherCase> cases)
    {
        var facts = new List<OperatorDecisionFact>();
        foreach (var item in cases)
        {
            if (item.Subject is null || item.State is not (WatcherCaseStates.Open or WatcherCaseStates.DecisionRequired))
                continue;
            var kind = item.LastDetails.GetValueOrDefault("stallKind");
            var type = kind is null ? null
                : BackendStallKinds.Contains(kind) ? OperatorDecisionTypes.BackendStalled
                : IdleWithReadyKinds.Contains(kind) ? OperatorDecisionTypes.ProgressIdleWithReady
                : null;
            if (type is null) continue;
            var since = item.SinceUtc ?? item.FirstSeenUtc;
            var project = item.Project == QuotaProbeSignalProbe.WorkspaceProject ? null : item.Project;
            var reason = string.IsNullOrWhiteSpace(item.SuggestedAction)
                ? item.LastSummary : $"{item.LastSummary} {item.SuggestedAction}";
            facts.Add(new OperatorDecisionFact(type, Key(type, item.Fingerprint, Stamp(since)), project,
                item.AffectedCards.Count == 1 ? item.AffectedCards[0] : null,
                FirstText(reason) ?? $"Watcher case {item.Id} ({kind}) is open.", Utc(since),
                [
                    new OperatorEvidenceLink("stalls", "/api/watcher/stalls"),
                    new OperatorEvidenceLink("interventions",
                        "/api/watcher/interventions?subject=" + Uri.EscapeDataString(item.Subject)),
                ],
                item.AffectedCards));
        }
        return facts;
    }

    /// <summary>
    /// A batch whose latest phase is red, and a red batch whose diagnostic
    /// children (manifests naming it as parent) are still forming or running.
    /// </summary>
    public static IReadOnlyList<OperatorDecisionFact> FromBatches(
        IReadOnlyList<(BatchGateManifest Manifest, BatchGateState? State)> batches)
    {
        var facts = new List<OperatorDecisionFact>();
        var children = batches.Where(item => item.Manifest.ParentBatchId is not null)
            .ToLookup(item => item.Manifest.ParentBatchId!, StringComparer.Ordinal);
        foreach (var (manifest, state) in batches)
        {
            if (state is not { Phase: BatchPhase.Red }) continue;
            var project = manifest.Scope.Project;
            var cards = manifest.Members.Select(member => member.TaskKey).ToArray();
            var evidence = new[]
            {
                new OperatorEvidenceLink("batch-gate-state", ProjectHref(project, "batch-gate/state")),
                new OperatorEvidenceLink("batch-gate-report", ProjectHref(project, "batch-gate/report")),
            };
            facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.BatchRed,
                Key(OperatorDecisionTypes.BatchRed, manifest.BatchId, Stamp(state.RecordedAtUtc.UtcDateTime)),
                project, cards.Length == 1 ? cards[0] : null,
                $"Batch {manifest.BatchId} is red: {FirstText(state.Reason) ?? "gate verdict red"}.",
                state.RecordedAtUtc.UtcDateTime, evidence, cards));
            var running = children[manifest.BatchId]
                .Where(child => child.State is null || RunningPhases.Contains(child.State.Phase)).ToArray();
            if (running.Length == 0) continue;
            facts.Add(new OperatorDecisionFact(OperatorDecisionTypes.BatchIsolating,
                Key(OperatorDecisionTypes.BatchIsolating, manifest.BatchId, Stamp(state.RecordedAtUtc.UtcDateTime)),
                project, cards.Length == 1 ? cards[0] : null,
                $"Batch {manifest.BatchId} isolates its red members in {running.Length} diagnostic "
                    + (running.Length == 1 ? "batch." : "batches."),
                running.Min(child => child.Manifest.ClosedAtUtc).UtcDateTime, evidence, cards));
        }
        return facts;
    }

    /// <summary>Pending integration pushes past the batch gate's own age alert.</summary>
    public static IReadOnlyList<OperatorDecisionFact> FromPendingPushes(string project,
        IEnumerable<PendingIntegrationPushWait> waits, Func<string, string?> cardKeyOf)
        => waits.Where(wait => wait.AgeAlert).Select(wait =>
        {
            var card = cardKeyOf(wait.JobId) ?? wait.JobId;
            return new OperatorDecisionFact(OperatorDecisionTypes.PushPendingOverdue,
                Key(OperatorDecisionTypes.PushPendingOverdue, project, wait.JobId, wait.ApprovedSha ?? "",
                    Stamp(wait.SinceUtc.UtcDateTime)),
                project, card,
                $"Push of {card} has been pending for {Math.Floor(wait.AgeMinutes).ToString(CultureInfo.InvariantCulture)} minutes: {wait.Reason}",
                wait.SinceUtc.UtcDateTime,
                [new OperatorEvidenceLink("batch-gate-state", ProjectHref(project, "batch-gate/state"))]);
        }).ToArray();

    /// <summary>
    /// Provider limits the runner recorded for a CLI; they apply to the whole
    /// installation. The registry replaces an entry when a later limit is
    /// observed, so the occurrence is the CLI until its limit clears.
    /// </summary>
    public static IReadOnlyList<OperatorDecisionFact> FromProviderLimits(IEnumerable<ProviderLimitStatus> limits)
        => limits.Select(limit => new OperatorDecisionFact(OperatorDecisionTypes.QuotaExhausted,
            Key(OperatorDecisionTypes.QuotaExhausted, "provider", limit.CliType.ToLowerInvariant()),
            null, null,
            $"{limit.CliType} reached its provider limit until {Stamp(limit.LimitedUntil)}: {limit.Reason}",
            Utc(limit.ObservedAt), [new OperatorEvidenceLink("quota", "/api/cli/quota")])).ToArray();

    public static string CardKey(TaskInfo task)
        => FirstText(task.Key, task.TaskKey) ?? task.Id;

    private static string ReviewReason(TaskInfo task)
    {
        if (task.OlderBriefDelivery is { Status: "pending" })
            return "Cannot be accepted: brief changed.";
        var grade = task.Tags.FirstOrDefault(tag => tag.StartsWith("code-review:grade-", StringComparison.OrdinalIgnoreCase));
        var letter = grade is null ? null : grade["code-review:grade-".Length..].ToUpperInvariant();
        if (letter is null) return "Ready for human review.";
        var concerns = GradeFindings(task.FolderPath);
        return concerns is { } count
            ? $"Grade {letter} · {count} {(count == 1 ? "concern" : "concerns")}."
            : $"Grade {letter} · review concerns unavailable.";
    }

    private static int? GradeFindings(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return null;
        try
        {
            if (!Directory.Exists(folder)) return null;
            var latest = Directory.EnumerateFiles(folder, "code-review-grade-*.md")
                .OrderByDescending(Path.GetFileName, StringComparer.Ordinal).FirstOrDefault();
            return latest is null ? null : CodeReviewFindingParsing.Parse(File.ReadAllText(latest)).Count;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static string TaskHref(TaskInfo task) => "/api/tasks/" + Uri.EscapeDataString(task.Id);

    private static string ProjectHref(string project, string suffix)
        => "/api/projects/" + Uri.EscapeDataString(project) + "/" + suffix;

    private static string Key(string type, params string[] parts) => type + "|" + string.Join("|", parts);

    private static string Stamp(DateTime value) => Utc(value).ToString("O", CultureInfo.InvariantCulture);

    private static DateTime Utc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Local => value.ToUniversalTime(),
        DateTimeKind.Unspecified => DateTime.SpecifyKind(value, DateTimeKind.Utc),
        _ => value,
    };

    private static string? FirstText(params string?[] values)
        => values.FirstOrDefault(value => !string.IsNullOrWhiteSpace(value))?.Trim();
}
