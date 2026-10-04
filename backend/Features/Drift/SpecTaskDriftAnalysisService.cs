using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Drift;

/// <summary>
/// Pure, testable assembly logic for the **Spec / Task / Job Drift** action -
/// the named producer that compares specifications, task prompts, queued and
/// in-flight jobs, completed evidence, and prior analysis reports to find
/// where work is diverging from intent.
/// </summary>
/// <remarks>
/// <para>
/// Three pure steps mirror <see cref="AdrCodeDriftAnalysisService"/> and
/// <see cref="DocsMarketingDriftAnalysisService"/> so each piece is covered by
/// its own test and the action stays read-only:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks the spec / planning
///   doc set, the active queue lanes (1-preparation through 5-human-review),
///   the recent-completed lane, and recent drift / analysis reports.
///   It also captures one short prompt excerpt per active job so the agent
///   can spot duplicates, contradictions, and missing-context prompts without
///   having to re-read every prompt.md.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template at <c>prompts/runtime/spec-task-job-drift.md</c>.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the
///   structured JSON sidecar from the agent's free-form Markdown reply with
///   explicit Structured / Unstructured / MalformedJson fallbacks. A failed
///   parse never hides the Markdown body.</description></item>
/// </list>
/// <para>
/// The action is analysis, not state mutation. It produces a
/// <see cref="DriftReport"/> and proposes follow-up tasks; it never edits a
/// task prompt, never moves a job between lanes, and never relaxes the "one
/// active coding task per project" boundary.
/// </para>
/// </remarks>
public sealed class SpecTaskDriftAnalysisService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Topic slug used to label the report and tag follow-ups.</summary>
    public const string Topic = "spec-task-job-drift";

    /// <summary>How many recent reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    /// <summary>How many active jobs per lane to surface in the prompt.</summary>
    public const int QueueJobsPerLaneLimit = 20;

    /// <summary>How many recent completed jobs to surface.</summary>
    public const int RecentCompletedLimit = 12;

    /// <summary>How many characters of <c>prompt.md</c> to inline per job. The
    /// excerpt is large enough for the agent to spot duplicates and missing
    /// context, but capped so a single task with a giant prompt cannot crowd
    /// out the rest of the queue.</summary>
    public const int PromptExcerptLimit = 600;

    /// <summary>Lanes representing active work. Spec / Task / Job drift pays
    /// attention to the full active pipeline because the question "do these
    /// queued tasks still match intent" requires looking at preparation and
    /// in-flight work, not just completed evidence.</summary>
    public static readonly IReadOnlyList<string> ActiveLanes = new[]
    {
        TaskStates.Preparation,
        TaskStates.OrchestratorPrep,
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
        TaskStates.HumanReview,
    };

    /// <summary>Lane sampled for recently shipped evidence (used to spot
    /// completed work that contradicts the current backlog).</summary>
    public const string RecentCompletedLane = TaskStates.Completed;

    private static readonly Regex JsonFenceRegex = new(
        @"```\s*json\s*\r?\n(?<body>[\s\S]*?)```",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly JsonSerializerOptions ParseOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
    };

    /// <summary>Walks the spec / planning doc set, the active queue lanes,
    /// the recent-completed lane, and recent drift / analysis reports.
    /// Returns a typed scope record the prompt template renders against.</summary>
    /// <param name="project">Project name as it appears in the watch path catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.</param>
    /// <param name="repoRoot">Source repository root (the dev checkout).</param>
    /// <param name="driftStore">Optional drift-report store; surfaces recent
    /// reports as evidence pointers.</param>
    /// <param name="analysisStore">Optional analysis-report store.</param>
    /// <param name="workspaceRoot">Workspace root used by both stores.</param>
    /// <param name="now">Wall-clock for the scope record.</param>
    public SpecTaskJobDriftScope SelectScope(
        string project,
        string projectRoot,
        string repoRoot,
        DriftReportStore? driftStore = null,
        AnalysisReportStore? analysisStore = null,
        string? workspaceRoot = null,
        DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var specDocs = BuildSpecDocList(repoRoot);
        var activeJobs = BuildActiveJobs(projectRoot);
        var recentCompleted = BuildRecentCompleted(projectRoot);
        var duplicateCandidates = DetectDuplicateTaskCandidates(activeJobs);
        var recentDrift = LookupRecentDriftReports(driftStore, workspaceRoot, project);
        var recentAnalysis = LookupRecentAnalysisReports(analysisStore, workspaceRoot, project);

        return new SpecTaskJobDriftScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            SpecDocs = specDocs,
            ActiveJobs = activeJobs,
            RecentCompleted = recentCompleted,
            DuplicateCandidates = duplicateCandidates,
            RecentDriftReports = recentDrift,
            RecentAnalysisReports = recentAnalysis,
            CapturedAt = now ?? DateTime.UtcNow,
        };
    }

    /// <summary>Renders the prompt template with the assembled scope.</summary>
    public string BuildPrompt(SpecTaskJobDriftScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["spec_docs"] = RenderRefList(scope.SpecDocs),
            ["active_jobs"] = RenderActiveJobs(scope.ActiveJobs),
            ["recent_completed"] = RenderJobRefs(scope.RecentCompleted),
            ["duplicate_candidates"] = RenderDuplicates(scope.DuplicateCandidates),
            ["recent_drift_reports"] = RenderReportPointers(scope.RecentDriftReports),
            ["recent_analysis_reports"] = RenderReportPointers(scope.RecentAnalysisReports),
        };

        return Regex.Replace(template, @"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", m =>
        {
            var key = m.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var v) ? v ?? string.Empty : m.Value;
        });
    }

    /// <summary>Extracts a JSON sidecar from a free-form agent reply. Returns
    /// the parse state, the typed dimensions, the verdict, and any parser
    /// error. A failed parse never hides the Markdown body.</summary>
    public SpecTaskJobDriftParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SpecTaskJobDriftParseResult(
                Status: SpecTaskJobDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: "Agent reply was empty; no structured spec / task / job drift analysis available.",
                Dimensions: null,
                FollowUps: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new SpecTaskJobDriftParseResult(
                Status: SpecTaskJobDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: ExtractFirstHeadingOrLine(rawText)
                    ?? "Agent reply contained no structured JSON sidecar.",
                Dimensions: null,
                FollowUps: null,
                ParseError: null);
        }

        var jsonBody = match.Groups["body"].Value;
        try
        {
            var dto = JsonSerializer.Deserialize<SpecTaskJobDriftJsonDto>(jsonBody, ParseOptions);
            if (dto is null)
            {
                return Malformed("JSON sidecar parsed to null.", rawText);
            }
            if (string.IsNullOrWhiteSpace(dto.Verdict))
            {
                return Malformed("JSON sidecar missing required field 'verdict'.", rawText);
            }
            var band = ParseScoreBand(dto.ScoreBand);
            if (band is null)
            {
                return Malformed(
                    $"scoreBand must be one of Healthy|Watch|Warn|Critical|Unknown (was '{dto.ScoreBand}').",
                    rawText);
            }
            var overall = dto.OverallScore;
            if (overall is < 0 or > 100)
            {
                return Malformed($"overallScore must be 0..100 (was {overall}).", rawText);
            }

            // Dimensions are optional in the agent's reply - a "no findings"
            // verdict can omit the array entirely. BuildReport synthesises a
            // single TaskJob entry so the resulting record is still
            // schema-valid (dimensions: minItems=1).
            var dims = ParseDimensions(dto.Dimensions);

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<SpecTaskJobDriftFollowUpDto>())
                .Select(s => new DriftFollowUpSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? DriftFollowUpPriority.Normal,
                    RelatedDimension: ParseDimensionType(s.RelatedDimension)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new SpecTaskJobDriftParseResult(
                Status: SpecTaskJobDriftParseStatus.Structured,
                ScoreBand: band.Value,
                OverallScore: overall,
                Summary: dto.Verdict.Trim(),
                Dimensions: dims,
                FollowUps: followUps,
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

    /// <summary>Composes the typed <see cref="DriftReport"/> for one run.</summary>
    public DriftReport BuildReport(
        SpecTaskJobDriftScope scope,
        SpecTaskJobDriftParseResult parse,
        string reportId,
        DateTime createdAt,
        DriftReportTrigger trigger = DriftReportTrigger.Manual)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        var sourceRefs = BuildSourceRefs(scope);

        IReadOnlyList<DriftDimension> dimensions;
        DriftScoreBand band;
        int overall;

        if (parse.Status == SpecTaskJobDriftParseStatus.Structured)
        {
            dimensions = (parse.Dimensions is { Count: > 0 })
                ? parse.Dimensions
                : new[]
                {
                    new DriftDimension(
                        Type: DriftDimensionType.TaskJob,
                        Score: 100,
                        Severity: DriftSeverity.Info,
                        Confidence: 0.5,
                        SourceCoverage: 0.5,
                        Status: DriftFindingStatus.New,
                        Summary: "No drift findings reported; agent verdict is healthy.",
                        EvidenceRefs: BuildEvidenceRefSnapshot(scope),
                        RecommendedActions: Array.Empty<string>()),
                };
            band = parse.ScoreBand;
            overall = parse.OverallScore;
        }
        else
        {
            dimensions = new[]
            {
                new DriftDimension(
                    Type: DriftDimensionType.TaskJob,
                    Score: 0,
                    Severity: DriftSeverity.Info,
                    Confidence: 0,
                    SourceCoverage: 0,
                    Status: DriftFindingStatus.New,
                    Summary: parse.Status == SpecTaskJobDriftParseStatus.MalformedJson
                        ? $"Agent JSON sidecar failed to parse; Markdown body remains the durable artifact. Reason: {parse.ParseError}"
                        : "No agent narrative supplied; evidence-only scope assembled.",
                    EvidenceRefs: BuildEvidenceRefSnapshot(scope),
                    RecommendedActions: new[]
                    {
                        "Run the embedded prompt against a CLI agent and POST the reply back.",
                    }),
            };
            band = DriftScoreBand.Unknown;
            overall = 0;
        }

        var followUps = parse.FollowUps ?? Array.Empty<DriftFollowUpSuggestion>();

        var parseStatus = parse.Status switch
        {
            SpecTaskJobDriftParseStatus.Structured => DriftReportParseStatus.Structured,
            SpecTaskJobDriftParseStatus.MalformedJson => DriftReportParseStatus.MalformedJson,
            _ => DriftReportParseStatus.Unstructured,
        };

        var report = new DriftReport(
            ReportId: reportId,
            Project: scope.Project,
            CreatedAt: createdAt,
            Trigger: trigger,
            Scope: new DriftReportScope(
                Kind: DriftReportScopeKind.Project,
                SourceRefs: sourceRefs),
            OverallScore: overall,
            ScoreBand: band,
            Dimensions: dimensions,
            Summary: parse.Summary,
            FollowUpTaskSuggestions: followUps,
            SchemaVersion: CurrentSchemaVersion,
            Producer: new DriftReportProducer(MapProducerKind(trigger), Agent: Topic),
            ParseStatus: parseStatus,
            ParseError: parse.ParseError);

        return report;
    }

    /// <summary>
    /// Detects pairs of active jobs that look like duplicates by token-overlap
    /// on the slug and the title. The result is a *hint* surfaced to the
    /// agent in the prompt; the agent makes the final call. Exposed as a
    /// public hook so a test can pin the heuristic.
    /// </summary>
    public static IReadOnlyList<DuplicateTaskPair> DetectDuplicateTaskCandidates(
        IReadOnlyList<ActiveJobRef> jobs,
        double overlapThreshold = 0.6)
    {
        if (jobs is null || jobs.Count < 2) return Array.Empty<DuplicateTaskPair>();

        var tokens = jobs
            .Select(j => (Job: j, Tokens: Tokenize(j.JobId + " " + j.Title)))
            .Where(t => t.Tokens.Count > 0)
            .ToArray();

        var pairs = new List<DuplicateTaskPair>();
        for (int i = 0; i < tokens.Length; i++)
        {
            for (int j = i + 1; j < tokens.Length; j++)
            {
                var a = tokens[i];
                var b = tokens[j];
                if (a.Job.JobId == b.Job.JobId) continue;

                var overlap = JaccardOverlap(a.Tokens, b.Tokens);
                if (overlap >= overlapThreshold)
                {
                    pairs.Add(new DuplicateTaskPair(
                        LeftLane: a.Job.Lane,
                        LeftJobId: a.Job.JobId,
                        RightLane: b.Job.Lane,
                        RightJobId: b.Job.JobId,
                        Overlap: Math.Round(overlap, 3)));
                }
            }
        }

        return pairs
            .OrderByDescending(p => p.Overlap)
            .ToArray();
    }

    private static HashSet<string> Tokenize(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var tokens = Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length >= 3)
            .ToArray();
        return new HashSet<string>(tokens, StringComparer.OrdinalIgnoreCase);
    }

    private static double JaccardOverlap(HashSet<string> a, HashSet<string> b)
    {
        if (a.Count == 0 || b.Count == 0) return 0;
        var intersect = a.Intersect(b, StringComparer.OrdinalIgnoreCase).Count();
        var union = a.Count + b.Count - intersect;
        return union == 0 ? 0 : (double)intersect / union;
    }

    private static DriftReportProducerKind MapProducerKind(DriftReportTrigger trigger) => trigger switch
    {
        DriftReportTrigger.Scheduled => DriftReportProducerKind.Scheduled,
        DriftReportTrigger.MetaCycle => DriftReportProducerKind.MetaCycle,
        DriftReportTrigger.SupportingAgent => DriftReportProducerKind.SupportingAgent,
        DriftReportTrigger.ExternalMonitor => DriftReportProducerKind.ExternalMonitor,
        _ => DriftReportProducerKind.Manual,
    };

    // ------------------------------------------------------------------
    // Scope assembly
    // ------------------------------------------------------------------

    private static IReadOnlyList<DriftRef> BuildSpecDocList(string repoRoot)
    {
        var docs = new List<DriftRef>();
        AddIfExists(docs, repoRoot, "ROADMAP.md", "ROADMAP");
        AddIfExists(docs, repoRoot, "README.md", "README");
        AddIfExists(docs, repoRoot, "AGENTS.md", "AGENTS");
        AddIfExists(docs, repoRoot, "docs/quality/design-principles.md", "Design principles");
        AddIfExists(docs, repoRoot, "docs/system/contracts/agent-task.md", "Agent task contract");
        AddIfExists(docs, repoRoot, "docs/system/contracts/filesystem.md", "Filesystem contract");
        AddIfExists(docs, repoRoot, "docs/system/architecture/decisions/adr-archive.md", "Architecture decisions (ADR archive)");

        // Mockups carry per-project specs. After the 2026-07 docs migration
        // standalone mockups live under docs/concepts/mockups/ (loose HTML or
        // nested subfolders) and the active families were promoted to
        // top-level docs/concepts/<family>/ folders.
        var mockups = Path.Combine(repoRoot, "docs", "concepts", "mockups");
        if (Directory.Exists(mockups))
        {
            foreach (var file in Directory.EnumerateFiles(mockups, "*.html").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                docs.Add(new DriftRef($"docs/concepts/mockups/{name}", $"Mockup / spec: {Path.GetFileNameWithoutExtension(file)}"));
            }
            foreach (var dir in Directory.EnumerateDirectories(mockups).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                docs.Add(new DriftRef($"docs/concepts/mockups/{name}/", $"Mockup / spec: {name}"));
            }
        }

        foreach (var family in PromotedMockupFamilies)
        {
            if (Directory.Exists(Path.Combine(repoRoot, "docs", "concepts", family)))
                docs.Add(new DriftRef($"docs/concepts/{family}/", $"Mockup / spec: {family}"));
        }

        return docs;
    }

    // Active mockup families promoted to top-level concept folders during the
    // 2026-07 docs migration (former docs/mockups/<family>/ -> docs/concepts/<family>/).
    private static readonly string[] PromotedMockupFamilies =
    {
        "project-urls",
        "project-overview-dashboard",
        "task-processing-pipeline",
        "task-detail-header-state-actions",
    };

    private static IReadOnlyList<ActiveJobRef> BuildActiveJobs(string projectRoot)
    {
        var jobs = new List<ActiveJobRef>();
        foreach (var lane in ActiveLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            if (!Directory.Exists(laneDir)) continue;

            int countInLane = 0;
            foreach (var dir in Directory.EnumerateDirectories(laneDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (countInLane >= QueueJobsPerLaneLimit) break;
                var entry = ReadActiveJobRef(dir, lane);
                if (entry is null) continue;
                jobs.Add(entry);
                countInLane++;
            }
        }
        return jobs;
    }

    private static IReadOnlyList<TaskRef> BuildRecentCompleted(string projectRoot)
    {
        var laneDir = Path.Combine(projectRoot, RecentCompletedLane);
        if (!Directory.Exists(laneDir)) return Array.Empty<TaskRef>();

        var entries = new List<TaskRef>();
        foreach (var dir in Directory.EnumerateDirectories(laneDir))
        {
            var entry = ReadJobRef(dir, RecentCompletedLane);
            if (entry is null) continue;
            entries.Add(entry);
        }
        return entries
            .OrderByDescending(t => t.LastWriteUtc ?? DateTime.MinValue)
            .Take(RecentCompletedLimit)
            .ToArray();
    }

    private static ActiveJobRef? ReadActiveJobRef(string dir, string lane)
    {
        var jobJson = Path.Combine(dir, "task.json");
        if (!File.Exists(jobJson)) return null;

        var id = Path.GetFileName(dir);
        var title = id;
        DateTime? touched = null;
        try
        {
            var text = File.ReadAllText(jobJson);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString() ?? id;
            if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? id;
        }
        catch (JsonException __ex)
        {
            SilentCatch.Note(__ex, "SpecTaskDriftAnalysisService: Malformed task.json - still surface the slug so the queue is visible.");
            // Malformed task.json - still surface the slug so the queue is visible.
        }

        try { touched = Directory.GetLastWriteTimeUtc(dir); }
        catch (Exception __ex) { SilentCatch.Note(__ex, "SpecTaskDriftAnalysisService: best-effort"); /* best-effort */ }

        var promptExcerpt = ReadPromptExcerpt(dir);
        var hasStatus = File.Exists(Path.Combine(dir, "status.md"));
        var hasLogs = Directory.Exists(Path.Combine(dir, "logs"));

        return new ActiveJobRef(id, title, lane, touched, promptExcerpt, hasStatus, hasLogs);
    }

    private static TaskRef? ReadJobRef(string dir, string lane)
    {
        var jobJson = Path.Combine(dir, "task.json");
        if (!File.Exists(jobJson)) return null;

        var id = Path.GetFileName(dir);
        var title = id;
        DateTime? touched = null;
        try
        {
            var text = File.ReadAllText(jobJson);
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                id = idEl.GetString() ?? id;
            if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                title = titleEl.GetString() ?? id;
        }
        catch (JsonException __ex) { SilentCatch.Note(__ex, "SpecTaskDriftAnalysisService: surface slug regardless"); /* surface slug regardless */ }

        try { touched = Directory.GetLastWriteTimeUtc(dir); }
        catch (Exception __ex) { SilentCatch.Note(__ex, "SpecTaskDriftAnalysisService: best-effort"); /* best-effort */ }

        return new TaskRef(id, title, lane, touched);
    }

    private static string? ReadPromptExcerpt(string dir)
    {
        var prompt = Path.Combine(dir, "prompt.md");
        if (!File.Exists(prompt)) return null;
        try
        {
            var text = File.ReadAllText(prompt);
            text = text.Replace("\r\n", "\n");
            if (text.Length > PromptExcerptLimit)
                text = text[..PromptExcerptLimit] + "...";
            return text.Trim();
        }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IReadOnlyList<ReportPointer> LookupRecentDriftReports(
        DriftReportStore? store, string? workspaceRoot, string project)
    {
        if (store is null || string.IsNullOrWhiteSpace(workspaceRoot)) return Array.Empty<ReportPointer>();
        return store.Snapshot(workspaceRoot, project)
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentReportLimit)
            .Select(r => new ReportPointer(
                ReportId: r.ReportId,
                Topic: "drift",
                CreatedAt: r.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")))
            .ToArray();
    }

    private static IReadOnlyList<ReportPointer> LookupRecentAnalysisReports(
        AnalysisReportStore? store, string? workspaceRoot, string project)
    {
        if (store is null || string.IsNullOrWhiteSpace(workspaceRoot)) return Array.Empty<ReportPointer>();
        return store.Snapshot(workspaceRoot, project)
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentReportLimit)
            .Select(r => new ReportPointer(
                ReportId: r.ReportId,
                Topic: r.Topic,
                CreatedAt: r.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")))
            .ToArray();
    }

    private static void AddIfExists(List<DriftRef> docs, string repoRoot, string relativePath, string label)
    {
        var full = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
            docs.Add(new DriftRef(relativePath, label));
    }

    private static IReadOnlyList<string> BuildSourceRefs(SpecTaskJobDriftScope scope)
    {
        var refs = new List<string>(capacity: 64);
        foreach (var d in scope.SpecDocs) refs.Add(d.Path);
        foreach (var j in scope.ActiveJobs) refs.Add($"{scope.Project}/{j.Lane}/{j.JobId}");
        foreach (var j in scope.RecentCompleted) refs.Add($"{scope.Project}/{j.Lane}/{j.JobId}");
        foreach (var p in scope.RecentDriftReports) refs.Add($"drift:{p.ReportId}");
        foreach (var p in scope.RecentAnalysisReports) refs.Add($"analysis:{p.ReportId}");
        return refs;
    }

    private static IReadOnlyList<string> BuildEvidenceRefSnapshot(SpecTaskJobDriftScope scope)
    {
        var refs = new List<string>(capacity: 8);
        if (scope.SpecDocs.Count > 0) refs.Add(scope.SpecDocs[0].Path);
        foreach (var d in scope.SpecDocs.Skip(1).Take(2)) refs.Add(d.Path);
        if (scope.ActiveJobs.Count > 0)
            refs.Add($"{scope.Project}/{scope.ActiveJobs[0].Lane}/{scope.ActiveJobs[0].JobId}");
        return refs;
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private static string RenderRefList(IReadOnlyList<DriftRef> refs)
    {
        if (refs.Count == 0) return "(none found)";
        var sb = new StringBuilder();
        foreach (var r in refs)
            sb.Append("- `").Append(r.Path).Append("` - ").AppendLf(r.Label);
        return sb.ToString().TrimEnd();
    }

    private static string RenderActiveJobs(IReadOnlyList<ActiveJobRef> jobs)
    {
        if (jobs.Count == 0) return "(no active queue jobs)";
        var sb = new StringBuilder();
        foreach (var t in jobs)
        {
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ")
                .Append(t.Title);
            var tags = new List<string>();
            if (t.HasStatus) tags.Add("status.md");
            if (t.HasLogs) tags.Add("logs/");
            if (tags.Count > 0)
            {
                sb.Append(" _(");
                sb.Append(string.Join(", ", tags));
                sb.Append(")_");
            }
            sb.AppendLf();
            if (!string.IsNullOrWhiteSpace(t.PromptExcerpt))
            {
                var excerpt = t.PromptExcerpt!
                    .Replace("\n", " ")
                    .Replace("\r", " ");
                if (excerpt.Length > PromptExcerptLimit)
                    excerpt = excerpt[..PromptExcerptLimit] + "...";
                sb.Append("    > ").AppendLf(excerpt);
            }
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderJobRefs(IReadOnlyList<TaskRef> jobs)
    {
        if (jobs.Count == 0) return "(no recent completed tasks)";
        var sb = new StringBuilder();
        foreach (var t in jobs)
        {
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ")
                .AppendLf(t.Title);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderDuplicates(IReadOnlyList<DuplicateTaskPair> pairs)
    {
        if (pairs.Count == 0) return "(no duplicate candidates flagged by the slug / title heuristic)";
        var sb = new StringBuilder();
        foreach (var p in pairs)
        {
            sb.Append("- `").Append(p.LeftLane).Append('/').Append(p.LeftJobId).Append('`')
                .Append(" vs `").Append(p.RightLane).Append('/').Append(p.RightJobId).Append("` ")
                .Append("(token overlap ").Append(p.Overlap.ToString("0.00", System.Globalization.CultureInfo.InvariantCulture)).AppendLf(")");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderReportPointers(IReadOnlyList<ReportPointer> reports)
    {
        if (reports.Count == 0) return "(none)";
        var sb = new StringBuilder();
        foreach (var r in reports)
            sb.Append("- `").Append(r.ReportId).Append("` _(").Append(r.Topic).Append(", ").Append(r.CreatedAt).AppendLf(")_");
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static SpecTaskJobDriftParseResult Malformed(string error, string rawText)
        => new(
            Status: SpecTaskJobDriftParseStatus.MalformedJson,
            ScoreBand: DriftScoreBand.Unknown,
            OverallScore: 0,
            Summary: ExtractFirstHeadingOrLine(rawText)
                ?? "Agent reply contained an unparseable JSON sidecar; Markdown body remains the durable artifact.",
            Dimensions: null,
            FollowUps: null,
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
            return line.Length > 200 ? line[..200] + "..." : line;
        }
        return null;
    }

    private static IReadOnlyList<DriftDimension>? ParseDimensions(SpecTaskJobDriftDimensionDto[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        var dims = new List<DriftDimension>(raw.Length);
        foreach (var d in raw)
        {
            var type = ParseDimensionType(d.Type)
                ?? throw new JsonException(
                    $"dimension.type must be one of the schema's drift dimensions (was '{d.Type}').");
            var severity = ParseSeverity(d.Severity)
                ?? throw new JsonException(
                    $"dimension.severity must be Info|Warn|High|Critical (was '{d.Severity}').");
            var status = ParseFindingStatus(d.Status)
                ?? throw new JsonException(
                    $"dimension.status must be New|Accepted|Ignored|Tracked|Resolved (was '{d.Status}').");
            if (d.Score is < 0 or > 100)
                throw new JsonException($"dimension.score must be 0..100 (was {d.Score}).");
            if (d.Confidence is < 0 or > 1)
                throw new JsonException($"dimension.confidence must be 0..1 (was {d.Confidence}).");
            if (d.SourceCoverage is < 0 or > 1)
                throw new JsonException($"dimension.sourceCoverage must be 0..1 (was {d.SourceCoverage}).");
            if (string.IsNullOrWhiteSpace(d.Summary))
                throw new JsonException($"dimension.summary required for {type}.");

            dims.Add(new DriftDimension(
                Type: type,
                Score: d.Score,
                Severity: severity,
                Confidence: d.Confidence,
                SourceCoverage: d.SourceCoverage,
                Status: status,
                Summary: d.Summary.Trim(),
                EvidenceRefs: (d.EvidenceRefs ?? Array.Empty<string>())
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim()).ToArray(),
                RecommendedActions: (d.RecommendedActions ?? Array.Empty<string>())
                    .Where(a => !string.IsNullOrWhiteSpace(a))
                    .Select(a => a.Trim()).ToArray()));
        }
        return dims;
    }

    private static DriftSeverity? ParseSeverity(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DriftSeverity.Info;
        return Enum.TryParse<DriftSeverity>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static DriftFindingStatus? ParseFindingStatus(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DriftFindingStatus.New;
        return Enum.TryParse<DriftFindingStatus>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static DriftDimensionType? ParseDimensionType(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        return Enum.TryParse<DriftDimensionType>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static DriftScoreBand? ParseScoreBand(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DriftScoreBand.Unknown;
        return Enum.TryParse<DriftScoreBand>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    private static DriftFollowUpPriority? ParseFollowUpPriority(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return DriftFollowUpPriority.Normal;
        return Enum.TryParse<DriftFollowUpPriority>(raw.Trim(), ignoreCase: true, out var v) ? v : null;
    }

    // ------------------------------------------------------------------
    // Records used by SelectScope / BuildPrompt
    // ------------------------------------------------------------------

    public sealed record DriftRef(string Path, string Label);

    public sealed record TaskRef(string JobId, string Title, string Lane, DateTime? LastWriteUtc);

    /// <summary>Active-queue job entry. Carries the prompt excerpt and a couple
    /// of "evidence on disk" booleans so the agent can decide where to drill
    /// without re-listing the folder.</summary>
    public sealed record ActiveJobRef(
        string JobId,
        string Title,
        string Lane,
        DateTime? LastWriteUtc,
        string? PromptExcerpt,
        bool HasStatus,
        bool HasLogs);

    public sealed record ReportPointer(string ReportId, string Topic, string CreatedAt);

    /// <summary>One pair of active jobs whose slug + title token sets overlap
    /// above the heuristic threshold. The agent confirms or dismisses; the
    /// host only flags.</summary>
    public sealed record DuplicateTaskPair(
        string LeftLane,
        string LeftJobId,
        string RightLane,
        string RightJobId,
        double Overlap);

    // ------------------------------------------------------------------
    // DTOs the JSON sidecar deserialises into
    // ------------------------------------------------------------------

    private sealed record SpecTaskJobDriftJsonDto
    {
        public string? Verdict { get; init; }
        public string? ScoreBand { get; init; }
        public int OverallScore { get; init; }
        public SpecTaskJobDriftDimensionDto[]? Dimensions { get; init; }
        public SpecTaskJobDriftFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record SpecTaskJobDriftDimensionDto
    {
        public string? Type { get; init; }
        public int Score { get; init; }
        public string? Severity { get; init; }
        public double Confidence { get; init; }
        public double SourceCoverage { get; init; }
        public string? Status { get; init; }
        public string? Summary { get; init; }
        public string[]? EvidenceRefs { get; init; }
        public string[]? RecommendedActions { get; init; }
    }

    private sealed record SpecTaskJobDriftFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedDimension { get; init; }
    }
}

/// <summary>Snapshot of the spec-doc + active-queue + recent-completed +
/// duplicate-candidate evidence the action gathered before talking to the
/// agent.</summary>
public sealed class SpecTaskJobDriftScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.DriftRef> SpecDocs { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.ActiveJobRef> ActiveJobs { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.TaskRef> RecentCompleted { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.DuplicateTaskPair> DuplicateCandidates { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.ReportPointer> RecentDriftReports { get; init; }
    public required IReadOnlyList<SpecTaskDriftAnalysisService.ReportPointer> RecentAnalysisReports { get; init; }
    public required DateTime CapturedAt { get; init; }
}

public enum SpecTaskJobDriftParseStatus
{
    Structured,
    Unstructured,
    MalformedJson,
}

public sealed record SpecTaskJobDriftParseResult(
    SpecTaskJobDriftParseStatus Status,
    DriftScoreBand ScoreBand,
    int OverallScore,
    string Summary,
    IReadOnlyList<DriftDimension>? Dimensions,
    IReadOnlyList<DriftFollowUpSuggestion>? FollowUps,
    string? ParseError);
