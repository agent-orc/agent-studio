using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using AgentStudio.Drift;
using AgentStudio.Runner;

namespace AgentStudio.Prompts;

/// <summary>
/// Loads runtime prompt templates from Markdown files and renders simple
/// <c>{{variable}}</c> placeholders. Prompt text stays editable outside the
/// codebase's control flow, while services keep owning the decisions about
/// which template is used and when.
/// </summary>
public sealed partial class RuntimePromptService
{
    public const string RunnerFreshStart = "runner-fresh-start.md";
    public const string RunnerResumeInterrupted = "runner-resume-interrupted.md";
    public const string RunnerResumeRestart = "runner-resume-restart.md";
    public const string RunnerRecoveryContinuation = "runner-recovery-continuation.md";
    public const string RunnerReissueChange = "runner-reissue-change.md";
    public const string RunnerReissueControlV1 = "runner-reissue-control-v1.md";
    public const string RunnerReissueTreatmentV1 = "runner-reissue-treatment-v1.md";
    public const string EpicDecomposition = "epic-decomposition.md";
    public const string SummaryProtocol = "summary-protocol.md";
    public const string CommitMessage = "commit-message.md";
    public const string ModeFramingReadOnly = "mode-framing-readonly.md";
    public const string ModeFramingResearch = "mode-framing-research.md";
    public const string ModeFramingConcept = "mode-framing-concept.md";
    public const string ModeFramingApproval = "mode-framing-approval.md";
    public const string ModeFramingDossierMaintenance = "mode-framing-dossier-maintenance.md";
    public const string ModeFramingWeb = "mode-framing-web.md";
    public const string ProposalFeedbackRefine = "proposal-feedback-refine.md";
    public const string ProposalDraftGenerate = "proposal-draft-generate.md";
    public const string WikiSearchExpand = "wiki-search-expand.md";

    private static readonly IReadOnlyDictionary<string, string?> NoValues =
        new Dictionary<string, string?>();

    private readonly IConfiguration _configuration;
    private readonly ILogger<RuntimePromptService> _logger;
    private readonly ConcurrentDictionary<string, LoadedPrompt> _cache = new(StringComparer.OrdinalIgnoreCase);
    private readonly PromptCallTelemetryService? _telemetry;

    public RuntimePromptService(IConfiguration configuration, ILogger<RuntimePromptService> logger)
        : this(configuration, logger, null)
    {
    }

    public RuntimePromptService(
        IConfiguration configuration,
        ILogger<RuntimePromptService> logger,
        PromptCallTelemetryService? telemetry)
    {
        _configuration = configuration;
        _logger = logger;
        _telemetry = telemetry;
    }

    public string Render(
        string templateName,
        IReadOnlyDictionary<string, string?> values,
        PromptCallContext? context = null)
    {
        var loaded = Load(templateName);
        var rendered = RenderContent(loaded.Content, values);
        RecordCall(templateName, loaded, rendered, values, context);
        return rendered;
    }

    /// <summary>
    /// Records and returns a project-level pipeline prompt override. Legacy
    /// <c>pipelineSteps[*].prompt</c> values are already complete prompt text,
    /// so they are not slot-rendered, but they still belong to the bound
    /// runtime prompt's version and call history.
    /// </summary>
    public string UseProjectOverride(
        string templateName,
        string content,
        PromptCallContext context)
    {
        var loaded = new LoadedPrompt(
            content,
            ContentSha(content),
            "project-override");
        RecordCall(templateName, loaded, content, NoValues, context);
        return content;
    }

    private void RecordCall(
        string templateName,
        LoadedPrompt loaded,
        string rendered,
        IReadOnlyDictionary<string, string?> values,
        PromptCallContext? context)
    {
        var inferred = InferContext(values, context);
        _telemetry?.Record(new PromptCallRecord
        {
            Timestamp = DateTimeOffset.UtcNow,
            PromptId = templateName,
            Version = loaded.Version,
            Source = loaded.Source,
            InputTokens = PromptTokenEstimator.EstimateOrNull(rendered) ?? 0,
            Model = inferred.Model,
            Project = inferred.Project,
            Step = inferred.Step,
        });
    }

