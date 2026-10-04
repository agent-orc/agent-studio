using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Analysis;

/// <summary>
/// Pure, testable assembly logic for the **Steering Docs Summary and Drift
/// Check** producer - the named action that summarises the agent-facing
/// steering surface (README, AGENTS, the task contract, skills, ADRs,
/// roadmap, design principles, project settings, runtime prompts) and checks
/// whether that surface still matches the queue, roadmap, recent analysis
/// reports, and observed job behaviour.
/// </summary>
/// <remarks>
/// <para>
/// Three pure steps mirror <see cref="RoadmapAlignmentReviewService"/> and
/// <see cref="RecurringOutputPatternService"/> so each piece is covered by its
/// own test:
/// </para>
/// <list type="number">
///   <item><description><see cref="SelectScope"/> walks the canonical steering
///   source set, samples a few recent active and completed jobs as
///   queue-behaviour evidence, and captures pointers to recent analysis
///   reports.</description></item>
///   <item><description><see cref="BuildPrompt"/> renders the runtime prompt
///   template at <c>prompts/runtime/steering-docs-summary-and-drift.md</c> so
///   the wording is editable without recompiling the backend.</description></item>
///   <item><description><see cref="TryParseAgentResponse"/> extracts the JSON
///   sidecar from the agent's free-form Markdown reply with explicit
///   <see cref="AnalysisReportParseStatus"/> fallbacks. A failed parse never
///   hides the Markdown body.</description></item>
/// </list>
/// <para>
/// The action is analysis and proposal generation, not state mutation. It
/// produces an <see cref="AnalysisReport"/> and proposes follow-up tasks; it
/// never rewrites a steering doc, never moves a job, and never relaxes the
/// "one active coding task per project" boundary.
/// </para>
/// </remarks>
public sealed class SteeringDocsSummaryDriftService
{
    /// <summary>Schema-version sentinel reused from the analysis-report contract.</summary>
    public const int CurrentSchemaVersion = 1;

    /// <summary>Canonical topic slug the UI uses for this producer.</summary>
    public const string Topic = "steering-docs-summary-and-drift";

    /// <summary>How many recent analysis reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    /// <summary>How many recent jobs to surface per inspected lane.</summary>
    public const int JobsPerLaneLimit = 8;

    /// <summary>Lanes sampled as "recent job-output evidence". Active +
    /// recently completed work is the right surface for "what does the queue
    /// imply we need a rule for"; <c>1-preparation</c> and <c>7-archive</c>
    /// are excluded by design.</summary>
    public static readonly IReadOnlyList<string> InspectedLanes = new[]
    {
        TaskStates.Ready,
        TaskStates.Progress,
        TaskStates.AutoReview,
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
    /// Walks the canonical steering source set in <paramref name="repoRoot"/>,
    /// samples <see cref="JobsPerLaneLimit"/> recent jobs per inspected lane in
    /// <paramref name="projectRoot"/>, and looks up recent analysis reports.
    /// Returns a typed scope record the prompt template renders against.
    /// </summary>
    /// <param name="project">Project name as it appears in the watch path catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.</param>
    /// <param name="repoRoot">Source repository root (the dev checkout).</param>
    /// <param name="reportStore">Optional analysis-report store. When supplied,
    /// the service includes the most recent <see cref="RecentReportLimit"/>
    /// reports as evidence pointers.</param>
    /// <param name="workspaceRoot">Workspace root used by
    /// <paramref name="reportStore"/>.</param>
    /// <param name="now">Wall-clock for the scope record.</param>
    public SteeringDocsSummaryDriftScope SelectScope(
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

        var sources = BuildSourceInventory(repoRoot);
        var warnings = BuildInventoryWarnings(sources);
        var jobsByLane = BuildJobsByLane(projectRoot);
        var recentReports = LookupRecentAnalysisReports(reportStore, workspaceRoot, project);
        var inventoryClean = warnings.Count == 0;

        return new SteeringDocsSummaryDriftScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            Sources = sources,
            Warnings = warnings,
            JobsByLane = jobsByLane,
            RecentAnalysisReports = recentReports,
            InventoryClean = inventoryClean,
            CapturedAt = now ?? DateTime.UtcNow,
        };
    }

