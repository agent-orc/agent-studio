namespace AgentStudio.Tasks;

/// <summary>
/// T2b (ASS-1740): the single read layer for one task. The principle is
/// "open task -&gt; read all raw data once -&gt; ONE in-memory representation"
/// from which the existing surfaces project. Before this, each view re-parsed
/// the same on-disk sources on its own - the run-list endpoint built the run
/// timeline from <c>session-events.jsonl</c> + <c>cli-output.log</c>, the
/// timeline endpoint read <c>timeline.jsonl</c> separately, and the per-run
/// endpoints rebuilt the run timeline a third time. This reader loads those
/// sources plus terminal RunAttempt history once into a <see cref="TaskReadModel"/> and the model owns the
/// projections, so a caller never stitches the parsers together by hand.
///
/// <para>
/// The reader holds no policy. Each projection on <see cref="TaskReadModel"/>
/// is the exact logic the corresponding endpoint ran inline before T2b, so
/// switching a view onto the reader keeps the projection policies inside their
/// pure builders. The unified representation joins the
/// <c>lane_changed</c> ledger rows back to the ASS-1724 commit-provenance
/// anchors recorded alongside them, so a single consumer sees the whole lane
/// crossing (von / nach / wann / ausloeser + branch-tip / work-branch-head)
/// without re-reading <c>task.json</c>.
/// </para>
/// </summary>
public sealed class TaskReader
{
    private readonly TaskScannerService _scanner;
    private readonly TaskSessionLog _sessions;
    private readonly TimelineLog _timeline;
    private readonly AttemptAuthorityService _attempts;

    public TaskReader(
        TaskScannerService scanner,
        TaskSessionLog sessions,
        TimelineLog timeline,
        AttemptAuthorityService attempts)
    {
        _scanner = scanner;
        _sessions = sessions;
        _timeline = timeline;
        _attempts = attempts;
    }

    /// <summary>
    /// Load every raw source for one task into a single <see cref="TaskReadModel"/>,
    /// or null when the task cannot be resolved. <paramref name="nowUtc"/> is
    /// threaded into the model so the still-running run tail stays deterministic
    /// in tests (the pure builders never read the wall clock themselves).
    /// </summary>
    public TaskReadModel? Read(string jobId, string? watchPath, DateTime? nowUtc = null)
    {
        var detail = _scanner.GetJobDetail(jobId, watchPath);
        if (detail == null) return null;

        var info = detail.Info;
        var sessionEvents = _sessions.ReadSessionEvents(jobId, watchPath);
        var cliOutputLines = CliOutputLogParser.ParseFile(TaskPaths.CliOutputLog(info.FolderPath));
        var ledger = _timeline.ReadAll(info.FolderPath);
        var runnerEvents = AgentStudio.Projection.RunnerEventSource.ReadRecords(info);
        var runAttempts = _attempts.GetTaskProjection(TaskIdentity.AuthorityKey(info), includeArchived: true).RunAttempts;

        return new TaskReadModel(
            detail,
            sessionEvents,
            cliOutputLines,
            ledger,
            nowUtc ?? DateTime.UtcNow,
            runnerEvents,
            runAttempts);
    }
}

/// <summary>
/// The single in-memory representation of one task's raw data (T2b / ASS-1740):
/// the scanned <see cref="TaskDetail"/>, the session-event log, the parsed
/// <c>cli-output.log</c> lines, terminal RunAttempts, and the unified timeline ledger - all loaded
/// once by <see cref="TaskReader"/>. The projection methods derive the existing
/// per-view shapes from these raw sources; the model carries no source of truth
/// of its own.
/// </summary>
public sealed class TaskReadModel
{
    public TaskDetail Detail { get; }
    public TaskInfo Info => Detail.Info;
    public IReadOnlyList<SessionEvent> SessionEvents { get; }
    public IReadOnlyList<CliOutputLine> CliOutputLines { get; }
    /// <summary>The raw <c>timeline.jsonl</c> rows, untouched. Use <see cref="BuildLedger"/> for the meshed projection.</summary>
    public IReadOnlyList<TimelineEvent> Ledger { get; }
    public DateTime NowUtc { get; }
    public IReadOnlyList<RunnerRecordedEvent> RunnerEvents { get; }
    public IReadOnlyList<RunAttemptDto> RunAttempts { get; }

    public TaskReadModel(
        TaskDetail detail,
        IReadOnlyList<SessionEvent> sessionEvents,
        IReadOnlyList<CliOutputLine> cliOutputLines,
        IReadOnlyList<TimelineEvent> ledger,
        DateTime nowUtc,
        IReadOnlyList<RunnerRecordedEvent>? runnerEvents = null,
        IReadOnlyList<RunAttemptDto>? runAttempts = null)
    {
        Detail = detail;
        SessionEvents = sessionEvents ?? [];
        CliOutputLines = cliOutputLines ?? [];
        Ledger = ledger ?? [];
        NowUtc = nowUtc;
        RunnerEvents = runnerEvents ?? [];
        RunAttempts = runAttempts ?? [];
    }

