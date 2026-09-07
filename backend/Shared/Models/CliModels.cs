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
    /// <summary>Economy Codex model for bounded supporting-agent and pipeline work.
    /// Normal availability comes from live CLI discovery; registry metadata is
    /// retained for the bounded stale-discovery fallback.</summary>
    public const string Gpt54Mini = "gpt-5.4-mini";
    public const string Gpt5Codex = "gpt-5-codex";
    public const string Gpt41 = "gpt-4.1";
    public const string Gpt4o = "gpt-4o";
    public const string Gemini25Pro = "gemini-2.5-pro";
    public const string Gemini25Flash = "gemini-2.5-flash";
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
    string? FamilyId = null,
    long GenerationOrder = 0)
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
            thinkingLevels: ["low", "medium", "high", "xhigh", "max"], defaultThinkingLevel: "high",
            familyId: ModelFamilies.ClaudeOpus, generationOrder: 5_000_000),
        Claude(ModelIds.ClaudeFable51, "Claude Fable 5.1", context: 200_000,
            aliases: ["claude-fable-5.1"],
            thinkingLevels: ["low", "medium", "high", "xhigh", "max"], defaultThinkingLevel: "high"),
        Claude(ModelIds.ClaudeSonnet5, "Claude Sonnet 5", context: 200_000,
            familyId: ModelFamilies.ClaudeSonnet, generationOrder: 5_000_000),
        Claude(ModelIds.ClaudeOpus48, "Claude Opus 4.8", context: 200_000, aliases: ["claude-opus-4.8"],
            familyId: ModelFamilies.ClaudeOpus, generationOrder: 4_008_000),
        Claude(ModelIds.ClaudeOpus47, "Claude Opus 4.7", context: 200_000, aliases: ["claude-opus-4.7"],
            familyId: ModelFamilies.ClaudeOpus, generationOrder: 4_007_000),
        Claude(ModelIds.ClaudeOpus46, "Claude Opus 4.6", context: 200_000, aliases: ["claude-opus-4.6"],
            familyId: ModelFamilies.ClaudeOpus, generationOrder: 4_006_000),
        Claude(ModelIds.ClaudeOpus45, "Claude Opus 4.5", context: 200_000, aliases: ["claude-opus-4.5"],
            familyId: ModelFamilies.ClaudeOpus, generationOrder: 4_005_000),
        Claude(ModelIds.ClaudeSonnet46, "Claude Sonnet 4.6", context: 200_000, aliases: ["claude-sonnet-4.6"],
            familyId: ModelFamilies.ClaudeSonnet, generationOrder: 4_006_000),
        Claude(ModelIds.ClaudeSonnet45, "Claude Sonnet 4.5", context: 200_000, aliases: ["claude-sonnet-4.5"],
            familyId: ModelFamilies.ClaudeSonnet, generationOrder: 4_005_000),
        Claude(ModelIds.ClaudeHaiku45, "Claude Haiku 4.5", context: 200_000,
            aliases: ["claude-haiku-4.5", "claude-haiku-4-5-20251001"],
            familyId: ModelFamilies.ClaudeHaiku, generationOrder: 4_005_000),
        // gpt-5.5 is the current Codex/OpenAI default. codex-cli 0.143 on a
        // ChatGPT account rejects gpt-5-codex with a 400 invalid_request, so
        // the default must be the account-valid model (AGT-1941). Pricing is
        // left null until authoritative numbers are confirmed (same posture as
        // the GPT-4.1 / GPT-4o entries) so no invented cost is asserted.
        new(ModelIds.Gpt55, "GPT-5.5", "openai", IsDefault: true, Deprecated: false, Available: true,
            ContextWindow: 400_000, FamilyId: ModelFamilies.GptFlagship, GenerationOrder: 5_005_000),
        new(ModelIds.Gpt54Mini, "GPT-5.4 Mini", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 400_000, FamilyId: ModelFamilies.GptMini, GenerationOrder: 5_004_000),
        // gpt-5-codex is retained (API-key accounts still accept it) but is no
        // longer the default: a ChatGPT-account spawn rejects it outright.
        new(ModelIds.Gpt5Codex, "GPT-5 Codex", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 272_000, FamilyId: ModelFamilies.GptFlagship, GenerationOrder: 5_000_000),
        new(ModelIds.Gpt41, "GPT-4.1", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 1_000_000, FamilyId: ModelFamilies.GptFlagship, GenerationOrder: 4_001_000),
        new(ModelIds.Gpt4o, "GPT-4o", "openai", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 128_000, FamilyId: ModelFamilies.GptFlagship, GenerationOrder: 4_000_000),
        new(ModelIds.Gemini25Pro, "Gemini 2.5 Pro", "google", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 2_000_000),
        new(ModelIds.Gemini25Flash, "Gemini 2.5 Flash", "google", IsDefault: false, Deprecated: false, Available: true,
            ContextWindow: 1_000_000),
    ];

    private static readonly IReadOnlyDictionary<string, ModelMetadata> ById = Entries
        .SelectMany(e => new[] { e.Id }.Concat(e.Aliases ?? []).Select(id => (id, e)))
        .ToDictionary(x => x.id, x => x.e, StringComparer.OrdinalIgnoreCase);

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

    public static IReadOnlyList<ModelMetadata> All => Entries;

    public static IReadOnlyList<ModelMetadata> ForVendor(string vendor)
        => Entries.Where(e => string.Equals(e.Vendor, vendor, StringComparison.OrdinalIgnoreCase)).ToList();

    /// <summary>Known registry models in newest-first order for one logical family.</summary>
    public static IReadOnlyList<ModelMetadata> ForFamily(string familyId)
    {
        var normalized = ModelFamilies.Normalize(familyId);
        return Entries
            .Where(entry => string.Equals(entry.FamilyId, normalized, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(entry => entry.GenerationOrder)
            .ToList();
    }

    /// <summary>
    /// Returns the logical family for a known or conventionally named model id.
    /// Registry metadata wins; the naming fallback lets live discovery surface
    /// a newer CLI model before Studio ships metadata for that exact id.
    /// </summary>
    public static string? FamilyFor(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var metadata = Find(modelId);
        if (!string.IsNullOrWhiteSpace(metadata?.FamilyId)) return metadata.FamilyId;

        var normalized = modelId.Trim().ToLowerInvariant().Replace('.', '-');
        if (normalized.StartsWith("claude-haiku-", StringComparison.Ordinal))
            return ModelFamilies.ClaudeHaiku;
        if (normalized.StartsWith("claude-sonnet-", StringComparison.Ordinal))
            return ModelFamilies.ClaudeSonnet;
        if (normalized.StartsWith("claude-opus-", StringComparison.Ordinal))
            return ModelFamilies.ClaudeOpus;
        if (!normalized.StartsWith("gpt-", StringComparison.Ordinal)) return null;

        return Regex.IsMatch(normalized, @"(?:^|-)mini(?:-|$)", RegexOptions.CultureInvariant)
            ? ModelFamilies.GptMini
            : ModelFamilies.GptFlagship;
    }

    /// <summary>
    /// Comparable generation order for a registry or live-discovered model.
    /// Explicit registry metadata is authoritative. Conventional numeric ids
    /// are parsed only for newly discovered models that the registry does not
    /// know yet.
    /// </summary>
    public static long? GenerationOrderFor(string? modelId)
    {
        if (string.IsNullOrWhiteSpace(modelId)) return null;

        var metadata = Find(modelId);
        if (metadata?.GenerationOrder > 0) return metadata.GenerationOrder;

        var normalized = modelId.Trim().ToLowerInvariant();
        var match = Regex.Match(
            normalized,
            @"^(?:claude-(?:haiku|sonnet|opus)-|gpt-)(?<major>\d+)(?:[.-](?<minor>\d+))?",
            RegexOptions.CultureInvariant);
        var minor = 0L;
        if (!match.Success
            || !long.TryParse(match.Groups["major"].Value, out var major)
            || (match.Groups["minor"].Success
                && !long.TryParse(match.Groups["minor"].Value, out minor)))
        {
            return null;
        }

        if (major > (long.MaxValue - Math.Min(minor, 999) * 1_000) / 1_000_000)
            return null;
        return major * 1_000_000 + Math.Min(minor, 999) * 1_000;
    }

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
        var metadata = Find(model);
        if (!string.IsNullOrWhiteSpace(metadata?.DefaultThinkingLevel)
            && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.DefaultThinkingLevel;
        }

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

    public static ModelMetadata? FindByLabelOrAlias(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        var normalized = Regex.Replace(value.Trim(), @"\s+", " ");
        return Find(normalized)
               ?? Entries.FirstOrDefault(entry =>
                   string.Equals(entry.Label, normalized, StringComparison.OrdinalIgnoreCase)
                   || (entry.Aliases?.Any(alias =>
                       string.Equals(alias, normalized, StringComparison.OrdinalIgnoreCase)) ?? false));
    }

    public static string NormalizeId(string? id)
        => Find(id)?.Id ?? id?.Trim() ?? "";

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

    public static IReadOnlyList<string> ThinkingLevelsFor(string? cliType, string? model)
    {
        var metadata = Find(model);
        if (metadata?.ThinkingLevels is { Length: > 0 }
            && IsCompatibleWithCli(cliType, metadata.Id))
        {
            return metadata.ThinkingLevels;
        }

        return CliThinkingLevels.For(cliType, model);
    }

    public static string? NormalizeThinkingLevel(string? cliType, string? model, string? requested)
    {
        var metadata = Find(model);
        if (metadata?.ThinkingLevels is not { Length: > 0 }
            || !IsCompatibleWithCli(cliType, metadata.Id))
        {
            return CliThinkingLevels.Normalize(cliType, model, requested);
        }

        var levels = metadata.ThinkingLevels;
        var match = levels.FirstOrDefault(level =>
            string.Equals(level, requested?.Trim(), StringComparison.OrdinalIgnoreCase));
        return match ?? DefaultThinkingLevelForCli(cliType, model);
    }

    private static ModelMetadata Claude(
        string id,
        string label,
        bool isDefault = false,
        long context = 200_000,
        string[]? aliases = null,
        string[]? thinkingLevels = null,
        string? defaultThinkingLevel = null,
        string? familyId = null,
        long generationOrder = 0)
        => new(id, label, "anthropic", isDefault, Deprecated: false, Available: true,
            ContextWindow: context, Aliases: aliases,
            ThinkingLevels: thinkingLevels, DefaultThinkingLevel: defaultThinkingLevel,
            FamilyId: familyId, GenerationOrder: generationOrder);

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
