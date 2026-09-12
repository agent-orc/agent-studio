using System.Text.RegularExpressions;

namespace AgentStudio.Shared;

public record StartJobRequest
{
    public string? AgentOverride { get; init; }
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
}

public record ContinueJobRequest
{
    public string Prompt { get; init; } = "";
    public string? Model { get; init; }
    public string? CliType { get; init; }
    public string? ThinkingLevel { get; init; }
    /// <summary>
    /// How the follow-up should be interpreted. <c>continue</c> (default) is a
    /// next-turn message in the same conversation. <c>steer</c> frames the
    /// follow-up as a course correction. <c>extend</c> appends a new prompt
    /// file to the job folder so the task history grows blog-style.
    /// <c>newTask</c> starts a new sub-task in the same session.
    /// See <see cref="ContinueModes"/>.
    /// </summary>
    public string? Mode { get; init; }
}

/// <summary>
/// Discriminated response for <c>POST /api/tasks/{id}/continue</c> and
/// <c>POST /api/tasks/{id}/start</c>. <c>started</c> means the run is
/// actually live; <c>queued</c> means the project was busy with another
/// job, the user's intent has been saved as a draft on the target task,
/// and the target task has been moved to the top of <c>2-ready</c> so the
/// auto-pickup loop will run it on the next tick. The frontend treats
/// queued as success-with-info (no modal); the chat carries the
/// orchestrator's <c>[queued]</c> meta line for user-facing feedback.
/// </summary>
public record ContinueJobResponse
{
    /// <summary><c>started</c> | <c>queued</c></summary>
    public string Status { get; init; } = "started";
    public CliExecution? Execution { get; init; }
    public ContinueJobQueuedInfo? Queued { get; init; }
}

public record ContinueJobQueuedInfo
{
    /// <summary>One of <see cref="FollowUpQueueReasons"/>.</summary>
    public string Reason { get; init; } = FollowUpQueueReasons.ProjectBusy;
    /// <summary>The job that was running when the user's send hit; for context only.</summary>
    public string? ActiveJobId { get; init; }
    public string? ActiveJobTitle { get; init; }
    /// <summary>Where in the <c>2-ready</c> queue the target ended up (1 = next pickup).</summary>
    public int Position { get; init; }
    /// <summary>The state the target was in before the queue promotion.</summary>
    public string? PromotedFromState { get; init; }
}

/// <summary>
/// Saved user intent on a job that could not run immediately - the project was
/// busy, the lane did not admit a local run, a delivery was under review,
/// execution is routed to a remote runner, or a run carrying the follow-up was
/// stopped before it could act on it. Persisted as <c>pending-intent.json</c>
/// in the job folder. The auto-pickup loop reads and consumes this when it runs
/// the job, which turns the auto-pickup into a UserContinue with the saved
/// follow-up + mode instead of a fresh start.
/// </summary>
public record PendingIntent
{
    public int Version { get; init; } = 1;
    /// <summary>One of <see cref="ContinueModes"/>.</summary>
    public string Mode { get; init; } = ContinueModes.Continue;
    public string Prompt { get; init; } = "";
    public DateTime SavedAt { get; init; }
    /// <summary>
    /// One of <see cref="FollowUpQueueReasons"/>, or <c>run-stopped:&lt;reason&gt;</c>
    /// when a stopped run's unconsumed follow-up was written back.
    /// </summary>
    public string SavedReason { get; init; } = FollowUpQueueReasons.ProjectBusy;
    /// <summary>Diagnostic only: which job was active when this was saved.</summary>
    public string? SavedAgainstActiveJobId { get; init; }
}

/// <summary>
/// String values accepted on <see cref="ContinueJobRequest.Mode"/>. Kept as
/// constants (not an enum) so the JSON wire format is the literal string,
/// which is friendlier for hand-written API calls and stable across enum
/// renames.
/// </summary>
public static class ContinueModes
{
    public const string Continue = "continue";
    public const string Steer    = "steer";
    public const string Extend   = "extend";
    public const string NewTask  = "newTask";

    public static readonly string[] All = [Continue, Steer, Extend, NewTask];

    public static string Normalize(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Continue;
        var v = value.Trim();
        foreach (var m in All)
            if (string.Equals(m, v, StringComparison.OrdinalIgnoreCase)) return m;
        return Continue;
    }
}

// CliModelInfo and CliModelCatalog now come from the CodingAgentRunner package
// (aliased in the csproj).

