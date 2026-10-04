using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.Prompts;

namespace AgentStudio.Drift;

/// <summary>
/// Pure, testable assembly logic for the **Software / Architecture Drift**
/// action - the named producer that compares the documented high-level
/// architecture model against the current source tree, schemas, tests,
/// runtime signals, and recent task evidence.
/// </summary>
/// <remarks>
/// <para>
/// Mirrors <see cref="AdrCodeDriftAnalysisService"/> in shape: scope ->
/// prompt -> parse -> report. Adds a small architecture-model frontmatter
/// reader so the action is the first in-process consumer of
/// <c>docs/app/schemas/architecture-model.schema.json</c> (per
/// <c>docs/system/architecture/model.md</c> Section 8 "Implementation status and
/// parser ownership"). The model file lives with project evidence; the
/// reader looks under the watched project's
/// <c>architecture/&lt;modelId&gt;.md</c> first, then under the workspace's
/// <c>projects/&lt;projectKey&gt;/architecture/</c> folder.
/// </para>
/// <para>
/// The analyzer enforces the schema's hard ten-element ceiling at parse
/// time. A model with more than ten elements is rejected and surfaced as a
/// Critical Architecture finding; a missing model is surfaced as a High
/// Architecture finding ("not yet defined") rather than silently producing
/// an empty marble surface.
/// </para>
/// </remarks>
public sealed class SoftwareArchitectureDriftAnalysisService
{
    public const int CurrentSchemaVersion = 1;

    /// <summary>Topic slug used to label the report and tag follow-ups.</summary>
    public const string Topic = "software-architecture-drift";

    /// <summary>
    /// Hard ceiling on element count. Mirrors the schema's
    /// <c>maxItems: 10</c> and the prose contract in
    /// <c>docs/system/architecture/model.md</c>. Models that exceed this are
    /// rejected and surface as Critical drift findings.
    /// </summary>
    public const int MaxArchitectureElements = 10;

    /// <summary>How many recent jobs to surface as evidence pointers.</summary>
    public const int RecentTaskLimit = 8;

    /// <summary>How many recent reports to surface as evidence pointers.</summary>
    public const int RecentReportLimit = 5;

