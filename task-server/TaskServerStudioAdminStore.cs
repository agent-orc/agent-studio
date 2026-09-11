using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

/// <summary>
/// Studio administration surface: the orchestrator/supervisor config toggle
/// catalog and the runtime prompt-template catalog/detail. Both read a
/// static, product-shipped catalog (a code constant and an embedded default
/// prompt tree respectively) layered with durable overrides from
/// <c>studio_settings</c>. Reduced from the legacy backend on purpose: prompt
/// review metadata, call telemetry, and cross-project pipeline overrides are
/// separate subsystems that have not moved to the standalone Task Server yet,
/// so those fields report honest zero/empty defaults instead of fabricated
/// data (see <see cref="StudioPromptCallAnalytics"/> usage below).
/// </summary>
public sealed partial class TaskServerStore
{
    private const string AdminConfigScope = "admin-config";
    private const string AdminConfigOverridesKey = "orchestrator-overrides";
    private const string PromptOverrideScope = "prompt-override";
    private const string PromptOverrideMetaScope = "prompt-override-meta";
    private const string PromptResourcePrefix = "AgentStudio.TaskServer.Prompts.";

    public async Task<StudioOrchestratorConfigSnapshot> GetOrchestratorConfigSnapshotAsync(CancellationToken ct)
    {
        var overrides = await GetSettingAsync<Dictionary<string, JsonElement>>(
            AdminConfigScope, AdminConfigOverridesKey, ct) ?? [];
        var options = StudioOrchestratorConfigCatalog.Definitions.Select(def =>
        {
            var hasOverride = overrides.TryGetValue(def.Key, out var raw);
            object? current = hasOverride ? CoerceValue(def, raw) : null;
            current ??= def.DefaultValue;
            return new StudioOrchestratorConfigOption(
                def.Key, def.Group, def.Label, def.Description, def.Type,
                def.DefaultValue, current, current, hasOverride,
                RestartRequired: false, def.SourceFile, def.EnumOptions);
        }).ToList();
        return new StudioOrchestratorConfigSnapshot(options, "(durable Task Server setting)", overrides.Count > 0);
    }

    private static object? CoerceValue(StudioOrchestratorConfigDefinition def, JsonElement value) => def.Type switch
    {
        "bool" => value.ValueKind is JsonValueKind.True or JsonValueKind.False ? value.GetBoolean() : null,
        "int" => value.TryGetInt32(out var i) ? i : null,
        _ => value.ValueKind == JsonValueKind.String ? value.GetString() : null,
    };

    public async Task<StudioPromptCatalogResponse> GetPromptCatalogAsync(CancellationToken ct)
    {
        var names = EnumeratePromptTemplateNames();
        var overrides = await GetSettingsByScopeAsync<string>(PromptOverrideScope, ct);
        var metas = await GetSettingsByScopeAsync<PromptOverrideSidecar>(PromptOverrideMetaScope, ct);
        var items = names.Select(name =>
        {
            var meta = StudioPromptDescriptionCatalog.Describe(name);
            var defaultContent = TryReadDefaultPromptContent(name);
            var hasOverride = overrides.ContainsKey(name);
            var effective = hasOverride ? overrides[name] : defaultContent;
            var changed = hasOverride && metas.TryGetValue(name, out var sidecar) && sidecar.BaseDefaultSha != null
                && defaultContent != null && !string.Equals(sidecar.BaseDefaultSha, Sha(defaultContent), StringComparison.OrdinalIgnoreCase);
            return new StudioPromptCatalogItem(
                name, meta.Title, meta.Description, meta.Group,
                StudioPromptDescriptionCatalog.PromptClassFor(name, meta.Group),
                HasDefault: defaultContent != null,
                HasOverride: hasOverride,
                DefaultChangedSinceOverride: changed,
                Slots: ExtractPromptSlots(effective),
                UsageCount: 0,
                ReviewStatus: null,
                ReviewFindingCount: 0,
                ProjectOverrideCount: 0,
                Calls: new StudioPromptCallAnalytics(IsDead: true));
        })
        .OrderBy(item => StudioPromptDescriptionCatalog.GroupOrder(item.Group))
        .ThenBy(item => item.Title, StringComparer.OrdinalIgnoreCase)
        .ToList();
        return new StudioPromptCatalogResponse(
            items,
            OverrideDirectory: "(durable Task Server setting)",
            TelemetryPath: null,
            DeadPromptDays: 30,
            CostDisclaimer: PromptCostDisclaimer,
            OrphanedOverrides: []);
    }