/// <summary>Canonical model id constants. Call sites should reference these instead of repeated literals.</summary>
public static class ModelIds
{
    public const string ClaudeOpus5 = "claude-opus-5";
    public const string ClaudeFable51 = "claude-fable-5-1";
    public const string ClaudeOpus48 = "claude-opus-4-8";
    public const string ClaudeOpus47 = "claude-opus-4-7";
    public const string ClaudeOpus46 = "claude-opus-4-6";
    public const string ClaudeOpus45 = "claude-opus-4-5";
    public const string ClaudeSonnet5 = "claude-sonnet-5";
    public const string ClaudeSonnet46 = "claude-sonnet-4-6";
    public const string ClaudeSonnet45 = "claude-sonnet-4-5";
    public const string ClaudeHaiku45 = "claude-haiku-4-5";
    /// <summary>Current default Codex/OpenAI model. codex-cli 0.143 on a
    /// ChatGPT account rejects the older <c>gpt-5-codex</c> with a 400
    /// invalid_request ("model not supported when using Codex with a ChatGPT
    /// account"); <c>gpt-5.5</c> is the account-valid model per
    /// <c>~/.codex/config.toml</c> and live test (AGT-1941).</summary>
    public const string Gpt55 = "gpt-5.5";
    /// <summary>Flagship Codex model id once the installed codex CLI advertises
    /// it. gpt-5.6 is intentionally NOT a static catalog entry: its
    /// availability follows the live CLI via <c>CodexModelDiscovery</c> (house
    /// rule: convention/derivation over a hardcoded list, AGT-2025). This
    /// constant only names the well-known id for detection defaults and
    /// tests; <see cref="ModelMetadataRegistry.DefaultForCli"/> returns it when
    /// discovery has detected it, otherwise it falls back to <see cref="Gpt55"/>.</summary>
    public const string Gpt56Sol = "gpt-5.6-sol";
    /// <summary>Lower cost tiers of the gpt-5.6 family (model-routing-policy.md).
    /// Unlike <see cref="Gpt56Sol"/> these ARE registry entries (AGT-2707 round 2),
    /// solely so a codex-cli that does not list one renders it disabled with a
    /// reason instead of leaving it invisible. Their registry <c>Available</c>
    /// baseline is false: the whole gpt-5.6 family stays detection-only (AGT-2025),
    /// so a total CLI-probe failure must never assume one is offered. Their
    /// reasoning ladder and default are NOT curated here and are NOT onboarded for
    /// live-discovered per-model ladders either; both keep resolving through the
    /// static <c>CliThinkingLevels</c> table, same as <see cref="Gpt56Sol"/>.</summary>
    public const string Gpt56Terra = "gpt-5.6-terra";
    /// <summary>See <see cref="Gpt56Terra"/>.</summary>
    public const string Gpt56Luna = "gpt-5.6-luna";
    /// <summary>Economy Codex model for bounded supporting-agent and pipeline work.
    /// Registry-onboarded (AGT-2707 round 2) so a codex-cli that does not offer it
    /// (observed on codex-cli 0.144.1, AGT-2707) renders it disabled with a reason
    /// instead of leaving it invisible while <c>PipelineStepModelDefaults</c> and
    /// friends keep requesting it by id.</summary>
    public const string Gpt54Mini = "gpt-5.4-mini";
    /// <summary>Onboarded OpenAI flagship of the gpt-6 generation. Unlike the
    /// gpt-5.6 family this one IS a registry entry, so the picker can show it
    /// as a disabled, explained option when the installed codex-cli does not
    /// offer it yet (AGT-2707). It is not the product default; that stays with
    /// <see cref="Gpt56Sol"/> detection / the <see cref="Gpt55"/> baseline.</summary>
    public const string Gpt6Astra = "gpt-6-astra";
    public const string Gpt5Codex = "gpt-5-codex";
    public const string Gpt41 = "gpt-4.1";
    public const string Gpt4o = "gpt-4o";
    public const string Gemini25Pro = "gemini-2.5-pro";
    public const string Gemini25Flash = "gemini-2.5-flash";
}

/// <summary>Model family ids used by <see cref="ModelFamilyResolver"/> to pick the
/// newest available member of a generation lineage instead of a pinned literal.
/// Only families that today have more than one generation, or are expected to
/// gain one, are onboarded here (haiku/sonnet/opus for Claude, mini/flagship
/// for Codex). Fable and the non-tiered vendors have no family entry.</summary>
public static class ModelFamilies
{
    public const string ClaudeHaiku = "claude-haiku";
    public const string ClaudeSonnet = "claude-sonnet";
    public const string ClaudeOpus = "claude-opus";
    public const string GptMini = "gpt-mini";
    public const string GptFlagship = "gpt-flagship";
}

