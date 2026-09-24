using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Cli;

/// <summary>
/// Single parsed snapshot a CLI's "turn completed" frame yields. Source of truth
/// for token / context-window extraction; the bridge wraps this into
/// <see cref="AgentMessageTokens"/> + <see cref="AgentMessageContextWindow"/>.
/// </summary>
/// <remarks>
/// The legacy code parsed <c>usage</c> in two places (OrchestratorRunner.ParseResult
/// and ClaudeEventAdapter.FormatUsage). This record removes that duplication and
/// gives both call sites the same enrichment (context-window snapshot, file count,
/// reasoning-tokens roll-up where the model exposes it).
/// </remarks>
public sealed record ParsedTurnUsage(
    string? Model,
    long Input,
    long Output,
    long CacheRead,
    long CacheWrite,
    long? ReasoningOutput,
    AgentMessageContextWindow? ContextWindow,
    bool InputIncludesCached = false,
    string? PinnedModel = null,
    bool ModelMismatch = false)
{
    /// <summary>Sum of all tokens that occupied the context this turn.</summary>
    public long ContextUsed => Input + CacheRead;

    public AgentMessageTokens ToBusTokens() => new(
        Input: Input,
        Output: Output,
        CacheRead: CacheRead == 0 ? null : CacheRead,
        CacheWrite: CacheWrite == 0 ? null : CacheWrite,
        Model: Model,
        Dollars: null,
        ContextWindow: ContextWindow,
        InputIncludesCached: InputIncludesCached,
        PinnedModel: PinnedModel,
        ModelMismatch: ModelMismatch);
}

/// <summary>
/// Maps a single CLI's "turn finished" JSON frame onto <see cref="ParsedTurnUsage"/>.
/// </summary>
public interface ICliUsageParser
{
    /// <summary>CLI identifier this parser handles (lowercase, e.g. "claude", "codex").</summary>
    string CliType { get; }

    /// <summary>
    /// Try to extract token + context-window data from one JSON object the CLI
    /// emits when a turn finishes. Returns false when the frame does not carry
    /// usage data (tool frames, session-init frames, etc.). Never throws.
    /// </summary>
    /// <param name="frame">Parsed JSON object the CLI wrote (one NDJSON line, or
    /// the full <c>--print --output-format=json</c> blob).</param>
    /// <param name="modelHint">Fallback model id if the frame does not echo one.</param>
    /// <param name="modelRegistry">Lookup for the model's context-window
    /// total. May return null for unknown models; the parser then leaves
    /// <see cref="AgentMessageContextWindow.TotalSize"/> unset.</param>
    /// <param name="usage">The parsed snapshot when the return is true.</param>
    bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage);

    IReadOnlyList<ParsedTurnUsage> ParseAll(
        JsonElement frame,
        string? modelHint,
        ICliModelRegistry modelRegistry)
        => TryParse(frame, modelHint, modelRegistry, out var usage) ? [usage] : [];
}

/// <summary>Resolves a model id to its known context-window size.</summary>
public interface ICliModelRegistry
{
    /// <summary>Total context-window size in tokens, or null when unknown.</summary>
    long? TotalContextSize(string? modelId);
}

/// <summary>Adapter over the canonical model metadata registry.</summary>
public sealed class CliModelRegistry : ICliModelRegistry
{
    public long? TotalContextSize(string? modelId) => ModelMetadataRegistry.ContextWindowFor(modelId);
}

/// <summary>
/// Claude Code's usage extractor. Handles both the <c>--print
/// --output-format=json</c> blob (orchestrator path) and the
/// <c>result</c> frame from <c>stream-json</c> (task agent path) - they share
/// the same <c>usage</c> shape.
/// </summary>
public sealed class ClaudeUsageParser : ICliUsageParser
{
    public string CliType => "claude";

