using System.Globalization;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace AgentStudio.Runner;

/// <summary>
/// One CLI invocation between two user inputs - the unit of conversation
/// in the runs/sessions model documented in
/// <c>docs/quality/design-principles.md</c>. Produced by
/// <see cref="RunTimelineBuilder.Build"/> from <c>session-events.jsonl</c>
/// + <c>cli-output.log</c>; consumed by the
/// <c>/api/tasks/{id}/runs</c> endpoint and the protocol-pane run
/// timeline.
///
/// All fields are derived; nothing here is the source of truth on its
/// own. <see cref="LineStart"/> / <see cref="LineEnd"/> are 1-based
/// indices into the cli-output.log file so the frontend can fetch the
/// run's slice without re-parsing the whole file.
/// </summary>
public sealed record RunRecord
{
    /// <summary>1-based chronological index in the session.</summary>
    public int Index { get; init; }
    /// <summary><c>start</c> | <c>continue</c> | <c>recovery</c> | <c>restart</c>.</summary>
    public string Intent { get; init; } = "";
    public string? Trigger { get; init; }
    public string? TriggeredBy { get; init; }
    public string? TriggerReason { get; init; }
    public string? TriggerSource { get; init; }
    public DateTime StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    /// <summary><c>running</c>, <c>completed</c>, or a typed terminal result status such as <c>failed</c>, <c>blocked</c>, or <c>superseded</c>.</summary>
    public string Status { get; init; } = "unknown";
    /// <summary>Canonical terminal outcome, for example done, failed, noop, or superseded.</summary>
    public string? Result { get; init; }
    /// <summary>Evidence used to close the row. See <see cref="RunCloseoutSources"/>.</summary>
    public string? CloseoutSource { get; init; }
    public string? Cli { get; init; }
    /// <summary>Effective model resolved by the runner before this CLI process started.</summary>
    public string? Model { get; init; }
    /// <summary>Effective thinking / reasoning level resolved for this run.</summary>
    public string? ThinkingLevel { get; init; }
    public TaskExecutionLocation? ExecutionLocation { get; init; }
    public int? ExitCode { get; init; }
    public double? DurationSeconds { get; init; }
    public string? InputSessionId { get; init; }
    public string? CapturedSessionId { get; init; }
    public bool Resumed { get; init; }
    public string? Reason { get; init; }
    /// <summary>The user follow-up that triggered this run (the most recent <c>[user]</c>-stream line before the run started). Null when the run was an auto-pickup or fresh start.</summary>
    public string? UserFollowup { get; init; }
    /// <summary>1-based line number in <c>cli-output.log</c> where this run begins (the <c>[taskboard] Started ... CLI</c> marker).</summary>
    public int? LineStart { get; init; }
    /// <summary>1-based line number in <c>cli-output.log</c> where this run ends (the <c>[taskboard] ... CLI exited</c> marker, or the file's last line for the still-running tail).</summary>
    public int? LineEnd { get; init; }
    /// <summary>Lower bound of the run's deterministic commit range. For worktree runs this may be the integration HEAD captured under the merge lock.</summary>
    public string? HeadShaBefore { get; init; }
    /// <summary>Upper bound of the run's deterministic commit range. Equal to <see cref="HeadShaBefore"/> when the agent did not commit.</summary>
    public string? HeadShaAfter { get; init; }
    /// <summary>
    /// Relative path (under the job folder) to the captured context this run
    /// was started with (see <see cref="SessionEvent.ContextRef"/>). Non-null
    /// signals the protocol-pane run card to offer "Show passed context"; the
    /// full text is fetched on demand from
    /// <c>GET /api/tasks/{id}/runs/{index}/context</c>, never inlined here.
    /// </summary>
    public string? ContextRef { get; init; }
    /// <summary>
    /// The read-only execution context this run's CLI loaded beyond the prompt
    /// (ASS-1739 / T1a): memory / session paths, the instruction-file chain,
    /// global config, MCP servers, plus model / permission mode / cwd. Copied
    /// straight from <see cref="SessionEvent.ExecutionContext"/>; null for runs
    /// recorded before the capture existed. Drives the run-detail "Execution
    /// Context" panel.
    /// </summary>
    public AgentStudio.Shared.CliExecutionContext? ExecutionContext { get; init; }
}

