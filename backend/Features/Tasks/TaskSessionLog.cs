using System.Text;
using System.Text.Json;

namespace AgentStudio.Tasks;

/// <summary>
/// Terminal facts copied onto one session-event row. The owning execution
/// source remains authoritative: Attempt Authority for remote runs and the CLI
/// execution result for local runs.
/// </summary>
public sealed record RunSessionCloseout
{
    public string? RunAttemptId { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime FinishedAt { get; init; }
    public string? Result { get; init; }
    public string? Status { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? CapturedSessionId { get; init; }
    public string? InputSessionId { get; init; }
}

/// <summary>
/// Everything that records "what happened to this job's CLI session":
/// the persisted <c>sessionName</c> and full <c>sessionChain</c> in
/// <c>task.json</c>, the JSONL event log under <c>logs/session-events.jsonl</c>
/// that drives the run/session history and carries its terminal display
/// receipt, plus the related
/// usage snapshot writeback. Reads of the same data live in
/// <see cref="TaskScannerService"/> alongside the rest of the read
/// surface; this service owns the writes plus the JSONL append-line
/// reader that the protocol pane needs.
/// </summary>
public class TaskSessionLog
{
    private readonly TaskScannerService _scanner;
    private readonly ILogger<TaskSessionLog> _logger;

    private static readonly JsonSerializerOptions SessionEventJsonOpts = new()
    {
        WriteIndented = false,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.Never
    };

    public TaskSessionLog(TaskScannerService scanner, ILogger<TaskSessionLog> logger)
    {
        _scanner = scanner;
        _logger = logger;
    }

    public bool SetJobSessionName(string jobId, string? sessionName, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", sessionName ?? "", _logger);
        return true;
    }

    /// <summary>
    /// Appends <paramref name="sessionId"/> to the job's <c>sessionChain</c>
    /// (no-op if the id is already the chain's tail) and updates
    /// <c>sessionName</c> in lockstep so the existing single-id consumers keep
    /// working. Used after a CLI emits its session UUID so a later
    /// <c>--resume</c> can still see the latest fork as the tail.
    /// </summary>
    public bool AppendSessionToChain(string jobId, string sessionId, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(sessionId)) return false;
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var chain = new List<string>(info.SessionChain);
        if (chain.Count == 0 || !string.Equals(chain[^1], sessionId, StringComparison.Ordinal))
        {
            chain.Add(sessionId);
            TaskJsonFile.UpdateField(info.FolderPath, "sessionChain", chain, _logger);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", sessionId, _logger);
        return true;
    }

    /// <summary>
    /// Resets the chain when a recovery-continue is performed. The previous
    /// session ids are preserved as history, but a sentinel <c>"(recovery)"</c>
    /// marker is appended so consumers can spot chain breaks. The next captured
    /// session id will start the new chain segment.
    /// </summary>
    public bool MarkSessionChainRecovery(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var chain = new List<string>(info.SessionChain);
        if (chain.Count > 0 && chain[^1] != "(recovery)")
        {
            chain.Add("(recovery)");
            TaskJsonFile.UpdateField(info.FolderPath, "sessionChain", chain, _logger);
        }
        TaskJsonFile.UpdateField(info.FolderPath, "sessionName", "", _logger);
        return true;
    }

    /// <summary>
    /// Captures the exact context string handed to the CLI for one run into
    /// its own file under <c>logs/run-context/</c> and returns the relative,
    /// forward-slashed path to store on the run's <see cref="SessionEvent.ContextRef"/>.
    /// Kept out of <c>session-events.jsonl</c> (and therefore the polled runs
    /// list) on purpose: a rendered prompt is multi-KB, so inlining it would
    /// bloat every 5 s poll. The file is served on demand by the per-run
    /// context endpoint. Best-effort: a write failure returns null and the run
    /// is recorded with no context ref rather than failing the spawn.
    /// </summary>
    public string? PersistRunContext(string jobFolder, string context)
    {
        try
        {
            var dir = TaskPaths.RunContextDir(jobFolder);
            Directory.CreateDirectory(dir);
            var fileName = $"run-{DateTime.UtcNow:yyyyMMdd-HHmmss-fff}.md";
            File.WriteAllText(Path.Combine(dir, fileName), context ?? string.Empty, Encoding.UTF8);
            return $"{TaskPaths.LogsDirName}/{TaskPaths.RunContextDirName}/{fileName}";
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to persist run context under {JobFolder}", jobFolder);
            return null;
        }
    }

    /// <summary>
    /// Append-one-line writer for <c>logs/session-events.jsonl</c>. JSONL keeps
    /// the file cheap to tail and tolerant to interrupted writes — a torn line
    /// is one lost event, not a corrupted document.
    /// </summary>
    public bool AppendSessionEvent(string jobId, SessionEvent evt, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        try
        {
            Directory.CreateDirectory(TaskPaths.LogsDir(info.FolderPath));

            // A confirmed successor start is itself terminal evidence for an
            // older open row. Close it before appending so the durable log
            // never exposes two simultaneous open runs for one task.
            var predecessor = ReadSessionEvents(jobId, watchPath).LastOrDefault();
            if (predecessor is { FinishedAt: null })
            {
                CloseSessionEvent(jobId, new RunSessionCloseout
                {
                    StartedAt = predecessor.Ts,
                    FinishedAt = evt.Ts,
                    Result = "superseded",
                    Status = "superseded"
                }, watchPath);
            }

            var path = TaskPaths.SessionEventsLog(info.FolderPath);
            var line = JsonSerializer.Serialize(evt, SessionEventJsonOpts) + Environment.NewLine;
            File.AppendAllText(path, line, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to append session event for job {JobId}", jobId);
            return false;
        }
    }

    /// <summary>
    /// Closes the matching durable run row. Remote runs match their fenced
    /// Attempt Authority id; local runs match their confirmed process start.
    /// Replaying the same close-out is idempotent.
    /// </summary>
    public bool CloseSessionEvent(string jobId, RunSessionCloseout closeout, string? watchPath = null)
    {
        return MutateSessionEvent(
            jobId,
            watchPath,
            evt => MatchesCloseout(evt, closeout),
            evt =>
            {
                var duration = closeout.DurationSeconds;
                if (duration is null && closeout.FinishedAt >= evt.Ts)
                    duration = (closeout.FinishedAt - evt.Ts).TotalSeconds;
                if (duration is < 0) duration = 0;

                return evt with
                {
                    RunAttemptId = closeout.RunAttemptId ?? evt.RunAttemptId,
                    FinishedAt = closeout.FinishedAt,
                    Result = closeout.Result ?? evt.Result,
                    Status = closeout.Status ?? evt.Status,
                    ExitCode = closeout.ExitCode ?? evt.ExitCode,
                    DurationSeconds = duration ?? evt.DurationSeconds,
                    CapturedSessionId = closeout.CapturedSessionId ?? evt.CapturedSessionId,
                    InputSessionId = closeout.InputSessionId ?? evt.InputSessionId
                };
            });
    }

    /// <summary>
    /// Rewrites the last row in <c>session-events.jsonl</c> with the
    /// <paramref name="capturedSessionId"/>. Called from <c>OnCliFinished</c>
    /// after a CLI emits its session UUID. Best-effort: a missing or
    /// unparseable last line is ignored. Used to fill in the
    /// <c>capturedSessionId</c> that the start-of-run event couldn't know yet.
    /// </summary>
    public bool BackfillLatestSessionEventCapturedId(string jobId, string capturedSessionId, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(capturedSessionId)) return false;
        return MutateLatestSessionEvent(jobId, watchPath, evt => evt with { CapturedSessionId = capturedSessionId });
    }

    /// <summary>
    /// Records the post-run HEAD SHA on the most recent session event so
    /// downstream readers (the run timeline, the per-run commits endpoint)
    /// can derive "commits made during this run" via the deterministic
    /// SHA range <c>HeadShaBefore..HeadShaAfter</c> instead of falling
    /// back to the wall-clock window.
    /// </summary>
    public bool BackfillLatestSessionEventHeadShaAfter(string jobId, string? sha, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(sha)) return false;
        return MutateLatestSessionEvent(jobId, watchPath, evt => evt with { HeadShaAfter = sha });
    }

    /// <summary>
    /// Rewrites the latest event's deterministic commit range. Worktree runs
    /// use this after integration to replace the spawn-time branch snapshot
    /// with the integration-branch range that actually landed for this task.
    /// </summary>
    public bool BackfillLatestSessionEventHeadShaRange(
        string jobId, string? beforeSha, string? afterSha, string? watchPath = null)
    {
        if (string.IsNullOrWhiteSpace(beforeSha) || string.IsNullOrWhiteSpace(afterSha)) return false;
        return MutateLatestSessionEvent(jobId, watchPath,
            evt => evt with { HeadShaBefore = beforeSha, HeadShaAfter = afterSha });
    }

    /// <summary>
    /// Records the read-only execution-context snapshot on the most recent
    /// session event (ASS-1739 / T1a). Called from <c>OnCliFinished</c> while
    /// the per-run process info is still alive, in lockstep with the
    /// captured-session-id backfill. Best-effort: a null context or a missing /
    /// unparseable last line is ignored.
    /// </summary>
    public bool BackfillLatestSessionEventExecutionContext(string jobId, CliExecutionContext? context, string? watchPath = null)
    {
        if (context == null) return false;
        return MutateLatestSessionEvent(jobId, watchPath, evt => evt with { ExecutionContext = context });
    }

    /// <summary>
    /// S2 (AGT-1784): correct the latest event's <see cref="SessionEvent.Resumed"/>
    /// to the EFFECTIVE resume decision. The start-of-run event is written from
    /// <c>plan.ResumeFlag</c> before the cwd-binding guard runs, so a run that the
    /// guard turned into a fresh start would otherwise lie as <c>Resumed:true</c>.
    /// Optionally records why it was downgraded.
    /// </summary>
    public bool BackfillLatestSessionEventResumed(string jobId, bool resumed, string? reason = null, string? watchPath = null)
    {
        return MutateLatestSessionEvent(jobId, watchPath,
            evt => evt with { Resumed = resumed, Reason = reason ?? evt.Reason });
    }

    private bool MutateLatestSessionEvent(string jobId, string? watchPath, Func<SessionEvent, SessionEvent> mutate)
        => MutateSessionEvent(jobId, watchPath, _ => true, mutate);

    private bool MutateSessionEvent(
        string jobId,
        string? watchPath,
        Func<SessionEvent, bool> matches,
        Func<SessionEvent, SessionEvent> mutate)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        var path = TaskPaths.SessionEventsLog(info.FolderPath);
        if (!File.Exists(path)) return false;
        try
        {
            var lines = File.ReadAllLines(path).ToList();
            var idx = -1;
            SessionEvent? evt = null;
            for (var candidate = lines.Count - 1; candidate >= 0; candidate--)
            {
                if (string.IsNullOrWhiteSpace(lines[candidate])) continue;
                try { evt = JsonSerializer.Deserialize<SessionEvent>(lines[candidate], TaskJsonFile.ReadOpts); }
                catch { continue; }
                if (evt is null || !matches(evt)) continue;
                idx = candidate;
                break;
            }
            if (idx < 0 || evt == null) return false;
            lines[idx] = JsonSerializer.Serialize(mutate(evt), SessionEventJsonOpts);
            File.WriteAllLines(path, lines, Encoding.UTF8);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to mutate latest session event for job {JobId}", jobId);
            return false;
        }
    }

