using System.Globalization;
using System.Text.Json.Serialization;

namespace AgentStudio.Projects;

/// <summary>One non-overlapping part of a card's latest completion episode.</summary>
public sealed record FlowSegment(
    string Class, string? Step, string? Resource, string? Kind,
    DateTime Start, DateTime End, double Seconds,
    string? AttemptId = null, string? Executor = null)
{
    public long Tokens { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    public decimal CostUsd { get; init; }
    public int Calls { get; init; }
    public int UnpricedCalls { get; init; }
}

public sealed record FlowEpisode(
    DateTime Start, DateTime End, DateTime? MachineDoneAt,
    IReadOnlyList<FlowSegment> Segments,
    int ReviewAttempts, int ReviewAttemptsWithoutVerdict,
    IReadOnlyList<string> DataGaps)
{
    [JsonIgnore] public double? ActiveSnapshot { get; init; }
    [JsonIgnore] public double? ResourceSnapshot { get; init; }
    [JsonIgnore] public double? SystemSnapshot { get; init; }
    [JsonIgnore] public double? HumanSnapshot { get; init; }
    [JsonIgnore] public double? DependencySnapshot { get; init; }
    [JsonIgnore] public string? LargestResourceSnapshot { get; init; }
    [JsonIgnore] public long? TokensSnapshot { get; init; }
    [JsonIgnore] public decimal? CostSnapshot { get; init; }
    [JsonIgnore] public int? CallsSnapshot { get; init; }
    [JsonIgnore] public int? UnpricedSnapshot { get; init; }
    public double EpisodeSeconds => (End - Start).TotalSeconds;
    public double ActiveSeconds => ActiveSnapshot ?? Sum("active");
    public double ResourceWaitSeconds => ResourceSnapshot ?? Sum("resourceWait");
    public double SystemHoldSeconds => SystemSnapshot ?? Sum("systemHold");
    public double HumanWaitSeconds => HumanSnapshot ?? Sum("humanWait");
    public double DependencyWaitSeconds => DependencySnapshot ?? Sum("dependencyWait");
    public double MachineCycleSeconds => ActiveSeconds + ResourceWaitSeconds;
    public string? LargestResource => LargestResourceSnapshot ?? Segments.Where(s => s.Class == "resourceWait")
        .GroupBy(s => s.Resource).OrderByDescending(g => g.Sum(s => s.Seconds)).FirstOrDefault()?.Key;
    public long Tokens => TokensSnapshot ?? Segments.Sum(s => s.Tokens);
    public decimal CostUsd => CostSnapshot ?? Segments.Sum(s => s.CostUsd);
    public int Calls => CallsSnapshot ?? Segments.Sum(s => s.Calls);
    public int UnpricedCalls => UnpricedSnapshot ?? Segments.Sum(s => s.UnpricedCalls);
    private double Sum(string kind) => Segments.Where(s => s.Class == kind).Sum(s => s.Seconds);

    public FlowEpisode WithoutSegments() => this with
    {
        ActiveSnapshot = ActiveSeconds,
        ResourceSnapshot = ResourceWaitSeconds,
        SystemSnapshot = SystemHoldSeconds,
        HumanSnapshot = HumanWaitSeconds,
        DependencySnapshot = DependencyWaitSeconds,
        LargestResourceSnapshot = LargestResource,
        TokensSnapshot = Tokens,
        CostSnapshot = CostUsd,
        CallsSnapshot = Calls,
        UnpricedSnapshot = UnpricedCalls,
        Segments = [],
    };
}

/// <summary>
/// Builds a partition from lane stays and bounded evidence spans. Higher priority
/// spans win at every timestamp; the sweep guarantees that no second is counted twice.
/// </summary>
public static class FlowEpisodeClassifier
{
    private sealed record Span(DateTime Start, DateTime End, string Class,
        string? Step = null, string? Resource = null, string? Kind = null,
        string? AttemptId = null, string? Executor = null, int Priority = 0);

    public static FlowEpisode? Build(TaskInfo task, IReadOnlyList<TimelineEvent> ledger,
        PipelineExecutionRecord? pipeline, DateTime createdAt, DateTime completedAt,
        IReadOnlyList<FlowSegment>? gateEvidence = null,
        IReadOnlyList<ReviewAttemptDto>? reviewAuthority = null)
    {
        var changes = ledger.Where(e => e.Kind == TimelineEventKinds.LaneChanged && e.Ts <= completedAt)
            .OrderBy(e => e.Ts).ToList();
        var priorCompletion = changes.Where(e => To(e) == TaskStates.Completed && e.Ts < completedAt)
            .Select(e => e.Ts).LastOrDefault();
        var lower = priorCompletion == default ? createdAt : priorCompletion;
        var first = changes.FirstOrDefault(e => e.Ts > lower && IsEpisodeLane(To(e)));
        var initialLane = Detail(ledger.FirstOrDefault(e => e.Kind == TimelineEventKinds.PromptCreated), "targetState")
            ?? changes.FirstOrDefault()?.Details?.GetValueOrDefault("from") ?? TaskStates.Backlog;
        DateTime? start = priorCompletion == default && IsEpisodeLane(initialLane) ? createdAt : first?.Ts;
        if (start is null || start > completedAt) return null;

        var spans = new List<Span>();
        var reviewStays = new List<(DateTime Start, DateTime End, TimelineEvent? Exit)>();
        var gaps = new List<string>();
        var cursor = createdAt;
        var lane = initialLane;
        foreach (var change in changes)
        {
            AddLane(cursor, change.Ts, lane, change);
            cursor = change.Ts;
            lane = To(change) ?? lane;
        }
        AddLane(cursor, completedAt, lane, changes.LastOrDefault());

        // Explicit active and hold evidence can occur in human lanes as well.
        var reviewClaims = ledger.Where(e => e.Kind == TimelineEventKinds.ReviewAttemptClaimed
            && e.Ts >= start && e.Ts < completedAt).ToList();
        var attemptIds = new HashSet<string>(StringComparer.Ordinal);
        var withoutVerdict = 0;
        foreach (var claim in reviewClaims)
        {
            var id = Detail(claim, "attemptId") ?? claim.RunId;
            if (id is null) continue;
            attemptIds.Add(id);
            var rows = ledger.Where(e => e.Ts >= claim.Ts && e.Ts < completedAt
                && (Detail(e, "attemptId") == id || e.RunId == id)
                && (e.Kind == TimelineEventKinds.PostStepFinished
                    || e.Kind == TimelineEventKinds.ReviewInfrastructureRetryScheduled
                    || e.Kind == TimelineEventKinds.ReviewAttemptSuperseded)).ToList();
            var last = rows.LastOrDefault();
            var end = last?.Ts ?? claim.Ts;
            var verdict = rows.Any(e => e.Kind == TimelineEventKinds.PostStepFinished
                && (Detail(e, "pipelineStepId") ?? "").StartsWith("aspect-", StringComparison.Ordinal));
            var exitAfterClaim = changes.FirstOrDefault(e => e.Ts > claim.Ts);
            var nextClaim = reviewClaims.FirstOrDefault(e => e.Ts > claim.Ts);
            if (!verdict && exitAfterClaim is not null
                && (nextClaim is null || exitAfterClaim.Ts < nextClaim.Ts)
                && Detail(exitAfterClaim, "cause") is "review-verdict" or "gate-failure" or "quality-loop")
                verdict = true;
            if (!verdict) withoutVerdict++;
            var executor = Detail(claim, "executorId") ?? Detail(claim, "executor");
            Add(claim.Ts, end, "active", verdict ? "reviewOther" : "reviewFailedAttempt",
                attemptId: id, executor: executor, priority: 65);
        }

        foreach (var attempt in reviewAuthority ?? [])
        {
            if (attempt.CreatedAt >= start && attempt.CreatedAt < completedAt)
            {
                if (attemptIds.Add(attempt.AttemptId)
                    && !ledger.Any(e => e.Kind == TimelineEventKinds.PostStepFinished
                        && (Detail(e, "attemptId") == attempt.AttemptId || e.RunId == attempt.AttemptId)
                        && (Detail(e, "pipelineStepId") ?? "").StartsWith("aspect-", StringComparison.Ordinal))
                    && !changes.Any(e => e.Ts >= attempt.CreatedAt
                        && e.Ts <= (attempt.TerminalAt ?? completedAt)
                        && (Detail(e, "cause") is "review-verdict" or "gate-failure" or "quality-loop")))
                    withoutVerdict++;
            }
            if (attempt.FailureClassification == "LeaseExpired"
                && attempt.Lease?.LastHeartbeat is DateTime heartbeat
                && attempt.TerminalAt is DateTime terminal)
                Add(heartbeat, terminal, "systemHold", kind: "leaseExpiredTail",
                    attemptId: attempt.AttemptId, priority: 75);
        }

        // A projected post step's duration is authoritative when its start row
        // is absent. This also handles zero-length step markers safely.
        foreach (var finish in ledger.Where(e => e.Kind == TimelineEventKinds.PostStepFinished
                     && e.Ts >= start && e.Ts <= completedAt))
        {
            var stepId = Detail(finish, "pipelineStepId") ?? "";
            var attempt = Detail(finish, "attemptId") ?? finish.RunId;
            var matchingStart = ledger.LastOrDefault(e => e.Kind == TimelineEventKinds.PostStepStarted
                && e.Ts <= finish.Ts && Detail(e, "stepId") == Detail(finish, "stepId")
                && Detail(e, "attemptId") == Detail(finish, "attemptId"));
            var ms = ParseMs(Detail(finish, "durationMs"));
            var from = matchingStart?.Ts ?? finish.Ts.AddMilliseconds(-ms);
            var reviewClaim = reviewClaims.FirstOrDefault(e =>
                (Detail(e, "attemptId") ?? e.RunId) == attempt);
            var executor = Detail(reviewClaim, "executorId") ?? Detail(reviewClaim, "executor");
            Add(from, finish.Ts, "active", ReviewStep(stepId), attemptId: attempt,
                executor: executor, priority: 110);
            if (attempt is not null) attemptIds.Add(attempt);
        }
        if (pipeline is not null)
        {
            foreach (var record in new[] { pipeline }.Concat(pipeline.PreviousAttempts))
            foreach (var step in record.Steps)
            {
                if (step.StartedAt is null || step.CompletedAt is null) continue;
                if (!step.StepId.StartsWith("post-", StringComparison.Ordinal)
                    && !step.StepId.StartsWith("aspect-", StringComparison.Ordinal)) continue;
                // Ledger evidence wins for this step when present.
                if (ledger.Any(e => e.Kind == TimelineEventKinds.PostStepFinished
                    && Detail(e, "pipelineStepId") == step.StepId
                    && Math.Abs((e.Ts - step.CompletedAt.Value).TotalSeconds) < 2)) continue;
                Add(step.StartedAt.Value, step.CompletedAt.Value, "active", ReviewStep(step.StepId), priority: 105);
            }
        }

        foreach (var retry in ledger.Where(e => e.Kind == TimelineEventKinds.ReviewInfrastructureRetryScheduled
                     && e.Ts >= start && e.Ts < completedAt))
            if (DateTime.TryParse(Detail(retry, "dueAtUtc"), CultureInfo.InvariantCulture,
                    DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out var due))
                Add(retry.Ts, due, "systemHold", kind: "infraRetryBackoff", priority: 70);

        if (gateEvidence is not null)
            foreach (var gate in gateEvidence)
                Add(gate.Start, gate.End, gate.Class, gate.Step, gate.Resource, gate.Kind,
                    gate.AttemptId, gate.Executor, gate.Class == "active" ? 108
                        : gate.Class == "systemHold" ? 75 : 60);

        foreach (var waiting in ledger.Where(e => e.Kind == TimelineEventKinds.CauseBreakerWaiting
                     && e.Ts >= start && e.Ts < completedAt))
        {
            var release = ledger.FirstOrDefault(e => e.Ts > waiting.Ts &&
                (e.Kind is TimelineEventKinds.CauseBreakerReleased or TimelineEventKinds.ReviewAttemptClaimed
                    or TimelineEventKinds.LaneChanged));
            Add(waiting.Ts, release?.Ts ?? completedAt, "systemHold", kind: "causeBreaker", priority: 75);
        }

        var integrationStarts = ledger.Where(e => e.Kind == TimelineEventKinds.IntegrationStarted).ToList();
        foreach (var outcome in ledger.Where(e => e.Kind is TimelineEventKinds.IntegrationSucceeded
                     or TimelineEventKinds.IntegrationFailed or TimelineEventKinds.IntegrationOverridden))
        {
            var from = integrationStarts.LastOrDefault(e => e.Ts <= outcome.Ts);
            if (from is null) continue;
            Add(from.Ts, outcome.Ts, "active", "integration", priority: 120);
            var reviewVerdict = ledger.LastOrDefault(e => e.Ts <= from.Ts && e.Ts >= start
                && e.Kind == TimelineEventKinds.PostStepFinished
                && (Detail(e, "pipelineStepId") ?? "").StartsWith("aspect-", StringComparison.Ordinal));
            if (reviewVerdict is not null)
                Add(reviewVerdict.Ts, from.Ts, "resourceWait", resource: "integrationLane", priority: 20);
        }

        // Once an attempt or integration has produced its final activity, the
        // remaining review-lane stay belongs to the actor that moves the card.
        foreach (var stay in reviewStays)
        {
            var exit = stay.Exit;
            var end = stay.End;
            var hasAttempt = ledger.Any(e => e.Ts >= stay.Start && e.Ts < end
                && e.Kind == TimelineEventKinds.ReviewAttemptClaimed)
                || (reviewAuthority ?? []).Any(a => a.CreatedAt >= stay.Start && a.CreatedAt < end);
            var firstAttempt = (reviewAuthority ?? []).Where(a => a.CreatedAt >= stay.Start
                && a.CreatedAt < end).OrderBy(a => a.CreatedAt).FirstOrDefault();
            if (firstAttempt is not null && firstAttempt.CreatedAt > stay.Start)
                Add(stay.Start, firstAttempt.CreatedAt, "systemHold", kind: "noReviewAttempt", priority: 75);
            var hasStep = ledger.Any(e => e.Ts >= stay.Start && e.Ts < end
                && e.Kind == TimelineEventKinds.PostStepFinished);
            var deferredWithoutPending = (reviewAuthority ?? []).Any(a => a.CreatedAt >= stay.Start
                && a.CreatedAt < end && a.Subject.Plan?.BuildTestDeferredToBatch == true
                && a.Outcome == ReviewTerminalOutcome.Pass)
                && !(gateEvidence ?? []).Any(e => e.Start >= stay.Start && e.Start < end
                    && (e.Resource == "gateHost"
                    || e.Step is "batchGate" or "batchGateNoVerdict"));
            if (deferredWithoutPending)
            {
                var lastStep = ledger.Where(e => e.Ts >= stay.Start && e.Ts < end
                    && e.Kind == TimelineEventKinds.PostStepFinished)
                    .Select(e => e.Ts).LastOrDefault();
                Add(lastStep == default ? stay.Start : lastStep, end,
                    "systemHold", kind: "batchDefect", priority: 75);
            }
            if (hasStep && !hasAttempt && !gaps.Contains("review-claim-unknown"))
                gaps.Add("review-claim-unknown");
            if (!hasStep && !hasAttempt && !TimelineActors.IsHuman(exit?.Actor))
                Add(stay.Start, end, "systemHold", kind: "noReviewAttempt", priority: 12);
            var lastActivity = ledger.Where(e => e.Ts >= stay.Start && e.Ts < end &&
                (e.Kind == TimelineEventKinds.PostStepFinished
                    && (Detail(e, "pipelineStepId") ?? "").StartsWith("aspect-", StringComparison.Ordinal)
                 || e.Kind is TimelineEventKinds.IntegrationSucceeded or TimelineEventKinds.IntegrationFailed
                     or TimelineEventKinds.IntegrationOverridden)).Select(e => e.Ts)
                .Concat((gateEvidence ?? []).Where(e => e.Step == "batchGate"
                    && e.End >= stay.Start && e.End <= end).Select(e => e.End))
                .DefaultIfEmpty().Max();
            if (lastActivity != default)
                Add(lastActivity, end, TimelineActors.IsHuman(exit?.Actor) ? "humanWait" : "systemHold",
                    kind: TimelineActors.IsHuman(exit?.Actor) ? "operatorDecision" : "machineTail", priority: 10);
        }

        var points = spans.SelectMany(s => new[] { s.Start, s.End }).Append(start.Value).Append(completedAt)
            .Where(t => t >= start && t <= completedAt).Distinct().OrderBy(t => t).ToList();
        var segments = new List<FlowSegment>();
        for (var i = 0; i + 1 < points.Count; i++)
        {
            var a = points[i]; var b = points[i + 1];
            if (b <= a) continue;
            var winner = spans.Where(s => s.Start <= a && s.End >= b)
                .OrderByDescending(s => s.Priority).FirstOrDefault();
            winner ??= new Span(a, b, "systemHold", Kind: "unclassified");
            if (winner.Kind == "unclassified" && !gaps.Contains("wait-unclassified")) gaps.Add("wait-unclassified");
            if (segments.Count > 0 && Same(segments[^1], winner) && segments[^1].End == a)
            {
                var old = segments[^1];
                segments[^1] = old with { End = b, Seconds = (b - old.Start).TotalSeconds };
            }
            else segments.Add(new FlowSegment(winner.Class, winner.Step, winner.Resource, winner.Kind,
                a, b, (b - a).TotalSeconds, winner.AttemptId, winner.Executor));
        }
        var done = segments.Where(s => s.Class is "active" or "resourceWait" or "systemHold")
            .Select(s => (DateTime?)s.End).LastOrDefault();
        return new FlowEpisode(start.Value, completedAt, done, segments, attemptIds.Count, withoutVerdict, gaps);

        void AddLane(DateTime from, DateTime to, string laneName, TimelineEvent? exit)
        {
            if (to <= start || from >= completedAt || to <= from) return;
            if (laneName == TaskStates.Ready)
                Add(from, to, "resourceWait", resource: "codingSlot", executor: exit?.Actor);
            else if (laneName is TaskStates.Progress or TaskStates.FailedPickup or TaskStates.CodeNotComplete)
                Add(from, to, "active", (Detail(exit, "cause") is "delivered" or "escalated")
                    || (exit is not null && To(exit) is TaskStates.AutoReview or TaskStates.Escalated)
                    ? "coding" : "codingNoDelivery",
                    attemptId: Detail(exit, "attemptId") ?? exit?.RunId, executor: exit?.Actor);
            else if (laneName == TaskStates.AutoReview)
            {
                Add(from, to, "resourceWait", resource: "reviewSlot");
                reviewStays.Add((from, to, exit));
            }
            else if (laneName == TaskStates.HumanReview)
                Add(from, to, "humanWait", kind: "humanReview");
            else if (laneName == TaskStates.Escalated)
                Add(from, to, "humanWait", kind: "escalated");
            else if (laneName == TaskStates.OrchestratorPrep)
                Add(from, to, "active", "orchestratorPrep");
            else if (laneName is TaskStates.Backlog or TaskStates.Preparation)
                Add(from, to, "humanWait", kind: "backlog");
            else Add(from, to, "systemHold", kind: "unclassified");
        }
        void Add(DateTime from, DateTime to, string @class, string? step = null,
            string? resource = null, string? kind = null, string? attemptId = null,
            string? executor = null, int priority = 0)
        {
            if (start is null) return;
            var a = from < start ? start.Value : from;
            var b = to > completedAt ? completedAt : to;
            if (b > a) spans.Add(new Span(a, b, @class, step, resource, kind, attemptId, executor, priority));
        }
    }

    private static bool IsEpisodeLane(string? lane) => lane is TaskStates.Ready or TaskStates.Progress
        or TaskStates.FailedPickup or TaskStates.CodeNotComplete or TaskStates.AutoReview;
    private static string? To(TimelineEvent row) => Detail(row, "to");
    private static string? Detail(TimelineEvent? row, string key) => row?.Details?.GetValueOrDefault(key);
    private static long ParseMs(string? raw) => long.TryParse(raw, CultureInfo.InvariantCulture, out var ms) ? ms : 0;
    private static string ReviewStep(string id) => id == PipelineCatalogue.BuildTestGateStepId
        ? "reviewGate" : id.StartsWith("aspect-", StringComparison.Ordinal) ? "reviewAspects" : "reviewOther";
    private static bool Same(FlowSegment segment, Span span) => segment.Class == span.Class
        && segment.Step == span.Step && segment.Resource == span.Resource && segment.Kind == span.Kind
        && segment.AttemptId == span.AttemptId && segment.Executor == span.Executor;
}