/// <summary>
/// One prompt handed to the agent, projected onto the run timeline. The full
/// prompt/context text stays lazy via <c>/runs/{index}/context</c>; this row is
/// the compact metadata needed to render Prompt #1, #2, ... in the picker.
/// </summary>
public sealed record RunPromptEntry
{
    /// <summary>1-based chronological prompt number shown in the UI.</summary>
    public int Index { get; init; }
    /// <summary>1-based run index this prompt started, matching <see cref="RunRecord.Index"/>.</summary>
    public int RunIndex { get; init; }
    public string Intent { get; init; } = "";
    public DateTime At { get; init; }
    public string Label { get; init; } = "";
    /// <summary><c>prompt.md</c>, <c>prompt-N.md</c>, <c>user-followup</c>, or the captured context ref source.</summary>
    public string? FileName { get; init; }
    /// <summary>Where <see cref="PromptTokenEstimate"/> came from.</summary>
    public string PromptTokenSource { get; init; } = "";
    public string? PromptPreview { get; init; }
    /// <summary>Best-effort local estimate; the repo currently has no tokenizer utility.</summary>
    public int? PromptTokenEstimate { get; init; }
    /// <summary>Estimated tokens in the captured context handed to the CLI for this run.</summary>
    public int? ContextTokenEstimate { get; init; }
    public string? ContextRef { get; init; }
    public RunPromptContextSnapshot? ContextSnapshot { get; init; }
}

/// <summary>
/// Compact context-size snapshot attached to a prompt entry. For modern runs
/// this is derived from <see cref="SessionEvent.ContextRef"/> and therefore
/// reflects the captured context at spawn time.
/// </summary>
public sealed record RunPromptContextSnapshot
{
    public string Source { get; init; } = "";
    public string? Ref { get; init; }
    public DateTime? At { get; init; }
    public string? Status { get; init; }
    public int? TokenEstimate { get; init; }
    public List<ContextUsageMetric> Metrics { get; init; } = [];
}

/// <summary>
/// One operator-owned review-attempt epoch projected beside the CLI run
/// timeline. Epoch zero is the initial cycle. Later epochs are opened only by
/// explicit human requeues and retain the recorded reason and artifact-rotation
/// count for audit.
/// </summary>
public sealed record ReviewAttemptCycle
{
    public int Epoch { get; init; }
    public bool IsCurrent { get; init; }
    public DateTime? StartedAt { get; init; }
    public DateTime? EndedAt { get; init; }
    public string? Actor { get; init; }
    public string? Reason { get; init; }
    public string? FromState { get; init; }
    public string? ToState { get; init; }
    public int RotatedArtifacts { get; init; }
}

/// <summary>
/// One read-only refinement of the original task prompt. The projection folds
/// existing task evidence together; it never creates a second persistence
/// path. <c>operator</c> rows come from <c>prompt-N.md</c> or the user
/// follow-up paired to a run, while <c>system</c> rows come from the
/// append-only <c>orchestrator-follow-up-history/</c> files.
/// </summary>
public sealed record TaskRefinementEntry
{
    public string Id { get; init; } = "";
    public DateTime At { get; init; }
    /// <summary><c>operator</c>, <c>agent</c>, or <c>system</c>.</summary>
    public string Actor { get; init; } = "system";
    public string? Reason { get; init; }
    public string Markdown { get; init; } = "";
    /// <summary><c>prompt-history</c>, <c>run-log</c>, or <c>orchestrator-history</c>.</summary>
    public string Source { get; init; } = "";
    public int? RunIndex { get; init; }
}

/// <summary>
/// Top-level session shape the <c>/api/tasks/{id}/runs</c> endpoint
/// returns. The runs list is the primary surface; the aggregates above
/// it (<see cref="RunCount"/>, <see cref="LastActivityAt"/>) are derived
/// once on the backend so every consumer renders the same numbers.
/// </summary>
public sealed record RunTimeline
{
    public int RunCount { get; init; }
    public DateTime? FirstStartedAt { get; init; }
    public DateTime? LastActivityAt { get; init; }
    /// <summary>True when the last run is still running (no end marker yet).</summary>
    public bool HasActiveRun { get; init; }
    public List<RunRecord> Runs { get; init; } = [];
    public List<RunPromptEntry> PromptEntries { get; init; } = [];
    /// <summary>Chronological prompt refinements derived from existing task evidence.</summary>
    public List<TaskRefinementEntry> Refinements { get; init; } = [];
    /// <summary>Typed standalone-runner lifecycle replay. Diagnostics are retained for Trace but never inferred from CLI prose.</summary>
    public List<RunnerRecordedEvent> RunnerEvents { get; init; } = [];
    /// <summary>Current operator-owned review epoch. Legacy tasks are epoch zero.</summary>
    public int ReviewAttemptEpoch { get; init; }
    /// <summary>Current and closed cycles, newest first, for the task-detail Runs surface.</summary>
    public List<ReviewAttemptCycle> ReviewAttemptCycles { get; init; } = [];
}

/// <summary>
/// Read-time refinement projection for the Task inspector tab. Extend-mode
/// files preserve the full multiline operator prompt; other operator
/// follow-ups are recovered from the run/log projection; system reissues are
/// parsed from the existing append-only orchestrator steering history.
/// </summary>
public static class TaskRefinementTimelineBuilder
{
    private const string SteeringPromptHeading = "## Steering prompt (verbatim)";