    private static bool MatchesCloseout(SessionEvent evt, RunSessionCloseout closeout)
    {
        if (!string.IsNullOrWhiteSpace(closeout.RunAttemptId))
        {
            return string.Equals(
                evt.RunAttemptId,
                closeout.RunAttemptId,
                StringComparison.OrdinalIgnoreCase);
        }

        if (closeout.StartedAt is DateTime startedAt)
            return Math.Abs((evt.Ts - startedAt).TotalSeconds) <= 2;

        return evt.FinishedAt is null && evt.Ts <= closeout.FinishedAt;
    }

    /// <summary>
    /// Reads <c>logs/session-events.jsonl</c> into a list. Tolerant to torn
    /// trailing lines: a line that fails to parse is skipped. Lives next to
    /// the writers because the JSONL shape and parse rules need to evolve in
    /// lockstep.
    /// </summary>
    public List<SessionEvent> ReadSessionEvents(string jobId, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return [];
        var path = TaskPaths.SessionEventsLog(info.FolderPath);
        if (!File.Exists(path)) return [];
        var result = new List<SessionEvent>();
        foreach (var line in File.ReadAllLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var evt = JsonSerializer.Deserialize<SessionEvent>(line, TaskJsonFile.ReadOpts);
                if (evt != null) result.Add(evt);
            }
            catch (Exception __ex)
            {
                SilentCatch.Note(__ex, "TaskSessionLog: Best-effort: skip torn / malformed lines.");
                // Best-effort: skip torn / malformed lines.
            }
        }
        return result;
    }

    public bool UpdateLastUsage(string jobId, SessionUsage usage, string? watchPath = null)
    {
        var info = _scanner.FindJob(jobId, watchPath);
        if (info == null) return false;
        TaskJsonFile.UpdateField(info.FolderPath, "lastUsage", usage, _logger);
        return true;
    }
}