/// <summary>
/// Resolves "the newest available model in a family" so runtime defaults never
/// hardcode a generation literal (AGT-2716). Two source layers, most
/// authoritative first:
/// <list type="number">
/// <item>Live CLI discovery: <see cref="ModelMetadataRegistry.DetectedVendorAvailability"/>,
/// published by <c>ClaudeModelDiscovery</c>/<c>CodexModelDiscovery</c> after
/// every catalogue read. When discovery has run for the family's vendor, only
/// a member that catalogue actually reported is returned.</item>
/// <item>Static registry fallback: the family's members in declared order
/// (already newest-first) filtered to <c>Available &amp;&amp; !Deprecated</c>. Used
/// when discovery has not run yet (process just started) or reported nothing
/// useful for the family.</item>
/// </list>
/// Gpt-flagship is a thin alias over the already-live <see cref="ModelMetadataRegistry.DefaultForCli"/>
/// Codex detection layer, so it stays in lockstep with the existing gpt-5.6
/// mechanism instead of duplicating it. Gpt-mini has exactly one member today
/// (<see cref="ModelIds.Gpt54Mini"/>, not a static registry entry - "availability
/// comes from live CLI discovery" per its declaration comment) and resolves to
/// it directly until a second mini generation is onboarded.
/// </summary>
public static class ModelFamilyResolver
{
    /// <summary>Family members in declared (newest-first) order. Claude entries are
    /// registry ids; gpt-mini's sole member is not a registry entry (see class doc).</summary>
    private static readonly IReadOnlyDictionary<string, string[]> StaticMembers =
        new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase)
        {
            [ModelFamilies.ClaudeHaiku] = [ModelIds.ClaudeHaiku45],
            [ModelFamilies.ClaudeSonnet] = [ModelIds.ClaudeSonnet5, ModelIds.ClaudeSonnet46, ModelIds.ClaudeSonnet45],
            [ModelFamilies.ClaudeOpus] =
                [ModelIds.ClaudeOpus5, ModelIds.ClaudeOpus48, ModelIds.ClaudeOpus47, ModelIds.ClaudeOpus46, ModelIds.ClaudeOpus45],
            [ModelFamilies.GptMini] = [ModelIds.Gpt54Mini],
        };

    private static readonly IReadOnlyDictionary<string, string> VendorForFamily =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [ModelFamilies.ClaudeHaiku] = "anthropic",
            [ModelFamilies.ClaudeSonnet] = "anthropic",
            [ModelFamilies.ClaudeOpus] = "anthropic",
            [ModelFamilies.GptMini] = "openai",
        };

    /// <summary>Resolve the newest available model id for <paramref name="family"/>
    /// (one of <see cref="ModelFamilies"/>). Never returns null: when discovery
    /// or the registry rules out every member (e.g. a transient discovery
    /// hiccup), the family's newest declared member is the last resort so a
    /// caller never has to null-check a runtime default.</summary>
    public static string Resolve(string family)
    {
        if (string.Equals(family, ModelFamilies.GptFlagship, StringComparison.OrdinalIgnoreCase))
            return ModelMetadataRegistry.DefaultForCli(CliTypes.Codex) ?? ModelIds.Gpt55;

        if (!StaticMembers.TryGetValue(family, out var members) || members.Length == 0)
            throw new ArgumentException($"Unknown model family '{family}'.", nameof(family));

        var vendor = VendorForFamily[family];
        var detected = ModelMetadataRegistry.DetectedVendorAvailability(vendor);
        foreach (var id in members)
        {
            if (detected != null)
            {
                if (detected.Contains(id)) return id;
                continue;
            }
            var metadata = ModelMetadataRegistry.Find(id);
            if (metadata is null || (metadata.Available && !metadata.Deprecated)) return id;
        }
        return members[0];
    }
}

public sealed record ModelMetadata(
    string Id,
    string Label,
    string? Vendor,
    bool IsDefault,
    bool Deprecated,
    bool Available,
    long? ContextWindow,
    string[]? Aliases = null,
    string[]? ThinkingLevels = null,
    string? DefaultThinkingLevel = null,
    string? MinimumCliVersion = null)
{
    // Pricing is intentionally a live catalog pass-through. Studio owns no
    // rates; callers that need historical cost use TokenPricing.Estimate.
    private TokenEconomy.ModelPrice? CurrentPrice =>
        TokenEconomy.ModelPriceCatalog.Default
            .ResolvePrice(Id, DateTime.UtcNow).Price;
    public decimal? InputPricePerMillion => CurrentPrice?.InputPerMTok;
    public decimal? OutputPricePerMillion => CurrentPrice?.OutputPerMTok;
    public decimal? CacheReadPerMillionOverride => CurrentPrice?.CacheReadPerMTok;
    public decimal? CacheWritePerMillionOverride => CurrentPrice?.CacheWritePerMTok;
}