    public static List<TaskRefinementEntry> Build(
        string jobFolder,
        IReadOnlyList<RunRecord> runs,
        IReadOnlyList<TaskPromptHistoryEntry> promptHistory)
    {
        runs ??= [];
        promptHistory ??= [];

        var result = new List<TaskRefinementEntry>();
        foreach (var entry in promptHistory.OrderBy(entry => entry.WrittenAt).ThenBy(entry => entry.Index))
        {
            if (string.IsNullOrWhiteSpace(entry.Markdown)) continue;
            result.Add(new TaskRefinementEntry
            {
                Id = $"prompt-history-{entry.Index}",
                At = entry.WrittenAt,
                Actor = "operator",
                Reason = "Task extended",
                Markdown = entry.Markdown.Trim(),
                Source = "prompt-history",
            });
        }

        foreach (var run in runs.OrderBy(run => run.StartedAt).ThenBy(run => run.Index))
        {
            if (string.IsNullOrWhiteSpace(run.UserFollowup)) continue;
            if (MatchesNearbyPromptHistory(run, promptHistory)) continue;
            result.Add(new TaskRefinementEntry
            {
                Id = $"run-followup-{run.Index}",
                At = run.StartedAt,
                Actor = "operator",
                Reason = NormalizeRunReason(run),
                Markdown = run.UserFollowup.Trim(),
                Source = "run-log",
                RunIndex = run.Index,
            });
        }

        result.AddRange(ReadOrchestratorHistory(jobFolder));
        return result
            .OrderBy(entry => entry.At)
            .ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .ToList();
    }

    private static bool MatchesNearbyPromptHistory(
        RunRecord run,
        IReadOnlyList<TaskPromptHistoryEntry> promptHistory)
    {
        var followup = NormalizeText(run.UserFollowup);
        return promptHistory.Any(entry =>
            Math.Abs((run.StartedAt - entry.WrittenAt).TotalMinutes) <= 10
            && string.Equals(followup, NormalizeText(entry.Markdown), StringComparison.Ordinal));
    }

    private static string? NormalizeRunReason(RunRecord run)
    {
        if (!string.IsNullOrWhiteSpace(run.Reason))
        {
            var reason = run.Reason.Trim();
            return reason.StartsWith("mode=", StringComparison.OrdinalIgnoreCase)
                ? $"{reason["mode=".Length..]} follow-up"
                : reason;
        }
        return string.Equals(run.Intent, "continue", StringComparison.OrdinalIgnoreCase)
            ? null
            : string.IsNullOrWhiteSpace(run.Intent) ? null : $"{run.Intent.Trim()} run";
    }

    private static IEnumerable<TaskRefinementEntry> ReadOrchestratorHistory(string jobFolder)
    {
        var historyDir = Path.Combine(jobFolder, "orchestrator-follow-up-history");
        if (!Directory.Exists(historyDir)) yield break;

        foreach (var path in Directory.EnumerateFiles(historyDir, "*.md", SearchOption.TopDirectoryOnly)
                     .OrderBy(path => path, StringComparer.Ordinal))
        {
            string markdown;
            try { markdown = File.ReadAllText(path); }
            catch { continue; }

            var prompt = ReadSectionBody(markdown, SteeringPromptHeading);
            if (string.IsNullOrWhiteSpace(prompt)) continue;

            var fileName = Path.GetFileName(path);
            yield return new TaskRefinementEntry
            {
                Id = $"orchestrator-history-{fileName}",
                At = ReadMetadataDate(markdown, "timestamp")
                    ?? File.GetLastWriteTimeUtc(path),
                Actor = "system",
                Reason = ReadMetadata(markdown, "reason")
                    ?? ReadMetadata(markdown, "cause")
                    ?? ReadMetadata(markdown, "verdict"),
                Markdown = prompt.Trim(),
                Source = "orchestrator-history",
            };
        }
    }