    public bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage)
    {
        usage = null!;
        if (frame.ValueKind != JsonValueKind.Object) return false;

        // Both flows have a top-level "usage" object on a "result"-shaped frame.
        if (!frame.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;

        var declaredModel = frame.TryGetProperty("model", out var md) ? md.GetString() : null;
        var observedModels = ObservedModels(frame);
        var model = observedModels.Count == 1 ? observedModels[0] : declaredModel ?? modelHint;

        var input      = GetLong(u, "input_tokens");
        var output     = GetLong(u, "output_tokens");
        var cacheRead  = GetLong(u, "cache_read_input_tokens");
        var cacheWrite = GetLong(u, "cache_creation_input_tokens");

        var contextWindow = BuildContextWindow(model, input, cacheRead, modelRegistry);

        usage = new ParsedTurnUsage(
            Model: model,
            Input: input,
            Output: output,
            CacheRead: cacheRead,
            CacheWrite: cacheWrite,
            ReasoningOutput: null,
            ContextWindow: contextWindow,
            PinnedModel: modelHint,
            ModelMismatch: ModelAttribution.IsMismatch(modelHint, model));
        return true;
    }

    public IReadOnlyList<ParsedTurnUsage> ParseAll(
        JsonElement frame,
        string? modelHint,
        ICliModelRegistry modelRegistry)
    {
        if (frame.ValueKind != JsonValueKind.Object
            || !frame.TryGetProperty("modelUsage", out var modelUsage)
            || modelUsage.ValueKind != JsonValueKind.Object)
            return TryParse(frame, modelHint, modelRegistry, out var fallbackUsage) ? [fallbackUsage] : [];

        var result = new List<ParsedTurnUsage>();
        foreach (var property in modelUsage.EnumerateObject())
        {
            if (property.Value.ValueKind != JsonValueKind.Object) continue;
            var value = property.Value;
            var input = GetLongEither(value, "inputTokens", "input_tokens");
            var output = GetLongEither(value, "outputTokens", "output_tokens");
            var cacheRead = GetLongEither(value, "cacheReadInputTokens", "cache_read_input_tokens");
            var cacheWrite = GetLongEither(value, "cacheCreationInputTokens", "cache_creation_input_tokens");
            if (input + output + cacheRead + cacheWrite <= 0) continue;
            var model = property.Name.Trim();
            result.Add(new ParsedTurnUsage(
                model,
                input,
                output,
                cacheRead,
                cacheWrite,
                ReasoningOutput: null,
                BuildContextWindow(model, input, cacheRead, modelRegistry),
                PinnedModel: modelHint,
                ModelMismatch: ModelAttribution.IsMismatch(modelHint, model)));
        }

        return result.Count > 0
            ? result
            : TryParse(frame, modelHint, modelRegistry, out var aggregateUsage) ? [aggregateUsage] : [];
    }

    private static List<string> ObservedModels(JsonElement frame)
    {
        if (!frame.TryGetProperty("modelUsage", out var modelUsage)
            || modelUsage.ValueKind != JsonValueKind.Object)
            return [];
        return modelUsage.EnumerateObject()
            .Select(property => property.Name.Trim())
            .Where(model => model.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private static AgentMessageContextWindow? BuildContextWindow(string? model, long input, long cacheRead, ICliModelRegistry registry)
    {
        var total = registry.TotalContextSize(model);
        var used = input + cacheRead;
        if (total is null && used == 0) return null;
        return new AgentMessageContextWindow(
            TotalSize: total,
            Used: used,
            Remaining: total is { } t ? Math.Max(0, t - used) : null,
            // System-prompt + conversation split needs the cache_creation events
            // across the run; not derivable from a single frame. Left null until
            // the runner aggregates that across turns.
            SystemPromptTokens: null,
            ConversationTokens: null);
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;

    private static long GetLongEither(JsonElement obj, string first, string second)
        => GetLong(obj, first) is var value && value > 0 ? value : GetLong(obj, second);
}

/// <summary>
/// Codex's usage extractor for <c>turn.completed</c> frames in
/// <c>codex exec --json</c> output. OpenAI's <c>input_tokens</c> includes
/// <c>cached_input_tokens</c>; the parser stores only uncached input alongside
/// the cache-read dimension so downstream pricing never counts cache hits twice.
/// </summary>
public sealed class CodexUsageParser : ICliUsageParser
{
    public string CliType => "codex";

    public bool TryParse(JsonElement frame, string? modelHint, ICliModelRegistry modelRegistry, out ParsedTurnUsage usage)
    {
        usage = null!;
        if (frame.ValueKind != JsonValueKind.Object) return false;

        // turn.completed wraps usage under "usage"; legacy session_meta does not.
        var type = frame.TryGetProperty("type", out var t) ? t.GetString() : null;
        if (!string.Equals(type, "turn.completed", StringComparison.Ordinal)) return false;

        if (!frame.TryGetProperty("usage", out var u) || u.ValueKind != JsonValueKind.Object) return false;

        var declaredModel = frame.TryGetProperty("model", out var md) ? md.GetString() : null;
        var model = declaredModel ?? modelHint;

        var normalized = ProviderUsageNormalization.OpenAi(
            GetLong(u, "input_tokens"),
            GetLong(u, "cached_input_tokens"));
        var output    = GetLong(u, "output_tokens");
        var reasoning = GetLong(u, "reasoning_output_tokens");

        var contextWindow = BuildContextWindow(model, normalized.ContextInputTokens, modelRegistry);

        usage = new ParsedTurnUsage(
            Model: model,
            Input: normalized.InputTokens,
            Output: output,
            CacheRead: normalized.CacheReadTokens,
            CacheWrite: 0,
            ReasoningOutput: reasoning,
            ContextWindow: contextWindow,
            InputIncludesCached: normalized.InputIncludesCached,
            PinnedModel: modelHint,
            ModelMismatch: ModelAttribution.IsMismatch(modelHint, model));
        return true;
    }

    private static AgentMessageContextWindow? BuildContextWindow(string? model, long contextInput, ICliModelRegistry registry)
    {
        var total = registry.TotalContextSize(model);
        var used = contextInput;
        if (total is null && used == 0) return null;
        return new AgentMessageContextWindow(
            TotalSize: total,
            Used: used,
            Remaining: total is { } t ? Math.Max(0, t - used) : null);
    }

    private static long GetLong(JsonElement obj, string name)
        => obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : 0L;
}

public static class ModelAttribution
{
    public static bool IsMismatch(string? pinnedModel, string? observedModel)
    {
        if (string.IsNullOrWhiteSpace(pinnedModel) || string.IsNullOrWhiteSpace(observedModel)) return false;
        var pinned = ModelMetadataRegistry.Find(pinnedModel)?.Id ?? StripDatedClaudeSuffix(pinnedModel.Trim());
        var observed = ModelMetadataRegistry.Find(observedModel)?.Id ?? StripDatedClaudeSuffix(observedModel.Trim());
        return !string.Equals(pinned, observed, StringComparison.OrdinalIgnoreCase);
    }

    private static string StripDatedClaudeSuffix(string model)
    {
        var separator = model.LastIndexOf('-');
        if (separator <= 0) return model;
        var suffix = model[(separator + 1)..];
        return suffix.Length == 8 && suffix.All(char.IsDigit) ? model[..separator] : model;
    }
}

/// <summary>
/// Lookup that dispatches a CLI type to the matching parser. Single instance per
/// process; parsers are stateless so the registry is safe to share.
/// </summary>
public sealed class CliUsageParserRegistry
{
    private readonly Dictionary<string, ICliUsageParser> _byCli;

    public CliUsageParserRegistry(IEnumerable<ICliUsageParser> parsers)
    {
        _byCli = parsers.ToDictionary(p => p.CliType, StringComparer.OrdinalIgnoreCase);
    }

    public ICliUsageParser? Get(string? cliType)
    {
        if (string.IsNullOrWhiteSpace(cliType)) return null;
        return _byCli.TryGetValue(cliType, out var p) ? p : null;
    }
}
