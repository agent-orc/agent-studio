using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Drift;

/// <summary>
/// Pure, testable assembly logic for the **Docs / Marketing Drift** action -
/// the named producer that compares README, ROADMAP, AGENTS, project docs,
/// and marketing / website-planning material against actual product behavior
/// and the current queued work.
/// </summary>
/// <remarks>
/// <para>
/// Three pure steps mirror <see cref="AdrCodeDriftAnalysisService"/> so each
/// piece is covered by its own test and the action stays read-only:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks README / ROADMAP /
///   AGENTS / ADR index / design-principles, the mockup doc set, the current
///   queue lanes, recent completed evidence, the optional marketing
///   repository (when configured), and recent drift / analysis reports. No
///   agent calls happen here.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template with the assembled scope. The template lives at
///   <c>prompts/runtime/docs-marketing-drift.md</c> so the wording is
///   editable without recompiling the backend.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the
///   structured JSON sidecar from the agent's free-form Markdown reply with
///   explicit Structured / Unstructured / MalformedJson fallbacks. A failed
///   parse never hides the Markdown body.</description></item>
/// </list>
/// <para>
/// The action is analysis, not editing. It produces a
/// <see cref="DriftReport"/> and proposes follow-up tasks; it never edits
/// README, AGENTS, ROADMAP, marketing docs, or job folders. The marketing
/// repository path is never hardcoded: callers pass it explicitly so a
/// missing or unconfigured external repo is a normal scope state rather
/// than a crash.
/// </para>
/// </remarks>
public sealed class DocsMarketingDriftAnalysisService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Topic slug used to label the report and tag follow-ups.</summary>
    public const string Topic = "docs-marketing-drift";

    /// <summary>How many recent jobs to surface as evidence pointers per lane bucket.</summary>
    public const int QueueJobsPerLaneLimit = 12;

    /// <summary>How many recent completed jobs to surface.</summary>
    public const int RecentCompletedLimit = 8;

    /// <summary>How many recent reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    /// <summary>How many marketing docs to surface in the prompt (cap so a
    /// large external repository does not flood the rendered prompt).</summary>
    public const int MarketingDocLimit = 40;

    /// <summary>Lanes representing the current queue. The action asks "do
    /// the docs match what the user is about to ship", so it includes the
    /// in-flight pipeline lanes.</summary>
    public static readonly IReadOnlyList<string> QueueLanes = new[]
    {
        TaskStates.Preparation,
        TaskStates.OrchestratorPrep,
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
        TaskStates.HumanReview,
    };

    /// <summary>Lane sampled for recently shipped evidence.</summary>
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

    /// <summary>Walks the canonical doc set, the mockup folders, the current
    /// queue, the recent-completed lane, and (when configured) the external
    /// marketing repository. Returns a typed scope record the prompt template
    /// renders against.</summary>
    /// <param name="project">Project name as it appears in the watch path catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.</param>
    /// <param name="repoRoot">Source repository root (the dev checkout).</param>
    /// <param name="marketingRepoRoot">Optional path to the marketing /
    /// website-planning repository. <c>null</c> or whitespace means "not
    /// configured" - the scope records the absence and the prompt renders
    /// it explicitly so the agent does not invent claims.</param>
    /// <param name="driftStore">Optional drift-report store; surfaces recent
    /// reports as evidence pointers.</param>
    /// <param name="analysisStore">Optional analysis-report store.</param>
    /// <param name="workspaceRoot">Workspace root used by both stores.</param>
    /// <param name="now">Wall-clock for the scope record.</param>
    public DocsMarketingDriftScope SelectScope(
        string project,
        string projectRoot,
        string repoRoot,
        string? marketingRepoRoot = null,
        DriftReportStore? driftStore = null,
        AnalysisReportStore? analysisStore = null,
        string? workspaceRoot = null,
        DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var canonicalDocs = BuildCanonicalDocList(repoRoot);
        var mockupDocs = BuildMockupDocList(repoRoot);
        var queueJobs = BuildQueueJobs(projectRoot);
        var recentCompleted = BuildRecentCompleted(projectRoot);
        var marketing = BuildMarketingScope(marketingRepoRoot);
        var recentDrift = LookupRecentDriftReports(driftStore, workspaceRoot, project);
        var recentAnalysis = LookupRecentAnalysisReports(analysisStore, workspaceRoot, project);

        return new DocsMarketingDriftScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            CanonicalDocs = canonicalDocs,
            MockupDocs = mockupDocs,
            QueueJobs = queueJobs,
            RecentCompleted = recentCompleted,
            Marketing = marketing,
            RecentDriftReports = recentDrift,
            RecentAnalysisReports = recentAnalysis,
            CapturedAt = now ?? DateTime.UtcNow,
        };
    }

    /// <summary>Renders the prompt template with the assembled scope.</summary>
    public string BuildPrompt(DocsMarketingDriftScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["canonical_docs"] = RenderRefList(scope.CanonicalDocs),
            ["mockup_docs"] = RenderRefList(scope.MockupDocs),
            ["queue_jobs"] = RenderQueueJobs(scope.QueueJobs),
            ["recent_completed"] = RenderRecentTasks(scope.RecentCompleted),
            ["marketing_status"] = RenderMarketingStatus(scope.Marketing),
            ["marketing_docs"] = RenderMarketingDocs(scope.Marketing),
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
    public DocsMarketingDriftParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new DocsMarketingDriftParseResult(
                Status: DocsMarketingDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: "Agent reply was empty; no structured docs / marketing drift analysis available.",
                Dimensions: null,
                FollowUps: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new DocsMarketingDriftParseResult(
                Status: DocsMarketingDriftParseStatus.Unstructured,
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
            var dto = JsonSerializer.Deserialize<DocsMarketingDriftJsonDto>(jsonBody, ParseOptions);
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
            // single Documentation entry so the resulting record is still
            // schema-valid (dimensions: minItems=1).
            var dims = ParseDimensions(dto.Dimensions);

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<DocsMarketingDriftFollowUpDto>())
                .Select(s => new DriftFollowUpSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? DriftFollowUpPriority.Normal,
                    RelatedDimension: ParseDimensionType(s.RelatedDimension)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new DocsMarketingDriftParseResult(
                Status: DocsMarketingDriftParseStatus.Structured,
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
        DocsMarketingDriftScope scope,
        DocsMarketingDriftParseResult parse,
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

        if (parse.Status == DocsMarketingDriftParseStatus.Structured)
        {
            dimensions = (parse.Dimensions is { Count: > 0 })
                ? parse.Dimensions
                : new[]
                {
                    new DriftDimension(
                        Type: DriftDimensionType.Documentation,
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
                    Type: DriftDimensionType.Documentation,
                    Score: 0,
                    Severity: DriftSeverity.Info,
                    Confidence: 0,
                    SourceCoverage: 0,
                    Status: DriftFindingStatus.New,
                    Summary: parse.Status == DocsMarketingDriftParseStatus.MalformedJson
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
            DocsMarketingDriftParseStatus.Structured => DriftReportParseStatus.Structured,
            DocsMarketingDriftParseStatus.MalformedJson => DriftReportParseStatus.MalformedJson,
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

    private static IReadOnlyList<DriftRef> BuildCanonicalDocList(string repoRoot)
    {
        var docs = new List<DriftRef>();
        AddIfExists(docs, repoRoot, "README.md", "README");
        AddIfExists(docs, repoRoot, "ROADMAP.md", "ROADMAP");
        AddIfExists(docs, repoRoot, "AGENTS.md", "AGENTS");
        AddIfExists(docs, repoRoot, "docs/system/architecture/decisions/adr-archive.md", "Architecture decisions (ADR archive)");
        AddIfExists(docs, repoRoot, "docs/quality/design-principles.md", "Design principles");
        AddIfExists(docs, repoRoot, "docs/system/contracts/agent-task.md", "Agent task contract");
        AddIfExists(docs, repoRoot, "docs/concepts/skills-architecture.md", "Skills architecture");
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

    private static IReadOnlyList<DriftRef> BuildMockupDocList(string repoRoot)
    {
        var entries = new List<DriftRef>();

        // Standalone mockups now live directly under docs/concepts/mockups/ as
        // loose HTML pages (or, for future families, as nested subfolders).
        var mockupsDir = Path.Combine(repoRoot, "docs", "concepts", "mockups");
        if (Directory.Exists(mockupsDir))
        {
            foreach (var file in Directory.EnumerateFiles(mockupsDir, "*.html").OrderBy(f => f, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(file);
                entries.Add(new DriftRef(
                    Path: $"docs/concepts/mockups/{name}",
                    Label: $"Mockup: {Path.GetFileNameWithoutExtension(file)}"));
            }
            foreach (var dir in Directory.EnumerateDirectories(mockupsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                var name = Path.GetFileName(dir);
                entries.Add(new DriftRef(
                    Path: $"docs/concepts/mockups/{name}/",
                    Label: $"Mockup: {name}"));
            }
        }

        // Promoted mockup families sit directly under docs/concepts/.
        foreach (var family in PromotedMockupFamilies)
        {
            if (Directory.Exists(Path.Combine(repoRoot, "docs", "concepts", family)))
                entries.Add(new DriftRef(
                    Path: $"docs/concepts/{family}/",
                    Label: $"Mockup: {family}"));
        }

        return entries;
    }

    private static IReadOnlyList<TaskRef> BuildQueueJobs(string projectRoot)
    {
        var jobs = new List<TaskRef>();
        foreach (var lane in QueueLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            if (!Directory.Exists(laneDir)) continue;

            int countInLane = 0;
            foreach (var dir in Directory.EnumerateDirectories(laneDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                if (countInLane >= QueueJobsPerLaneLimit) break;
                var entry = ReadJobRef(dir, lane);
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
        catch (JsonException __ex)
        {
            SilentCatch.Note(__ex, "DocsMarketingDriftAnalysisService: Malformed task.json - still surface the slug as evidence so the");
            // Malformed task.json - still surface the slug as evidence so the
            // drift report can flag the queue dirty.
        }

        try { touched = Directory.GetLastWriteTimeUtc(dir); }
        catch (Exception __ex) { SilentCatch.Note(__ex, "DocsMarketingDriftAnalysisService: best-effort"); /* best-effort */ }

        return new TaskRef(id, title, lane, touched);
    }

    private static MarketingScope BuildMarketingScope(string? marketingRepoRoot)
    {
        if (string.IsNullOrWhiteSpace(marketingRepoRoot))
        {
            return new MarketingScope(
                Configured: false,
                Exists: false,
                Root: null,
                Docs: Array.Empty<DriftRef>(),
                Note: "Marketing repository path not configured.");
        }

        if (!Directory.Exists(marketingRepoRoot))
        {
            return new MarketingScope(
                Configured: true,
                Exists: false,
                Root: marketingRepoRoot,
                Docs: Array.Empty<DriftRef>(),
                Note: $"Marketing repository configured but not found on disk at '{marketingRepoRoot}'.");
        }

        var docs = new List<DriftRef>();
        try
        {
            foreach (var file in Directory
                .EnumerateFiles(marketingRepoRoot, "*.md", SearchOption.AllDirectories)
                .OrderBy(f => f, StringComparer.Ordinal))
            {
                if (docs.Count >= MarketingDocLimit) break;
                var rel = Path.GetRelativePath(marketingRepoRoot, file).Replace('\\', '/');
                // Skip noisy sub-trees that no marketing-drift run cares about.
                if (rel.StartsWith(".git/", StringComparison.Ordinal)) continue;
                if (rel.StartsWith("node_modules/", StringComparison.Ordinal)) continue;
                docs.Add(new DriftRef(rel, rel));
            }
        }
        catch (UnauthorizedAccessException)
        {
            return new MarketingScope(
                Configured: true,
                Exists: true,
                Root: marketingRepoRoot,
                Docs: Array.Empty<DriftRef>(),
                Note: "Marketing repository is not readable; treat as unavailable.");
        }
        catch (IOException)
        {
            return new MarketingScope(
                Configured: true,
                Exists: true,
                Root: marketingRepoRoot,
                Docs: Array.Empty<DriftRef>(),
                Note: "Marketing repository scan failed; treat as unavailable.");
        }

        return new MarketingScope(
            Configured: true,
            Exists: true,
            Root: marketingRepoRoot,
            Docs: docs,
            Note: docs.Count >= MarketingDocLimit
                ? $"Listing capped at {MarketingDocLimit} files; deeper drilling left to the agent."
                : null);
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

    private static IReadOnlyList<string> BuildSourceRefs(DocsMarketingDriftScope scope)
    {
        var refs = new List<string>(capacity: 64);
        foreach (var d in scope.CanonicalDocs) refs.Add(d.Path);
        foreach (var d in scope.MockupDocs) refs.Add(d.Path);
        foreach (var j in scope.QueueJobs) refs.Add($"{scope.Project}/{j.Lane}/{j.JobId}");
        foreach (var j in scope.RecentCompleted) refs.Add($"{scope.Project}/{j.Lane}/{j.JobId}");
        if (scope.Marketing.Exists)
        {
            foreach (var d in scope.Marketing.Docs) refs.Add($"marketing:{d.Path}");
        }
        else if (scope.Marketing.Configured)
        {
            refs.Add("marketing:(configured-but-missing)");
        }
        else
        {
            refs.Add("marketing:(not-configured)");
        }
        foreach (var p in scope.RecentDriftReports) refs.Add($"drift:{p.ReportId}");
        foreach (var p in scope.RecentAnalysisReports) refs.Add($"analysis:{p.ReportId}");
        return refs;
    }

    private static IReadOnlyList<string> BuildEvidenceRefSnapshot(DocsMarketingDriftScope scope)
    {
        var refs = new List<string>(capacity: 8);
        if (scope.CanonicalDocs.Count > 0) refs.Add(scope.CanonicalDocs[0].Path);
        foreach (var d in scope.CanonicalDocs.Skip(1).Take(3)) refs.Add(d.Path);
        if (scope.Marketing.Exists && scope.Marketing.Docs.Count > 0)
            refs.Add($"marketing:{scope.Marketing.Docs[0].Path}");
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

    private static string RenderQueueJobs(IReadOnlyList<TaskRef> jobs)
    {
        if (jobs.Count == 0) return "(no queued jobs)";
        var sb = new StringBuilder();
        foreach (var t in jobs)
        {
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ")
                .AppendLf(t.Title);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderRecentTasks(IReadOnlyList<TaskRef> tasks)
    {
        if (tasks.Count == 0) return "(no recent completed tasks)";
        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ")
                .AppendLf(t.Title);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderMarketingStatus(MarketingScope marketing)
    {
        if (!marketing.Configured)
            return "Status: **not configured**. No marketing repository path was supplied.";
        if (!marketing.Exists)
            return $"Status: **configured but missing on disk** (`{marketing.Root}`). Treat marketing claims as out of scope for this run.";
        var bodyNote = string.IsNullOrWhiteSpace(marketing.Note) ? string.Empty : $" ({marketing.Note})";
        return $"Status: **available** at `{marketing.Root}` ({marketing.Docs.Count} docs surfaced).{bodyNote}";
    }

    private static string RenderMarketingDocs(MarketingScope marketing)
    {
        if (!marketing.Configured) return "(marketing repository not configured)";
        if (!marketing.Exists) return "(marketing repository not found on disk)";
        if (marketing.Docs.Count == 0) return "(marketing repository has no Markdown files)";
        var sb = new StringBuilder();
        foreach (var d in marketing.Docs)
            sb.Append("- `").Append(d.Path).AppendLf("`");
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

    private static DocsMarketingDriftParseResult Malformed(string error, string rawText)
        => new(
            Status: DocsMarketingDriftParseStatus.MalformedJson,
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

    private static IReadOnlyList<DriftDimension>? ParseDimensions(DocsMarketingDriftDimensionDto[]? raw)
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

    public sealed record ReportPointer(string ReportId, string Topic, string CreatedAt);

    /// <summary>Snapshot of the (optional) external marketing repository.
    /// <see cref="Configured"/> distinguishes "no path supplied" from
    /// <see cref="Exists"/> "path supplied but missing on disk" so the
    /// downstream prompt can render the absence explicitly rather than
    /// inventing claims.</summary>
    public sealed record MarketingScope(
        bool Configured,
        bool Exists,
        string? Root,
        IReadOnlyList<DriftRef> Docs,
        string? Note);

    // ------------------------------------------------------------------
    // DTOs the JSON sidecar deserialises into
    // ------------------------------------------------------------------

    private sealed record DocsMarketingDriftJsonDto
    {
        public string? Verdict { get; init; }
        public string? ScoreBand { get; init; }
        public int OverallScore { get; init; }
        public DocsMarketingDriftDimensionDto[]? Dimensions { get; init; }
        public DocsMarketingDriftFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record DocsMarketingDriftDimensionDto
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

    private sealed record DocsMarketingDriftFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedDimension { get; init; }
    }
}

/// <summary>Snapshot of the canonical doc + mockup + queue + recent-completed +
/// optional-marketing evidence the action gathered before talking to the
/// agent.</summary>
public sealed class DocsMarketingDriftScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.DriftRef> CanonicalDocs { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.DriftRef> MockupDocs { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.TaskRef> QueueJobs { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.TaskRef> RecentCompleted { get; init; }
    public required DocsMarketingDriftAnalysisService.MarketingScope Marketing { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.ReportPointer> RecentDriftReports { get; init; }
    public required IReadOnlyList<DocsMarketingDriftAnalysisService.ReportPointer> RecentAnalysisReports { get; init; }
    public required DateTime CapturedAt { get; init; }
}

public enum DocsMarketingDriftParseStatus
{
    Structured,
    Unstructured,
    MalformedJson,
}

public sealed record DocsMarketingDriftParseResult(
    DocsMarketingDriftParseStatus Status,
    DriftScoreBand ScoreBand,
    int OverallScore,
    string Summary,
    IReadOnlyList<DriftDimension>? Dimensions,
    IReadOnlyList<DriftFollowUpSuggestion>? FollowUps,
    string? ParseError);
