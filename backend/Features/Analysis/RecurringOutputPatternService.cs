using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Analysis;

/// <summary>
/// Pure, testable assembly logic for the "Recurring Output Pattern Review"
/// action - the producer that scans recent project evidence for repeating
/// failure shapes (the same blocked reason across N jobs, the same noop
/// outcome, the same missing-status complaint) and proposes a steering-doc or
/// process update.
/// </summary>
/// <remarks>
/// <para>
/// Like <see cref="RoadmapAlignmentReviewService"/>, this action splits into
/// three pure steps:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks recent project lanes,
///   reads each job's <c>prompt.md</c>, <c>status.md</c>, the tail of
///   <c>logs/cli-output.log</c>, and <c>task.json</c>, then groups the
///   extracted signals (sentinel outcome, normalised reason, evidence-gaps)
///   into <see cref="RecurringPatternGroup"/>s. No agent calls happen
///   here.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template with the assembled scope so the agent can write the proposed
///   steering update against actual repeated evidence.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the JSON
///   sidecar from the agent's free-form Markdown reply with explicit
///   <see cref="AnalysisReportParseStatus"/> fallbacks. A failed parse never
///   hides the Markdown body.</description></item>
/// </list>
/// <para>
/// Constraints: this action is analysis, not editing. It produces an
/// <see cref="AnalysisReport"/> and proposes follow-up tasks; it never
/// rewrites README, AGENTS, prompts, skills, or process docs directly, never
/// moves jobs, and never relaxes the one-coding-task-per-project rule.
/// </para>
/// </remarks>
public sealed class RecurringOutputPatternService
{
    /// <summary>Schema-version sentinel reused from the analysis-report contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Canonical topic slug the UI uses for this producer.</summary>
    public const string Topic = "recurring-output-pattern";

    /// <summary>Lanes the action inspects for recent run evidence. Active +
    /// recently completed work is the right surface for "what keeps going
    /// wrong"; <c>1-preparation</c>, <c>1a-orchestrator-prep</c>, and
    /// <c>7-archive</c> are excluded by design.</summary>
    public static readonly IReadOnlyList<string> InspectedLanes = new[]
    {
        TaskStates.Progress,
        TaskStates.AutoReview,
        TaskStates.HumanReview,
        TaskStates.Completed,
    };

    /// <summary>Tail bytes of <c>cli-output.log</c> read per job so the prompt
    /// and detector see only the recent end of the conversation.</summary>
    public const int CliOutputTailBytes = 32 * 1024;

    /// <summary>Minimum group size before a pattern is surfaced. A single hit
    /// is noise; the producer's value is "this happened more than once".</summary>
    public const int MinimumPatternCount = 2;

    /// <summary>How many recent reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    private static readonly Regex JsonFenceRegex = new(
        @"```\s*json\s*\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>
    /// Walks recent jobs under <paramref name="projectRoot"/>, extracts
    /// signals from each job's evidence, and groups repeated patterns. The
    /// returned scope is plain data so tests can build fixtures without
    /// spinning up the surrounding services.
    /// </summary>
    /// <param name="project">Project name as it appears in the watch path
    /// catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.
    /// Layout: <c>{projectRoot}/{lane}/{jobId}/task.json</c>.</param>
    /// <param name="windowFrom">Earliest <c>task.json.updatedAt</c> /
    /// fallback-folder-mtime that should be considered. Pass
    /// <c>DateTime.MinValue</c> to scan everything in the inspected lanes.</param>
    /// <param name="windowTo">Wall-clock the report's "captured at" timestamp
    /// will record. Injected so tests can pin a deterministic value.</param>
    /// <param name="reportStore">Optional analysis-report store. When supplied,
    /// the service includes the most recent <see cref="RecentReportLimit"/>
    /// reports as evidence pointers so the agent can build on rather than
    /// re-derive prior findings.</param>
    /// <param name="workspaceRoot">Workspace root used by
    /// <paramref name="reportStore"/>. Pass <c>null</c> when no store lookup is
    /// needed (the service degrades gracefully).</param>
    public RecurringPatternScope SelectScope(
        string project,
        string projectRoot,
        DateTime windowFrom,
        DateTime windowTo,
        AnalysisReportStore? reportStore = null,
        string? workspaceRoot = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);