    private static string? ReadMetadata(string markdown, string key)
    {
        var prefix = $"- {key}:";
        var line = markdown.Replace("\r\n", "\n")
            .Split('\n')
            .FirstOrDefault(candidate => candidate.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        var value = line?[prefix.Length..].Trim();
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }

    private static DateTime? ReadMetadataDate(string markdown, string key)
        => DateTime.TryParse(
            ReadMetadata(markdown, key),
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind,
            out var parsed)
                ? parsed
                : null;

    private static string? ReadSectionBody(string markdown, string heading)
    {
        var normalized = markdown.Replace("\r\n", "\n");
        var headingIndex = normalized.IndexOf(heading, StringComparison.OrdinalIgnoreCase);
        if (headingIndex < 0) return null;
        var bodyStart = headingIndex + heading.Length;
        return normalized[bodyStart..].TrimStart('\n').Trim();
    }

    private static string NormalizeText(string? value)
        => string.Join(
            " ",
            (value ?? "").Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>
/// Pure projection of operator-requeue ledger events into review-attempt cycle
/// history. The durable sidecar supplies the authoritative current epoch; the
/// timeline supplies operator reason, actor, lane crossing, and rotation count.
/// Missing best-effort timeline rows still yield a visible current epoch.
/// </summary>
public static class ReviewAttemptTimelineBuilder
{
    public static List<ReviewAttemptCycle> Build(
        int currentEpoch,
        IReadOnlyList<TimelineEvent> events,
        DateTime? initialStartedAt)
    {
        currentEpoch = Math.Max(0, currentEpoch);
        events ??= [];

        var boundaries = events
            .Where(e => string.Equals(
                e.Kind,
                TimelineEventKinds.OperatorRequeued,
                StringComparison.Ordinal))
            .Select(e => (Event: e, Epoch: ReadEpoch(e)))
            .Where(x => x.Epoch is > 0 && x.Epoch <= currentEpoch)
            .GroupBy(x => x.Epoch!.Value)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(x => x.Event.Ts).First().Event);

        var cycles = new List<ReviewAttemptCycle>(currentEpoch + 1);
        for (var epoch = currentEpoch; epoch >= 0; epoch--)
        {
            boundaries.TryGetValue(epoch, out var boundary);
            var nextBoundary = boundaries
                .Where(pair => pair.Key > epoch)
                .OrderBy(pair => pair.Key)
                .Select(pair => pair.Value)
                .FirstOrDefault();

            cycles.Add(new ReviewAttemptCycle
            {
                Epoch = epoch,
                IsCurrent = epoch == currentEpoch,
                StartedAt = epoch == 0 ? initialStartedAt : boundary?.Ts,
                EndedAt = epoch == currentEpoch ? null : nextBoundary?.Ts,
                Actor = boundary?.Actor,
                Reason = epoch == 0
                    ? "Initial review cycle."
                    : ReadDetail(boundary, "reason") ?? boundary?.Summary,
                FromState = ReadDetail(boundary, "from"),
                ToState = ReadDetail(boundary, "to"),
                RotatedArtifacts = ReadNonNegativeInt(boundary, "rotatedArtifacts"),
            });
        }
        return cycles;
    }

    private static int? ReadEpoch(TimelineEvent evt)
    {
        var value = ReadDetail(evt, "attemptEpoch");
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var epoch)
            ? Math.Max(0, epoch)
            : null;
    }

    private static int ReadNonNegativeInt(TimelineEvent? evt, string key)
    {
        var value = ReadDetail(evt, key);
        return int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number)
            ? Math.Max(0, number)
            : 0;
    }

    private static string? ReadDetail(TimelineEvent? evt, string key)
        => evt?.Details != null && evt.Details.TryGetValue(key, out var value)
            && !string.IsNullOrWhiteSpace(value)
                ? value
                : null;
}

public static class RunPromptTimelineBuilder
{
    public static List<RunPromptEntry> Build(
        IReadOnlyList<RunRecord> runs,
        string jobFolder,
        string? promptMarkdown,
        IReadOnlyList<TaskPromptHistoryEntry> promptHistory,
        ContextUsageSnapshot? contextUsage)
    {
        runs ??= [];
        promptHistory ??= [];

        var result = new List<RunPromptEntry>(runs.Count);
        foreach (var run in runs.OrderBy(r => r.Index))
        {
            var contextText = ReadContextText(jobFolder, run.ContextRef);
            var source = ResolvePromptSource(run, promptMarkdown, promptHistory, contextText);
            var contextTokenEstimate = PromptTokenEstimator.EstimateOrNull(contextText);

            result.Add(new RunPromptEntry
            {
                Index = result.Count + 1,
                RunIndex = run.Index,
                Intent = run.Intent,
                At = run.StartedAt,
                Label = $"Prompt #{result.Count + 1}",
                FileName = source.FileName,
                PromptTokenSource = source.Source,
                PromptPreview = Preview(source.Text),
                PromptTokenEstimate = PromptTokenEstimator.EstimateOrNull(source.Text),
                ContextTokenEstimate = contextTokenEstimate,
                ContextRef = run.ContextRef,
                ContextSnapshot = BuildContextSnapshot(run.ContextRef, contextTokenEstimate, contextUsage)
            });
        }
        return result;
    }

    private static (string? Text, string? FileName, string Source) ResolvePromptSource(
        RunRecord run,
        string? promptMarkdown,
        IReadOnlyList<TaskPromptHistoryEntry> promptHistory,
        string? contextText)
    {
        if (run.Index == 1 && !string.IsNullOrWhiteSpace(promptMarkdown))
        {
            return (promptMarkdown, "prompt.md", "task-prompt");
        }

        var history = promptHistory.FirstOrDefault(h => h.Index == run.Index - 1);
        if (history is not null && !string.IsNullOrWhiteSpace(history.Markdown))
        {
            return (history.Markdown, history.FileName, "prompt-history");
        }

        if (!string.IsNullOrWhiteSpace(run.UserFollowup))
        {
            return (run.UserFollowup, "user-followup", "user-followup");
        }

        if (!string.IsNullOrWhiteSpace(contextText))
        {
            return (contextText, run.ContextRef, "captured-context");
        }

        return (null, run.ContextRef, "missing");
    }

