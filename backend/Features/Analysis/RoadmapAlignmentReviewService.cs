using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Analysis;

/// <summary>
/// Pure, testable assembly logic for the "Roadmap Alignment Review" action -
/// the named producer behind the recurring user request "review all upcoming
/// jobs, compare them with README, ROADMAP, ADRs, and other internal docs,
/// then tell me whether we are on track."
/// </summary>
/// <remarks>
/// <para>
/// The action is deliberately split into three pure steps so each piece is
/// covered by its own test:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks the inspected lanes
///   (<c>1-preparation</c>, <c>2-ready</c>, <c>3-progress</c>,
///   <c>4-auto-review</c>, <c>5-human-review</c>) and the canonical doc set,
///   then returns a typed <see cref="RoadmapAlignmentReviewScope"/>. No agent
///   calls happen here.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template with the assembled scope. The template lives at
///   <c>prompts/runtime/roadmap-alignment-review.md</c> so the wording is
///   editable without recompiling the backend.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the
///   structured JSON sidecar from an agent's free-form Markdown reply with
///   explicit <see cref="AnalysisReportParseStatus"/> fallbacks. A failed
///   parse never hides the Markdown body.</description></item>
/// </list>
/// <para>
/// The action is analysis, not code editing. It produces an
/// <see cref="AnalysisReport"/> and proposes follow-up tasks; it never moves
/// jobs between lanes, never edits source files, and never relaxes the
/// one-coding-task-per-project rule.
/// </para>
/// </remarks>
public sealed class RoadmapAlignmentReviewService
{
    /// <summary>Schema-version sentinel reused from the analysis-report contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>The fixed lane catalogue this action inspects. Out-of-band lanes
    /// (<c>6-completed</c>, <c>7-archive</c>) are excluded by design: the action
    /// asks "are we on track?" against the active queue.</summary>
    public static readonly IReadOnlyList<string> InspectedLanes = new[]
    {
        TaskStates.Preparation,
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
        TaskStates.HumanReview,
    };

    /// <summary>Canonical topic slug the UI uses for this producer.</summary>
    public const string Topic = "roadmap-alignment";

