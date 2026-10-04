using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Drift;

/// <summary>
/// Pure, testable assembly logic for the **ADR / Code Drift** action -
/// the named producer that compares the architecture decisions archive and
/// the architecture documentation against the current source tree, schema
/// set, and recent task evidence.
/// </summary>
/// <remarks>
/// <para>
/// Three pure steps mirror <see cref="RoadmapAlignmentReviewService"/> so
/// each piece is covered by its own test and the action stays read-only:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks the ADR archive,
///   architecture notes, the source-tree top level, the per-module split
///   under <c>backend/Services</c> and <c>frontend/src</c>, the schema set,
///   and recent task evidence. No agent calls happen here; the result is a
///   plain data record the prompt template renders against.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template with the assembled scope. The template lives at
///   <c>prompts/runtime/adr-code-drift.md</c> so the wording is editable
///   without recompiling the backend.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the
///   structured JSON sidecar from the agent's free-form Markdown reply with
///   explicit Structured / Unstructured / MalformedJson fallbacks. A failed
///   parse never hides the Markdown body.</description></item>
/// </list>
/// <para>
/// The action is analysis, not code editing. It produces a
/// <see cref="DriftReport"/> and proposes follow-up tasks; it never writes
/// ADRs, never edits source files, never moves jobs between lanes, and never
/// treats the score as an architecture decision in itself.
/// </para>
/// </remarks>
public sealed class AdrCodeDriftAnalysisService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Topic slug used to label the report and tag follow-ups.</summary>
    public const string Topic = "adr-code-drift";

    /// <summary>How many recent jobs to surface as evidence pointers.</summary>
    public const int RecentTaskLimit = 8;

    /// <summary>How many recent reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    /// <summary>
    /// Lanes the action samples for "recent task evidence". Limits to
    /// already-reviewed lanes so the prompt does not pull in active
    /// in-flight work as evidence for drift it has not yet produced.
    /// </summary>
    public static readonly IReadOnlyList<string> RecentTaskLanes = new[]
    {
        TaskStates.HumanReview,
        TaskStates.Completed,
    };

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
    /// Walks the project lane folders for recent task evidence and the repo
    /// root for ADRs, architecture notes, source-tree structure, module
    /// boundaries, and the schema set. Returns a typed scope record the
    /// prompt template will render against.
    /// </summary>
    /// <param name="project">Project name as it appears in the watch path catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.</param>
    /// <param name="repoRoot">Source repository root (the dev checkout).</param>
    /// <param name="driftStore">Optional drift-report store. When supplied, the most
    /// recent <see cref="RecentReportLimit"/> drift reports are surfaced as evidence
    /// pointers so the agent can build on prior runs rather than restart.</param>
    /// <param name="analysisStore">Optional analysis-report store. Used to
    /// surface the most recent generic analysis reports next to drift
    /// reports.</param>
    /// <param name="workspaceRoot">Workspace root used by both stores.</param>
    /// <param name="now">Wall-clock for the scope record. Injected so tests can
    /// pin a deterministic timestamp.</param>
    public AdrCodeDriftScope SelectScope(
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

        var docs = BuildDocList(repoRoot);
        var sourceTree = BuildSourceTree(repoRoot);
        var moduleBoundaries = BuildModuleBoundaries(repoRoot);
        var schemas = BuildSchemaList(repoRoot);
        var recentTasks = BuildRecentTasks(projectRoot);
        var recentDrift = LookupRecentDriftReports(driftStore, workspaceRoot, project);
        var recentAnalysis = LookupRecentAnalysisReports(analysisStore, workspaceRoot, project);

        return new AdrCodeDriftScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            Docs = docs,
            SourceTree = sourceTree,
            ModuleBoundaries = moduleBoundaries,
            Schemas = schemas,
            RecentTasks = recentTasks,
            RecentDriftReports = recentDrift,
            RecentAnalysisReports = recentAnalysis,
            CapturedAt = now ?? DateTime.UtcNow,
        };
    }

    /// <summary>
    /// Renders the prompt template with the assembled scope. Placeholders
    /// follow the <c>{{name}}</c> convention used elsewhere in this folder.
    /// </summary>
    public string BuildPrompt(AdrCodeDriftScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["doc_list"] = RenderRefList(scope.Docs),
            ["source_tree"] = RenderRefList(scope.SourceTree),
            ["module_boundaries"] = RenderRefList(scope.ModuleBoundaries),
            ["schema_list"] = RenderRefList(scope.Schemas),
            ["recent_tasks"] = RenderRecentTasks(scope.RecentTasks),
            ["recent_drift_reports"] = RenderReportPointers(scope.RecentDriftReports),
            ["recent_analysis_reports"] = RenderReportPointers(scope.RecentAnalysisReports),
        };

        return Regex.Replace(template, @"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", m =>
        {
            var key = m.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var v) ? v ?? string.Empty : m.Value;
        });
    }

    /// <summary>
    /// Extracts a JSON sidecar from a free-form agent reply. Returns the
    /// parse state, the typed dimensions, the verdict, and any parser error.
    /// Failed JSON parses never hide the Markdown body; the caller decides
    /// how to render the body alongside the parse status.
    /// </summary>
    public AdrCodeDriftParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new AdrCodeDriftParseResult(
                Status: AdrCodeDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: "Agent reply was empty; no structured drift analysis available.",
                Dimensions: null,
                FollowUps: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new AdrCodeDriftParseResult(
                Status: AdrCodeDriftParseStatus.Unstructured,
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
            var dto = JsonSerializer.Deserialize<AdrCodeDriftJsonDto>(jsonBody, ParseOptions);
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

            // Dimensions are optional in the agent's reply: a "no findings"
            // verdict can omit the array entirely. The caller's BuildReport
            // synthesises a single Healthy dimension so the resulting record
            // is still schema-valid (dimensions: minItems=1).
            var dims = ParseDimensions(dto.Dimensions);

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<AdrCodeDriftFollowUpDto>())
                .Select(s => new DriftFollowUpSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? DriftFollowUpPriority.Normal,
                    RelatedDimension: ParseDimensionType(s.RelatedDimension)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new AdrCodeDriftParseResult(
                Status: AdrCodeDriftParseStatus.Structured,
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

    /// <summary>
    /// Composes the typed <see cref="DriftReport"/> for one run. The caller
    /// supplies the report id and the wall clock so this service stays free
    /// of clock + id concerns.
    /// </summary>
    public DriftReport BuildReport(
        AdrCodeDriftScope scope,
        AdrCodeDriftParseResult parse,
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

        if (parse.Status == AdrCodeDriftParseStatus.Structured)
        {
            // The schema requires at least one dimension; if the agent
            // declared the project healthy and omitted the dimensions array
            // entirely, synthesise a single Architecture entry so the
            // record is still valid and the "no finding" verdict is
            // explicit on disk.
            dimensions = (parse.Dimensions is { Count: > 0 })
                ? parse.Dimensions
                : new[]
                {
                    new DriftDimension(
                        Type: DriftDimensionType.Architecture,
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
            // Unstructured / MalformedJson: no agent narrative or unparseable
            // sidecar. Emit a single Architecture dimension marked Unknown so
            // the report is still schema-valid; the band and overall score
            // record that the analysis is incomplete rather than healthy.
            dimensions = new[]
            {
                new DriftDimension(
                    Type: DriftDimensionType.Architecture,
                    Score: 0,
                    Severity: DriftSeverity.Info,
                    Confidence: 0,
                    SourceCoverage: 0,
                    Status: DriftFindingStatus.New,
                    Summary: parse.Status == AdrCodeDriftParseStatus.MalformedJson
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
            AdrCodeDriftParseStatus.Structured => DriftReportParseStatus.Structured,
            AdrCodeDriftParseStatus.MalformedJson => DriftReportParseStatus.MalformedJson,
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

    private static IReadOnlyList<DriftRef> BuildDocList(string repoRoot)
    {
        var docs = new List<DriftRef>();
        AddIfExists(docs, repoRoot, "docs/system/architecture/decisions/adr-archive.md", "Architecture decisions (ADR archive)");
        AddIfExists(docs, repoRoot, "docs/quality/design-principles.md", "Design principles");
        AddIfExists(docs, repoRoot, "ROADMAP.md", "ROADMAP");
        AddIfExists(docs, repoRoot, "README.md", "README");
        AddIfExists(docs, repoRoot, "AGENTS.md", "AGENTS");
        AddIfExists(docs, repoRoot, "docs/system/contracts/agent-task.md", "Agent task contract");
        AddIfExists(docs, repoRoot, "docs/concepts/skills-architecture.md", "Skills architecture");
        AddIfExists(docs, repoRoot, "docs/system/contracts/protocol-style.md", "Protocol & image style");
        AddIfExists(docs, repoRoot, "docs/system/contracts/filesystem.md", "Filesystem contract");
        AddIfExists(docs, repoRoot, "docs/system/reports/analysis-reports.md", "Analysis reports contract");
        return docs;
    }

    /// <summary>
    /// Lists the top-level source folders so the agent can see the actual
    /// shape of the tree before drilling in. Hidden / build folders are
    /// skipped: <c>.git</c>, <c>.vs</c>, <c>node_modules</c>, <c>bin</c>,
    /// <c>obj</c>, <c>dist</c>, <c>test-results</c>, <c>.angular</c>.
    /// </summary>
    private static IReadOnlyList<DriftRef> BuildSourceTree(string repoRoot)
    {
        if (!Directory.Exists(repoRoot)) return Array.Empty<DriftRef>();
        var skip = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            ".git", ".vs", ".idea", ".github", ".angular", ".vscode",
            "node_modules", "bin", "obj", "dist", "test-results", "logs",
        };
        var entries = new List<DriftRef>();
        foreach (var dir in Directory.EnumerateDirectories(repoRoot).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (skip.Contains(name)) continue;
            entries.Add(new DriftRef(name + "/", $"Top-level folder: {name}"));
        }
        return entries;
    }

    /// <summary>
    /// Lists the per-module subfolders under <c>backend/Services/</c>. These
    /// are the runtime module boundaries the ADR archive most often calls
    /// out (Task Access, CLI, Runner, Supervisor, Analysis, Drift, ...).
    /// </summary>
    private static IReadOnlyList<DriftRef> BuildModuleBoundaries(string repoRoot)
    {
        var backendServices = Path.Combine(repoRoot, "backend", "Services");
        if (!Directory.Exists(backendServices)) return Array.Empty<DriftRef>();

        var entries = new List<DriftRef>();
        foreach (var dir in Directory.EnumerateDirectories(backendServices).OrderBy(d => d, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            entries.Add(new DriftRef(
                Path: $"backend/Services/{name}/",
                Label: $"Backend module: {name}"));
        }
        return entries;
    }

    private static IReadOnlyList<DriftRef> BuildSchemaList(string repoRoot)
    {
        var schemaDir = Path.Combine(repoRoot, "docs", "app", "schemas");
        if (!Directory.Exists(schemaDir)) return Array.Empty<DriftRef>();

        var entries = new List<DriftRef>();
        foreach (var file in Directory.EnumerateFiles(schemaDir, "*.json").OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            entries.Add(new DriftRef(
                Path: $"docs/app/schemas/{name}",
                Label: name));
        }
        return entries;
    }

    private static IReadOnlyList<RecentTaskRef> BuildRecentTasks(string projectRoot)
    {
        var entries = new List<RecentTaskRef>();
        foreach (var lane in RecentTaskLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            if (!Directory.Exists(laneDir)) continue;

            foreach (var dir in Directory.EnumerateDirectories(laneDir))
            {
                var jobJson = Path.Combine(dir, "task.json");
                if (!File.Exists(jobJson)) continue;

                string id = Path.GetFileName(dir);
                string title = id;
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
                    SilentCatch.Note(__ex, "AdrCodeDriftAnalysisService: Malformed task.json: still surface the slug as evidence.");
                    // Malformed task.json: still surface the slug as evidence.
                }

                try { touched = Directory.GetLastWriteTimeUtc(dir); }
                catch (Exception __ex) { SilentCatch.Note(__ex, "AdrCodeDriftAnalysisService: best-effort"); /* best-effort */ }

                entries.Add(new RecentTaskRef(id, title, lane, touched));
            }
        }

        return entries
            .OrderByDescending(t => t.LastWriteUtc ?? DateTime.MinValue)
            .Take(RecentTaskLimit)
            .ToArray();
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

    private static IReadOnlyList<string> BuildSourceRefs(AdrCodeDriftScope scope)
    {
        var refs = new List<string>(capacity: 64);
        foreach (var d in scope.Docs) refs.Add(d.Path);
        foreach (var t in scope.SourceTree) refs.Add(t.Path);
        foreach (var m in scope.ModuleBoundaries) refs.Add(m.Path);
        foreach (var s in scope.Schemas) refs.Add(s.Path);
        foreach (var r in scope.RecentTasks) refs.Add($"{scope.Project}/{r.Lane}/{r.JobId}");
        foreach (var p in scope.RecentDriftReports) refs.Add($"drift:{p.ReportId}");
        foreach (var p in scope.RecentAnalysisReports) refs.Add($"analysis:{p.ReportId}");
        return refs;
    }

    private static IReadOnlyList<string> BuildEvidenceRefSnapshot(AdrCodeDriftScope scope)
    {
        // Subset of source refs kept to a small, scannable list so the
        // synthesised "no finding" or "evidence only" dimension still cites
        // concrete paths the user can click into.
        var refs = new List<string>(capacity: 8);
        if (scope.Docs.Count > 0) refs.Add(scope.Docs[0].Path);
        foreach (var d in scope.Docs.Skip(1).Take(3)) refs.Add(d.Path);
        if (scope.ModuleBoundaries.Count > 0) refs.Add(scope.ModuleBoundaries[0].Path);
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

    private static string RenderRecentTasks(IReadOnlyList<RecentTaskRef> tasks)
    {
        if (tasks.Count == 0) return "(no recent task evidence)";
        var sb = new StringBuilder();
        foreach (var t in tasks)
        {
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ")
                .AppendLf(t.Title);
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

    private static AdrCodeDriftParseResult Malformed(string error, string rawText)
        => new(
            Status: AdrCodeDriftParseStatus.MalformedJson,
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

    private static IReadOnlyList<DriftDimension>? ParseDimensions(AdrCodeDriftDimensionDto[]? raw)
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

    public sealed record RecentTaskRef(string JobId, string Title, string Lane, DateTime? LastWriteUtc);

    public sealed record ReportPointer(string ReportId, string Topic, string CreatedAt);

    // ------------------------------------------------------------------
    // DTOs the JSON sidecar deserialises into
    // ------------------------------------------------------------------

    private sealed record AdrCodeDriftJsonDto
    {
        public string? Verdict { get; init; }
        public string? ScoreBand { get; init; }
        public int OverallScore { get; init; }
        public AdrCodeDriftDimensionDto[]? Dimensions { get; init; }
        public AdrCodeDriftFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record AdrCodeDriftDimensionDto
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

    private sealed record AdrCodeDriftFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedDimension { get; init; }
    }
}

/// <summary>
/// Snapshot of the ADR + architecture-doc + source-tree + schema + recent-task
/// evidence the action gathered before talking to the agent.
/// </summary>
public sealed class AdrCodeDriftScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.DriftRef> Docs { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.DriftRef> SourceTree { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.DriftRef> ModuleBoundaries { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.DriftRef> Schemas { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.RecentTaskRef> RecentTasks { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.ReportPointer> RecentDriftReports { get; init; }
    public required IReadOnlyList<AdrCodeDriftAnalysisService.ReportPointer> RecentAnalysisReports { get; init; }
    public required DateTime CapturedAt { get; init; }
}

/// <summary>
/// Three explicit parse states. A failed JSON parse never hides the
/// Markdown body; the caller renders the body and the parse error side by
/// side.
/// </summary>
public enum AdrCodeDriftParseStatus
{
    Structured,
    Unstructured,
    MalformedJson,
}

/// <summary>Result of <see cref="AdrCodeDriftAnalysisService.TryParseAgentResponse"/>.</summary>
public sealed record AdrCodeDriftParseResult(
    AdrCodeDriftParseStatus Status,
    DriftScoreBand ScoreBand,
    int OverallScore,
    string Summary,
    IReadOnlyList<DriftDimension>? Dimensions,
    IReadOnlyList<DriftFollowUpSuggestion>? FollowUps,
    string? ParseError);