    private static RunPromptContextSnapshot? BuildContextSnapshot(
        string? contextRef,
        int? contextTokenEstimate,
        ContextUsageSnapshot? latestContextUsage)
    {
        if (!string.IsNullOrWhiteSpace(contextRef))
        {
            return new RunPromptContextSnapshot
            {
                Source = "captured-context",
                Ref = contextRef,
                Status = contextTokenEstimate.HasValue ? "captured" : "missing",
                TokenEstimate = contextTokenEstimate
            };
        }

        if (latestContextUsage is null) return null;
        return new RunPromptContextSnapshot
        {
            Source = "latest-context-usage",
            At = latestContextUsage.At,
            Status = latestContextUsage.Status,
            Metrics = latestContextUsage.Metrics
        };
    }

    private static string? ReadContextText(string jobFolder, string? contextRef)
    {
        if (string.IsNullOrWhiteSpace(jobFolder) || string.IsNullOrWhiteSpace(contextRef)) return null;
        try
        {
            var folderFull = Path.GetFullPath(jobFolder);
            var contextFull = Path.GetFullPath(Path.Combine(jobFolder, contextRef));
            if (!contextFull.StartsWith(folderFull, StringComparison.Ordinal) || !File.Exists(contextFull))
                return null;
            return File.ReadAllText(contextFull);
        }
        catch
        {
            return null;
        }
    }

    private static string? Preview(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var compact = Regex.Replace(text.Trim(), @"\s+", " ");
        return compact.Length <= 180 ? compact : compact[..177].TrimEnd() + "...";
    }
}

public static class PromptTokenEstimator
{
    public static int? EstimateOrNull(string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        return Math.Max(1, (int)Math.Ceiling(text.Length / 4.0));
    }
}

/// <summary>Read-time terminal evidence available to the pure run projection.</summary>
public sealed record RunTimelineFallbackContext(
    IReadOnlyList<RunAttemptDto> RunAttempts,
    IReadOnlyList<TimelineEvent> Ledger,
    bool TaskMayHaveActiveRun);

/// <summary>Stable wire values that explain how a projected run was closed.</summary>
public static class RunCloseoutSources
{
    public const string SessionEvent = "session-event";
    public const string AttemptAuthority = "attempt-authority";
    public const string Timeline = "timeline";
    public const string CliExit = "cli-exit";
    public const string SuccessorStart = "successor-start";
    public const string LegacyActivity = "legacy-activity";
    public const string LegacyMissing = "legacy-missing";
}

/// <summary>Pure mapping from terminal outcome truth to the Runs-panel status.</summary>
public static class RunCloseoutPolicy
{
    public static string StatusFor(string? result, string? recordedStatus)
    {
        var normalizedResult = Normalize(result);
        var mapped = normalizedResult switch
        {
            "done" or "success" or "noop" or "committed-partial" or "completed" => "completed",
            "failed" or "unverified" or "environmentfailure" => "failed",
            "superseded" => "superseded",
            "blocked" => "blocked",
            "needsinput" or "needs-input" => "needs-input",
            "cancelled" or "canceled" => "cancelled",
            "interrupted" => "interrupted",
            _ => null
        };
        if (mapped is not null) return mapped;

        var normalizedStatus = Normalize(recordedStatus);
        return string.IsNullOrWhiteSpace(normalizedStatus) ? "unknown" : normalizedStatus;
    }

    private static string? Normalize(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim().ToLowerInvariant();
}

/// <summary>
/// Pure builder. Takes session events, CLI output, and optional terminal
/// authority/timeline evidence, then returns a fully-populated
/// <see cref="RunTimeline"/>. No I/O,
/// no DI, no dates relative to "now" - the caller passes
/// <paramref name="nowUtc"/> so the still-running tail's
/// <c>EndedAt</c> stays deterministic in tests.
///
/// <para>
/// <b>Why pure.</b> The timeline is the load-bearing aggregation
/// behind the run-list UI; it is also the input to per-run summaries
/// and per-run commit lookups. A pure builder lets us lock the entire
/// shape with fixture-based xUnit tests, the same pattern
/// <see cref="RunPlanner"/> and <see cref="RunOutcomePolicy"/> follow.
/// </para>
/// </summary>
public static class RunTimelineBuilder
{
    private static readonly Regex StartedRegex = new(
        @"^\[taskboard\]\s+Started\s+(?<cli>\S+)\s+CLI\b",
        RegexOptions.Compiled);

    private static readonly Regex ExitedRegex = new(
        @"^\[taskboard\]\s+(?<cli>\S+)\s+CLI\s+exited:\s*status=(?<status>\w+)(?:,\s*exitCode=(?<code>-?\d+|\?))?(?:,\s*duration=(?<dur>[\d.]+)s)?",
        RegexOptions.Compiled);