    /// <summary>How many recent analysis reports to surface as evidence pointers.</summary>
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
    /// Walks the <paramref name="projectRoot"/> lane folders and the
    /// <paramref name="repoRoot"/> doc set, then returns a typed scope record
    /// the prompt template will render against.
    /// </summary>
    /// <param name="project">Project name as it appears in the watch path
    /// catalogue. Used both to label the scope and to look up recent reports.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.
    /// Layout: <c>{projectRoot}/{lane}/{jobId}/task.json</c>. The caller resolves
    /// this from the workspace's watch path; the service does not consult
    /// configuration.</param>
    /// <param name="repoRoot">Filesystem root for the source repository (the
    /// dev checkout). Used to surface the canonical doc list.</param>
    /// <param name="reportStore">Optional analysis-report store. When supplied,
    /// the service includes the most recent <see cref="RecentReportLimit"/>
    /// reports as evidence pointers so the agent can read its predecessors.</param>
    /// <param name="workspaceRoot">Workspace root used by
    /// <paramref name="reportStore"/>. Pass <c>null</c> when no store lookup is
    /// needed (the service degrades gracefully).</param>
    /// <param name="now">Wall-clock for the scope record. Injected so tests can
    /// pin a deterministic timestamp.</param>
    public RoadmapAlignmentReviewScope SelectScope(
        string project,
        string projectRoot,
        string repoRoot,
        AnalysisReportStore? reportStore = null,
        string? workspaceRoot = null,
        DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var jobsByLane = new Dictionary<string, IReadOnlyList<TaskSummary>>(StringComparer.Ordinal);
        var stray = new List<string>();
        foreach (var lane in InspectedLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            jobsByLane[lane] = ReadLane(laneDir, lane, stray);
        }

        var docs = BuildDocList(repoRoot);
        var recent = LookupRecentReports(reportStore, workspaceRoot, project);
        var queueIsClean = stray.Count == 0;

        return new RoadmapAlignmentReviewScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            JobsByLane = jobsByLane,
            StrayLaneFolders = stray,
            Docs = docs,
            RecentReports = recent,
            QueueIsClean = queueIsClean,
            CapturedAt = now ?? DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Renders the prompt template with the assembled scope. Placeholders
    /// follow the existing <see cref="RuntimePromptService"/> convention
    /// (<c>{{name}}</c>) so the template is editable as plain Markdown.
    /// </summary>
    /// <remarks>
    /// The service does not load the template from disk (that lives in
    /// <see cref="RuntimePromptService"/>). Callers pass the loaded text so
    /// tests can pin the wording without round-tripping through the file
    /// system.
    /// </remarks>
    public string BuildPrompt(RoadmapAlignmentReviewScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["queue_summary"] = RenderQueueSummary(scope),
            ["jobs_by_lane"] = RenderJobsByLane(scope),
            ["doc_list"] = RenderDocList(scope),
            ["recent_reports"] = RenderRecentReports(scope),
            ["stray_folders"] = RenderStrayFolders(scope),
            ["queue_clean_flag"] = scope.QueueIsClean ? "yes" : "no",
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
    /// <list type="bullet">
    ///   <item><description><see cref="AnalysisReportParseStatus.Structured"/>
    ///   - a fenced ```json block was found and validated; typed fields are
    ///   populated.</description></item>
    ///   <item><description><see cref="AnalysisReportParseStatus.Unstructured"/>
    ///   - no fenced JSON block was present. The Markdown body is the only
    ///   artifact; severity defaults to <see cref="AnalysisReportSeverity.Info"/>.</description></item>
    ///   <item><description><see cref="AnalysisReportParseStatus.MalformedJson"/>
    ///   - a fenced JSON block was present but failed to parse or validate. The
    ///   parser error is carried back so a reviewer can fix the sidecar without
    ///   re-running the analysis.</description></item>
    /// </list>
    /// </remarks>
    public RoadmapAlignmentParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new RoadmapAlignmentParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: "Agent reply was empty; no structured analysis available.",
                Findings: null,
                FollowUps: null,
                PriorityOrder: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new RoadmapAlignmentParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: ExtractFirstHeadingOrLine(rawText)
                    ?? "Agent reply contained no structured JSON sidecar.",
                Findings: null,
                FollowUps: null,
                PriorityOrder: null,
                ParseError: null);
        }