/// <summary>
/// Single server-side source of truth for known model metadata: catalog labels,
/// defaults, pricing, context windows, aliases, and deprecation status.
/// </summary>
public static class ModelMetadataRegistry
{
    private static readonly ModelMetadata[] Entries =
    [
        Claude(ModelIds.ClaudeOpus5, "Claude Opus 5", isDefault: true, context: 1_000_000,
            thinkingLevels: ["low", "medium", "high", "xhigh", "max"], defaultThinkingLevel: "high"),
        Claude(ModelIds.ClaudeFable51, "Claude Fable 5.1", context: 200_000,
            aliases: ["claude-fable-5.1"],
            thinkingLevels: ["low", "medium", "high", "xhigh", "max"], defaultThinkingLevel: "high"),
        Claude(ModelIds.ClaudeSonnet5, "Claude Sonnet 5", context: 200_000),
        Claude(ModelIds.ClaudeOpus48, "Claude Opus 4.8", context: 200_000, aliases: ["claude-opus-4.8"]),
        Claude(ModelIds.ClaudeOpus47, "Claude Opus 4.7", context: 200_000, aliases: ["claude-opus-4.7"]),
        Claude(ModelIds.ClaudeOpus46, "Claude Opus 4.6", context: 200_000, aliases: ["claude-opus-4.6"]),
        Claude(ModelIds.ClaudeOpus45, "Claude Opus 4.5", context: 200_000, aliases: ["claude-opus-4.5"]),
        Claude(ModelIds.ClaudeSonnet46, "Claude Sonnet 4.6", context: 200_000, aliases: ["claude-sonnet-4.6"]),
        Claude(ModelIds.ClaudeSonnet45, "Claude Sonnet 4.5", context: 200_000, aliases: ["claude-sonnet-4.5"]),
        Claude(ModelIds.ClaudeHaiku45, "Claude Haiku 4.5", context: 200_000,
            aliases: ["claude-haiku-4.5", "claude-haiku-4-5-20251001"]),
        // gpt-5.5 is the current Codex/OpenAI default. codex-cli 0.143 on a
        // ChatGPT account rejects gpt-5-codex with a 400 invalid_request, so
        // the default must be the account-valid model (AGT-1941). Pricing is
        // left null until authoritative numbers are confirmed (same posture as
        // the GPT-4.1 / GPT-4o entries) so no invented cost is asserted.
        new(ModelIds.Gpt55, "GPT-5.5", "openai", IsDefault: true, Deprecated: false, Available: true,
            ContextWindow: 400_000),
        // gpt-6-astra is onboarded as a known model so the picker can show it
        // disabled-with-a-reason on a codex-cli that does not offer it yet
        // (AGT-2707). Its reasoning ladder and default level are deliberately
        // absent here: they come from live discovery, which is the only source
        // that stays correct across CLI releases. Pricing is left null (no
        // invented rates), same posture as the GPT-4.1 / GPT-4o entries.
        new(ModelIds.Gpt6Astra, "GPT-6 Astra", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 272_000, MinimumCliVersion: "0.153.0"),
        // gpt-5.6-terra / gpt-5.6-luna are the lower cost tiers of the gpt-5.6
        // family used by model-routing-policy.md. Onboarded as registry entries
        // (AGT-2707 round 2) purely so the picker disables them with a reason on
        // a codex-cli that does not list one, instead of leaving them invisible -
        // gpt-5.6-sol deliberately still has no entry (AGT-2025: the flagship
        // stays detection-only). Available:false here is the same detection-only
        // rule applied to these two: FallbackCatalog (the total-probe-failure
        // path) must never assume a gpt-5.6 model is offered without a live
        // CLI answer. Live discovery overrides this to Available:true whenever
        // the installed CLI actually lists one (2026-09-11 evidence: codex-cli
        // 0.144.1 lists sol, terra, and luna). No curated ThinkingLevels /
        // DefaultThinkingLevel: their ladder keeps resolving through the static
        // CliThinkingLevels table, and neither is onboarded for a live-discovered
        // per-model ladder override (that allowlist holds only gpt-6-astra).
        new(ModelIds.Gpt56Terra, "GPT-5.6 Terra", "openai", IsDefault: false, Deprecated: false, Available: false,
            ContextWindow: 272_000),
        new(ModelIds.Gpt56Luna, "GPT-5.6 Luna", "openai", IsDefault: false, Deprecated: false, Available: false,
            ContextWindow: 272_000),
        // gpt-5-codex is retained (API-key accounts still accept it) but is no
        // longer the default: a ChatGPT-account spawn rejects it outright.
        new(ModelIds.Gpt5Codex, "GPT-5 Codex", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 272_000),
        // gpt-5.4-mini backs bounded orchestrator/support steps
        // (PipelineStepModelDefaults, ReviewDecisionOrchestrator, GitService
        // commit summaries). Registry-onboarded (AGT-2707 round 2) so the picker
        // disables it with a reason when the installed CLI does not offer it
        // (2026-09-11 evidence: codex-cli 0.144.1 rejects it with HTTP 400 and
        // omits it from `debug models`) instead of leaving it invisible while
        // production code keeps requesting it by id.
        new(ModelIds.Gpt54Mini, "GPT-5.4 Mini", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 272_000),
        new(ModelIds.Gpt41, "GPT-4.1", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 1_000_000),
        new(ModelIds.Gpt4o, "GPT-4o", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 128_000),
        new(ModelIds.Gemini25Pro, "Gemini 2.5 Pro", "google", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 2_000_000),
        new(ModelIds.Gemini25Flash, "Gemini 2.5 Flash", "google", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 1_000_000),
    ];

    private static readonly IReadOnlyDictionary<string, ModelMetadata> ById = Entries
        .SelectMany(e => new[] { e.Id }.Concat(e.Aliases ?? []).Select(id => (id, e)))
        .ToDictionary(x => x.id, x => x.e, StringComparer.OrdinalIgnoreCase);

    // Case-insensitive index from display label (e.g. "Claude Sonnet 5") back
    // to the entry. Durable token receipts historically persisted the label
    // as the model field (AGT-2740); this index is what lets a stored label
    // resolve back to a catalog id on read without migrating task.json files.
    private static readonly IReadOnlyDictionary<string, ModelMetadata> ByLabel = Entries
        .ToDictionary(e => e.Label, e => e, StringComparer.OrdinalIgnoreCase);

    // Detection-driven Codex default id, published by CodexModelDiscovery after
    // a live catalog fetch (house rule: derive from the installed CLI, do not
    // hardcode a catalog - AGT-2025). Volatile because it is read on request
    // threads and written from the discovery gate. Null => CLI not yet probed,
    // unavailable, or no gpt-5.6 detected => the static gpt-5.5 baseline holds.
    private static volatile string? _detectedCodexDefaultId;

    /// <summary>
    /// Publish the Codex default model derived from the installed CLI. Pass null
    /// to clear (CLI unavailable / no gpt-5.6 detected) so
    /// <see cref="DefaultForCli"/> falls back to the static gpt-5.5 baseline.
    /// </summary>
    public static void SetDetectedCodexDefault(string? modelId)
        => _detectedCodexDefaultId = string.IsNullOrWhiteSpace(modelId) ? null : modelId.Trim();

    /// <summary>The last Codex default detected from the installed CLI, or null.</summary>
    public static string? DetectedCodexDefault => _detectedCodexDefaultId;

