namespace AgentStudio.Shared;

public record SessionUsage
{
    public DateTime At { get; init; }
    public string? Tokens { get; init; }
    public string? Changes { get; init; }
    public string? Requests { get; init; }
}

/// <summary>
/// Per-job token rollup attached to the kanban card. Covers token-usage
/// bus events attributed to this job, including coding-agent turns and
/// orchestrator/supporting calls.
/// The frontend renders a single colour-tiered "bubble" with the total,
/// and a hover popover with the breakdown plus per-call rows.
/// </summary>
public record TaskTokenSummary
{
    public int Calls { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    /// <summary>Sum of all four token counts. Drives the bubble label.</summary>
    public long TotalTokens { get; init; }
    /// <summary>Historical list-price estimate, summed per call at each call's timestamp.</summary>
    public decimal EstimatedApiCostUsd { get; init; }
    /// <summary>False when at least one attributed call has no catalog price for its timestamp.</summary>
    public bool AllModelsPriced { get; init; }
    /// <summary>Most recent coding-agent model when present, otherwise the most recent recorded model. Null when no model was recorded.</summary>
    public string? LastModel { get; init; }
    /// <summary>Timestamp of the most recent attributed token usage entry. Null when never updated.</summary>
    public DateTime? LastUpdate { get; init; }
    /// <summary>Per-call rows for the popover, oldest first.</summary>
    public List<TaskTokenCall> Entries { get; init; } = [];
}

/// <summary>
/// One token usage call attributed to a job. Used by the popover to list
/// per-run rows below the aggregate.
/// </summary>
public record TaskTokenCall
{
    public DateTime Ts { get; init; }
    /// <summary>Canonical catalog model id (e.g. <c>claude-sonnet-5</c>), or a raw unrecognized id. Never a display label - see <see cref="DisplayModel"/>.</summary>
    public string? Model { get; init; }
    /// <summary>Human-readable model label for the UI (e.g. <c>Claude Sonnet 5</c>). Falls back to <see cref="Model"/> for ids absent from the registry.</summary>
    public string? DisplayModel { get; init; }
    /// <summary>Bus participant that produced this token usage row, e.g. <c>agent:codex</c> or <c>orchestrator:Project</c>.</summary>
    public string? ParticipantId { get; init; }
    public long InputTokens { get; init; }
    public long OutputTokens { get; init; }
    public long CacheReadTokens { get; init; }
    public long CacheCreationTokens { get; init; }
    /// <summary>Historical list-price estimate at <see cref="Ts"/>.</summary>
    public decimal EstimatedApiCostUsd { get; init; }
    /// <summary>Whether the price catalog resolved the model at <see cref="Ts"/>.</summary>
    public bool ModelPriced { get; init; }
}

/// <summary>
/// One row in <c>logs/session-events.jsonl</c>. Records every start / continue
/// / recovery so the user can see whether a follow-up actually loaded the
/// previous CLI session or had to reconstruct from files.
/// </summary>
public record SessionEvent
{
    public DateTime Ts { get; init; }
    /// <summary><c>start</c> | <c>continue</c> | <c>recovery</c></summary>
    public string Kind { get; init; } = "";
    public string? Cli { get; init; }
    /// <summary>
    /// Effective model resolved for this run at confirmed process start.
    /// Stored independently of CLI init frames and token reporting so every
    /// new run has durable model attribution.
    /// </summary>
    public string? Model { get; init; }
    /// <summary>Effective thinking / reasoning level resolved for this run.</summary>
    public string? ThinkingLevel { get; init; }
    /// <summary>Runtime-backed execution owner captured at run start for durable history.</summary>
    public TaskExecutionLocation? ExecutionLocation { get; init; }
    /// <summary>
    /// Fenced Attempt Authority id for a remote run. Null for local and legacy
    /// rows. It correlates terminal authority with the row without making the
    /// session log a second authority.
    /// </summary>
    public string? RunAttemptId { get; init; }
    /// <summary>Terminal instant copied from the settled attempt or local CLI execution.</summary>
    public DateTime? FinishedAt { get; init; }
    /// <summary>Canonical terminal outcome, for example done, failed, noop, or superseded.</summary>
    public string? Result { get; init; }
    /// <summary>Terminal process/display status, for example completed, failed, or stopped.</summary>
    public string? Status { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    /// <summary>Session id we attempted to resume (null on fresh start / recovery).</summary>
    public string? InputSessionId { get; init; }
    /// <summary>Session id the CLI emitted in this run (filled after the run starts streaming).</summary>
    public string? CapturedSessionId { get; init; }
    /// <summary>
    /// S2 (AGT-1784): the working directory this session was BORN in (the worktree
    /// path the CLI actually ran in). The only durable ground truth that lets a
    /// later reissue detect a cross-path resume target — the CLI keys
    /// <c>--resume</c> by cwd, so a session born in a different dir must start
    /// fresh, not resume. Null on legacy rows recorded before this was tracked
    /// (treated as "unknown → fall back to reuse behavior").
    /// </summary>
    public string? Cwd { get; init; }
    /// <summary>True when we passed <c>-r</c> and the CLI accepted it; false on fresh start / recovery / dropped session.</summary>
    public bool Resumed { get; init; }
    /// <summary>Human-readable note when <see cref="Resumed"/> is false (e.g. <c>no session recorded</c>, <c>incompatible session id</c>).</summary>
    public string? Reason { get; init; }
    /// <summary>
    /// HEAD SHA captured for the run's deterministic commit range. Sequential
    /// runs capture the project working tree immediately before the CLI starts.
    /// Worktree-isolated runs may rewrite this after integration to the
    /// integration-branch HEAD observed under the merge lock, so
    /// <see cref="HeadShaBefore"/>..<see cref="HeadShaAfter"/> contains only
    /// the task branch commits folded in by that run, not sibling commits that
    /// landed while the task was running. Null when the project has no repo
    /// configured or git was unavailable.
    /// </summary>
    public string? HeadShaBefore { get; init; }
    /// <summary>
    /// HEAD SHA captured after the run finished (backfilled in
    /// <c>OnCliFinishedAsync</c>, in lockstep with
    /// <see cref="CapturedSessionId"/>). Equal to <see cref="HeadShaBefore"/>
    /// when the agent did not commit during the run.
    /// </summary>
    public string? HeadShaAfter { get; init; }
    /// <summary>
    /// Relative path (under the job folder, forward-slashed) to the file
    /// that captured the exact context string handed to the CLI for this
    /// run - the rendered prompt template plus the task's prompt.md,
    /// attachments list, mode framing, and any foregrounded reissue
    /// open-items block. Written at spawn time so reruns / escalations are
    /// auditable. Null for runs recorded before this was captured, or when
    /// the file write failed. The full text is served on demand by
    /// <c>GET /api/tasks/{id}/runs/{index}/context</c> and never inlined in
    /// the polled runs list.
    /// </summary>
    public string? ContextRef { get; init; }

    /// <summary>
    /// The context sources this run's CLI loaded beyond the prompt - memory /
    /// session paths, the instruction-file chain, global config, MCP servers,
    /// plus model / effective permission mode / cwd (ASS-1739 / T1a).
    /// Backfilled at run finish from
    /// <c>ICliExecutionService.DescribeContextSources</c> while the per-run
    /// process info is still alive. Null for runs recorded before this capture
    /// existed, or when the CLI returned no context. Flows through
    /// <c>RunTimelineBuilder</c> into the run-detail "Execution Context" panel.
    /// </summary>
    public CliExecutionContext? ExecutionContext { get; init; }
}

/// <summary>
/// Per-job derived view of "what the agent actually did", folded from
/// <c>logs/session-events.jsonl</c> (one row per CLI start / continue /
/// recovery) and <c>logs/tool-calls.jsonl</c> (one row per tool started /
/// completed). Drives the Overview tab's Agent Work block so the user sees
/// concrete metrics (call count, tool mix, recovery status) instead of an
/// inert session UUID. Every field tolerates a missing log file by
/// returning zeros / nulls; the endpoint never throws on absent logs.
/// </summary>
public record AgentWorkSummary
{
    /// <summary>Number of session-event rows (start + continue + recovery).</summary>
    public int Calls { get; init; }
    /// <summary>True when at least one session event has <c>Kind == "recovery"</c>.</summary>
    public bool Recovered { get; init; }
    /// <summary>Total <c>kind=started</c> tool-call rows.</summary>
    public int ToolCalls { get; init; }
    /// <summary>Per-tool started counts, sorted by count descending.</summary>
    public List<AgentWorkToolCount> ToolCounts { get; init; } = [];
    /// <summary>Timestamp of the earliest session event, or null when the log is empty.</summary>
    public DateTime? StartedAt { get; init; }
    /// <summary>
    /// Timestamp of the latest signal we have - max(latest session event,
    /// latest tool-call row). Null when both logs are empty.
    /// </summary>
    public DateTime? LastTouchAt { get; init; }
    /// <summary>Echoed from <c>job.json</c> for the Debug tooltip; the operator-facing UI hides this by default.</summary>
    public string? CurrentSessionId { get; init; }
}

public record AgentWorkToolCount
{
    public string Tool { get; init; } = "";
    public int Count { get; init; }
}

/// <summary>
/// Drill-down companion to <see cref="AgentWorkSummary"/>: the same
/// <c>logs/tool-calls.jsonl</c> rows folded into per-tool groups, each
/// carrying the individual calls so the Overview tab can show *what* the
/// agent did (the command / file / pattern in each call's argument), not
/// just how many times. Folded by <c>AgentWorkSummaryReader.ReadDetail</c>;
/// missing log yields an empty list. Groups are ordered by count descending
/// to match the summary chips.
/// </summary>
public record AgentWorkDetail
{
    /// <summary>Per-tool groups, most-used first.</summary>
    public List<AgentWorkToolGroup> Groups { get; init; } = [];
    /// <summary>Total <c>kind=started</c> tool-call rows across all groups (uncapped).</summary>
    public int TotalCalls { get; init; }
}

/// <summary>One tool type (Bash / Read / Edit / …) and the calls made with it.</summary>
public record AgentWorkToolGroup
{
    public string Tool { get; init; } = "";
    /// <summary>Full started count for this tool (may exceed <see cref="Calls"/>.Count when capped).</summary>
    public int Count { get; init; }
    /// <summary>
    /// The individual calls, oldest first. Capped to the most recent N per
    /// group so a pathological job cannot produce an unbounded payload;
    /// <see cref="Count"/> is the honest total.
    /// </summary>
    public List<AgentWorkCall> Calls { get; init; } = [];
}

/// <summary>
/// One agent tool invocation: the <c>started</c> row's argument (the
/// command/file/pattern that says *what* was done) paired with the
/// <c>completed</c> row's outcome when one was observed. A still-open call
/// (in-flight or crashed before completion) leaves <see cref="Completed"/>
/// false and the result fields null.
/// </summary>
public record AgentWorkCall
{
    /// <summary>Timestamp of the <c>started</c> row.</summary>
    public DateTime? Ts { get; init; }
    /// <summary>The started row's argument: shell command, file path, grep pattern, etc. May be empty.</summary>
    public string? Argument { get; init; }
    /// <summary>True once a matching <c>completed</c> row was seen.</summary>
    public bool Completed { get; init; }
    /// <summary>From the completed row: true when the tool reported an error.</summary>
    public bool? IsError { get; init; }
    /// <summary>From the completed row: the first line of the tool result, when captured.</summary>
    public string? ResultFirstLine { get; init; }
}

/// <summary>
/// The per-job task plan the plan strip renders above the activity log. Folded
/// by <c>PlanReader</c> from <c>logs/plan-snapshots.jsonl</c> (the agent's own
/// TodoWrite / update_plan frames) and <c>logs/tool-calls.jsonl</c>. Read-only
/// observability: no model call, no edits. When the agent never emitted a plan
/// (or the CLI has no native plan frame), <see cref="HasPlan"/> is false and the
/// strip is hidden. See <c>docs/mockups/task-progress-tracking/</c>.
/// </summary>
public record TaskPlanView
{
    /// <summary>False when no plan snapshot exists; the strip renders nothing.</summary>
    public bool HasPlan { get; init; }
    /// <summary>Frame kind that produced the latest snapshot: <c>claude/TodoWrite</c> or <c>codex/update_plan</c>.</summary>
    public string? Source { get; init; }
    /// <summary>Number of plan snapshots observed for this job.</summary>
    public int SnapshotCount { get; init; }
    /// <summary>Id of the single item currently <c>active</c>, or null when none is.</summary>
    public string? ActiveItemId { get; init; }
    /// <summary>Median sub-action count of already-<c>done</c> siblings; null below two samples (no estimate band drawn).</summary>
    public int? SoftEstimateMedian { get; init; }
    /// <summary>The latest snapshot's items, each with its derived sub-actions.</summary>
    public List<TaskPlanItemView> Items { get; init; } = [];
    /// <summary>Tool calls observed before any plan item was active ("before plan").</summary>
    public List<TaskPlanSubAction> UnassignedSubActions { get; init; } = [];
}

/// <summary>One top-level plan item plus the sub-actions attributed to it.</summary>
public record TaskPlanItemView
{
    public string Id { get; init; } = "";
    public string Title { get; init; } = "";
    /// <summary><c>pending</c> | <c>active</c> | <c>done</c>.</summary>
    public string Status { get; init; } = "pending";
    public int SubActionCount { get; init; }
    public List<TaskPlanSubAction> SubActions { get; init; } = [];
}

/// <summary>One tool call attributed to a plan item; the "Sub-Tasks" the user wants to see after an item finishes.</summary>
public record TaskPlanSubAction
{
    public DateTime Ts { get; init; }
    public string Tool { get; init; } = "";
    public string? Label { get; init; }
}