    /// <summary>
    /// Build the timeline. <paramref name="lines"/> is the parsed
    /// <c>cli-output.log</c>; <paramref name="events"/> is the parsed
    /// <c>session-events.jsonl</c>; <paramref name="fallback"/> supplies
    /// terminal evidence for legacy rows. Every input can be empty.
    /// </summary>
    public static RunTimeline Build(
        IReadOnlyList<SessionEvent> events,
        IReadOnlyList<CliOutputLine> lines,
        DateTime nowUtc,
        RunTimelineFallbackContext? fallback = null)
    {
        events ??= [];
        lines ??= [];
        fallback ??= new RunTimelineFallbackContext([], [], TaskMayHaveActiveRun: true);

        // Pre-index the log by [taskboard] marker so we can pair each
        // SessionEvent with the run boundary that came from the runner.
        // The marker carries the authoritative status / exit code /
        // duration, while the SessionEvent carries the intent / session
        // ids / user-visible reason.
        var startedMarkers = new List<(int LineIndex, DateTime Ts, string Cli)>();
        var exitedMarkers = new List<(int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)>();
        for (int i = 0; i < lines.Count; i++)
        {
            var l = lines[i];
            if (!string.Equals(l.Stream, "system", StringComparison.OrdinalIgnoreCase)) continue;
            var text = l.Text ?? string.Empty;
            var sm = StartedRegex.Match(text);
            if (sm.Success)
            {
                startedMarkers.Add((i, l.Timestamp, sm.Groups["cli"].Value));
                continue;
            }
            var em = ExitedRegex.Match(text);
            if (em.Success)
            {
                int? exitCode = null;
                if (em.Groups["code"].Success && int.TryParse(em.Groups["code"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var ec))
                    exitCode = ec;
                double? duration = null;
                if (em.Groups["dur"].Success && double.TryParse(em.Groups["dur"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d))
                    duration = d;
                exitedMarkers.Add((i, l.Timestamp, em.Groups["cli"].Value, em.Groups["status"].Value, exitCode, duration));
            }
        }

        var runs = new List<RunRecord>(events.Count);
        for (int idx = 0; idx < events.Count; idx++)
        {
            var evt = events[idx];
            var nextEvt = idx + 1 < events.Count ? events[idx + 1] : null;

            // Pair the event with the [taskboard] Started marker whose
            // timestamp is closest at-or-after the event ts. Recovery
            // events occasionally don't have a Started marker (e.g.
            // because the planner stopped before spawning a CLI), so a
            // missing pair is allowed.
            var startedMarker = FindFirstAfter(startedMarkers, evt.Ts);

            // The exit marker is the first one strictly after the run's
            // started marker (or after the event ts if no started marker
            // matched). Cap it at the next event's ts so concurrent
            // runs cannot leak into each other. (In this product runs
            // are sequential per project, so this is mostly defensive.)
            DateTime upperBound = nextEvt?.Ts ?? DateTime.MaxValue;
            var exitMarker = FindFirstAfter(exitedMarkers, startedMarker?.Ts ?? evt.Ts);
            if (exitMarker is { } em2 && em2.Ts >= upperBound) exitMarker = null;

            // User follow-up: the most recent [user] line strictly
            // before the run started. Keep it brief; the activity log
            // has the full text if the user wants to see it.
            var followup = FindLastUserBefore(lines, startedMarker?.LineIndex ?? FindLineNearTimestamp(lines, evt.Ts));

            var closeout = ResolveCloseout(evt, upperBound, exitMarker, lines, fallback);
            var isLatestRun = idx == events.Count - 1;
            var hasLeasedAttempt = fallback.RunAttempts.Any(attempt =>
                AttemptMatches(evt, upperBound, attempt)
                && attempt.State == AttemptLifecycleState.Leased);
            var status = closeout?.Status
                         ?? ((isLatestRun && fallback.TaskMayHaveActiveRun
                              && (startedMarker.HasValue || hasLeasedAttempt || IsRemote(evt)))
                             ? "running"
                             : "unknown");
            var endedAt = closeout?.FinishedAt;
            var lineStart = startedMarker?.LineIndex is int li ? li + 1 : (int?)null;
            int? lineEnd;
            if (exitMarker.HasValue)
            {
                lineEnd = exitMarker.Value.LineIndex + 1;
            }
            else if (startedMarker.HasValue)
            {
                // Still running - the tail of the log belongs to this
                // run. Anchor on the next event's start (sequential
                // model) or the file's last line.
                lineEnd = nextEvt != null
                    ? Math.Max(lineStart ?? 1, FindLineNearTimestamp(lines, nextEvt.Ts) + 1)
                    : lines.Count;
            }
            else
            {
                lineEnd = null;
            }

            runs.Add(new RunRecord
            {
                Index = idx + 1,
                Intent = evt.Kind ?? "",
                Trigger = evt.Trigger,
                TriggeredBy = evt.TriggeredBy,
                TriggerReason = evt.TriggerReason,
                TriggerSource = evt.TriggerSource,
                StartedAt = evt.Ts,
                EndedAt = endedAt,
                Status = status,
                Result = closeout?.Result,
                CloseoutSource = closeout?.Source
                                 ?? (status == "running" ? null : RunCloseoutSources.LegacyMissing),
                Cli = evt.Cli ?? startedMarker?.Cli,
                Model = evt.Model,
                ThinkingLevel = evt.ThinkingLevel,
                ExecutionLocation = evt.ExecutionLocation is null ? null : evt.ExecutionLocation with { Historical = true },
                ExitCode = closeout?.ExitCode,
                DurationSeconds = closeout?.DurationSeconds,
                InputSessionId = evt.InputSessionId,
                CapturedSessionId = evt.CapturedSessionId,
                Resumed = evt.Resumed,
                Reason = evt.Reason,
                UserFollowup = followup,
                LineStart = lineStart,
                LineEnd = lineEnd,
                HeadShaBefore = evt.HeadShaBefore,
                HeadShaAfter = evt.HeadShaAfter,
                ContextRef = evt.ContextRef,
                ExecutionContext = evt.ExecutionContext
            });
        }

        DateTime? lastActivity =
            runs.Count == 0 ? null
            : runs[^1].EndedAt ?? (lines.Count > 0 ? lines[^1].Timestamp : runs[^1].StartedAt);

        return new RunTimeline
        {
            RunCount = runs.Count,
            FirstStartedAt = runs.Count > 0 ? runs[0].StartedAt : null,
            LastActivityAt = lastActivity,
            HasActiveRun = runs.Count > 0 && string.Equals(runs[^1].Status, "running", StringComparison.OrdinalIgnoreCase),
            Runs = runs
        };
    }

    private static ProjectedRunCloseout? ResolveCloseout(
        SessionEvent evt,
        DateTime upperBound,
        (int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)? exitMarker,
        IReadOnlyList<CliOutputLine> lines,
        RunTimelineFallbackContext fallback)
    {
        if (evt.FinishedAt is not null || evt.DurationSeconds is not null)
        {
            var finishedAt = evt.FinishedAt
                             ?? (evt.DurationSeconds is double duration
                                 ? evt.Ts.AddSeconds(Math.Max(0, duration))
                                 : evt.Ts);
            return NewCloseout(
                evt.Ts,
                finishedAt,
                evt.Result,
                evt.Status,
                evt.ExitCode,
                evt.DurationSeconds,
                RunCloseoutSources.SessionEvent);
        }

        var attempt = fallback.RunAttempts
            .Where(candidate => AttemptMatches(evt, upperBound, candidate))
            .Where(candidate => candidate.TerminalAt is not null)
            .OrderBy(candidate => Math.Abs((candidate.CreatedAt - evt.Ts).TotalSeconds))
            .FirstOrDefault();
        if (attempt?.TerminalAt is DateTime authorityFinish)
        {
            var authorityResult = attempt.TerminalOutcome ?? attempt.State.ToString().ToLowerInvariant();
            return NewCloseout(
                evt.Ts,
                authorityFinish,
                authorityResult,
                recordedStatus: null,
                exitCode: null,
                durationSeconds: null,
                RunCloseoutSources.AttemptAuthority);
        }

        var terminalEvent = fallback.Ledger
            .Where(candidate => string.Equals(
                candidate.Kind,
                TimelineEventKinds.AgentRunFinished,
                StringComparison.Ordinal))
            .Where(candidate => candidate.Ts >= evt.Ts && candidate.Ts < upperBound)
            .Where(candidate => TimelineMatches(evt, candidate))
            .OrderBy(candidate => candidate.Ts)
            .FirstOrDefault();
        if (terminalEvent is not null)
        {
            var result = Detail(terminalEvent, "status") ?? Detail(terminalEvent, "outcome");
            var exitCode = int.TryParse(Detail(terminalEvent, "exitCode"), out var parsedCode)
                ? parsedCode
                : (int?)null;
            var duration = double.TryParse(
                Detail(terminalEvent, "durationSeconds"),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out var parsedDuration)
                ? parsedDuration
                : (double?)null;
            return NewCloseout(
                evt.Ts,
                terminalEvent.Ts,
                result,
                recordedStatus: result,
                exitCode,
                duration,
                RunCloseoutSources.Timeline);
        }

        if (exitMarker is { } marker)
        {
            return NewCloseout(
                evt.Ts,
                marker.Ts,
                result: null,
                marker.Status,
                marker.ExitCode,
                marker.Duration,
                RunCloseoutSources.CliExit);
        }

        // A later session row proves that this invocation cannot still be
        // running. Older writers did not close the predecessor before a
        // Continue/Reissue start, so heal that shape as superseded at the
        // successor boundary.
        if (upperBound != DateTime.MaxValue)
        {
            return NewCloseout(
                evt.Ts,
                upperBound,
                result: "superseded",
                recordedStatus: "superseded",
                exitCode: null,
                durationSeconds: null,
                RunCloseoutSources.SuccessorStart);
        }

        if (!fallback.TaskMayHaveActiveRun)
        {
            var lastActivity = lines
                .Where(line => line.Timestamp > evt.Ts && line.Timestamp < upperBound)
                .Select(line => (DateTime?)line.Timestamp)
                .LastOrDefault();
            if (lastActivity is DateTime activityAt)
            {
                return NewCloseout(
                    evt.Ts,
                    activityAt,
                    result: null,
                    recordedStatus: null,
                    exitCode: null,
                    durationSeconds: null,
                    RunCloseoutSources.LegacyActivity);
            }
        }

        return null;
    }

    private static ProjectedRunCloseout NewCloseout(
        DateTime startedAt,
        DateTime finishedAt,
        string? result,
        string? recordedStatus,
        int? exitCode,
        double? durationSeconds,
        string source)
    {
        var duration = durationSeconds ?? Math.Max(0, (finishedAt - startedAt).TotalSeconds);
        return new ProjectedRunCloseout(
            finishedAt,
            RunCloseoutPolicy.StatusFor(result, recordedStatus),
            string.IsNullOrWhiteSpace(result) ? null : result.Trim().ToLowerInvariant(),
            exitCode,
            duration,
            source);
    }

    private static bool AttemptMatches(SessionEvent evt, DateTime upperBound, RunAttemptDto attempt)
    {
        if (!string.IsNullOrWhiteSpace(evt.RunAttemptId))
            return string.Equals(evt.RunAttemptId, attempt.AttemptId, StringComparison.OrdinalIgnoreCase);
        return attempt.CreatedAt >= evt.Ts.AddSeconds(-2) && attempt.CreatedAt < upperBound;
    }

    private static bool TimelineMatches(SessionEvent evt, TimelineEvent candidate)
    {
        if (string.IsNullOrWhiteSpace(evt.RunAttemptId)) return true;
        return string.Equals(evt.RunAttemptId, candidate.RunId, StringComparison.OrdinalIgnoreCase)
               || string.Equals(evt.RunAttemptId, Detail(candidate, "runAttemptId"), StringComparison.OrdinalIgnoreCase);
    }

    private static string? Detail(TimelineEvent evt, string key)
        => evt.Details is not null && evt.Details.TryGetValue(key, out var value)
            ? value
            : null;

    private static bool IsRemote(SessionEvent evt)
        => string.Equals(evt.Cli, "remote-runner", StringComparison.OrdinalIgnoreCase)
           || string.Equals(evt.ExecutionLocation?.ExecutionKind, "remote", StringComparison.OrdinalIgnoreCase);

    private sealed record ProjectedRunCloseout(
        DateTime FinishedAt,
        string Status,
        string? Result,
        int? ExitCode,
        double DurationSeconds,
        string Source);

    private static (int LineIndex, DateTime Ts, string Cli)? FindFirstAfter(
        List<(int LineIndex, DateTime Ts, string Cli)> markers,
        DateTime threshold)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Ts >= threshold.AddSeconds(-2))
                return markers[i];
        }
        return null;
    }