    public async Task<StudioPromptDetail> GetPromptDetailAsync(string name, CancellationToken ct)
    {
        if (!IsSafePromptName(name)) throw new KeyNotFoundException($"Unknown prompt '{name}'.");
        var defaultContent = TryReadDefaultPromptContent(name);
        var overrideContent = await GetSettingAsync<string>(PromptOverrideScope, name, ct);
        if (defaultContent is null && overrideContent is null)
            throw new KeyNotFoundException($"Unknown prompt '{name}'.");
        var sidecar = await GetSettingAsync<PromptOverrideSidecar>(PromptOverrideMetaScope, name, ct);
        var hasOverride = overrideContent != null;
        var defaultSha = defaultContent is null ? null : Sha(defaultContent);
        var changed = hasOverride && sidecar?.BaseDefaultSha != null && defaultSha != null
            && !string.Equals(sidecar.BaseDefaultSha, defaultSha, StringComparison.OrdinalIgnoreCase);
        var meta = StudioPromptDescriptionCatalog.Describe(name);
        var effective = overrideContent ?? defaultContent ?? string.Empty;
        return new StudioPromptDetail(
            name, meta.Title, meta.Description, meta.Group,
            StudioPromptDescriptionCatalog.PromptClassFor(name, meta.Group),
            HasDefault: defaultContent != null,
            HasOverride: hasOverride,
            DefaultContent: defaultContent,
            OverrideContent: overrideContent,
            EffectiveContent: effective,
            DefaultSha: defaultSha,
            DefaultChangedSinceOverride: changed,
            OverrideUpdatedAt: hasOverride ? sidecar?.UpdatedAt : null,
            Slots: ExtractPromptSlots(effective),
            CostDisclaimer: PromptCostDisclaimer,
            Calls: new StudioPromptCallAnalytics(IsDead: true),
            Usages: [],
            ProjectOverrides: []);
    }

    private static IReadOnlyList<string> EnumeratePromptTemplateNames()
        => typeof(TaskServerStore).Assembly.GetManifestResourceNames()
            .Where(resource => resource.StartsWith(PromptResourcePrefix, StringComparison.Ordinal))
            .Select(resource => resource[PromptResourcePrefix.Length..])
            .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    private static string? TryReadDefaultPromptContent(string name)
    {
        using var stream = typeof(TaskServerStore).Assembly.GetManifestResourceStream(PromptResourcePrefix + name);
        if (stream is null) return null;
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static bool IsSafePromptName(string name)
        => !string.IsNullOrWhiteSpace(name)
           && name.EndsWith(".md", StringComparison.OrdinalIgnoreCase)
           && name.IndexOfAny(['/', '\\']) < 0
           && !name.Contains("..", StringComparison.Ordinal);

    private static string Sha(string content)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(content.Replace("\r\n", "\n")));
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    private static readonly Regex PromptPlaceholder = new(@"\{\{\s*(?<key>[A-Za-z0-9_]+)\s*\}\}", RegexOptions.Compiled);

    private static IReadOnlyList<string> ExtractPromptSlots(string? content)
    {
        if (string.IsNullOrEmpty(content)) return [];
        var seen = new List<string>();
        var known = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match match in PromptPlaceholder.Matches(content))
        {
            var key = match.Groups["key"].Value.Trim();
            if (known.Add(key)) seen.Add(key);
        }
        return seen;
    }

    private const string PromptCostDisclaimer =
        "Theoretical API-equivalent estimate for the rendered prompt input only. "
        + "Runs use CLI subscriptions, so this is a comparison metric, not an invoice. "
        + "Tokens are estimated and calls without a historically priced model remain unpriced.";

    private sealed record PromptOverrideSidecar(string? BaseDefaultSha, string? UpdatedAt);
}

internal sealed record StudioOrchestratorConfigDefinition(
    string Key,
    string Group,
    string Label,
    string Description,
    string Type,
    object? DefaultValue,
    string SourceFile,
    string[]? EnumOptions = null);