    /// <summary>
    /// Applies the same <c>{{slot}}</c> substitution as <see cref="Render"/> to a
    /// raw template string instead of a file. Unmatched placeholders are left
    /// intact; a slot present with a null value renders empty. The registry's
    /// "Probelauf" uses this to preview an unsaved draft without writing it.
    /// </summary>
    public static string RenderContent(string? template, IReadOnlyDictionary<string, string?> values)
    {
        if (string.IsNullOrEmpty(template)) return string.Empty;
        return PlaceholderRegex().Replace(template, match =>
        {
            var key = match.Groups["key"].Value.Trim();
            return values.TryGetValue(key, out var value) ? value ?? string.Empty : match.Value;
        });
    }

    /// <summary>
    /// Composes the per-mode framing block injected into runner prompts via the
    /// <c>{{mode_framing}}</c> placeholder. Report-only modes (planning /
    /// research) get the strict read-only block; research also gets its HTML
    /// deliverable contract; concept gets its repository-dossier contract; web
    /// access appends the web block. Coding with web off yields an empty string.
    /// A non-empty result ends with a blank-line separator so it slots in front
    /// of the following section cleanly.
    /// </summary>
    public string RenderModeFraming(
        string? mode,
        bool allowWebAccess,
        PromptCallContext? context = null)
    {
        var parts = new List<string>();
        if (TaskModes.IsReportOnly(mode))
            parts.Add(Render(ModeFramingReadOnly, NoValues, context).Trim());
        if (TaskModes.Normalize(mode) == TaskModes.Research)
            parts.Add(Render(ModeFramingResearch, NoValues, context).Trim());
        if (TaskModes.IsConcept(mode))
            parts.Add(Render(ModeFramingConcept, NoValues, context).Trim());
        if (TaskModes.IsConcept(mode) || TaskModes.Normalize(mode) == TaskModes.Coding)
            parts.Add(Render(ModeFramingApproval, NoValues, context).Trim());
        if (allowWebAccess)
            parts.Add(Render(ModeFramingWeb, NoValues, context).Trim());
        if (parts.Count == 0) return string.Empty;
        return string.Join("\n\n", parts) + "\n\n";
    }

    /// <summary>
    /// Renders the mandatory, task-specific Dossier update contract. Selection
    /// stays in the Dossier feature; the prompt boundary only fills the stable
    /// task key and canonical target list.
    /// </summary>
    public string RenderDossierMaintenanceFraming(
        string taskKey,
        string dossierTargets,
        PromptCallContext? context = null)
    {
        if (string.IsNullOrWhiteSpace(taskKey) || string.IsNullOrWhiteSpace(dossierTargets))
            return string.Empty;
        return Render(
            ModeFramingDossierMaintenance,
            new Dictionary<string, string?>
            {
                ["task_key"] = taskKey.Trim(),
                ["dossier_targets"] = dossierTargets.Trim(),
            },
            context).Trim() + "\n\n";
    }

    private LoadedPrompt Load(string templateName)
    {
        if (string.IsNullOrWhiteSpace(templateName))
            throw new ArgumentException("Template name is required.", nameof(templateName));

        return _cache.GetOrAdd(templateName, name =>
        {
            var path = ResolveTemplatePath(name);
            _logger.LogDebug("Loading runtime prompt template {Template} from {Path}", name, path);
            var content = File.ReadAllText(path);
            return new LoadedPrompt(
                content,
                ContentSha(content),
                path.StartsWith(
                    Path.GetFullPath(OverrideDirectory) + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase)
                    ? "application-override"
                    : "file");
        });
    }