    /// <summary>
    /// Project the run timeline (the <c>/runs</c> surface): the per-CLI-invocation
    /// records plus the prompt entries folded onto them. Identical to the logic
    /// the <c>/runs</c> endpoint ran inline before T2b.
    /// </summary>
    public RunTimeline BuildRunTimeline()
    {
        var timeline = RunTimelineBuilder.Build(
            SessionEvents,
            CliOutputLines,
            NowUtc,
            new RunTimelineFallbackContext(
                RunAttempts,
                Ledger,
                string.Equals(Info.State, TaskStates.Progress, StringComparison.OrdinalIgnoreCase)));
        var reviewAttemptEpoch = OperatorReviewRequeueService.ReadEpoch(Info.FolderPath);
        return timeline with
        {
            RunnerEvents = RunnerEvents.ToList(),
            PromptEntries = RunPromptTimelineBuilder.Build(
                timeline.Runs,
                Info.FolderPath,
                Detail.PromptMarkdown,
                Detail.PromptHistory,
                Detail.ContextUsage),
            Refinements = TaskRefinementTimelineBuilder.Build(
                Info.FolderPath,
                timeline.Runs,
                Detail.PromptHistory),
            ReviewAttemptEpoch = reviewAttemptEpoch,
            ReviewAttemptCycles = ReviewAttemptTimelineBuilder.Build(
                reviewAttemptEpoch,
                Ledger,
                timeline.FirstStartedAt ?? Info.CreatedAt)
        };
    }

    /// <summary>
    /// Resolve the run at <paramref name="index"/> (1-based) from the projected
    /// run timeline, or null with a 404-friendly <paramref name="error"/>. Lets
    /// the per-run endpoints share the same lookup path the run list uses.
    /// </summary>
    public RunRecord? ResolveRun(int index, out string error)
    {
        error = "";
        var runs = BuildRunTimeline().Runs;
        if (index < 1 || index > runs.Count)
        {
            error = $"Run #{index} not in this job's timeline (have {runs.Count}).";
            return null;
        }
        return runs[index - 1];
    }

    /// <summary>
    /// Project the unified ledger (the <c>/timeline</c> surface), meshing each
    /// <c>lane_changed</c> row with the ASS-1724 commit-provenance anchor recorded
    /// alongside it. The raw row carries von / nach / wann / ausloeser; the
    /// branch-tip + work-branch-head live on the provenance transition in
    /// <c>task.json</c> and are deliberately NOT duplicated into the ledger
    /// (no double-bookkeeping). We join them at READ time - by target lane plus
    /// nearest timestamp, since a task can cross the same lane more than once -
    /// and enrich the event's <see cref="TimelineEvent.Details"/> with
    /// <c>branchTip</c> / <c>workBranchHead</c>. Every other event kind passes
    /// through untouched, so the projection is behaviour-identical for the
    /// surfaces that existed before the lane-change rows did.
    /// </summary>
    public List<TimelineEvent> BuildLedger()
    {
        var transitions = Info.Provenance?.Transitions;
        var hasAnchors = transitions is { Count: > 0 };

        var result = new List<TimelineEvent>(Ledger.Count);
        foreach (var evt in Ledger)
        {
            if (hasAnchors
                && string.Equals(evt.Kind, TimelineEventKinds.LaneChanged, StringComparison.Ordinal)
                && MatchAnchor(evt, transitions!) is { } anchor
                && (!string.IsNullOrWhiteSpace(anchor.BranchTip) || !string.IsNullOrWhiteSpace(anchor.WorkBranchHead)))
            {
                result.Add(MeshAnchor(evt, anchor));
            }
            else
            {
                result.Add(evt);
            }
        }
        return result;
    }

    /// <summary>
    /// Pick the provenance transition that records the same lane crossing as a
    /// <c>lane_changed</c> event: same target lane, closest wall-clock instant
    /// (the two are written in the same move flow, milliseconds apart). Returns
    /// null when the event has no <c>to</c> lane or no transition matches.
    /// </summary>
    private static TaskProvenanceTransition? MatchAnchor(
        TimelineEvent evt,
        IReadOnlyList<TaskProvenanceTransition> transitions)
    {
        var to = evt.Details != null && evt.Details.TryGetValue("to", out var t) ? t : null;
        if (string.IsNullOrWhiteSpace(to)) return null;

        TaskProvenanceTransition? best = null;
        var bestDelta = TimeSpan.MaxValue;
        foreach (var tr in transitions)
        {
            if (!string.Equals(tr.Lane, to, StringComparison.Ordinal)) continue;
            var delta = (tr.AtUtc - evt.Ts).Duration();
            if (delta < bestDelta)
            {
                bestDelta = delta;
                best = tr;
            }
        }
        return best;
    }

    private static TimelineEvent MeshAnchor(TimelineEvent evt, TaskProvenanceTransition anchor)
    {
        var details = evt.Details != null
            ? new Dictionary<string, string>(evt.Details)
            : new Dictionary<string, string>();
        if (!string.IsNullOrWhiteSpace(anchor.BranchTip)) details["branchTip"] = anchor.BranchTip!;
        if (!string.IsNullOrWhiteSpace(anchor.WorkBranchHead)) details["workBranchHead"] = anchor.WorkBranchHead!;
        return evt with { Details = details };
    }
}