    /// <summary>
    /// Reasoning ladders the installed Codex CLI reported per model
    /// (<c>supported_reasoning_levels</c> + <c>default_reasoning_level</c>).
    /// Same posture as <see cref="_detectedCodexDefaultId"/>: the installed CLI
    /// is the source of truth and the static <c>CliThinkingLevels</c> table is
    /// only a fallback, because that table drifts every time a CLI release
    /// reshapes a ladder (AGT-2707: the 5.6 family gained <c>max</c> and lost
    /// <c>minimal</c>). Empty => not probed yet, so the static table holds.
    /// </summary>
    private static volatile IReadOnlyDictionary<string, CliReasoningLadder> _detectedCodexLadders =
        new Dictionary<string, CliReasoningLadder>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Model ids whose reasoning ladder and default are allowed to come from
    /// live codex-cli discovery instead of the static <c>CliThinkingLevels</c>
    /// table (AGT-2707: onboarding gpt-6-astra). Deliberately an explicit,
    /// narrow allowlist rather than "every codex model": a 2026-09-07 review
    /// blocked a broader version of this feature because letting the CLI's
    /// own <c>default_reasoning_level</c> answer for the already-shipped
    /// gpt-5.6 family silently changed their product default and ladder
    /// order. Whether to extend live-discovered ladders to gpt-5.6 is a
    /// separate operator decision (see model-routing-policy.md); add ids here
    /// only when that decision is made.
    /// </summary>
    private static readonly HashSet<string> LiveDiscoveredLadderModelIds =
        new(StringComparer.OrdinalIgnoreCase) { ModelIds.Gpt6Astra };

    /// <summary>Whether <paramref name="model"/> is onboarded to take its reasoning
    /// ladder/default from live CLI discovery rather than the static table.</summary>
    public static bool UsesLiveDiscoveredThinkingLadder(string? model)
        => !string.IsNullOrWhiteSpace(model) && LiveDiscoveredLadderModelIds.Contains(model.Trim());

    /// <summary>
    /// Publish the per-model reasoning ladders discovered from the installed
    /// Codex CLI. Models not on <see cref="LiveDiscoveredLadderModelIds"/> or
    /// that reported no ladder are skipped so they keep falling back to the
    /// static table. Pass null/empty to clear.
    /// </summary>
    public static void SetDetectedCodexLadders(IEnumerable<CliModelInfo>? models)
    {
        var ladders = new Dictionary<string, CliReasoningLadder>(StringComparer.OrdinalIgnoreCase);
        foreach (var model in models ?? [])
        {
            if (string.IsNullOrWhiteSpace(model.Id) || !UsesLiveDiscoveredThinkingLadder(model.Id)) continue;
            var levels = model.ThinkingLevels;
            if (levels is not { Count: > 0 }) continue;
            ladders[model.Id.Trim()] = new CliReasoningLadder(
                [.. levels],
                string.IsNullOrWhiteSpace(model.DefaultThinkingLevel) ? null : model.DefaultThinkingLevel.Trim());
        }
        _detectedCodexLadders = ladders;
    }

    private static CliReasoningLadder? DetectedLadder(string? cliType, string? model)
    {
        if (string.IsNullOrWhiteSpace(model)) return null;
        if (!CliTypes.IsValid(cliType) || CliTypes.Normalize(cliType) != CliTypes.Codex) return null;
        if (!UsesLiveDiscoveredThinkingLadder(model)) return null;
        return _detectedCodexLadders.TryGetValue(model.Trim(), out var ladder) ? ladder : null;
    }

    // Family-scoped live availability, keyed by vendor and published by
    // ClaudeModelDiscovery/CodexModelDiscovery after every catalogue read
    // (fresh, mem-cache, or disk-cache), mirroring the existing
    // _detectedCodexDefaultId pattern above but generalized across vendors so
    // ModelFamilyResolver can ask "did discovery actually report this id" for
    // any family, not just the single Codex flagship scalar. Vendor absent =>
    // discovery has not run yet for that vendor, so callers fall back to the
    // static registry's Available/Deprecated flags.
    private static volatile IReadOnlyDictionary<string, IReadOnlySet<string>> _detectedVendorAvailability =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Publish the model ids the installed CLI actually reported as
    /// available for a vendor. Replaces any previously published set for that
    /// vendor (does not merge), so a model the CLI stops advertising drops out.</summary>
    public static void SetDetectedVendorAvailability(string vendor, IEnumerable<string> availableModelIds)
    {
        if (string.IsNullOrWhiteSpace(vendor)) return;
        var next = new Dictionary<string, IReadOnlySet<string>>(_detectedVendorAvailability, StringComparer.OrdinalIgnoreCase)
        {
            [vendor.Trim()] = new HashSet<string>(availableModelIds ?? [], StringComparer.OrdinalIgnoreCase)
        };
        _detectedVendorAvailability = next;
    }

    /// <summary>The last set of available model ids discovery published for a
    /// vendor, or null when discovery has not run yet for that vendor.</summary>
    public static IReadOnlySet<string>? DetectedVendorAvailability(string vendor)
        => !string.IsNullOrWhiteSpace(vendor) && _detectedVendorAvailability.TryGetValue(vendor.Trim(), out var ids)
            ? ids
            : null;