/// <summary>
/// Ported verbatim from <c>backend/Features/Configuration/OrchestratorConfigService.cs</c>'s
/// <c>OrchestratorConfigCatalog</c>: the finite set of orchestrator/supervisor
/// flags exposed to the admin panel. Kept in sync manually; a drift test in
/// TaskServer.Tests compares the key list against the legacy catalog.
/// </summary>
internal static class StudioOrchestratorConfigCatalog
{
    public static readonly StudioOrchestratorConfigDefinition[] Definitions =
    [
        new("ReviewDecisionOrchestrator:Enabled", "Orchestrator", "Review-decision orchestrator",
            "Auto-review lane gets reissue / accept-as-done / escalate decisions on a tick.",
            "bool", false, "backend/Services/Runner/ReviewDecisionOrchestrator.cs"),
        new("ReviewDecisionOrchestrator:IntervalSeconds", "Orchestrator", "Review-decision tick (seconds)",
            "How often the review-decision orchestrator scans the auto-review lane.",
            "int", 30, "backend/Services/Runner/ReviewDecisionOrchestrator.cs"),
        new("Orchestrator:PrepEnabled", "Orchestrator", "Orchestrator prep lane",
            "Hosted service that processes incoming orchestrator-prep jobs.",
            "bool", false, "backend/Services/Supervisor/OrchestratorPrepHostedService.cs"),
        new("Orchestrator:SessionTurns:ActiveLimit", "Orchestrator", "Session-turn active limit",
            "Maximum orchestrator session turns allowed to run at once before later turns report queued positions.",
            "int", 4, "backend/Features/Orchestrator/OrchestratorTurnService.cs"),
        new("Supervisor:MetaCycleEnabled", "Supervisor", "Layer-2.5 meta-cycle",
            "Quiet-batch meta-cycle: pause, inspect evidence, write report, resume / queue / escalate.",
            "bool", false, "backend/Services/Supervisor/MetaCycleHostedService.cs"),
        new("Supervisor:SoftReasoningEnabled", "Supervisor", "Soft-reasoning pass",
            "Layer-2 soft-reasoning second-opinion pass over runner state.",
            "bool", false, "backend/Services/Supervisor/SoftReasoningHostedService.cs"),
        new("Supervisor:HardCheckEnabled", "Supervisor", "Hard health checks",
            "Periodic deterministic health checks over the runner / workspace.",
            "bool", true, "backend/Services/Supervisor/HardHealthCheckHostedService.cs"),
        new("Supervisor:ChatNoteEnabled", "Supervisor", "Supervisor chat-notes",
            "Periodic [supervisor] chat-notes summarising recent observations.",
            "bool", true, "backend/Services/Supervisor/ChatNoteHostedService.cs"),
        new("Supervisor:AutoInterventionEnabled", "Auto-Intervention", "Auto-intervention",
            "Promote selected advisories to automatic pause / cancel / fail / resume invocations. Gated by ADR-0017.",
            "bool", false, "backend/Services/Supervisor/AutoInterventionHostedService.cs"),
        new("Supervisor:AutoInterventionRateLimit", "Auto-Intervention", "Rate limit (per project / hour)",
            "Maximum auto-intervention invocations per project per rolling hour.",
            "int", 3, "backend/Services/Supervisor/AutoInterventionHostedService.cs"),
        new("Supervisor:AutoInterventionSeverityThreshold", "Auto-Intervention", "Severity threshold",
            "Minimum advisory severity that may trigger an auto-intervention.",
            "enum", "High", "backend/Services/Supervisor/AutoInterventionHostedService.cs", ["Info", "Warn", "High"]),
    ];
}

/// <summary>
/// Ported from <c>backend/Features/Prompts/PromptAdminService.cs</c>'s
/// <c>PromptDescriptionCatalog</c>: human title/description/group for every
/// shipped runtime prompt template. A template not listed here still appears
/// with a generic description, so a newly added template is never hidden.
/// </summary>
internal static class StudioPromptDescriptionCatalog
{
    internal sealed record Meta(string Title, string Description, string Group);

    private static readonly string[] GroupRank =
        ["Runner", "Review", "Orchestrator", "Drift & Analysis", "Supervisor", "Utility", "Other"];

    public static int GroupOrder(string group)
    {
        var idx = Array.IndexOf(GroupRank, group);
        return idx < 0 ? GroupRank.Length : idx;
    }

    public static string PromptClassFor(string name, string group)
    {
        if (name.StartsWith("mode-framing-", StringComparison.OrdinalIgnoreCase)) return "framing";
        if (string.Equals(group, "Orchestrator", StringComparison.OrdinalIgnoreCase)) return "orchestrator";
        if (string.Equals(group, "Drift & Analysis", StringComparison.OrdinalIgnoreCase)) return "drift";
        return "runtime-step";
    }