        var jobs = new List<TaskEvidence>();
        foreach (var lane in InspectedLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            if (!Directory.Exists(laneDir)) continue;

            foreach (var dir in Directory.EnumerateDirectories(laneDir))
            {
                var ev = ReadJobEvidence(dir, lane);
                if (ev is null) continue;
                if (ev.LastActivityAt < windowFrom) continue;
                jobs.Add(ev);
            }
        }

        // Stable order so prompts diff cleanly across runs.
        jobs.Sort((a, b) => string.CompareOrdinal(a.JobId, b.JobId));

        var groups = GroupPatterns(jobs);
        var recent = LookupRecentReports(reportStore, workspaceRoot, project);

        return new RecurringPatternScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            CapturedAt = windowTo,
            WindowFrom = windowFrom,
            WindowTo = windowTo,
            Jobs = jobs,
            Groups = groups,
            RecentReports = recent,
        };
    }

    /// <summary>
    /// Renders the prompt template with the assembled scope. Placeholders
    /// follow the existing <see cref="RuntimePromptService"/> convention
    /// (<c>{{name}}</c>) so the template is editable as plain Markdown.
    /// </summary>
    public string BuildPrompt(RecurringPatternScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["window_from"] = scope.WindowFrom == DateTime.MinValue
                ? "(open-ended)"
                : scope.WindowFrom.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["window_to"] = scope.WindowTo.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["project_root"] = scope.ProjectRoot,
            ["job_count"] = scope.Jobs.Count.ToString(),
            ["pattern_groups"] = RenderGroups(scope),
            ["job_evidence"] = RenderJobs(scope),
            ["recent_reports"] = RenderRecentReports(scope),
            ["has_findings_flag"] = scope.Groups.Count > 0 ? "yes" : "no",
        };

        return Regex.Replace(template, @"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", m =>
        {
            var key = m.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var v) ? v ?? string.Empty : m.Value;
        });
    }

    /// <summary>
    /// Extracts a JSON sidecar from a free-form agent reply. Returns the parse
    /// status the report should record, the typed fields the JSON described
    /// (when present), and a parser error suitable for
    /// <see cref="AnalysisReport.ParseError"/> when validation failed.
    /// </summary>
    /// <remarks>
    /// Three states are explicit and mirror <see cref="AnalysisReportParseStatus"/>:
    /// Structured (fenced JSON parsed cleanly), Unstructured (no fenced JSON
    /// at all - Markdown body is the only artifact), MalformedJson (JSON
    /// present but failed to parse or validate; parser error carried back).
    /// </remarks>
    public RecurringPatternParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new RecurringPatternParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: "Agent reply was empty; no structured analysis available.",
                Findings: null,
                FollowUps: null,
                Confidence: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new RecurringPatternParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: ExtractFirstHeadingOrLine(rawText)
                    ?? "Agent reply contained no structured JSON sidecar.",
                Findings: null,
                FollowUps: null,
                Confidence: null,
                ParseError: null);
        }

        var jsonBody = match.Groups["body"].Value;
        try
        {
            var dto = JsonSerializer.Deserialize<RecurringPatternJsonDto>(jsonBody, ParseOptions);
            if (dto is null)
            {
                return Malformed("JSON sidecar parsed to null.", rawText);
            }

            if (string.IsNullOrWhiteSpace(dto.Verdict))
            {
                return Malformed("JSON sidecar missing required field 'verdict'.", rawText);
            }

            var severity = ParseSeverity(dto.Severity)
                ?? throw new JsonException(
                    $"severity must be one of Info|Warn|High|Critical (was '{dto.Severity}').");

            var findings = (dto.Findings ?? Array.Empty<RecurringPatternFindingDto>())
                .Select(f => new AnalysisReportFinding(
                    Topic: string.IsNullOrWhiteSpace(f.Topic) ? "recurring-pattern" : f.Topic.Trim(),
                    Severity: ParseSeverity(f.Severity) ?? AnalysisReportSeverity.Info,
                    Message: (f.Message ?? string.Empty).Trim(),
                    EvidenceRefs: f.EvidenceRefs))
                .Where(f => !string.IsNullOrWhiteSpace(f.Message))
                .ToArray();

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<RecurringPatternFollowUpDto>())
                .Select(s => new AnalysisReportFollowUpTaskSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: ParseRelatedTopic(s.RelatedTopic),
                    TargetState: NormaliseTargetState(s.TargetState)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new RecurringPatternParseResult(
                Status: AnalysisReportParseStatus.Structured,
                Severity: severity,
                Summary: dto.Verdict.Trim(),
                Findings: findings,
                FollowUps: followUps,
                Confidence: NormaliseConfidence(dto.Confidence),
                ParseError: null);
        }
        catch (JsonException ex)
        {
            return Malformed($"JSON sidecar failed to parse: {ex.Message}", rawText);
        }
        catch (Exception ex)
        {
            return Malformed($"JSON sidecar failed validation: {ex.Message}", rawText);
        }
    }

    /// <summary>
    /// Composes the typed <see cref="AnalysisReport"/> for one run. The caller
    /// supplies the report id (ULID/UUID v7) and the Markdown body so this
    /// service stays free of clock + id concerns the endpoint already owns.
    /// </summary>
    public AnalysisReport BuildReport(
        RecurringPatternScope scope,
        RecurringPatternParseResult parse,
        string reportId,
        DateTime createdAt,
        AnalysisReportProducerKind producerKind = AnalysisReportProducerKind.Manual,
        AnalysisReportTrigger trigger = AnalysisReportTrigger.Manual,
        string? participantId = null,
        string? agent = null)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        var noFinding = scope.Groups.Count == 0;
        var summary = noFinding
            ? "No recurring pattern detected in the inspected window."
            : parse.Summary;
        var severity = noFinding ? AnalysisReportSeverity.Info : parse.Severity;

        var references = BuildReferences(scope);
        return new AnalysisReport(
            ReportId: reportId,
            CreatedAt: createdAt,
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: scope.Project),
            Producer: new AnalysisReportProducer(producerKind, ParticipantId: participantId, Agent: agent),
            Trigger: trigger,
            Topic: Topic,
            Summary: summary,
            Severity: severity,
            ParseStatus: parse.Status,
            References: references,
            FollowUpTaskSuggestions: noFinding
                ? Array.Empty<AnalysisReportFollowUpTaskSuggestion>()
                : parse.FollowUps ?? Array.Empty<AnalysisReportFollowUpTaskSuggestion>(),
            ParseError: parse.ParseError,
            Tags: BuildTags(scope, parse, noFinding),
            Findings: noFinding ? Array.Empty<AnalysisReportFinding>() : parse.Findings,
            SchemaVersion: CurrentSchemaVersion);
    }

    // ------------------------------------------------------------------
    // Pattern extraction
    // ------------------------------------------------------------------

    private static TaskEvidence? ReadJobEvidence(string dir, string lane)
    {
        var jobJson = Path.Combine(dir, "task.json");
        if (!File.Exists(jobJson)) return null;

        string id = Path.GetFileName(dir);
        string title = id;
        string? agent = null;
        string? cliType = null;
        DateTime updatedAt = SafeMtime(jobJson);
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(jobJson));
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString() ?? id;
            if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? id;
            if (root.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.String)
                agent = agentEl.GetString();
            if (root.TryGetProperty("cliType", out var cliEl) && cliEl.ValueKind == JsonValueKind.String)
                cliType = cliEl.GetString();
            if (root.TryGetProperty("updatedAt", out var upEl) && upEl.ValueKind == JsonValueKind.String
                && DateTime.TryParse(upEl.GetString(), null, System.Globalization.DateTimeStyles.RoundtripKind, out var parsed))
            {
                updatedAt = parsed;
            }
        }
        catch (JsonException __ex)
        {
            SilentCatch.Note(__ex, "RecurringOutputPatternService: Malformed task.json: keep defaults; the job still surfaces with");
            // Malformed task.json: keep defaults; the job still surfaces with
            // its folder id so the agent can flag it.
        }

        var statusPath = Path.Combine(dir, "status.md");
        var hasStatus = File.Exists(statusPath) && new FileInfo(statusPath).Length > 0;

        var logPath = Path.Combine(dir, "logs", "cli-output.log");
        var (logTail, runStarts) = ReadLogTail(logPath);

        var sentinel = ExtractLastSentinel(logTail);
        var hasCommits = HasCommitMarker(logTail);
        var hasScreenshots = HasResultsScreenshots(dir);

        return new TaskEvidence
        {
            JobId = id,
            Title = title,
            Lane = lane,
            Agent = agent,
            CliType = cliType,
            LastActivityAt = updatedAt,
            HasStatus = hasStatus,
            HasCliOutputLog = File.Exists(logPath),
            HasCommitMarker = hasCommits,
            HasScreenshots = hasScreenshots,
            RunStartCount = runStarts,
            SentinelKeyword = sentinel?.Keyword,
            SentinelReason = sentinel?.Reason,
        };
    }

    private static DateTime SafeMtime(string path)
    {
        try { return File.GetLastWriteTimeUtc(path); }
        catch { return DateTime.MinValue; }
    }

    private static (string tail, int runStarts) ReadLogTail(string path)
    {
        if (!File.Exists(path)) return (string.Empty, 0);
        try
        {
            string tail;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            {
                var len = fs.Length;
                var read = (int)Math.Min(len, CliOutputTailBytes);
                fs.Seek(-read, SeekOrigin.End);
                var buf = new byte[read];
                var got = fs.Read(buf, 0, read);
                tail = Encoding.UTF8.GetString(buf, 0, got);
            }

            // Count "[taskboard] Started" markers across the full file so
            // retry counts survive long sessions where the marker scrolls out
            // of the tail window.
            int starts = 0;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(fs, Encoding.UTF8))
            {
                string? line;
                while ((line = reader.ReadLine()) is not null)
                {
                    if (StartedMarkerRegex.IsMatch(line)) starts++;
                }
            }
            return (tail, starts);
        }
        catch
        {
            return (string.Empty, 0);
        }
    }

    private static readonly Regex StartedMarkerRegex = new(
        @"\[taskboard\]\s+Started",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex CommitMarkerRegex = new(
        @"\[taskboard\]\s+Committed|^\s*\[?[a-f0-9]{7,}\]?\s+(feat|fix|chore|docs|refactor|test|perf|build|ci|style|revert)",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.Multiline);

    private static (string Keyword, string? Reason)? ExtractLastSentinel(string text)
    {
        if (string.IsNullOrEmpty(text)) return null;
        var matches = AgentOutcomeAnalyzer.SentinelRegex.Matches(text);
        if (matches.Count == 0) return null;
        var last = matches[^1];
        var keyword = last.Groups["keyword"].Value.ToUpperInvariant();
        var reason = last.Groups["reason"].Success ? last.Groups["reason"].Value.Trim() : null;
        if (string.IsNullOrWhiteSpace(reason)) reason = null;
        return (keyword, reason);
    }

    private static bool HasCommitMarker(string tail) =>
        !string.IsNullOrEmpty(tail) && CommitMarkerRegex.IsMatch(tail);

    private static bool HasResultsScreenshots(string jobDir)
    {
        var resultsDir = Path.Combine(jobDir, "results");
        if (!Directory.Exists(resultsDir)) return false;
        try
        {
            foreach (var f in Directory.EnumerateFiles(resultsDir, "*", SearchOption.AllDirectories))
            {
                var ext = Path.GetExtension(f);
                if (string.Equals(ext, ".png", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".jpg", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".jpeg", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".webp", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(ext, ".gif", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }
        catch (Exception __ex) { SilentCatch.Note(__ex, "RecurringOutputPatternService: best-effort"); /* best-effort */ }
        return false;
    }

    private static List<RecurringPatternGroup> GroupPatterns(IReadOnlyList<TaskEvidence> jobs)
    {
        var groups = new Dictionary<string, RecurringPatternGroupBuilder>(StringComparer.Ordinal);

        foreach (var j in jobs)
        {
            // 1) Sentinel-shaped outcomes: same blocked/needs-input reason
            //    appearing across multiple jobs is the single highest-signal
            //    pattern.
            if (!string.IsNullOrWhiteSpace(j.SentinelKeyword)
                && (j.SentinelKeyword == "BLOCKED" || j.SentinelKeyword == "NEEDS_INPUT"))
            {
                var key = j.SentinelKeyword.ToLowerInvariant() + ":" + NormaliseReason(j.SentinelReason);
                AddTo(groups, new RecurringPatternKind(
                    Kind: j.SentinelKeyword == "BLOCKED" ? "blocked-reason" : "needs-input-reason",
                    NormalisedKey: key,
                    SampleLabel: j.SentinelReason ?? "(no reason)"),
                    j);
            }

            // 2) Repeated [[TASK_NOOP]]: a no-op outcome on a task that was
            //    expected to do work points at prompt ambiguity or recovery
            //    failure, not at the user.
            if (string.Equals(j.SentinelKeyword, "NOOP", StringComparison.Ordinal))
            {
                AddTo(groups, new RecurringPatternKind(
                    Kind: "noop",
                    NormalisedKey: "noop",
                    SampleLabel: "[[TASK_NOOP]]"),
                    j);
            }

            // 3) Repeated retry pattern: jobs whose CLI was started multiple
            //    times against the same job folder. One retry is normal;
            //    several is a recovery / session-loss signal.
            if (j.RunStartCount >= 3)
            {
                AddTo(groups, new RecurringPatternKind(
                    Kind: "repeated-retries",
                    NormalisedKey: "repeated-retries",
                    SampleLabel: $"{j.RunStartCount} starts"),
                    j);
            }

            // 4) Missing evidence: a job that landed in human-review or
            //    completed without a status.md is a documentation gap.
            if (!j.HasStatus
                && (string.Equals(j.Lane, TaskStates.HumanReview, StringComparison.Ordinal)
                    || string.Equals(j.Lane, TaskStates.Completed, StringComparison.Ordinal)))
            {
                AddTo(groups, new RecurringPatternKind(
                    Kind: "missing-status",
                    NormalisedKey: "missing-status",
                    SampleLabel: "no status.md present"),
                    j);
            }
        }

        return groups.Values
            .Where(g => g.Members.Count >= MinimumPatternCount)
            .OrderByDescending(g => g.Members.Count)
            .ThenBy(g => g.NormalisedKey, StringComparer.Ordinal)
            .Select(g => g.ToRecord())
            .ToList();
    }

    private static void AddTo(
        Dictionary<string, RecurringPatternGroupBuilder> groups,
        RecurringPatternKind kind,
        TaskEvidence job)
    {
        var key = kind.Kind + "::" + kind.NormalisedKey;
        if (!groups.TryGetValue(key, out var b))
        {
            b = new RecurringPatternGroupBuilder(kind);
            groups[key] = b;
        }
        b.Members.Add(job);
    }

    /// <summary>
    /// Lowercase, collapse whitespace, strip path-like fragments, and clip to
    /// 80 chars so two reasons that differ only by transient detail land in
    /// the same group.
    /// </summary>
    public static string NormaliseReason(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "(no reason)";
        var s = raw.Trim().ToLowerInvariant();
        // Collapse whitespace.
        s = Regex.Replace(s, @"\s+", " ");
        // Strip absolute paths and hash-y tokens that defeat grouping.
        s = Regex.Replace(s, @"[a-z]:\\[^\s]+", "<path>");
        s = Regex.Replace(s, @"/[^\s]+/[^\s]+", "<path>");
        s = Regex.Replace(s, @"\b[0-9a-f]{7,}\b", "<hash>");
        s = Regex.Replace(s, @"\b\d{2,}\b", "<n>");
        // Strip a trailing "<at|in|for|on> <path|hash|n> ..." tail so a reason
        // that differs only by where the failure was observed groups with its
        // siblings.
        s = Regex.Replace(s, @"\s+(?:at|in|for|on|under|near)\s+<(?:path|hash|n)>.*$", string.Empty);
        s = s.TrimEnd();
        if (s.Length > 80) s = s[..80];
        return s;
    }

    // ------------------------------------------------------------------
    // Rendering helpers
    // ------------------------------------------------------------------

    private static IReadOnlyList<AnalysisReportPointer> LookupRecentReports(
        AnalysisReportStore? store, string? workspaceRoot, string project)
    {
        if (store is null || string.IsNullOrWhiteSpace(workspaceRoot)) return Array.Empty<AnalysisReportPointer>();
        var snapshot = store.Snapshot(workspaceRoot, project);
        return snapshot
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentReportLimit)
            .Select(r => new AnalysisReportPointer(
                ReportId: r.ReportId,
                Topic: r.Topic,
                CreatedAt: r.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")))
            .ToArray();
    }

    private static string RenderGroups(RecurringPatternScope scope)
    {
        if (scope.Groups.Count == 0)
            return "(no recurring patterns detected in the window)";

        var sb = new StringBuilder();
        sb.AppendLf("| Kind | Sample | Hits | Job ids |");
        sb.AppendLf("|------|--------|-----:|---------|");
        foreach (var g in scope.Groups)
        {
            sb.Append("| ").Append(g.Kind).Append(" | ");
            sb.Append(EscapeMd(g.SampleLabel)).Append(" | ");
            sb.Append(g.Members.Count).Append(" | ");
            sb.Append(string.Join(", ", g.Members.Select(m => "`" + m.JobId + "`")));
            sb.AppendLf(" |");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderJobs(RecurringPatternScope scope)
    {
        if (scope.Jobs.Count == 0) return "(no jobs in the window)";
        var sb = new StringBuilder();
        foreach (var j in scope.Jobs)
        {
            sb.Append("- `").Append(j.JobId).Append("` (").Append(j.Lane).Append(")");
            if (!string.IsNullOrWhiteSpace(j.CliType))
                sb.Append(" cli=").Append(j.CliType);
            if (!string.IsNullOrWhiteSpace(j.SentinelKeyword))
                sb.Append(" outcome=").Append(j.SentinelKeyword!.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(j.SentinelReason))
                sb.Append(" reason=\"").Append(EscapeMd(j.SentinelReason!)).Append('"');
            sb.Append(" runs=").Append(j.RunStartCount);
            if (!j.HasStatus) sb.Append(" no-status");
            if (!j.HasCommitMarker) sb.Append(" no-commit");
            if (!j.HasScreenshots) sb.Append(" no-screenshots");
            sb.AppendLf();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderRecentReports(RecurringPatternScope scope)
    {
        if (scope.RecentReports.Count == 0) return "(no prior analysis reports for this project)";
        var sb = new StringBuilder();
        foreach (var r in scope.RecentReports)
            sb.Append("- `").Append(r.ReportId).Append("` _(").Append(r.Topic).Append(", ").Append(r.CreatedAt).AppendLf(")_");
        return sb.ToString().TrimEnd();
    }

    private static string EscapeMd(string s) => s.Replace("|", "\\|").Replace("\n", " ").Replace("\r", " ");

    private static IReadOnlyList<AnalysisReportReference> BuildReferences(RecurringPatternScope scope)
    {
        var refs = new List<AnalysisReportReference>();
        // Cite each job by stable id; do not copy log bodies.
        foreach (var j in scope.Jobs)
        {
            refs.Add(new AnalysisReportReference(
                Kind: AnalysisReportReferenceKind.Job,
                Ref: $"{scope.Project}/{j.Lane}/{j.JobId}",
                Label: j.Title));

            if (j.HasCliOutputLog)
            {
                refs.Add(new AnalysisReportReference(
                    Kind: AnalysisReportReferenceKind.LogSlice,
                    Ref: $"{scope.Project}/{j.Lane}/{j.JobId}/logs/cli-output.log",
                    Label: "cli-output.log tail"));
            }
        }
        foreach (var r in scope.RecentReports)
        {
            refs.Add(new AnalysisReportReference(
                AnalysisReportReferenceKind.PreviousReport,
                r.ReportId,
                $"{r.Topic} @ {r.CreatedAt}"));
        }
        return refs;
    }

    private static IReadOnlyList<string> BuildTags(
        RecurringPatternScope scope,
        RecurringPatternParseResult parse,
        bool noFinding)
    {
        var tags = new List<string> { "recurring-output-pattern" };
        if (noFinding) tags.Add("no-finding");
        else
        {
            foreach (var g in scope.Groups)
            {
                var t = "pattern:" + g.Kind;
                if (!tags.Contains(t)) tags.Add(t);
            }
        }
        if (parse.Status == AnalysisReportParseStatus.Unstructured) tags.Add("unstructured");
        if (parse.Status == AnalysisReportParseStatus.MalformedJson) tags.Add("malformed-json");
        return tags;
    }

    private static RecurringPatternParseResult Malformed(string error, string rawText)
        => new(
            Status: AnalysisReportParseStatus.MalformedJson,
            Severity: AnalysisReportSeverity.Info,
            Summary: ExtractFirstHeadingOrLine(rawText)
                ?? "Agent reply contained an unparseable JSON sidecar; Markdown body remains the durable artifact.",
            Findings: null,
            FollowUps: null,
            Confidence: null,
            ParseError: error);

    private static string? ExtractFirstHeadingOrLine(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.Length == 0) continue;
            if (line.StartsWith("```", StringComparison.Ordinal)) continue;
            if (line.StartsWith("#", StringComparison.Ordinal))
                return line.TrimStart('#').Trim();
            return line.Length > 200 ? line[..200] + "…" : line;
        }
        return null;
    }

    private static AnalysisReportSeverity? ParseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AnalysisReportSeverity.Info;
        return Enum.TryParse<AnalysisReportSeverity>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static AnalysisReportFollowUpPriority? ParseFollowUpPriority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return AnalysisReportFollowUpPriority.Normal;
        return Enum.TryParse<AnalysisReportFollowUpPriority>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static AnalysisReportFollowUpRelatedTopic? ParseRelatedTopic(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<AnalysisReportFollowUpRelatedTopic>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static string? NormaliseTargetState(string? raw)
    {
        // Constraint: this action proposes process changes; suggested follow-
        // ups must land in 1-preparation so the user reviews the proposed
        // steering update before it becomes a queued task.
        _ = raw;
        return AnalysisReportFollowUpTargetStates.OnePreparation;
    }

    private static double? NormaliseConfidence(double? raw)
    {
        if (raw is null) return null;
        if (raw < 0) return 0;
        if (raw > 1) return 1;
        return raw;
    }

    // ------------------------------------------------------------------
    // Records
    // ------------------------------------------------------------------

    /// <summary>One job's extracted evidence.</summary>
    public sealed record TaskEvidence
    {
        public required string JobId { get; init; }
        public required string Title { get; init; }
        public required string Lane { get; init; }
        public string? Agent { get; init; }
        public string? CliType { get; init; }
        public required DateTime LastActivityAt { get; init; }
        public required bool HasStatus { get; init; }
        public required bool HasCliOutputLog { get; init; }
        public required bool HasCommitMarker { get; init; }
        public required bool HasScreenshots { get; init; }
        public required int RunStartCount { get; init; }
        public string? SentinelKeyword { get; init; }
        public string? SentinelReason { get; init; }
    }

    /// <summary>One detected recurring-pattern group.</summary>
    public sealed record RecurringPatternGroup(
        string Kind,
        string NormalisedKey,
        string SampleLabel,
        IReadOnlyList<TaskEvidence> Members);

    /// <summary>Pointer to a recent analysis report so the agent can build on
    /// rather than restart the conversation.</summary>
    public sealed record AnalysisReportPointer(string ReportId, string Topic, string CreatedAt);

    private sealed record RecurringPatternKind(string Kind, string NormalisedKey, string SampleLabel);

    private sealed class RecurringPatternGroupBuilder
    {
        public RecurringPatternKind Kind { get; }
        public List<TaskEvidence> Members { get; } = new();
        public string NormalisedKey => Kind.NormalisedKey;
        public RecurringPatternGroupBuilder(RecurringPatternKind kind) { Kind = kind; }
        public RecurringPatternGroup ToRecord() =>
            new(Kind.Kind, Kind.NormalisedKey, Kind.SampleLabel, Members);
    }

    private sealed record RecurringPatternJsonDto
    {
        public string? Verdict { get; init; }
        public string? Severity { get; init; }
        public double? Confidence { get; init; }
        public RecurringPatternFindingDto[]? Findings { get; init; }
        public RecurringPatternFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record RecurringPatternFindingDto
    {
        public string? Topic { get; init; }
        public string? Severity { get; init; }
        public string? Message { get; init; }
        public string[]? EvidenceRefs { get; init; }
    }

    private sealed record RecurringPatternFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedTopic { get; init; }
        public string? TargetState { get; init; }
    }
}

/// <summary>Snapshot of the recent-evidence + pattern-group input the action
/// gathered before talking to the agent.</summary>
public sealed class RecurringPatternScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required DateTime CapturedAt { get; init; }
    public required DateTime WindowFrom { get; init; }
    public required DateTime WindowTo { get; init; }
    public required IReadOnlyList<RecurringOutputPatternService.TaskEvidence> Jobs { get; init; }
    public required IReadOnlyList<RecurringOutputPatternService.RecurringPatternGroup> Groups { get; init; }
    public required IReadOnlyList<RecurringOutputPatternService.AnalysisReportPointer> RecentReports { get; init; }
}

/// <summary>Result of <see cref="RecurringOutputPatternService.TryParseAgentResponse"/>.
/// Mirrors <see cref="RoadmapAlignmentParseResult"/> shape so consumers can
/// share rendering code.</summary>
public sealed record RecurringPatternParseResult(
    AnalysisReportParseStatus Status,
    AnalysisReportSeverity Severity,
    string Summary,
    IReadOnlyList<AnalysisReportFinding>? Findings,
    IReadOnlyList<AnalysisReportFollowUpTaskSuggestion>? FollowUps,
    double? Confidence,
    string? ParseError);