        var jsonBody = match.Groups["body"].Value;
        try
        {
            var dto = JsonSerializer.Deserialize<RoadmapAlignmentJsonDto>(jsonBody, ParseOptions);
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

            var findings = (dto.Findings ?? Array.Empty<RoadmapAlignmentFindingDto>())
                .Select(f => new AnalysisReportFinding(
                    Topic: string.IsNullOrWhiteSpace(f.Topic) ? "drift" : f.Topic.Trim(),
                    Severity: ParseSeverity(f.Severity) ?? AnalysisReportSeverity.Info,
                    Message: (f.Message ?? string.Empty).Trim(),
                    EvidenceRefs: f.EvidenceRefs))
                .Where(f => !string.IsNullOrWhiteSpace(f.Message))
                .ToArray();

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<RoadmapAlignmentFollowUpDto>())
                .Select(s => new AnalysisReportFollowUpTaskSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: ParseRelatedTopic(s.RelatedTopic),
                    TargetState: NormaliseTargetState(s.TargetState)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new RoadmapAlignmentParseResult(
                Status: AnalysisReportParseStatus.Structured,
                Severity: severity,
                Summary: dto.Verdict.Trim(),
                Findings: findings,
                FollowUps: followUps,
                PriorityOrder: dto.RecommendedPriorityOrder?.Where(p => !string.IsNullOrWhiteSpace(p)).ToArray(),
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
        RoadmapAlignmentReviewScope scope,
        RoadmapAlignmentParseResult parse,
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

        var references = BuildReferences(scope);
        return new AnalysisReport(
            ReportId: reportId,
            CreatedAt: createdAt,
            Scope: new AnalysisReportScope(AnalysisReportScopeKind.Project, Project: scope.Project),
            Producer: new AnalysisReportProducer(producerKind, ParticipantId: participantId, Agent: agent),
            Trigger: trigger,
            Topic: Topic,
            Summary: parse.Summary,
            Severity: parse.Severity,
            ParseStatus: parse.Status,
            References: references,
            FollowUpTaskSuggestions: parse.FollowUps ?? Array.Empty<AnalysisReportFollowUpTaskSuggestion>(),
            ParseError: parse.ParseError,
            Tags: BuildTags(scope, parse),
            Findings: parse.Findings,
            SchemaVersion: CurrentSchemaVersion);
    }

    private static IReadOnlyList<TaskSummary> ReadLane(string laneDir, string lane, List<string> stray)
    {
        if (!Directory.Exists(laneDir)) return Array.Empty<TaskSummary>();

        var jobs = new List<TaskSummary>();
        foreach (var dir in Directory.EnumerateDirectories(laneDir))
        {
            var jobJson = Path.Combine(dir, "task.json");
            if (!File.Exists(jobJson))
            {
                stray.Add($"{lane}/{Path.GetFileName(dir)}");
                continue;
            }

            try
            {
                // ReadAllText handles UTF-8 BOM, the encoding the rest of the
                // backend writes task.json with. JsonDocument.Parse(byte[])
                // does NOT skip the BOM so byte-level reads need a manual
                // strip; staying on string is simpler and matches
                // TaskScannerService.
                var text = File.ReadAllText(jobJson);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var id = root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String
                    ? idEl.GetString() ?? Path.GetFileName(dir)
                    : Path.GetFileName(dir);
                var title = root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String
                    ? titleEl.GetString() ?? id
                    : id;
                var agent = root.TryGetProperty("agent", out var agentEl) && agentEl.ValueKind == JsonValueKind.String
                    ? agentEl.GetString()
                    : null;
                var cliType = root.TryGetProperty("cliType", out var cliEl) && cliEl.ValueKind == JsonValueKind.String
                    ? cliEl.GetString()
                    : null;
                jobs.Add(new TaskSummary(id, title, lane, agent, cliType));
            }
            catch (JsonException)
            {
                // Malformed task.json: surface as a stray so the report flags it.
                stray.Add($"{lane}/{Path.GetFileName(dir)} (malformed task.json)");
            }
        }

        // Stable order so prompts diff cleanly across runs.
        jobs.Sort((a, b) => string.CompareOrdinal(a.JobId, b.JobId));
        return jobs;
    }

    private static IReadOnlyList<DocReference> BuildDocList(string repoRoot)
    {
        var docs = new List<DocReference>();
        AddIfExists(docs, repoRoot, "README.md", "README");
        AddIfExists(docs, repoRoot, "ROADMAP.md", "ROADMAP");
        AddIfExists(docs, repoRoot, "AGENTS.md", "AGENTS");
        AddIfExists(docs, repoRoot, "docs/system/architecture/decisions/adr-archive.md", "Architecture decisions");
        AddIfExists(docs, repoRoot, "docs/quality/design-principles.md", "Design principles");
        AddIfExists(docs, repoRoot, "docs/system/architecture/bus/agent-message-bus.md", "Agent Message Bus");
        AddIfExists(docs, repoRoot, "docs/system/reports/analysis-reports.md", "Analysis reports contract");

        // Mockups: list one entry per page/folder so the agent can drill into
        // the relevant one without the prompt swelling with copies of every
        // mockup body. After the 2026-07 docs migration, standalone mockups
        // live under docs/concepts/mockups/ (loose HTML or nested subfolders)
        // and the active families were promoted to top-level
        // docs/concepts/<family>/ folders.
        var mockupsDir = Path.Combine(repoRoot, "docs", "concepts", "mockups");
        if (Directory.Exists(mockupsDir))
        {
            foreach (var file in Directory.EnumerateFiles(mockupsDir, "*.html").OrderBy(f => f, StringComparer.Ordinal))
            {
                docs.Add(new DocReference(
                    Path: Path.GetRelativePath(repoRoot, file).Replace('\\', '/'),
                    Label: $"Mockup: {Path.GetFileNameWithoutExtension(file)}"));
            }
            foreach (var dir in Directory.EnumerateDirectories(mockupsDir).OrderBy(d => d, StringComparer.Ordinal))
            {
                docs.Add(new DocReference(
                    Path: Path.GetRelativePath(repoRoot, dir).Replace('\\', '/'),
                    Label: $"Mockup: {Path.GetFileName(dir)}"));
            }
        }

        foreach (var family in PromotedMockupFamilies)
        {
            var dir = Path.Combine(repoRoot, "docs", "concepts", family);
            if (Directory.Exists(dir))
                docs.Add(new DocReference(
                    Path: Path.GetRelativePath(repoRoot, dir).Replace('\\', '/'),
                    Label: $"Mockup: {family}"));
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

    private static void AddIfExists(List<DocReference> docs, string repoRoot, string relativePath, string label)
    {
        var full = Path.Combine(repoRoot, relativePath.Replace('/', Path.DirectorySeparatorChar));
        if (File.Exists(full))
            docs.Add(new DocReference(relativePath, label));
    }

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

    private static string RenderQueueSummary(RoadmapAlignmentReviewScope scope)
    {
        var sb = new StringBuilder();
        sb.AppendLf("| Lane | Count |");
        sb.AppendLf("|------|------:|");
        foreach (var lane in InspectedLanes)
        {
            var count = scope.JobsByLane.TryGetValue(lane, out var jobs) ? jobs.Count : 0;
            sb.Append("| `").Append(lane).Append("` | ").Append(count).AppendLf(" |");
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderJobsByLane(RoadmapAlignmentReviewScope scope)
    {
        var sb = new StringBuilder();
        foreach (var lane in InspectedLanes)
        {
            sb.Append("### ").AppendLf(lane);
            sb.AppendLf();
            var jobs = scope.JobsByLane.TryGetValue(lane, out var list) ? list : Array.Empty<TaskSummary>();
            if (jobs.Count == 0)
            {
                sb.AppendLf("(no jobs)");
                sb.AppendLf();
                continue;
            }
            foreach (var j in jobs)
            {
                sb.Append("- `").Append(j.JobId).Append("` - ").Append(j.Title);
                if (!string.IsNullOrWhiteSpace(j.CliType))
                    sb.Append(" _(cli: ").Append(j.CliType).Append(")_");
                sb.AppendLf();
            }
            sb.AppendLf();
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderDocList(RoadmapAlignmentReviewScope scope)
    {
        if (scope.Docs.Count == 0) return "(no canonical docs found)";
        var sb = new StringBuilder();
        foreach (var d in scope.Docs)
            sb.Append("- `").Append(d.Path).Append("` - ").AppendLf(d.Label);
        return sb.ToString().TrimEnd();
    }

    private static string RenderRecentReports(RoadmapAlignmentReviewScope scope)
    {
        if (scope.RecentReports.Count == 0) return "(no prior analysis reports for this project)";
        var sb = new StringBuilder();
        foreach (var r in scope.RecentReports)
            sb.Append("- `").Append(r.ReportId).Append("` _(").Append(r.Topic).Append(", ").Append(r.CreatedAt).AppendLf(")_");
        return sb.ToString().TrimEnd();
    }

    private static string RenderStrayFolders(RoadmapAlignmentReviewScope scope)
    {
        if (scope.StrayLaneFolders.Count == 0) return "(none)";
        var sb = new StringBuilder();
        foreach (var s in scope.StrayLaneFolders)
            sb.Append("- ").AppendLf(s);
        return sb.ToString().TrimEnd();
    }

    private static IReadOnlyList<AnalysisReportReference> BuildReferences(RoadmapAlignmentReviewScope scope)
    {
        var refs = new List<AnalysisReportReference>();
        foreach (var lane in InspectedLanes)
        {
            if (!scope.JobsByLane.TryGetValue(lane, out var jobs)) continue;
            foreach (var j in jobs)
            {
                refs.Add(new AnalysisReportReference(
                    Kind: AnalysisReportReferenceKind.Job,
                    Ref: $"{scope.Project}/{lane}/{j.JobId}",
                    Label: j.Title));
            }
        }
        foreach (var d in scope.Docs)
            refs.Add(new AnalysisReportReference(AnalysisReportReferenceKind.Doc, d.Path, d.Label));
        foreach (var r in scope.RecentReports)
            refs.Add(new AnalysisReportReference(AnalysisReportReferenceKind.PreviousReport, r.ReportId, $"{r.Topic} @ {r.CreatedAt}"));
        return refs;
    }

    private static IReadOnlyList<string> BuildTags(RoadmapAlignmentReviewScope scope, RoadmapAlignmentParseResult parse)
    {
        var tags = new List<string> { "roadmap-alignment", "are-we-on-track" };
        if (!scope.QueueIsClean) tags.Add("queue-dirty");
        if (parse.Status == AnalysisReportParseStatus.Unstructured) tags.Add("unstructured");
        if (parse.Status == AnalysisReportParseStatus.MalformedJson) tags.Add("malformed-json");
        return tags;
    }

    private static RoadmapAlignmentParseResult Malformed(string error, string rawText)
        => new(
            Status: AnalysisReportParseStatus.MalformedJson,
            Severity: AnalysisReportSeverity.Info,
            Summary: ExtractFirstHeadingOrLine(rawText)
                ?? "Agent reply contained an unparseable JSON sidecar; Markdown body remains the durable artifact.",
            Findings: null,
            FollowUps: null,
            PriorityOrder: null,
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
            // First non-blank, non-fence line as a fallback summary.
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
        // Constraint: this action must not place follow-ups directly in
        // 2-ready unless the user explicitly chooses that. The roadmap-
        // alignment producer is open-ended, so any value the agent emits is
        // coerced to 1-preparation regardless. The user can always promote a
        // suggestion to 2-ready through the existing task-creation entry point.
        _ = raw;
        return AnalysisReportFollowUpTargetStates.OnePreparation;
    }

    /// <summary>One queued / in-flight job entry. Title is the user-readable
    /// label; <see cref="Lane"/> records which inspected lane the entry came
    /// from so prompt rendering and reference building stay in lockstep.</summary>
    public sealed record TaskSummary(string JobId, string Title, string Lane, string? Agent, string? CliType);

    /// <summary>One canonical document the agent should read.</summary>
    public sealed record DocReference(string Path, string Label);

    /// <summary>Pointer to a recent analysis report so the agent can build on
    /// rather than restart the conversation.</summary>
    public sealed record AnalysisReportPointer(string ReportId, string Topic, string CreatedAt);

    // The DTOs below mirror the JSON shape the prompt template asks the agent
    // to emit. They are intentionally lenient (strings everywhere) so
    // <see cref="TryParseAgentResponse"/> can produce a typed parse error
    // rather than a generic deserialisation crash.
    private sealed record RoadmapAlignmentJsonDto
    {
        public string? Verdict { get; init; }
        public string? Severity { get; init; }
        public RoadmapAlignmentFindingDto[]? Findings { get; init; }
        public RoadmapAlignmentFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
        public string[]? RecommendedPriorityOrder { get; init; }
    }

    private sealed record RoadmapAlignmentFindingDto
    {
        public string? Topic { get; init; }
        public string? Severity { get; init; }
        public string? Message { get; init; }
        public string[]? EvidenceRefs { get; init; }
    }

    private sealed record RoadmapAlignmentFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedTopic { get; init; }
        public string? TargetState { get; init; }
    }
}

/// <summary>Snapshot of the queue + doc + history evidence the action gathered
/// before talking to the agent. The record is plain data so tests can build
/// fixtures without spinning up the surrounding services.</summary>
public sealed class RoadmapAlignmentReviewScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<RoadmapAlignmentReviewService.TaskSummary>> JobsByLane { get; init; }
    public required IReadOnlyList<string> StrayLaneFolders { get; init; }
    public required IReadOnlyList<RoadmapAlignmentReviewService.DocReference> Docs { get; init; }
    public required IReadOnlyList<RoadmapAlignmentReviewService.AnalysisReportPointer> RecentReports { get; init; }
    public required bool QueueIsClean { get; init; }
    public required DateTime CapturedAt { get; init; }
}

/// <summary>Result of <see cref="RoadmapAlignmentReviewService.TryParseAgentResponse"/>.
/// Carries the parse status the report should record together with the typed
/// fields the JSON sidecar described.</summary>
public sealed record RoadmapAlignmentParseResult(
    AnalysisReportParseStatus Status,
    AnalysisReportSeverity Severity,
    string Summary,
    IReadOnlyList<AnalysisReportFinding>? Findings,
    IReadOnlyList<AnalysisReportFollowUpTaskSuggestion>? FollowUps,
    IReadOnlyList<string>? PriorityOrder,
    string? ParseError);