    private static (int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)? FindFirstAfter(
        List<(int LineIndex, DateTime Ts, string Cli, string Status, int? ExitCode, double? Duration)> markers,
        DateTime threshold)
    {
        for (int i = 0; i < markers.Count; i++)
        {
            if (markers[i].Ts >= threshold)
                return markers[i];
        }
        return null;
    }

    private static string? FindLastUserBefore(IReadOnlyList<CliOutputLine> lines, int beforeIndex)
    {
        for (int i = Math.Min(beforeIndex - 1, lines.Count - 1); i >= 0; i--)
        {
            var l = lines[i];
            if (l == null) continue;
            if (string.Equals(l.Stream, "user", StringComparison.OrdinalIgnoreCase))
            {
                var text = (l.Text ?? string.Empty).Trim();
                return string.IsNullOrEmpty(text) ? null : Truncate(text, 280);
            }
        }
        return null;
    }

    private static int FindLineNearTimestamp(IReadOnlyList<CliOutputLine> lines, DateTime ts)
    {
        // Linear scan; the log files we deal with are small (thousands
        // of lines per job at the high end), so an index would be
        // overkill.
        for (int i = 0; i < lines.Count; i++)
        {
            if (lines[i].Timestamp >= ts) return i;
        }
        return lines.Count;
    }

    private static string Truncate(string s, int max)
    {
        if (s.Length <= max) return s;
        return s.Substring(0, max - 1) + "…";
    }
}