    public string? TryGetEffectiveVersion(string templateName)
    {
        try
        {
            return Load(templateName).Version;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Effective resolution: an application-wide override (user-data
    /// directory, survives updates) wins over the shipped default
    /// (bin / source tree, replaced on update). This is the
    /// Default/Override layering the admin surface edits.
    /// </summary>
    private string ResolveTemplatePath(string templateName)
    {
        var overridePath = OverridePathFor(templateName);
        if (File.Exists(overridePath)) return overridePath;

        var defaultPath = TryResolveDefaultPath(templateName);
        if (defaultPath != null) return defaultPath;

        throw new FileNotFoundException(
            $"Runtime prompt template '{templateName}' was not found. " +
            $"Set PromptTemplates:RuntimePath or ensure prompts/runtime is copied to the output directory.");
    }

    private string? TryResolveDefaultPath(string templateName)
    {
        foreach (var root in DefaultRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            var path = Path.GetFullPath(Path.Combine(root, templateName));
            if (File.Exists(path)) return path;
        }
        return null;
    }

    /// <summary>
    /// Absolute path of the shipped template, ignoring the application-wide
    /// override. Prompt review uses it for git provenance and the adjacent
    /// <c>*.md.meta.json</c> companion.
    /// </summary>
    public string? TryGetDefaultPath(string templateName) =>
        TryResolveDefaultPath(templateName);

    /// <summary>
    /// Path for the review companion of a shipped prompt. An explicitly
    /// configured runtime root remains authoritative (tests and packaged
    /// deployments can keep their own companions). During source-checkout
    /// development, companions live in the repository's
    /// <c>prompts/runtime</c> tree rather than beside copied bin assets.
    /// </summary>
    public string? TryGetReviewCompanionPath(string templateName)
    {
        var configured = _configuration["PromptTemplates:RuntimePath"];
        if (!string.IsNullOrWhiteSpace(configured))
            return Path.GetFullPath(Path.Combine(configured, templateName)) + ".meta.json";

        var sourcePath = Path.Combine(
            DriftRepoRootLocator.Resolve(),
            "prompts",
            "runtime",
            templateName);
        if (File.Exists(sourcePath))
            return Path.GetFullPath(sourcePath) + ".meta.json";

        var defaultPath = TryGetDefaultPath(templateName);
        return defaultPath is null ? null : defaultPath + ".meta.json";
    }

    private IEnumerable<string?> DefaultRoots()
    {
        var configured = _configuration["PromptTemplates:RuntimePath"];
        if (!string.IsNullOrWhiteSpace(configured)) yield return configured;

        yield return Path.Combine(AppContext.BaseDirectory, "prompts", "runtime");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "prompts", "runtime");
        yield return Path.Combine(Directory.GetCurrentDirectory(), "..", "prompts", "runtime");
    }

    /// <summary>
    /// Application-wide override directory. Survives app updates because it
    /// lives in user data, not in the install/bin tree. Resolution mirrors
    /// the other user-data stores (config override -&gt; TaskRepository -&gt;
    /// LocalAppData).
    /// </summary>
    public string OverrideDirectory
    {
        get
        {
            var configured = _configuration["PromptTemplates:OverridePath"];
            if (!string.IsNullOrWhiteSpace(configured)) return configured;

            var taskRepo = _configuration["TaskRepository"];
            if (!string.IsNullOrWhiteSpace(taskRepo))
                return Path.Combine(taskRepo, ".metadata", "prompt-overrides");

            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            return Path.Combine(local, "agent-taskboard", "prompt-overrides");
        }
    }

    private string OverridePathFor(string templateName) =>
        Path.GetFullPath(Path.Combine(OverrideDirectory, templateName));

    /// <summary>Shipped default content, ignoring any override. Null when the template ships no default.</summary>
    public string? TryReadDefault(string templateName)
    {
        var path = TryResolveDefaultPath(templateName);
        return path == null ? null : File.ReadAllText(path);
    }

    /// <summary>Override content, or null when no application-wide override exists.</summary>
    public string? TryReadOverride(string templateName)
    {
        var path = OverridePathFor(templateName);
        return File.Exists(path) ? File.ReadAllText(path) : null;
    }

    public bool HasOverride(string templateName) => File.Exists(OverridePathFor(templateName));

    /// <summary>
    /// Filenames of every shipped default template (first existing default
    /// root wins) plus any override-only file. Lets the admin surface list
    /// "ALL" templates, including ones added after the description catalog.
    /// </summary>
    public IReadOnlyList<string> EnumerateTemplateNames()
    {
        var names = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var root in DefaultRoots())
        {
            if (string.IsNullOrWhiteSpace(root)) continue;
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.md"))
                names.Add(Path.GetFileName(file));
            break; // first existing default root is authoritative
        }
        if (Directory.Exists(OverrideDirectory))
            foreach (var file in Directory.EnumerateFiles(OverrideDirectory, "*.md"))
                names.Add(Path.GetFileName(file));
        return names.ToList();
    }

    /// <summary>Writes (or replaces) the application-wide override and busts the render cache.</summary>
    public void WriteOverride(string templateName, string content)
    {
        var path = OverridePathFor(templateName);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = path + ".tmp";
        File.WriteAllText(temp, content ?? string.Empty);
        try { File.Replace(temp, path, destinationBackupFileName: null); }
        catch (FileNotFoundException) { File.Move(temp, path); }
        InvalidateCache(templateName);
        _logger.LogInformation("prompt-override-written template={Template} path={Path}", templateName, path);
    }

    /// <summary>Removes the override (reset to shipped default) and busts the render cache.</summary>
    public bool DeleteOverride(string templateName)
    {
        var path = OverridePathFor(templateName);
        if (!File.Exists(path))
        {
            InvalidateCache(templateName);
            return false;
        }
        File.Delete(path);
        InvalidateCache(templateName);
        _logger.LogInformation("prompt-override-reset template={Template} path={Path}", templateName, path);
        return true;
    }

    public void InvalidateCache(string? templateName = null)
    {
        if (templateName == null) _cache.Clear();
        else _cache.TryRemove(templateName, out _);
    }

    /// <summary>
    /// The distinct <c>{{slot}}</c> placeholder names a template declares, in
    /// first-seen order. This is the contract between the template text and the
    /// code that fills it: the registry surfaces it so an operator editing an
    /// override knows exactly which names the renderer will substitute.
    /// </summary>
    public static IReadOnlyList<string> ExtractSlots(string? content)
    {
        if (string.IsNullOrEmpty(content)) return Array.Empty<string>();
        var seen = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match m in PlaceholderRegex().Matches(content))
        {
            var key = m.Groups["key"].Value.Trim();
            if (known.Add(key)) seen.Add(key);
        }
        return seen;
    }

    [GeneratedRegex(@"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled)]
    private static partial Regex PlaceholderRegex();

    private static PromptCallContext InferContext(
        IReadOnlyDictionary<string, string?> values,
        PromptCallContext? explicitContext)
    {
        string? First(params string[] keys)
        {
            foreach (var key in keys)
            {
                if (values.TryGetValue(key, out var value)
                    && !string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }
            return null;
        }

        return new PromptCallContext(
            explicitContext?.Project
                ?? First("project", "project_name", "source_project", "target_project"),
            explicitContext?.Step
                ?? First("step", "step_id", "pipeline_step"),
            explicitContext?.Model
                ?? First("model", "model_id", "default_model"));
    }

    /// <summary>Stable normalized SHA used by every prompt override layer.</summary>
    public static string ContentSha(string content)
    {
        var normalized = content.Replace("\r\n", "\n");
        return Convert.ToHexString(
            SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))
            .ToLowerInvariant();
    }

    private sealed record LoadedPrompt(string Content, string Version, string Source);
}