    /// <summary>Test/reset hook: clears every previously published vendor
    /// availability set, restoring "discovery has not run" for every vendor.</summary>
    public static void ClearDetectedVendorAvailability()
        => _detectedVendorAvailability = new Dictionary<string, IReadOnlySet<string>>(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<ModelMetadata> All => Entries;

    public static IReadOnlyList<ModelMetadata> ForVendor(string vendor)
        => Entries.Where(e => string.Equals(e.Vendor, vendor, StringComparison.OrdinalIgnoreCase)).ToList();

    public static string? DefaultForCli(string? cliType)
    {
        // Codex follows the installed CLI: once discovery detects a newer top
        // model (gpt-5.6-*), it is published here and becomes the product
        // default everywhere Gpt55 was drawn (task creation, cli-type switch,
        // client-default materialization). Null => static gpt-5.5 baseline.
        if (CliTypes.IsValid(cliType) && CliTypes.Normalize(cliType) == CliTypes.Codex
            && _detectedCodexDefaultId is { Length: > 0 } detected)
            return detected;

        var vendor = VendorForCli(cliType);
        if (vendor == null) return null;
        var models = ForVendor(vendor).Where(e => e.Available && !e.Deprecated).ToList();
        return models.FirstOrDefault(e => e.IsDefault)?.Id ?? models.FirstOrDefault()?.Id;
    }

    /// <summary>
    /// Product default reasoning level for a CLI+model when the user/owner did
    /// not pick one. For codex the operator directive (AGT-2025) is the biggest
    /// reasoning value the installed CLI advertises for the model: the top of
    /// the CLI-derived thinking-level ladder (gpt-5.6 -> ultra, gpt-5.5 ->
    /// xhigh, gpt-5-codex -> high). Other CLIs keep the ladder's native default.
    /// </summary>
    public static string? DefaultThinkingLevelForCli(string? cliType, string? model)
    {
        // Curated registry metadata wins first: a model this Studio already
        // ships an explicit ladder for (the gpt-5.6 family) keeps its
        // curated default byte-for-byte regardless of what an installed CLI
        // reports for its OWN default_reasoning_level, which is a UX default
        // for the bare CLI, not necessarily this product's routing choice.
        var metadata = Find(model);
        if (!string.IsNullOrWhiteSpace(metadata?.DefaultThinkingLevel)
            && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.DefaultThinkingLevel;
        }

        // Only a model with NO curated default (gpt-6-astra today) falls
        // through to what the installed CLI itself reported (AGT-2707).
        var detected = DetectedLadder(cliType, model)?.Default;
        if (!string.IsNullOrWhiteSpace(detected)) return detected;

        if (CliTypes.IsValid(cliType) && CliTypes.Normalize(cliType) == CliTypes.Codex)
        {
            var top = ThinkingLevelsFor(cliType, model).LastOrDefault();
            if (!string.IsNullOrWhiteSpace(top)) return top;
        }
        return CliThinkingLevels.DefaultFor(cliType, model);
    }

    /// <summary>
    /// Resolve the effective reasoning level: an explicit or owner-provided
    /// choice wins (normalized to the model's ladder); otherwise fall back to
    /// the product default for the CLI (<see cref="DefaultThinkingLevelForCli"/>).
    /// </summary>
    public static string? ResolveThinkingLevel(string? cliType, string? model, string? requested)
        => string.IsNullOrWhiteSpace(requested)
            ? DefaultThinkingLevelForCli(cliType, model)
            : NormalizeThinkingLevel(cliType, model, requested);

    public static bool IsCompatibleWithCli(string? cliType, string? model)
    {
        if (string.IsNullOrWhiteSpace(cliType) || string.IsNullOrWhiteSpace(model)) return true;

        var expectedVendor = VendorForCli(cliType);
        if (expectedVendor == null) return true;

        var metadata = Find(model);
        return metadata == null
               || string.Equals(metadata.Vendor, expectedVendor, StringComparison.OrdinalIgnoreCase);
    }

    public static string? NormalizeForCli(string? cliType, string? model)
    {
        var trimmed = string.IsNullOrWhiteSpace(model) ? null : model.Trim();
        if (string.IsNullOrWhiteSpace(cliType)) return trimmed;
        return IsCompatibleWithCli(cliType, trimmed)
            ? trimmed ?? DefaultForCli(cliType)
            : DefaultForCli(cliType);
    }

    public static ModelMetadata? Find(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        return ById.TryGetValue(id.Trim(), out var metadata) ? metadata : null;
    }

    /// <summary>Case-insensitive lookup by exact display label (e.g. <c>"Claude Sonnet 5"</c>).</summary>
    public static ModelMetadata? FindByLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label)) return null;
        var normalized = Regex.Replace(label.Trim(), @"\s+", " ");
        return ByLabel.TryGetValue(normalized, out var metadata) ? metadata : null;
    }

    public static ModelMetadata? FindByLabelOrAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return Find(normalized)
               ?? FindByLabel(normalized)
               ?? Entries.FirstOrDefault(entry =>
                   entry.Aliases?.Any(alias =>
                       string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase)) ?? false);
    }

    /// <summary>
    /// Resolve any known spelling of a model - raw id, alias, or display
    /// label - to its canonical catalog id. The label fallback exists so a
    /// durable receipt that persisted <see cref="ModelMetadata.Label"/> as its
    /// model field (a historical bug, AGT-2740) still resolves to a priceable
    /// id on read, without a task.json migration.
    /// </summary>
    public static string NormalizeId(string? id)
        => FindByLabelOrAlias(id)?.Id ?? id?.Trim() ?? "";

    public static long? ContextWindowFor(string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var metadata = Find(id);
        if (metadata?.ContextWindow is { } exact) return exact;

        foreach (var entry in Entries)
        {
            if (id.StartsWith(entry.Id, StringComparison.OrdinalIgnoreCase))
                return entry.ContextWindow;
        }
        return null;
    }

    public static CliModelInfo ToCliModelInfo(ModelMetadata metadata, string cliType, bool? isDefault = null)
        => new()
        {
            Id = metadata.Id,
            Label = metadata.Label,
            Vendor = metadata.Vendor,
            IsDefault = isDefault ?? metadata.IsDefault,
            Available = metadata.Available && !metadata.Deprecated,
            Deprecated = metadata.Deprecated,
            ThinkingLevels = ThinkingLevelsFor(cliType, metadata.Id).ToList(),
            DefaultThinkingLevel = DefaultThinkingLevelForCli(cliType, metadata.Id)
        };

    public static CliModelInfo UnknownCliModel(string id, string? label, string? vendor, string cliType)
        => new()
        {
            Id = id,
            Label = string.IsNullOrWhiteSpace(label) ? id : label.Trim(),
            Vendor = vendor,
            IsDefault = false,
            Available = true,
            Deprecated = false,
            AvailabilityNote = "Discovered from CLI; missing registry metadata.",
            ThinkingLevels = ThinkingLevelsFor(cliType, id).ToList(),
            DefaultThinkingLevel = DefaultThinkingLevelForCli(cliType, id)
        };

    /// <summary>
    /// The reasoning ladder for a CLI + model, most authoritative source
    /// first: curated registry metadata (keeps the gpt-5.6 family's ladder
    /// byte-for-byte regardless of what the installed CLI reports), then the
    /// ladder the installed CLI reported for a model with no curated ladder
    /// (gpt-6-astra today), then the static <c>CliThinkingLevels</c> table.
    /// </summary>
    public static IReadOnlyList<string> ThinkingLevelsFor(string? cliType, string? model)
    {
        var metadata = Find(model);
        if (metadata?.ThinkingLevels is { Length: > 0 } && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.ThinkingLevels;
        }

        var detected = DetectedLadder(cliType, model)?.Levels;
        return detected is { Count: > 0 } ? detected : CliThinkingLevels.For(cliType, model);
    }

    /// <summary>
    /// The merged-catalog rule shared by every CLI discovery: a model this
    /// registry knows for the CLI's vendor but that live discovery did not
    /// report stays visible and is appended as unavailable with an explaining
    /// note, so an onboarded model is disabled rather than silently hidden
    /// (AGT-2707). Discovered models are returned untouched and first.
    /// </summary>
    public static List<CliModelInfo> AppendUnavailableRegistryEntries(
        IReadOnlyList<CliModelInfo> discovered,
        string vendor,
        string cliType,
        string availabilityNote)
    {
        var discoveredIds = new HashSet<string>(
            discovered.Select(m => m.Id), StringComparer.OrdinalIgnoreCase);
        var merged = discovered.ToList();

        foreach (var known in ForVendor(vendor))
        {
            if (discoveredIds.Contains(known.Id)) continue;
            if (known.Aliases?.Any(discoveredIds.Contains) == true) continue;
            merged.Add(ToCliModelInfo(known, cliType) with
            {
                IsDefault = false,
                Available = false,
                Deprecated = known.Deprecated,
                AvailabilityNote = availabilityNote
            });
        }

        return merged;
    }

    /// <summary>
    /// Note shown on a known model the installed CLI does not offer. The
    /// version is included when a probe has observed one, so the operator can
    /// tell "this CLI is too old" from "this model was withdrawn".
    /// </summary>
    public static string UnavailableOnInstalledCliNote(string cliLabel, string? cliVersion)
        => string.IsNullOrWhiteSpace(cliVersion)
            ? $"Not offered by the installed {cliLabel}."
            : $"Not offered by the installed {cliLabel} {cliVersion.Trim()}.";

    /// <summary>
    /// Explains a missing model as an actionable version requirement whenever
    /// the registry knows the first CLI release that offers it. Unknown,
    /// malformed, and sufficiently new host versions retain the generic
    /// not-offered wording because withdrawal and account visibility remain
    /// distinct from version drift.
    /// </summary>
    public static string UnavailableOnInstalledCliNote(
        string cliLabel,
        string? cliVersion,
        string? modelId)
    {
        var minimum = Find(modelId)?.MinimumCliVersion;
        if (!string.IsNullOrWhiteSpace(minimum)
            && SemanticCliVersion.TryCompare(cliVersion, minimum, out var comparison)
            && comparison < 0)
        {
            return $"Needs {cliLabel} ≥ {SemanticCliVersion.Display(minimum)} " +
                   $"(host has {cliVersion!.Trim()}).";
        }

        return UnavailableOnInstalledCliNote(cliLabel, cliVersion);
    }

    /// <summary>
    /// The ladder without the live-discovery layer: curated registry metadata,
    /// then the static capability table. Discovery itself uses this as its own
    /// fallback so a CLI that reports no ladder cannot be answered with the
    /// ladder a previous CLI release reported.
    /// </summary>
    public static IReadOnlyList<string> StaticThinkingLevelsFor(string? cliType, string? model)
    {
        var metadata = Find(model);
        if (metadata?.ThinkingLevels is { Length: > 0 }
            && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.ThinkingLevels;
        }

        return CliThinkingLevels.For(cliType, model);
    }

    /// <summary>
    /// Resolve a requested reasoning level against the model's effective ladder
    /// (CLI-reported when available, so a rung such as <c>max</c> that only the
    /// installed CLI knows about is accepted instead of being normalized away).
    /// </summary>
    public static string? NormalizeThinkingLevel(string? cliType, string? model, string? requested)
    {
        var levels = ThinkingLevelsFor(cliType, model);
        if (levels.Count == 0) return null;

        var match = levels.FirstOrDefault(level =>
            string.Equals(level, requested?.Trim(), StringComparison.OrdinalIgnoreCase));
        if (match != null) return match;

        // Out-of-ladder request: land on the model's own default rather than
        // the product top-of-ladder, so a stale or mistyped level never
        // silently escalates reasoning cost. Same source order as
        // DefaultThinkingLevelForCli, minus its codex top-of-ladder rule.
        var metadata = Find(model);
        if (!string.IsNullOrWhiteSpace(metadata?.DefaultThinkingLevel)
            && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.DefaultThinkingLevel;
        }

        var detected = DetectedLadder(cliType, model)?.Default;
        if (!string.IsNullOrWhiteSpace(detected)) return detected;

        return CliThinkingLevels.DefaultFor(cliType, model) ?? levels[0];
    }

    /// <summary>
    /// One model's reasoning ladder exactly as the installed CLI reported it:
    /// the supported rungs in CLI order plus the CLI's own default rung.
    /// </summary>
    private sealed record CliReasoningLadder(IReadOnlyList<string> Levels, string? Default);

    private static ModelMetadata Claude(
        string id,
        string label,
        bool isDefault = false,
        long context = 200_000,
        string[]? aliases = null,
        string[]? thinkingLevels = null,
        string? defaultThinkingLevel = null)
        => new(id, label, "anthropic", isDefault, Deprecated: false, Available: true,
            ContextWindow: context, Aliases: aliases,
            ThinkingLevels: thinkingLevels, DefaultThinkingLevel: defaultThinkingLevel);

    private static string? VendorForCli(string? cliType)
    {
        if (!CliTypes.IsValid(cliType)) return null;
        return CliTypes.Normalize(cliType) switch
        {
            CliTypes.Claude => "anthropic",
            CliTypes.Codex => "openai",
            CliTypes.Gemini => "google",
            _ => null
        };
    }
}