    /// <summary>Renders the prompt template with the assembled scope.</summary>
    public string BuildPrompt(SteeringDocsSummaryDriftScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["source_inventory"] = RenderSourceInventory(scope.Sources),
            ["inventory_warnings"] = RenderInventoryWarnings(scope.Warnings),
            ["recent_analysis_reports"] = RenderReportPointers(scope.RecentAnalysisReports),
            ["recent_job_evidence"] = RenderJobsByLane(scope.JobsByLane),
            ["inventory_clean_flag"] = scope.InventoryClean ? "yes" : "no",
        };

        return Regex.Replace(template, @"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", m =>
        {
            var key = m.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var v) ? v ?? string.Empty : m.Value;
        });
    }

    /// <summary>
    /// Extracts a JSON sidecar from a free-form agent reply. Returns the parse
    /// state, the typed verdict + severity, the typed findings, the typed
    /// proposal refs, the typed follow-ups, and a parser error suitable for
    /// <see cref="AnalysisReport.ParseError"/>. A failed parse never hides the
    /// Markdown body.
    /// </summary>
    public SteeringDocsSummaryDriftParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SteeringDocsSummaryDriftParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: "Agent reply was empty; no structured steering-docs analysis available.",
                Findings: null,
                FollowUps: null,
                ProposalRefs: null,
                Sources: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new SteeringDocsSummaryDriftParseResult(
                Status: AnalysisReportParseStatus.Unstructured,
                Severity: AnalysisReportSeverity.Info,
                Summary: ExtractFirstHeadingOrLine(rawText)
                    ?? "Agent reply contained no structured JSON sidecar.",
                Findings: null,
                FollowUps: null,
                ProposalRefs: null,
                Sources: null,
                ParseError: null);
        }

        var jsonBody = match.Groups["body"].Value;
        try
        {
            var dto = JsonSerializer.Deserialize<SteeringDocsJsonDto>(jsonBody, ParseOptions);
            if (dto is null)
            {
                return Malformed("JSON sidecar parsed to null.", rawText);
            }
            if (string.IsNullOrWhiteSpace(dto.Summary))
            {
                return Malformed("JSON sidecar missing required field 'summary'.", rawText);
            }
            // The schemaVersion / kind are advisory in the sidecar; the host
            // produces the canonical record. We surface a clear error on
            // explicit version mismatch so a future schema bump is visible
            // rather than silently accepted.
            if (dto.SchemaVersion is not null && dto.SchemaVersion != CurrentSchemaVersion)
            {
                return Malformed(
                    $"schemaVersion must be {CurrentSchemaVersion} (was {dto.SchemaVersion}).",
                    rawText);
            }
            if (!string.IsNullOrWhiteSpace(dto.Kind)
                && !string.Equals(dto.Kind, Topic, StringComparison.OrdinalIgnoreCase))
            {
                return Malformed(
                    $"kind must be '{Topic}' (was '{dto.Kind}').", rawText);
            }

            var severity = ParseSeverity(dto.Severity)
                ?? throw new JsonException(
                    $"severity must be one of Info|Warn|High|Critical (was '{dto.Severity}').");

            var findings = (dto.DriftFindings ?? Array.Empty<SteeringDocsFindingDto>())
                .Select(f => new AnalysisReportFinding(
                    Topic: string.IsNullOrWhiteSpace(f.Topic) ? "steering-drift" : f.Topic.Trim(),
                    Severity: ParseSeverity(f.Severity) ?? AnalysisReportSeverity.Info,
                    Message: (f.Message ?? string.Empty).Trim(),
                    EvidenceRefs: f.EvidenceRefs))
                .Where(f => !string.IsNullOrWhiteSpace(f.Message))
                .ToArray();

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<SteeringDocsFollowUpDto>())
                .Select(s => new AnalysisReportFollowUpTaskSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? AnalysisReportFollowUpPriority.Normal,
                    RelatedTopic: ParseRelatedTopic(s.RelatedTopic),
                    TargetState: NormaliseTargetState(s.TargetState)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            var proposalRefs = (dto.ProposalRefs ?? Array.Empty<SteeringDocsProposalRefDto>())
                .Select(p => new SteeringDocsProposalRef(
                    Path: (p.Path ?? string.Empty).Trim(),
                    Label: (p.Label ?? string.Empty).Trim()))
                .Where(p => !string.IsNullOrWhiteSpace(p.Path))
                .ToArray();

            var sources = (dto.Sources ?? Array.Empty<string>())
                .Where(s => !string.IsNullOrWhiteSpace(s))
                .Select(s => s.Trim())
                .ToArray();

            return new SteeringDocsSummaryDriftParseResult(
                Status: AnalysisReportParseStatus.Structured,
                Severity: severity,
                Summary: dto.Summary.Trim(),
                Findings: findings,
                FollowUps: followUps,
                ProposalRefs: proposalRefs,
                Sources: sources,
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
    /// supplies the report id (ULID / UUID v7) and the wall-clock so this
    /// service stays free of clock + id concerns the endpoint already owns.
    /// </summary>
    public AnalysisReport BuildReport(
        SteeringDocsSummaryDriftScope scope,
        SteeringDocsSummaryDriftParseResult parse,
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

        var references = BuildReferences(scope, parse);
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

    // ------------------------------------------------------------------
    // Source-inventory assembly
    // ------------------------------------------------------------------

    /// <summary>
    /// Canonical steering surface for this producer. The set is intentionally
    /// fixed (so the report names what was looked at, even when something is
    /// missing) and mirrors the surface inventoried by
    /// <c>ProjectSteeringDocsService</c>. Drift between the two lists is a
    /// signal the steering surface itself has shifted; keep them in sync.
    /// </summary>
    public static readonly IReadOnlyList<SteeringSourceDef> CanonicalSources = new List<SteeringSourceDef>
    {
        new("readme", "README", "README.md", SteeringSourceKind.ProjectReadme,
            "Product description and on-boarding entry point."),
        new("agents", "AGENTS.md", "AGENTS.md", SteeringSourceKind.AgentInstructions,
            "Single source of truth for agent instructions across CLIs."),
        new("claude-shim", "CLAUDE.md", "CLAUDE.md", SteeringSourceKind.AgentCliShim,
            "Compatibility shim that points Claude Code at AGENTS.md."),
        new("gemini-shim", "GEMINI.md", "GEMINI.md", SteeringSourceKind.AgentCliShim,
            "Compatibility shim that points Gemini CLI at AGENTS.md."),
        new("copilot-shim", ".github/copilot-instructions.md", ".github/copilot-instructions.md",
            SteeringSourceKind.AgentCliShim,
            "Compatibility shim for the GitHub Copilot coding agent."),
        new("frontend-agents", "frontend/AGENTS.md", "frontend/AGENTS.md",
            SteeringSourceKind.AgentInstructions,
            "Frontend-scoped agent instructions; applies under frontend/."),
        new("roadmap", "ROADMAP.md", "ROADMAP.md", SteeringSourceKind.Roadmap,
            "Product thesis, near-term themes, hard boundaries."),
        new("task-contract", "Task contract", "docs/system/contracts/agent-task.md",
            SteeringSourceKind.TaskContract,
            "App-owned lifecycle boundary copied into watched targets."),
        new("skills-architecture", "Skills architecture", "docs/concepts/skills-architecture.md",
            SteeringSourceKind.SkillsLookup,
            "How portable skills are defined and discovered."),
        new("cli-skills-readme", "CLI skills lookup", "docs/system/cli/skills/README.md",
            SteeringSourceKind.SkillsLookup,
            "Per-CLI skill index. Required reading before touching a CLI driver."),
        new("adr", "Architecture decisions", "docs/system/architecture/decisions/adr-archive.md",
            SteeringSourceKind.AdrIndex,
            "Durable archive of load-bearing architectural decisions."),
        new("design-principles", "Design principles", "docs/quality/design-principles.md",
            SteeringSourceKind.SteeringNote,
            "UX contract + design principles."),
        new("commit-doctrine", "Commit / push doctrine", "docs/operations/git/commit-push-doctrine.md",
            SteeringSourceKind.SteeringNote,
            "Where the application owns the commit and push boundary."),
        new("appsettings", "Project settings", "backend/appsettings.json",
            SteeringSourceKind.ProjectSettings,
            "Default backend settings (watch paths, supervisor toggles)."),
    };

    /// <summary>How many days of inactivity flips a source into "stale" when at
    /// least one other source has been touched in the last 30 days.</summary>
    public static readonly TimeSpan StaleThreshold = TimeSpan.FromDays(120);

    /// <summary>Inventory the canonical steering surface, plus a directory
    /// listing for <c>prompts/runtime/</c>. Missing files surface explicitly
    /// (the agent should know an expected source was absent).</summary>
    public static IReadOnlyList<SteeringSourceRef> BuildSourceInventory(string repoRoot)
    {
        var sources = new List<SteeringSourceRef>(capacity: CanonicalSources.Count + 2);
        foreach (var def in CanonicalSources)
        {
            sources.Add(InspectSource(repoRoot, def));
        }
        sources.Add(InspectRuntimePromptsDir(repoRoot));
        return sources;
    }

    private static SteeringSourceRef InspectSource(string repoRoot, SteeringSourceDef def)
    {
        var rel = NormaliseRel(def.RelPath);
        var full = Path.GetFullPath(Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar)));
        if (!File.Exists(full))
        {
            return new SteeringSourceRef(def.Id, def.Label, rel, def.Kind, def.Why,
                Exists: false, UpdatedAt: null, Size: 0, ChildCount: 0);
        }
        var fi = new FileInfo(full);
        return new SteeringSourceRef(def.Id, def.Label, rel, def.Kind, def.Why,
            Exists: true, UpdatedAt: fi.LastWriteTimeUtc, Size: fi.Length, ChildCount: 0);
    }

    private static SteeringSourceRef InspectRuntimePromptsDir(string repoRoot)
    {
        var dirRel = "prompts/runtime";
        var full = Path.GetFullPath(Path.Combine(repoRoot, dirRel.Replace('/', Path.DirectorySeparatorChar)));
        if (!Directory.Exists(full))
        {
            return new SteeringSourceRef("runtime-prompts", "Runtime prompts", dirRel,
                SteeringSourceKind.RuntimePrompt,
                "Editable Markdown templates rendered by backend runtime services.",
                Exists: false, UpdatedAt: null, Size: 0, ChildCount: 0);
        }
        long total = 0;
        DateTime? newest = null;
        int count = 0;
        foreach (var f in Directory.EnumerateFiles(full, "*.md", SearchOption.TopDirectoryOnly))
        {
            var fi = new FileInfo(f);
            count++;
            total += fi.Length;
            if (newest is null || fi.LastWriteTimeUtc > newest) newest = fi.LastWriteTimeUtc;
        }
        return new SteeringSourceRef("runtime-prompts", "Runtime prompts", dirRel,
            SteeringSourceKind.RuntimePrompt,
            "Editable Markdown templates rendered by backend runtime services.",
            Exists: count > 0, UpdatedAt: newest, Size: total, ChildCount: count);
    }

    private static IReadOnlyList<SteeringInventoryWarning> BuildInventoryWarnings(
        IReadOnlyList<SteeringSourceRef> sources)
    {
        var warnings = new List<SteeringInventoryWarning>();

        // Critical missing files first.
        foreach (var s in sources.Where(s => !s.Exists))
        {
            var critical = s.Kind == SteeringSourceKind.AgentInstructions
                || s.Kind == SteeringSourceKind.ProjectReadme
                || s.Kind == SteeringSourceKind.TaskContract;
            // Frontend AGENTS is not load-bearing on every project.
            if (s.Id == "frontend-agents") continue;
            warnings.Add(new SteeringInventoryWarning(
                Severity: critical ? AnalysisReportSeverity.High : AnalysisReportSeverity.Info,
                Kind: SteeringInventoryWarningKind.MissingSource,
                Message: critical
                    ? $"Required steering source is missing: {s.RelPath}."
                    : $"No {s.Label} found at {s.RelPath}.",
                SourceId: s.Id,
                EvidenceRefs: new[] { s.RelPath }));
        }

        // Shim drift: CLAUDE.md / copilot-instructions are meant to be tiny.
        foreach (var s in sources.Where(s => s.Exists && s.Kind == SteeringSourceKind.AgentCliShim && s.Size > 1024))
        {
            warnings.Add(new SteeringInventoryWarning(
                Severity: AnalysisReportSeverity.Warn,
                Kind: SteeringInventoryWarningKind.PossibleConflict,
                Message: $"{s.Label} is {s.Size:N0} bytes; compatibility shims should stay tiny and point at AGENTS.md.",
                SourceId: s.Id,
                EvidenceRefs: new[] { s.RelPath }));
        }

        // Stale: a source is older than the threshold while at least one other
        // moved in the last 30 days.
        var anyRecent = sources.Any(s => s.Exists && s.UpdatedAt is { } u
            && (DateTime.UtcNow - u) < TimeSpan.FromDays(30));
        if (anyRecent)
        {
            foreach (var s in sources.Where(s => s.Exists && s.UpdatedAt is { } u
                && (DateTime.UtcNow - u) > StaleThreshold))
            {
                warnings.Add(new SteeringInventoryWarning(
                    Severity: AnalysisReportSeverity.Warn,
                    Kind: SteeringInventoryWarningKind.Stale,
                    Message: $"{s.Label} hasn't moved in over {(int)StaleThreshold.TotalDays} days while other steering files have updated recently.",
                    SourceId: s.Id,
                    EvidenceRefs: new[] { s.RelPath }));
            }
        }

        return warnings;
    }

    // ------------------------------------------------------------------
    // Job evidence assembly
    // ------------------------------------------------------------------

    private static IReadOnlyDictionary<string, IReadOnlyList<TaskEvidenceRef>> BuildJobsByLane(string projectRoot)
    {
        var byLane = new Dictionary<string, IReadOnlyList<TaskEvidenceRef>>(StringComparer.Ordinal);
        foreach (var lane in InspectedLanes)
        {
            var laneDir = Path.Combine(projectRoot, lane);
            byLane[lane] = ReadLane(laneDir, lane);
        }
        return byLane;
    }

    private static IReadOnlyList<TaskEvidenceRef> ReadLane(string laneDir, string lane)
    {
        if (!Directory.Exists(laneDir)) return Array.Empty<TaskEvidenceRef>();
        var entries = new List<(TaskEvidenceRef Job, DateTime Touched)>();
        foreach (var dir in Directory.EnumerateDirectories(laneDir))
        {
            var jobJson = Path.Combine(dir, "task.json");
            if (!File.Exists(jobJson)) continue;
            var slug = Path.GetFileName(dir);
            var title = slug;
            try
            {
                var text = File.ReadAllText(jobJson);
                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                if (root.TryGetProperty("id", out var idEl) && idEl.ValueKind == JsonValueKind.String)
                    slug = idEl.GetString() ?? slug;
                if (root.TryGetProperty("title", out var titleEl) && titleEl.ValueKind == JsonValueKind.String)
                    title = titleEl.GetString() ?? slug;
            }
            catch (JsonException __ex)
            {
                SilentCatch.Note(__ex, "SteeringDocsSummaryDriftService: Surface the slug regardless; the agent can flag the malformed file.");
                // Surface the slug regardless; the agent can flag the malformed file.
            }
            DateTime touched;
            try { touched = Directory.GetLastWriteTimeUtc(dir); }
            catch { touched = DateTime.MinValue; }
            var hasStatus = File.Exists(Path.Combine(dir, "status.md"));
            var hasLogs = Directory.Exists(Path.Combine(dir, "logs"));
            entries.Add((new TaskEvidenceRef(slug, title, lane, touched, hasStatus, hasLogs), touched));
        }
        return entries
            .OrderByDescending(e => e.Touched)
            .Take(JobsPerLaneLimit)
            .Select(e => e.Job)
            .ToArray();
    }

    private static IReadOnlyList<AnalysisReportPointer> LookupRecentAnalysisReports(
        AnalysisReportStore? store, string? workspaceRoot, string project)
    {
        if (store is null || string.IsNullOrWhiteSpace(workspaceRoot))
            return Array.Empty<AnalysisReportPointer>();
        return store.Snapshot(workspaceRoot, project)
            .OrderByDescending(r => r.CreatedAt)
            .Take(RecentReportLimit)
            .Select(r => new AnalysisReportPointer(
                ReportId: r.ReportId,
                Topic: r.Topic,
                CreatedAt: r.CreatedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ")))
            .ToArray();
    }

    // ------------------------------------------------------------------
    // Rendering
    // ------------------------------------------------------------------

    private static string RenderSourceInventory(IReadOnlyList<SteeringSourceRef> sources)
    {
        if (sources.Count == 0) return "(no steering sources found)";
        var sb = new StringBuilder();
        foreach (var s in sources)
        {
            sb.Append("- `").Append(s.RelPath).Append("` - ").Append(s.Label);
            if (!s.Exists)
            {
                sb.Append(" _(missing)_");
            }
            else
            {
                sb.Append(" _(");
                if (s.UpdatedAt is { } u)
                    sb.Append("updated ").Append(u.ToUniversalTime().ToString("yyyy-MM-dd")).Append(", ");
                sb.Append(s.Size.ToString("N0", System.Globalization.CultureInfo.InvariantCulture)).Append(" bytes");
                if (s.ChildCount > 0)
                    sb.Append(", ").Append(s.ChildCount).Append(" entries");
                sb.Append(")_");
            }
            sb.AppendLf();
            if (!string.IsNullOrWhiteSpace(s.Why))
                sb.Append("    > ").AppendLf(s.Why);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderInventoryWarnings(IReadOnlyList<SteeringInventoryWarning> warnings)
    {
        if (warnings.Count == 0) return "(none)";
        var sb = new StringBuilder();
        foreach (var w in warnings)
        {
            sb.Append("- **").Append(w.Severity).Append("** _(")
                .Append(w.Kind).Append(")_ ").AppendLf(w.Message);
        }
        return sb.ToString().TrimEnd();
    }

    private static string RenderReportPointers(IReadOnlyList<AnalysisReportPointer> reports)
    {
        if (reports.Count == 0) return "(no prior analysis reports for this project)";
        var sb = new StringBuilder();
        foreach (var r in reports)
            sb.Append("- `").Append(r.ReportId).Append("` _(").Append(r.Topic).Append(", ").Append(r.CreatedAt).AppendLf(")_");
        return sb.ToString().TrimEnd();
    }

    private static string RenderJobsByLane(IReadOnlyDictionary<string, IReadOnlyList<TaskEvidenceRef>> byLane)
    {
        if (byLane.All(kv => kv.Value.Count == 0))
            return "(no recent job evidence in the inspected lanes)";
        var sb = new StringBuilder();
        foreach (var lane in InspectedLanes)
        {
            if (!byLane.TryGetValue(lane, out var jobs) || jobs.Count == 0) continue;
            sb.Append("### ").AppendLf(lane);
            foreach (var j in jobs)
            {
                sb.Append("- `").Append(j.Lane).Append('/').Append(j.JobId).Append("` - ")
                    .Append(j.Title);
                var tags = new List<string>();
                if (j.HasStatus) tags.Add("status.md");
                if (j.HasLogs) tags.Add("logs/");
                if (tags.Count > 0)
                {
                    sb.Append(" _(");
                    sb.Append(string.Join(", ", tags));
                    sb.Append(")_");
                }
                sb.AppendLf();
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // Reference assembly
    // ------------------------------------------------------------------

    private static IReadOnlyList<AnalysisReportReference> BuildReferences(
        SteeringDocsSummaryDriftScope scope,
        SteeringDocsSummaryDriftParseResult parse)
    {
        var refs = new List<AnalysisReportReference>();
        // Doc references for every existing source so the report cites by
        // stable id instead of duplicating bodies.
        foreach (var s in scope.Sources.Where(s => s.Exists))
        {
            refs.Add(new AnalysisReportReference(
                Kind: AnalysisReportReferenceKind.Doc,
                Ref: s.RelPath,
                Label: s.Label));
        }
        // Recent jobs we surfaced so the agent's evidenceRefs can match.
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
        // Prior analysis reports.
        foreach (var r in scope.RecentAnalysisReports)
            refs.Add(new AnalysisReportReference(
                AnalysisReportReferenceKind.PreviousReport, r.ReportId,
                $"{r.Topic} @ {r.CreatedAt}"));
        // Proposal refs the agent named explicitly become Doc references; the
        // path may be 'new:foo.md' for files that don't exist yet.
        if (parse.ProposalRefs is not null)
        {
            foreach (var p in parse.ProposalRefs)
            {
                if (string.IsNullOrWhiteSpace(p.Path)) continue;
                refs.Add(new AnalysisReportReference(
                    Kind: AnalysisReportReferenceKind.Doc,
                    Ref: p.Path,
                    Label: string.IsNullOrWhiteSpace(p.Label) ? "proposal" : $"proposal: {p.Label}"));
            }
        }
        return refs;
    }

    private static IReadOnlyList<string> BuildTags(
        SteeringDocsSummaryDriftScope scope,
        SteeringDocsSummaryDriftParseResult parse)
    {
        var tags = new List<string> { "steering-docs", "summary-and-drift" };
        if (!scope.InventoryClean) tags.Add("inventory-dirty");
        if (parse.Status == AnalysisReportParseStatus.Unstructured) tags.Add("unstructured");
        if (parse.Status == AnalysisReportParseStatus.MalformedJson) tags.Add("malformed-json");
        return tags;
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static SteeringDocsSummaryDriftParseResult Malformed(string error, string rawText)
        => new(
            Status: AnalysisReportParseStatus.MalformedJson,
            Severity: AnalysisReportSeverity.Info,
            Summary: ExtractFirstHeadingOrLine(rawText)
                ?? "Agent reply contained an unparseable JSON sidecar; Markdown body remains the durable artifact.",
            Findings: null,
            FollowUps: null,
            ProposalRefs: null,
            Sources: null,
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

    private static string NormaliseTargetState(string? raw)
    {
        // Constraint: this action must not place follow-ups directly in
        // 2-ready. The producer is open-ended; any value the agent emits is
        // coerced to 1-preparation regardless. The user can promote a
        // suggestion to 2-ready through the existing job-creation entry point.
        _ = raw;
        return AnalysisReportFollowUpTargetStates.OnePreparation;
    }

    private static string NormaliseRel(string rel) =>
        (rel ?? string.Empty).Replace('\\', '/').TrimStart('/');

    // ------------------------------------------------------------------
    // Records used by SelectScope / BuildPrompt
    // ------------------------------------------------------------------

    public sealed record SteeringSourceDef(
        string Id,
        string Label,
        string RelPath,
        SteeringSourceKind Kind,
        string Why);

    public sealed record SteeringSourceRef(
        string Id,
        string Label,
        string RelPath,
        SteeringSourceKind Kind,
        string Why,
        bool Exists,
        DateTime? UpdatedAt,
        long Size,
        int ChildCount);

    public sealed record SteeringInventoryWarning(
        AnalysisReportSeverity Severity,
        SteeringInventoryWarningKind Kind,
        string Message,
        string? SourceId,
        IReadOnlyList<string> EvidenceRefs);

    public sealed record TaskEvidenceRef(
        string JobId,
        string Title,
        string Lane,
        DateTime Touched,
        bool HasStatus,
        bool HasLogs);

    public sealed record AnalysisReportPointer(
        string ReportId,
        string Topic,
        string CreatedAt);

    public sealed record SteeringDocsProposalRef(string Path, string Label);

    // ------------------------------------------------------------------
    // DTOs the JSON sidecar deserialises into
    // ------------------------------------------------------------------

    private sealed record SteeringDocsJsonDto
    {
        public string? Kind { get; init; }
        public int? SchemaVersion { get; init; }
        public string? Summary { get; init; }
        public string? Severity { get; init; }
        public string[]? Sources { get; init; }
        public SteeringDocsFindingDto[]? DriftFindings { get; init; }
        public SteeringDocsProposalRefDto[]? ProposalRefs { get; init; }
        public SteeringDocsFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
        public string? ParseStatus { get; init; }
    }

    private sealed record SteeringDocsFindingDto
    {
        public string? Topic { get; init; }
        public string? Severity { get; init; }
        public string? Message { get; init; }
        public string[]? EvidenceRefs { get; init; }
    }

    private sealed record SteeringDocsProposalRefDto
    {
        public string? Path { get; init; }
        public string? Label { get; init; }
    }

    private sealed record SteeringDocsFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedTopic { get; init; }
        public string? TargetState { get; init; }
    }
}

/// <summary>The kinds of steering sources this producer inventories. Mirrors
/// the project Steering Docs surface so the two views stay aligned.</summary>
public enum SteeringSourceKind
{
    ProjectReadme,
    AgentInstructions,
    AgentCliShim,
    Roadmap,
    TaskContract,
    SkillsLookup,
    AdrIndex,
    RuntimePrompt,
    ProjectSettings,
    SteeringNote,
}

/// <summary>Why an inventory entry was flagged.</summary>
public enum SteeringInventoryWarningKind
{
    MissingSource,
    Stale,
    PossibleConflict,
}

/// <summary>Snapshot of the steering-source inventory + queue evidence + prior
/// reports the action gathered before talking to the agent. Plain data so
/// tests can build fixtures without spinning up the surrounding services.</summary>
public sealed class SteeringDocsSummaryDriftScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required IReadOnlyList<SteeringDocsSummaryDriftService.SteeringSourceRef> Sources { get; init; }
    public required IReadOnlyList<SteeringDocsSummaryDriftService.SteeringInventoryWarning> Warnings { get; init; }
    public required IReadOnlyDictionary<string, IReadOnlyList<SteeringDocsSummaryDriftService.TaskEvidenceRef>> JobsByLane { get; init; }
    public required IReadOnlyList<SteeringDocsSummaryDriftService.AnalysisReportPointer> RecentAnalysisReports { get; init; }
    public required bool InventoryClean { get; init; }
    public required DateTime CapturedAt { get; init; }
}

/// <summary>Result of <see cref="SteeringDocsSummaryDriftService.TryParseAgentResponse"/>.
/// Carries the parse status the report should record together with the typed
/// fields the JSON sidecar described.</summary>
public sealed record SteeringDocsSummaryDriftParseResult(
    AnalysisReportParseStatus Status,
    AnalysisReportSeverity Severity,
    string Summary,
    IReadOnlyList<AnalysisReportFinding>? Findings,
    IReadOnlyList<AnalysisReportFollowUpTaskSuggestion>? FollowUps,
    IReadOnlyList<SteeringDocsSummaryDriftService.SteeringDocsProposalRef>? ProposalRefs,
    IReadOnlyList<string>? Sources,
    string? ParseError);