    private static readonly Dictionary<string, Meta> Map = new(StringComparer.OrdinalIgnoreCase)
    {
        ["runner-fresh-start.md"] = new("Runner: fresh start",
            "Bootstrap prompt handed to the CLI agent when a task starts from scratch.", "Runner"),
        ["runner-resume-interrupted.md"] = new("Runner: resume interrupted",
            "Prompt used to resume a run that was interrupted mid-flight, in the same session.", "Runner"),
        ["runner-resume-restart.md"] = new("Runner: resume by restart",
            "Prompt used to resume a task by restarting the CLI session from disk state.", "Runner"),
        ["runner-recovery-continuation.md"] = new("Runner: recovery continuation",
            "Prompt used to continue a task after a recovery/crash-recovery boundary.", "Runner"),
        ["runner-reissue-control-v1.md"] = new("Runner: reissue control",
            "Control-arm prompt used by the versioned reissue experiment.", "Runner"),
        ["runner-reissue-treatment-v1.md"] = new("Runner: reissue treatment",
            "Structured treatment-arm prompt used by the versioned reissue experiment.", "Runner"),
        ["epic-decomposition.md"] = new("Epic decomposition",
            "Decomposes an epic-sized task into smaller child tasks.", "Runner"),
        ["mode-framing-readonly.md"] = new("Mode framing: read-only",
            "Framing block injected for read-only modes (planning / research) to forbid mutations.", "Runner"),
        ["mode-framing-research.md"] = new("Mode framing: research",
            "Research delivery contract for one primary HTML report with linked supporting material.", "Runner"),
        ["mode-framing-concept.md"] = new("Mode framing: concept",
            "Docs-only Dossier contract injected for concept-mode runs.", "Runner"),
        ["mode-framing-dossier-maintenance.md"] = new("Mode framing: Dossier maintenance",
            "Append-only implementation-log contract injected for cards linked to a living Dossier.", "Runner"),
        ["mode-framing-web.md"] = new("Mode framing: web access",
            "Framing block injected when a run is allowed to access the web.", "Runner"),
        ["commit-message.md"] = new("Commit message",
            "Generates the git commit message for the agent-produced change set.", "Runner"),
        ["summary-protocol.md"] = new("Summary protocol",
            "Generates the run summary / review protocol surfaced as status.md.", "Runner"),
        ["code-review-step.md"] = new("Code-review step",
            "Automated code-review pass over the run's diff in the post bracket.", "Review"),
        ["code-review-grade.md"] = new("Code-review quality grade",
            "Automatic post-CORE pass that grades the task change set A/B/C/D (quality grade on every task).", "Review"),
        ["review-aspect-code-quality.md"] = new("Aspect: code quality",
            "Review aspect that grades code quality of the change.", "Review"),
        ["review-aspect-requirement-fit.md"] = new("Aspect: requirement fit",
            "Review aspect that checks the change against the task's requirements.", "Review"),
        ["review-aspect-tests-and-evidence.md"] = new("Aspect: tests & evidence",
            "Review aspect that checks for adequate tests and verification evidence.", "Review"),
        ["review-aspect-documentation-impact.md"] = new("Aspect: documentation impact",
            "Review aspect that checks whether docs need updating for the change.", "Review"),
        ["post-abort-review.md"] = new("Post-abort review",
            "Verdict step run after a non-clean run end (rerun / stronger-reissue / human-review / accept).", "Review"),
        ["orchestrator-review-decision.md"] = new("Orchestrator review decision",
            "Final orchestrator verdict for the auto-review lane (accept / reissue / escalate).", "Orchestrator"),
        ["orchestrator-chat-clarify-first.md"] = new("Orchestrator chat: clarify first",
            "Standalone clarify-first guidance currently not loaded by a runtime code path.", "Orchestrator"),
        ["adr-code-drift.md"] = new("Drift: ADR vs code",
            "Reports drift between architecture-decision records and the code.", "Drift & Analysis"),
        ["docs-marketing-drift.md"] = new("Drift: docs vs behavior",
            "Reports drift between documentation / marketing copy and shipped behavior.", "Drift & Analysis"),
        ["software-architecture-drift.md"] = new("Drift: software architecture",
            "Reports drift in the described software architecture vs the code.", "Drift & Analysis"),
        ["spec-task-job-drift.md"] = new("Drift: spec vs tasks",
            "Reports drift between spec/task definitions and the actual task folders.", "Drift & Analysis"),
        ["steering-docs-summary-and-drift.md"] = new("Steering docs summary & drift",
            "Summarizes the steering docs and reports drift against them.", "Drift & Analysis"),
        ["roadmap-alignment-review.md"] = new("Roadmap alignment review",
            "Compares the task queue against the roadmap and reports misalignment.", "Drift & Analysis"),
        ["recurring-output-pattern-review.md"] = new("Recurring output-pattern review",
            "Scans recent agent outputs for recurring failure / output patterns.", "Drift & Analysis"),
        ["supervisor-soft-reasoning.md"] = new("Supervisor soft-reasoning",
            "Layer-2 soft-reasoning second-opinion pass over runner state.", "Supervisor"),
        ["title-generate.md"] = new("Title generation",
            "Generates a concise task title from the task prompt.", "Utility"),
        ["prompt-enhance.md"] = new("Prompt enhancement",
            "Expands / enhances a raw task prompt before it is queued.", "Utility"),
        ["wiki-search-expand.md"] = new("Wiki search expansion",
            "Expands a wiki search query with German/English synonyms for the semantic search layer.", "Utility"),
    };

    public static Meta Describe(string name)
    {
        if (Map.TryGetValue(name, out var meta)) return meta;
        var title = Path.GetFileNameWithoutExtension(name).Replace('-', ' ');
        if (title.Length > 0) title = char.ToUpperInvariant(title[0]) + title[1..];
        return new Meta(title, "Runtime prompt template (no catalog description yet).", "Other");
    }
}