/// <summary>
/// Small SemVer 2 comparator for CLI policy. Build metadata is ignored and a
/// pre-release sorts below its matching release; numeric identifiers sort
/// numerically and before non-numeric identifiers.
/// </summary>
public static class SemanticCliVersion
{
    private static readonly Regex Pattern = new(
        @"^[vV]?(?<core>0|[1-9]\d*)(?:\.(?<minor>0|[1-9]\d*))?(?:\.(?<patch>0|[1-9]\d*))?(?:-(?<pre>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static bool IsAtLeast(string? installed, string? minimum)
        => TryCompare(installed, minimum, out var comparison) && comparison >= 0;

    public static bool TryCompare(string? left, string? right, out int comparison)
    {
        comparison = 0;
        if (!TryParse(left, out var a) || !TryParse(right, out var b)) return false;
        comparison = Compare(a, b);
        return true;
    }

    public static string Display(string version)
    {
        if (!TryParse(version, out var parsed)) return version.Trim();
        return parsed.Patch == 0 && parsed.PreRelease.Length == 0
            ? $"{parsed.Major}.{parsed.Minor}"
            : version.Trim().TrimStart('v', 'V');
    }

    private static bool TryParse(string? value, out Parsed parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var match = Pattern.Match(value.Trim());
        if (!match.Success
            || !int.TryParse(match.Groups["core"].Value, out var major)
            || !int.TryParse(match.Groups["minor"].Success ? match.Groups["minor"].Value : "0", out var minor)
            || !int.TryParse(match.Groups["patch"].Success ? match.Groups["patch"].Value : "0", out var patch))
            return false;
        parsed = new Parsed(
            major,
            minor,
            patch,
            match.Groups["pre"].Success ? match.Groups["pre"].Value.Split('.') : []);
        return true;
    }

    private static int Compare(Parsed a, Parsed b)
    {
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;
        if (a.PreRelease.Length == 0) return b.PreRelease.Length == 0 ? 0 : 1;
        if (b.PreRelease.Length == 0) return -1;
        for (var i = 0; i < Math.Max(a.PreRelease.Length, b.PreRelease.Length); i++)
        {
            if (i >= a.PreRelease.Length) return -1;
            if (i >= b.PreRelease.Length) return 1;
            var leftNumeric = int.TryParse(a.PreRelease[i], out var leftNumber);
            var rightNumeric = int.TryParse(b.PreRelease[i], out var rightNumber);
            var item = leftNumeric && rightNumeric
                ? leftNumber.CompareTo(rightNumber)
                : leftNumeric ? -1
                : rightNumeric ? 1
                : string.Compare(a.PreRelease[i], b.PreRelease[i], StringComparison.Ordinal);
            if (item != 0) return item;
        }
        return 0;
    }

    private readonly record struct Parsed(int Major, int Minor, int Patch, string[] PreRelease);
}