    /// <summary>Lanes the action samples for "recent task evidence".</summary>
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
    /// Walks the architecture model file (when present), the ADR archive,
    /// architecture notes, source tree, module boundaries, schemas, test
    /// directories, and recent task evidence. Returns a typed scope record
    /// the prompt template renders against.
    /// </summary>
    /// <param name="project">Project name as it appears in the watch path catalogue.</param>
    /// <param name="projectRoot">Filesystem root that contains lane folders.</param>
    /// <param name="repoRoot">Source repository root (the dev checkout).</param>
    /// <param name="watchedProjectRoot">Optional path to the watched project's
    /// repository (where <c>architecture/&lt;modelId&gt;.md</c> normally
    /// lives). When null, the workspace fallback is used.</param>
    /// <param name="workspaceRoot">Workspace root used by the report stores
    /// and by the architecture-model fallback lookup.</param>
    /// <param name="driftStore">Optional drift-report store used to surface
    /// the most recent <see cref="RecentReportLimit"/> drift reports as
    /// evidence pointers.</param>
    /// <param name="analysisStore">Optional analysis-report store. Used to
    /// surface the most recent generic analysis reports next to drift
    /// reports.</param>
    /// <param name="now">Wall-clock for the scope record. Injected so tests
    /// can pin a deterministic timestamp.</param>
    public SoftwareArchitectureDriftScope SelectScope(
        string project,
        string projectRoot,
        string repoRoot,
        string? watchedProjectRoot = null,
        string? workspaceRoot = null,
        DriftReportStore? driftStore = null,
        AnalysisReportStore? analysisStore = null,
        DateTime? now = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(project);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(repoRoot);

        var (model, modelLookup) = LoadArchitectureModel(project, watchedProjectRoot, workspaceRoot);
        var docs = BuildDocList(repoRoot);
        var sourceTree = BuildSourceTree(repoRoot);
        var moduleBoundaries = BuildModuleBoundaries(repoRoot);
        var schemas = BuildSchemaList(repoRoot);
        var testDirs = BuildTestDirs(repoRoot);
        var recentTasks = BuildRecentTasks(projectRoot);
        var recentDrift = LookupRecentDriftReports(driftStore, workspaceRoot, project);
        var recentAnalysis = LookupRecentAnalysisReports(analysisStore, workspaceRoot, project);

        return new SoftwareArchitectureDriftScope
        {
            Project = project,
            ProjectRoot = projectRoot,
            RepoRoot = repoRoot,
            ArchitectureModel = model,
            ArchitectureModelLookup = modelLookup,
            Docs = docs,
            SourceTree = sourceTree,
            ModuleBoundaries = moduleBoundaries,
            Schemas = schemas,
            TestDirs = testDirs,
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
    public string BuildPrompt(SoftwareArchitectureDriftScope scope, string template)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentException.ThrowIfNullOrWhiteSpace(template);

        var values = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase)
        {
            ["project"] = scope.Project,
            ["captured_at"] = scope.CapturedAt.ToUniversalTime().ToString("yyyy-MM-ddTHH:mm:ssZ"),
            ["repo_root"] = scope.RepoRoot,
            ["project_root"] = scope.ProjectRoot,
            ["architecture_model"] = RenderArchitectureModel(scope.ArchitectureModel, scope.ArchitectureModelLookup),
            ["doc_list"] = RenderRefList(scope.Docs),
            ["source_tree"] = RenderRefList(scope.SourceTree),
            ["module_boundaries"] = RenderRefList(scope.ModuleBoundaries),
            ["schema_list"] = RenderRefList(scope.Schemas),
            ["test_dirs"] = RenderRefList(scope.TestDirs),
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
    /// parse state, the typed dimensions, the architecture-model element
    /// projection (if any), the verdict, and any parser error. Failed JSON
    /// parses never hide the Markdown body.
    /// </summary>
    public SoftwareArchitectureDriftParseResult TryParseAgentResponse(string? rawText)
    {
        if (string.IsNullOrWhiteSpace(rawText))
        {
            return new SoftwareArchitectureDriftParseResult(
                Status: SoftwareArchitectureDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: "Agent reply was empty; no structured architecture-drift analysis available.",
                Dimensions: null,
                ArchitectureElements: null,
                FollowUps: null,
                ParseError: null);
        }

        var match = JsonFenceRegex.Match(rawText);
        if (!match.Success)
        {
            return new SoftwareArchitectureDriftParseResult(
                Status: SoftwareArchitectureDriftParseStatus.Unstructured,
                ScoreBand: DriftScoreBand.Unknown,
                OverallScore: 0,
                Summary: ExtractFirstHeadingOrLine(rawText)
                    ?? "Agent reply contained no structured JSON sidecar.",
                Dimensions: null,
                ArchitectureElements: null,
                FollowUps: null,
                ParseError: null);
        }

        var jsonBody = match.Groups["body"].Value;
        try
        {
            var dto = JsonSerializer.Deserialize<SoftwareArchitectureDriftJsonDto>(jsonBody, ParseOptions);
            if (dto is null)
                return Malformed("JSON sidecar parsed to null.", rawText);
            if (string.IsNullOrWhiteSpace(dto.Verdict))
                return Malformed("JSON sidecar missing required field 'verdict'.", rawText);

            var band = ParseScoreBand(dto.ScoreBand);
            if (band is null)
                return Malformed(
                    $"scoreBand must be one of Healthy|Watch|Warn|Critical|Unknown (was '{dto.ScoreBand}').",
                    rawText);

            var overall = dto.OverallScore;
            if (overall is < 0 or > 100)
                return Malformed($"overallScore must be 0..100 (was {overall}).", rawText);

            var dims = ParseDimensions(dto.Dimensions);
            var elements = ParseArchitectureElements(dto.ArchitectureModel?.Elements);
            if (elements is { Count: > MaxArchitectureElements })
                return Malformed(
                    $"architectureModel.elements must contain at most {MaxArchitectureElements} entries (was {elements.Count}).",
                    rawText);

            var followUps = (dto.FollowUpTaskSuggestions ?? Array.Empty<SoftwareArchitectureFollowUpDto>())
                .Select(s => new DriftFollowUpSuggestion(
                    Title: (s.Title ?? string.Empty).Trim(),
                    Summary: (s.Summary ?? string.Empty).Trim(),
                    Priority: ParseFollowUpPriority(s.Priority) ?? DriftFollowUpPriority.Normal,
                    RelatedDimension: ParseDimensionType(s.RelatedDimension)))
                .Where(s => !string.IsNullOrWhiteSpace(s.Title))
                .ToArray();

            return new SoftwareArchitectureDriftParseResult(
                Status: SoftwareArchitectureDriftParseStatus.Structured,
                ScoreBand: band.Value,
                OverallScore: overall,
                Summary: dto.Verdict.Trim(),
                Dimensions: dims,
                ArchitectureElements: elements,
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
    /// Composes the typed <see cref="DriftReport"/> for one run. Synthesises
    /// the architecture-model projection from the source model + the agent's
    /// per-element scores. When the source model is missing, emits a single
    /// High Architecture finding so the report is still schema-valid and the
    /// "not yet defined" gap is visible.
    /// </summary>
    public DriftReport BuildReport(
        SoftwareArchitectureDriftScope scope,
        SoftwareArchitectureDriftParseResult parse,
        string reportId,
        DateTime createdAt,
        DriftReportTrigger trigger = DriftReportTrigger.Manual)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(parse);
        ArgumentException.ThrowIfNullOrWhiteSpace(reportId);

        var sourceRefs = BuildSourceRefs(scope);
        var lookup = scope.ArchitectureModelLookup;
        var model = scope.ArchitectureModel;

        IReadOnlyList<DriftDimension> dimensions;
        DriftArchitectureModel? archModel;
        DriftScoreBand band;
        int overall;

        if (parse.Status == SoftwareArchitectureDriftParseStatus.Structured)
        {
            band = parse.ScoreBand;
            overall = parse.OverallScore;

            if (model is null)
            {
                // Agent gave a verdict but the source model is missing; this is
                // itself High Architecture drift per the prompt contract.
                dimensions = new[] { BuildMissingModelDimension(scope, lookup) };
                archModel = null;
                if (band == DriftScoreBand.Healthy) band = DriftScoreBand.Warn;
            }
            else
            {
                dimensions = parse.Dimensions is { Count: > 0 }
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
                            Summary: "No architecture drift findings reported; agent verdict is healthy.",
                            EvidenceRefs: BuildEvidenceRefSnapshot(scope),
                            RecommendedActions: Array.Empty<string>()),
                    };
                archModel = ProjectArchitectureModel(model, parse.ArchitectureElements);
            }
        }
        else
        {
            // Unstructured / MalformedJson path: keep the model projection
            // healthy but mark the per-element scores as Unknown so the
            // marble surface still renders the source-of-truth elements.
            band = DriftScoreBand.Unknown;
            overall = 0;
            if (model is null)
            {
                dimensions = new[] { BuildMissingModelDimension(scope, lookup) };
                archModel = null;
            }
            else
            {
                dimensions = new[]
                {
                    new DriftDimension(
                        Type: DriftDimensionType.Architecture,
                        Score: 0,
                        Severity: DriftSeverity.Info,
                        Confidence: 0,
                        SourceCoverage: 0,
                        Status: DriftFindingStatus.New,
                        Summary: parse.Status == SoftwareArchitectureDriftParseStatus.MalformedJson
                            ? $"Agent JSON sidecar failed to parse; Markdown body remains the durable artifact. Reason: {parse.ParseError}"
                            : "No agent narrative supplied; evidence-only scope assembled from architecture model.",
                        EvidenceRefs: BuildEvidenceRefSnapshot(scope),
                        RecommendedActions: new[]
                        {
                            "Run the embedded prompt against a CLI agent and POST the reply back.",
                        }),
                };
                archModel = ProjectArchitectureModel(model, elements: null);
            }
        }

        var followUps = parse.FollowUps ?? Array.Empty<DriftFollowUpSuggestion>();

        var parseStatus = parse.Status switch
        {
            SoftwareArchitectureDriftParseStatus.Structured => DriftReportParseStatus.Structured,
            SoftwareArchitectureDriftParseStatus.MalformedJson => DriftReportParseStatus.MalformedJson,
            _ => DriftReportParseStatus.Unstructured,
        };

        return new DriftReport(
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
            ArchitectureModel: archModel,
            Producer: new DriftReportProducer(MapProducerKind(trigger), Agent: Topic),
            ParseStatus: parseStatus,
            ParseError: parse.ParseError);
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
    // Architecture model lookup + parser
    // ------------------------------------------------------------------

    private static (ArchitectureModelDocument? model, ArchitectureModelLookup lookup) LoadArchitectureModel(
        string project, string? watchedProjectRoot, string? workspaceRoot)
    {
        var attempted = new List<string>();
        var rejection = (string?)null;

        // Watched project repository: architecture/<modelId>.md (preferred).
        if (!string.IsNullOrWhiteSpace(watchedProjectRoot))
        {
            var dir = Path.Combine(watchedProjectRoot, "architecture");
            attempted.Add(dir);
            var hit = TryFindAndParse(dir, ref rejection);
            if (hit.found)
                return (hit.model, new ArchitectureModelLookup(hit.path!, attempted, rejection));
        }

        // Workspace fallback: <workspace>/projects/<project>/architecture/<modelId>.md.
        if (!string.IsNullOrWhiteSpace(workspaceRoot))
        {
            var dir = Path.Combine(workspaceRoot, "projects", project, "architecture");
            attempted.Add(dir);
            var hit = TryFindAndParse(dir, ref rejection);
            if (hit.found)
                return (hit.model, new ArchitectureModelLookup(hit.path!, attempted, rejection));
        }

        return (null, new ArchitectureModelLookup(SourcePath: null, AttemptedPaths: attempted, RejectionReason: rejection));
    }

    private static (bool found, ArchitectureModelDocument? model, string? path) TryFindAndParse(
        string dir, ref string? rejection)
    {
        if (!Directory.Exists(dir)) return (false, null, null);
        foreach (var file in Directory.EnumerateFiles(dir, "*.md").OrderBy(p => p, StringComparer.Ordinal))
        {
            try
            {
                var (model, error) = ParseArchitectureModelFile(file);
                if (model is not null)
                    return (true, model, file);
                if (error is not null)
                    rejection ??= $"{Path.GetFileName(file)}: {error}";
            }
            catch (Exception ex)
            {
                rejection ??= $"{Path.GetFileName(file)}: {ex.Message}";
            }
        }
        return (false, null, null);
    }

    /// <summary>
    /// Reads the first YAML frontmatter block of an architecture-model
    /// Markdown file and applies the schema's hard rules: at least one and at
    /// most ten elements, kebab-case ids, required per-element fields. Returns
    /// (null, error) when the file is not a valid model.
    /// </summary>
    internal static (ArchitectureModelDocument? model, string? error) ParseArchitectureModelFile(string path)
    {
        var text = File.ReadAllText(path);
        var trimmed = text.TrimStart('﻿', ' ', '\t', '\r', '\n');
        if (!trimmed.StartsWith("---", StringComparison.Ordinal))
            return (null, "missing YAML frontmatter delimiter");

        var afterOpen = trimmed.AsSpan(3);
        var newline = afterOpen.IndexOf('\n');
        if (newline < 0) return (null, "frontmatter delimiter not followed by content");

        var rest = afterOpen[(newline + 1)..];
        var closeIdx = FindFrontmatterClose(rest);
        if (closeIdx < 0) return (null, "frontmatter closing delimiter not found");

        var block = rest[..closeIdx].ToString();
        try
        {
            var node = MiniYaml.Parse(block);
            if (node is not MiniYamlMap map) return (null, "frontmatter root must be a mapping");
            return BuildModelFromYaml(map, path);
        }
        catch (FormatException ex)
        {
            return (null, ex.Message);
        }
    }

    private static int FindFrontmatterClose(ReadOnlySpan<char> rest)
    {
        int idx = 0;
        while (idx < rest.Length)
        {
            int lineStart = idx;
            while (idx < rest.Length && rest[idx] != '\n') idx++;
            var line = rest[lineStart..idx];
            var trimmed = line;
            while (trimmed.Length > 0 && (trimmed[^1] == '\r' || trimmed[^1] == ' ' || trimmed[^1] == '\t'))
                trimmed = trimmed[..^1];
            if (trimmed.SequenceEqual("---".AsSpan()) || trimmed.SequenceEqual("...".AsSpan()))
                return lineStart;
            if (idx < rest.Length) idx++;
        }
        return -1;
    }

    private static (ArchitectureModelDocument? model, string? error) BuildModelFromYaml(MiniYamlMap root, string sourcePath)
    {
        string? Get(string key) => root.TryGetScalar(key, out var v) ? v : null;
        var modelId = Get("modelId");
        var title = Get("title");
        var project = Get("project");
        var updatedAt = Get("updatedAt");
        var owner = Get("owner");
        var summary = Get("summary");
        var diagramHint = Get("diagramHint");
        var schemaVersion = Get("schemaVersion");

        if (string.IsNullOrWhiteSpace(modelId)) return (null, "modelId required");
        if (string.IsNullOrWhiteSpace(title)) return (null, "title required");
        if (!IsKebabCase(modelId)) return (null, $"modelId '{modelId}' is not kebab-case");
        if (!root.TryGetSequence("elements", out var elementsSeq))
            return (null, "elements required");
        if (elementsSeq.Count == 0)
            return (null, "elements must contain at least one entry");
        if (elementsSeq.Count > MaxArchitectureElements)
            return (null, $"elements must contain at most {MaxArchitectureElements} entries (was {elementsSeq.Count})");

        var elements = new List<ArchitectureModelElement>(elementsSeq.Count);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in elementsSeq)
        {
            if (item is not MiniYamlMap em) return (null, "elements entry must be a mapping");
            var elementId = em.TryGetScalar("elementId", out var eid) ? eid : null;
            var label = em.TryGetScalar("label", out var lbl) ? lbl : null;
            var expectedRole = em.TryGetScalar("expectedRole", out var role) ? role : null;
            if (string.IsNullOrWhiteSpace(elementId)) return (null, "element.elementId required");
            if (!IsKebabCase(elementId)) return (null, $"element.elementId '{elementId}' is not kebab-case");
            if (!seen.Add(elementId)) return (null, $"element.elementId '{elementId}' is not unique");
            if (string.IsNullOrWhiteSpace(label)) return (null, $"element {elementId} label required");
            if (string.IsNullOrWhiteSpace(expectedRole)) return (null, $"element {elementId} expectedRole required");

            em.TryGetSequenceOfStrings("ownershipBoundary", out var ownership);
            if (ownership is null || ownership.Count == 0)
                return (null, $"element {elementId} ownershipBoundary required (at least one entry)");
            em.TryGetSequenceOfStrings("guidelines", out var guidelines);
            em.TryGetSequenceOfStrings("allowedDependencies", out var allowedDeps);
            em.TryGetSequenceOfStrings("sourceRefs", out var sourceRefs);
            em.TryGetSequenceOfStrings("relevantTests", out var relTests);
            em.TryGetSequenceOfStrings("relevantSchemas", out var relSchemas);
            em.TryGetSequenceOfStrings("runtimeSignals", out var runtimeSignals);

            elements.Add(new ArchitectureModelElement(
                ElementId: elementId,
                Label: label,
                ExpectedRole: expectedRole,
                OwnershipBoundary: ownership,
                Guidelines: guidelines ?? Array.Empty<string>(),
                AllowedDependencies: allowedDeps ?? Array.Empty<string>(),
                SourceRefs: sourceRefs ?? Array.Empty<string>(),
                RelevantTests: relTests ?? Array.Empty<string>(),
                RelevantSchemas: relSchemas ?? Array.Empty<string>(),
                RuntimeSignals: runtimeSignals ?? Array.Empty<string>()));
        }

        return (new ArchitectureModelDocument(
            ModelId: modelId,
            Title: title,
            Project: project,
            UpdatedAt: updatedAt,
            Owner: owner,
            Summary: summary,
            DiagramHint: diagramHint,
            SchemaVersion: int.TryParse(schemaVersion, out var sv) ? sv : 1,
            Elements: elements,
            SourcePath: sourcePath), null);
    }

    private static bool IsKebabCase(string s)
    {
        if (string.IsNullOrEmpty(s) || s.Length > 64) return false;
        if (!(char.IsLower(s[0]) || char.IsDigit(s[0]))) return false;
        for (int i = 1; i < s.Length; i++)
        {
            var c = s[i];
            if (!(char.IsLower(c) || char.IsDigit(c) || c == '-')) return false;
        }
        return true;
    }

    private static DriftArchitectureModel ProjectArchitectureModel(
        ArchitectureModelDocument source,
        IReadOnlyList<SoftwareArchitectureElementParse>? elements)
    {
        var byId = elements?.ToDictionary(e => e.ElementId, StringComparer.Ordinal)
                   ?? new Dictionary<string, SoftwareArchitectureElementParse>(StringComparer.Ordinal);
        var sourceRef = source.SourcePath is null
            ? null
            : Path.GetFileName(source.SourcePath);

        var projected = new List<DriftArchitectureElement>(source.Elements.Count);
        foreach (var el in source.Elements)
        {
            byId.TryGetValue(el.ElementId, out var scored);
            var evidenceRefs = scored?.EvidenceRefs is { Count: > 0 } refs
                ? refs
                : DefaultEvidenceRefsFor(el);
            projected.Add(new DriftArchitectureElement(
                ElementId: el.ElementId,
                Label: el.Label,
                ExpectedRole: el.ExpectedRole,
                Score: scored?.Score ?? 0,
                Severity: scored?.Severity ?? DriftSeverity.Info,
                SourceCoverage: scored?.SourceCoverage ?? 0,
                Status: scored?.Status ?? DriftFindingStatus.New,
                EvidenceRefs: evidenceRefs,
                Guidelines: el.Guidelines,
                AllowedDependencies: el.AllowedDependencies,
                SourceRefs: el.SourceRefs,
                Summary: scored?.Summary,
                FollowUpTaskSuggestions: scored?.FollowUpTaskSuggestions ?? Array.Empty<string>()));
        }
        return new DriftArchitectureModel(
            ModelId: source.ModelId,
            Title: source.Title,
            Elements: projected,
            SourceRef: sourceRef);
    }

    private static IReadOnlyList<string> DefaultEvidenceRefsFor(ArchitectureModelElement el)
    {
        // Cite the element's own ownership boundaries as evidence so the
        // marble surface always points at concrete paths even when the agent
        // did not enumerate evidence for this element.
        var refs = new List<string>(capacity: 4);
        foreach (var b in el.OwnershipBoundary.Take(3)) refs.Add(b);
        if (refs.Count == 0) refs.Add(el.ElementId);
        return refs;
    }

    private DriftDimension BuildMissingModelDimension(
        SoftwareArchitectureDriftScope scope, ArchitectureModelLookup lookup)
    {
        var summary = lookup.RejectionReason is { Length: > 0 } rj
            ? $"Architecture model rejected: {rj}. Marble surface will be empty until a valid model is committed under architecture/<modelId>.md."
            : "Architecture model not yet defined for this project. Add architecture/<modelId>.md per docs/system/architecture/model.md so the marble surface can score elements.";

        var refs = new List<string>();
        foreach (var p in lookup.AttemptedPaths) refs.Add(p);
        if (refs.Count == 0) refs.Add("docs/system/architecture/model.md");

        return new DriftDimension(
            Type: DriftDimensionType.Architecture,
            Score: 0,
            Severity: DriftSeverity.High,
            Confidence: 1.0,
            SourceCoverage: 0,
            Status: DriftFindingStatus.New,
            Summary: summary,
            EvidenceRefs: refs,
            RecommendedActions: new[]
            {
                "Author an architecture/<modelId>.md per docs/system/architecture/model.md.",
                "Re-run Software / Architecture Drift after committing the model.",
            });
    }

    // ------------------------------------------------------------------
    // Scope helpers (mirror AdrCodeDriftAnalysisService)
    // ------------------------------------------------------------------

    private static IReadOnlyList<DriftRef> BuildDocList(string repoRoot)
    {
        var docs = new List<DriftRef>();
        AddIfExists(docs, repoRoot, "docs/system/architecture/model.md", "Architecture model contract");
        AddIfExists(docs, repoRoot, "docs/system/architecture/decisions/adr-archive.md", "Architecture decisions (ADR archive)");
        AddIfExists(docs, repoRoot, "docs/quality/design-principles.md", "Design principles");
        AddIfExists(docs, repoRoot, "ROADMAP.md", "ROADMAP");
        AddIfExists(docs, repoRoot, "AGENTS.md", "AGENTS");
        AddIfExists(docs, repoRoot, "docs/system/contracts/agent-task.md", "Agent task contract");
        AddIfExists(docs, repoRoot, "docs/system/contracts/protocol-style.md", "Protocol & image style");
        AddIfExists(docs, repoRoot, "docs/system/contracts/filesystem.md", "Filesystem contract");
        AddIfExists(docs, repoRoot, "docs/system/reports/drift-reports.md", "Drift reports contract");
        return docs;
    }

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

    private static IReadOnlyList<DriftRef> BuildTestDirs(string repoRoot)
    {
        var entries = new List<DriftRef>();
        AddDirIfExists(entries, repoRoot, "backend.Tests/", "Backend xUnit tests");
        AddDirIfExists(entries, repoRoot, "frontend/e2e/", "Frontend Playwright tests");
        AddDirIfExists(entries, repoRoot, "frontend/src/app/", "Frontend unit tests live alongside components");
        return entries;
    }

    private static void AddDirIfExists(List<DriftRef> list, string repoRoot, string relativeDir, string label)
    {
        var full = Path.Combine(repoRoot, relativeDir.TrimEnd('/').Replace('/', Path.DirectorySeparatorChar));
        if (Directory.Exists(full)) list.Add(new DriftRef(relativeDir, label));
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
                catch (JsonException __ex) { SilentCatch.Note(__ex, "SoftwareArchitectureDriftAnalysisService: still surface slug"); /* still surface slug */ }
                try { touched = Directory.GetLastWriteTimeUtc(dir); } catch (Exception __ex) { SilentCatch.Note(__ex, "SoftwareArchitectureDriftAnalysisService: best-effort"); /* best-effort */ }
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
        if (File.Exists(full)) docs.Add(new DriftRef(relativePath, label));
    }

    private static IReadOnlyList<string> BuildSourceRefs(SoftwareArchitectureDriftScope scope)
    {
        var refs = new List<string>(capacity: 64);
        if (scope.ArchitectureModel?.SourcePath is { Length: > 0 } modelPath)
            refs.Add(modelPath);
        foreach (var d in scope.Docs) refs.Add(d.Path);
        foreach (var t in scope.SourceTree) refs.Add(t.Path);
        foreach (var m in scope.ModuleBoundaries) refs.Add(m.Path);
        foreach (var s in scope.Schemas) refs.Add(s.Path);
        foreach (var t in scope.TestDirs) refs.Add(t.Path);
        foreach (var r in scope.RecentTasks) refs.Add($"{scope.Project}/{r.Lane}/{r.JobId}");
        foreach (var p in scope.RecentDriftReports) refs.Add($"drift:{p.ReportId}");
        foreach (var p in scope.RecentAnalysisReports) refs.Add($"analysis:{p.ReportId}");
        return refs;
    }

    private static IReadOnlyList<string> BuildEvidenceRefSnapshot(SoftwareArchitectureDriftScope scope)
    {
        var refs = new List<string>(capacity: 8);
        if (scope.ArchitectureModel?.SourcePath is { Length: > 0 } modelPath)
            refs.Add(modelPath);
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
            sb.Append("- `").Append(t.Lane).Append('/').Append(t.JobId).Append("` - ").AppendLf(t.Title);
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

    private static string RenderArchitectureModel(
        ArchitectureModelDocument? model, ArchitectureModelLookup lookup)
    {
        if (model is null)
        {
            var sb = new StringBuilder();
            sb.AppendLf("(no architecture model found)");
            if (lookup.RejectionReason is { Length: > 0 } rj)
                sb.Append("Rejected candidate: ").AppendLf(rj);
            if (lookup.AttemptedPaths.Count > 0)
            {
                sb.AppendLf("Looked under:");
                foreach (var p in lookup.AttemptedPaths)
                    sb.Append("- `").Append(p).AppendLf("`");
            }
            sb.AppendLf();
            sb.AppendLf("Treat the architecture model as **not yet defined** and emit a high-severity Architecture finding instead of inventing elements.");
            return sb.ToString().TrimEnd();
        }

        var body = new StringBuilder();
        body.Append("**Model:** `").Append(model.ModelId).Append("` - ").AppendLf(model.Title);
        if (!string.IsNullOrWhiteSpace(model.SourcePath))
            body.Append("Source: `").Append(model.SourcePath).AppendLf("`");
        if (!string.IsNullOrWhiteSpace(model.UpdatedAt))
            body.Append("Updated: ").AppendLf(model.UpdatedAt);
        body.AppendLf();
        for (int i = 0; i < model.Elements.Count; i++)
        {
            var el = model.Elements[i];
            body.Append("### ").Append(i + 1).Append(". `").Append(el.ElementId).Append("` - ").AppendLf(el.Label);
            body.Append("- expectedRole: ").AppendLf(el.ExpectedRole);
            if (el.OwnershipBoundary.Count > 0)
            {
                body.Append("- ownershipBoundary: ");
                body.AppendLf(string.Join(", ", el.OwnershipBoundary.Select(b => "`" + b + "`")));
            }
            if (el.AllowedDependencies.Count > 0)
            {
                body.Append("- allowedDependencies: ");
                body.AppendLf(string.Join(", ", el.AllowedDependencies.Select(b => "`" + b + "`")));
            }
            if (el.Guidelines.Count > 0)
            {
                body.Append("- guidelines: ");
                body.AppendLf(string.Join("; ", el.Guidelines));
            }
            if (el.SourceRefs.Count > 0)
            {
                body.Append("- sourceRefs: ");
                body.AppendLf(string.Join(", ", el.SourceRefs.Select(b => "`" + b + "`")));
            }
            if (el.RelevantTests.Count > 0)
            {
                body.Append("- relevantTests: ");
                body.AppendLf(string.Join(", ", el.RelevantTests.Select(b => "`" + b + "`")));
            }
            if (el.RelevantSchemas.Count > 0)
            {
                body.Append("- relevantSchemas: ");
                body.AppendLf(string.Join(", ", el.RelevantSchemas.Select(b => "`" + b + "`")));
            }
            if (el.RuntimeSignals.Count > 0)
            {
                body.Append("- runtimeSignals: ");
                body.AppendLf(string.Join(", ", el.RuntimeSignals.Select(b => "`" + b + "`")));
            }
            body.AppendLf();
        }
        return body.ToString().TrimEnd();
    }

    // ------------------------------------------------------------------
    // Parsing helpers
    // ------------------------------------------------------------------

    private static SoftwareArchitectureDriftParseResult Malformed(string error, string rawText)
        => new(
            Status: SoftwareArchitectureDriftParseStatus.MalformedJson,
            ScoreBand: DriftScoreBand.Unknown,
            OverallScore: 0,
            Summary: ExtractFirstHeadingOrLine(rawText)
                ?? "Agent reply contained an unparseable JSON sidecar; Markdown body remains the durable artifact.",
            Dimensions: null,
            ArchitectureElements: null,
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

    private static IReadOnlyList<DriftDimension>? ParseDimensions(SoftwareArchitectureDimensionDto[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        var dims = new List<DriftDimension>(raw.Length);
        foreach (var d in raw)
        {
            var type = ParseDimensionType(d.Type)
                ?? throw new JsonException($"dimension.type must be one of the schema's drift dimensions (was '{d.Type}').");
            var severity = ParseSeverity(d.Severity)
                ?? throw new JsonException($"dimension.severity must be Info|Warn|High|Critical (was '{d.Severity}').");
            var status = ParseFindingStatus(d.Status)
                ?? throw new JsonException($"dimension.status must be New|Accepted|Ignored|Tracked|Resolved (was '{d.Status}').");
            if (d.Score is < 0 or > 100) throw new JsonException($"dimension.score must be 0..100 (was {d.Score}).");
            if (d.Confidence is < 0 or > 1) throw new JsonException($"dimension.confidence must be 0..1 (was {d.Confidence}).");
            if (d.SourceCoverage is < 0 or > 1) throw new JsonException($"dimension.sourceCoverage must be 0..1 (was {d.SourceCoverage}).");
            if (string.IsNullOrWhiteSpace(d.Summary)) throw new JsonException($"dimension.summary required for {type}.");

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

    private static IReadOnlyList<SoftwareArchitectureElementParse>? ParseArchitectureElements(
        SoftwareArchitectureElementDto[]? raw)
    {
        if (raw is null || raw.Length == 0) return null;
        var list = new List<SoftwareArchitectureElementParse>(raw.Length);
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var el in raw)
        {
            if (string.IsNullOrWhiteSpace(el.ElementId))
                throw new JsonException("architectureModel.element.elementId required.");
            if (!seen.Add(el.ElementId))
                throw new JsonException($"architectureModel.element id '{el.ElementId}' is not unique.");
            if (el.Score is < 0 or > 100)
                throw new JsonException($"architectureModel.element {el.ElementId} score must be 0..100 (was {el.Score}).");
            if (el.SourceCoverage is < 0 or > 1)
                throw new JsonException($"architectureModel.element {el.ElementId} sourceCoverage must be 0..1 (was {el.SourceCoverage}).");
            var severity = ParseSeverity(el.Severity)
                ?? throw new JsonException($"architectureModel.element {el.ElementId} severity must be Info|Warn|High|Critical (was '{el.Severity}').");
            var status = ParseFindingStatus(el.Status)
                ?? throw new JsonException($"architectureModel.element {el.ElementId} status must be New|Accepted|Ignored|Tracked|Resolved (was '{el.Status}').");
            list.Add(new SoftwareArchitectureElementParse(
                ElementId: el.ElementId.Trim(),
                Score: el.Score,
                Severity: severity,
                SourceCoverage: el.SourceCoverage,
                Status: status,
                Summary: string.IsNullOrWhiteSpace(el.Summary) ? null : el.Summary.Trim(),
                EvidenceRefs: (el.EvidenceRefs ?? Array.Empty<string>())
                    .Where(r => !string.IsNullOrWhiteSpace(r))
                    .Select(r => r.Trim()).ToArray(),
                FollowUpTaskSuggestions: (el.FollowUpTaskSuggestions ?? Array.Empty<string>())
                    .Where(s => !string.IsNullOrWhiteSpace(s))
                    .Select(s => s.Trim()).ToArray()));
        }
        return list;
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

    private sealed record SoftwareArchitectureDriftJsonDto
    {
        public string? Verdict { get; init; }
        public string? ScoreBand { get; init; }
        public int OverallScore { get; init; }
        public SoftwareArchitectureDimensionDto[]? Dimensions { get; init; }
        public SoftwareArchitectureModelDto? ArchitectureModel { get; init; }
        public SoftwareArchitectureFollowUpDto[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record SoftwareArchitectureDimensionDto
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

    private sealed record SoftwareArchitectureModelDto
    {
        public SoftwareArchitectureElementDto[]? Elements { get; init; }
    }

    private sealed record SoftwareArchitectureElementDto
    {
        public string? ElementId { get; init; }
        public int Score { get; init; }
        public string? Severity { get; init; }
        public double SourceCoverage { get; init; }
        public string? Status { get; init; }
        public string? Summary { get; init; }
        public string[]? EvidenceRefs { get; init; }
        public string[]? FollowUpTaskSuggestions { get; init; }
    }

    private sealed record SoftwareArchitectureFollowUpDto
    {
        public string? Title { get; init; }
        public string? Summary { get; init; }
        public string? Priority { get; init; }
        public string? RelatedDimension { get; init; }
    }
}

/// <summary>
/// Snapshot of the architecture-model + ADR + source-tree + schema +
/// test-dirs + recent-task evidence the action gathered before talking to
/// the agent.
/// </summary>
public sealed class SoftwareArchitectureDriftScope
{
    public required string Project { get; init; }
    public required string ProjectRoot { get; init; }
    public required string RepoRoot { get; init; }
    public required ArchitectureModelDocument? ArchitectureModel { get; init; }
    public required ArchitectureModelLookup ArchitectureModelLookup { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.DriftRef> Docs { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.DriftRef> SourceTree { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.DriftRef> ModuleBoundaries { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.DriftRef> Schemas { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.DriftRef> TestDirs { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.RecentTaskRef> RecentTasks { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.ReportPointer> RecentDriftReports { get; init; }
    public required IReadOnlyList<SoftwareArchitectureDriftAnalysisService.ReportPointer> RecentAnalysisReports { get; init; }
    public required DateTime CapturedAt { get; init; }
}

/// <summary>
/// Where the action looked for the architecture model and why it ended up
/// where it did. Both the success and failure paths surface in the prompt
/// so a missing or rejected model is explicit, not silent.
/// </summary>
public sealed record ArchitectureModelLookup(
    string? SourcePath,
    IReadOnlyList<string> AttemptedPaths,
    string? RejectionReason);

/// <summary>
/// In-memory projection of one architecture-model Markdown file's
/// frontmatter. Mirrors the load-bearing fields in
/// <c>docs/app/schemas/architecture-model.schema.json</c>; per-element scoring
/// fields are added by the drift report, not the source model.
/// </summary>
public sealed record ArchitectureModelDocument(
    string ModelId,
    string Title,
    string? Project,
    string? UpdatedAt,
    string? Owner,
    string? Summary,
    string? DiagramHint,
    int SchemaVersion,
    IReadOnlyList<ArchitectureModelElement> Elements,
    string? SourcePath);

/// <summary>One element record inside an <see cref="ArchitectureModelDocument"/>.</summary>
public sealed record ArchitectureModelElement(
    string ElementId,
    string Label,
    string ExpectedRole,
    IReadOnlyList<string> OwnershipBoundary,
    IReadOnlyList<string> Guidelines,
    IReadOnlyList<string> AllowedDependencies,
    IReadOnlyList<string> SourceRefs,
    IReadOnlyList<string> RelevantTests,
    IReadOnlyList<string> RelevantSchemas,
    IReadOnlyList<string> RuntimeSignals);

/// <summary>Three explicit parse states. A failed JSON parse never hides
/// the Markdown body; the caller renders the body and the parse error side
/// by side.</summary>
public enum SoftwareArchitectureDriftParseStatus
{
    Structured,
    Unstructured,
    MalformedJson,
}

/// <summary>One per-element score parsed out of the agent's
/// <c>architectureModel.elements[]</c> sidecar entry.</summary>
public sealed record SoftwareArchitectureElementParse(
    string ElementId,
    int Score,
    DriftSeverity Severity,
    double SourceCoverage,
    DriftFindingStatus Status,
    string? Summary,
    IReadOnlyList<string> EvidenceRefs,
    IReadOnlyList<string> FollowUpTaskSuggestions);

/// <summary>Result of <see cref="SoftwareArchitectureDriftAnalysisService.TryParseAgentResponse"/>.</summary>
public sealed record SoftwareArchitectureDriftParseResult(
    SoftwareArchitectureDriftParseStatus Status,
    DriftScoreBand ScoreBand,
    int OverallScore,
    string Summary,
    IReadOnlyList<DriftDimension>? Dimensions,
    IReadOnlyList<SoftwareArchitectureElementParse>? ArchitectureElements,
    IReadOnlyList<DriftFollowUpSuggestion>? FollowUps,
    string? ParseError);

// ------------------------------------------------------------------
// Mini-YAML reader scoped to the architecture-model frontmatter shape.
// Handles top-level scalars, top-level sequences of strings, and a
// sequence of mappings (the elements: list). Single- and double-quoted
// scalars are unquoted; everything else is treated as a literal string.
// Block scalars (|, >) and inline flow ([], {}) are not supported - the
// schema does not need them and refusing them keeps the parser tight.
// ------------------------------------------------------------------

internal abstract record MiniYamlNode;

internal sealed record MiniYamlScalar(string Value) : MiniYamlNode;

internal sealed record MiniYamlMap(IReadOnlyDictionary<string, MiniYamlNode> Entries) : MiniYamlNode
{
    public bool TryGetScalar(string key, out string value)
    {
        if (Entries.TryGetValue(key, out var node) && node is MiniYamlScalar s)
        {
            value = s.Value;
            return true;
        }
        value = string.Empty;
        return false;
    }

    public bool TryGetSequence(string key, out IReadOnlyList<MiniYamlNode> seq)
    {
        if (Entries.TryGetValue(key, out var node) && node is MiniYamlSequence s)
        {
            seq = s.Items;
            return true;
        }
        seq = Array.Empty<MiniYamlNode>();
        return false;
    }

    public bool TryGetSequenceOfStrings(string key, out IReadOnlyList<string>? values)
    {
        if (Entries.TryGetValue(key, out var node) && node is MiniYamlSequence s)
        {
            var list = new List<string>(s.Items.Count);
            foreach (var item in s.Items)
            {
                if (item is MiniYamlScalar sc) list.Add(sc.Value);
            }
            values = list;
            return true;
        }
        values = null;
        return false;
    }
}

internal sealed record MiniYamlSequence(IReadOnlyList<MiniYamlNode> Items) : MiniYamlNode;

internal static class MiniYaml
{
    public static MiniYamlNode Parse(string text)
    {
        var lines = SplitLines(text);
        int idx = 0;
        return ParseMap(lines, ref idx, 0);
    }

    private static List<RawLine> SplitLines(string text)
    {
        var raw = text.Replace("\r\n", "\n").Split('\n');
        var output = new List<RawLine>(raw.Length);
        foreach (var line in raw)
        {
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ') indent++;
            var content = line[indent..];
            // Strip trailing comments (only when preceded by whitespace or BOL).
            int hash = -1;
            bool inSingle = false, inDouble = false;
            for (int i = 0; i < content.Length; i++)
            {
                var c = content[i];
                if (c == '\'' && !inDouble) inSingle = !inSingle;
                else if (c == '"' && !inSingle) inDouble = !inDouble;
                else if (c == '#' && !inSingle && !inDouble && (i == 0 || content[i - 1] == ' '))
                {
                    hash = i;
                    break;
                }
            }
            if (hash >= 0) content = content[..hash];
            content = content.TrimEnd();
            if (content.Length == 0) continue;
            output.Add(new RawLine(indent, content));
        }
        return output;
    }

    private static MiniYamlMap ParseMap(List<RawLine> lines, ref int idx, int indent)
    {
        var entries = new Dictionary<string, MiniYamlNode>(StringComparer.Ordinal);
        while (idx < lines.Count)
        {
            var line = lines[idx];
            if (line.Indent < indent) break;
            if (line.Indent > indent)
                throw new FormatException($"unexpected indentation (line: '{line.Content}')");
            if (line.Content.StartsWith("- ", StringComparison.Ordinal) || line.Content == "-")
                break;

            var colon = FindKeyColon(line.Content);
            if (colon < 0)
                throw new FormatException($"missing ':' in mapping line '{line.Content}'");
            var key = line.Content[..colon].Trim();
            var rest = line.Content[(colon + 1)..].TrimStart();
            idx++;
            if (rest.Length == 0)
            {
                // Block child - either a sequence or a nested mapping.
                if (idx < lines.Count && lines[idx].Indent > indent)
                {
                    var childIndent = lines[idx].Indent;
                    if (lines[idx].Content.StartsWith("- ", StringComparison.Ordinal) || lines[idx].Content == "-")
                    {
                        var seq = ParseSequence(lines, ref idx, childIndent);
                        entries[key] = seq;
                    }
                    else
                    {
                        var map = ParseMap(lines, ref idx, childIndent);
                        entries[key] = map;
                    }
                }
                else
                {
                    entries[key] = new MiniYamlScalar(string.Empty);
                }
            }
            else
            {
                entries[key] = new MiniYamlScalar(UnquoteScalar(rest));
            }
        }
        return new MiniYamlMap(entries);
    }

    private static MiniYamlSequence ParseSequence(List<RawLine> lines, ref int idx, int indent)
    {
        var items = new List<MiniYamlNode>();
        while (idx < lines.Count)
        {
            var line = lines[idx];
            if (line.Indent < indent) break;
            if (line.Indent > indent)
                throw new FormatException($"unexpected indent inside sequence (line: '{line.Content}')");
            if (!(line.Content.StartsWith("- ", StringComparison.Ordinal) || line.Content == "-"))
                break;

            var rest = line.Content == "-" ? string.Empty : line.Content[2..].TrimStart();
            idx++;
            if (rest.Length == 0)
            {
                // Block child mapping under the dash.
                if (idx < lines.Count && lines[idx].Indent > indent)
                {
                    var childIndent = lines[idx].Indent;
                    var map = ParseMap(lines, ref idx, childIndent);
                    items.Add(map);
                }
                else
                {
                    items.Add(new MiniYamlScalar(string.Empty));
                }
            }
            else if (LooksLikeKeyValue(rest))
            {
                // Inline mapping start: "- key: value" plus optional siblings on
                // the next indented lines. The inline key sits at indent + 2.
                var inline = new List<RawLine> { new(indent + 2, rest) };
                while (idx < lines.Count && lines[idx].Indent >= indent + 2)
                {
                    inline.Add(lines[idx]);
                    idx++;
                }
                int inlineIdx = 0;
                var map = ParseMap(inline, ref inlineIdx, indent + 2);
                items.Add(map);
            }
            else
            {
                items.Add(new MiniYamlScalar(UnquoteScalar(rest)));
            }
        }
        return new MiniYamlSequence(items);
    }

    private static int FindKeyColon(string content)
    {
        bool inSingle = false, inDouble = false;
        for (int i = 0; i < content.Length; i++)
        {
            var c = content[i];
            if (c == '\'' && !inDouble) inSingle = !inSingle;
            else if (c == '"' && !inSingle) inDouble = !inDouble;
            else if (c == ':' && !inSingle && !inDouble) return i;
        }
        return -1;
    }

    private static bool LooksLikeKeyValue(string content)
    {
        var colon = FindKeyColon(content);
        if (colon <= 0) return false;
        var key = content[..colon];
        foreach (var c in key)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_' || c == '-')) return false;
        }
        return true;
    }

    private static string UnquoteScalar(string value)
    {
        if (value.Length >= 2)
        {
            if (value[0] == '"' && value[^1] == '"') return value[1..^1];
            if (value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        }
        return value;
    }

    private readonly record struct RawLine(int Indent, string Content);
}
