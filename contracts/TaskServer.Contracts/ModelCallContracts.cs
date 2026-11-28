using System.Text.Json.Serialization;

namespace AgentStudio.TaskServer.Contracts;

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoricalModelCallBatch(string Fingerprint, IReadOnlyList<HistoricalModelCall> Calls);

[JsonUnmappedMemberHandling(JsonUnmappedMemberHandling.Disallow)]
public sealed record HistoricalModelCall(
    string Card, int Run, int Ordinal, long HappenedAtMs, int Revision,
    string Kind, string? Model, string? ReasoningLevel,
    long? PromptTokens, long? CachedInput, long? Output, long? Reasoning,
    string? ResponseAlias = null, string? ServiceTier = null);

public sealed record HistoricalModelCallAck(int Inserted, int Revised, int Replayed, int Aliased);

public sealed record ModelCallTotals(
    long Calls, long Tokens, long Waits, long StatusChecks,
    long FreshInput, long CachedInput, long Output,
    long PricedCalls, long UnpricedTokens, long MissingUsage, decimal? ListUsd, long UnclassifiedInput = 0);

public sealed record ModelCallGroup(string Key, ModelCallTotals Totals);
public sealed record ModelCallRow(
    string Id, int Ordinal, long HappenedAtMs, string Kind, long? Tokens, string PriceStatus, long? KnownTokens = null);
public sealed record ModelCallWindow(
    ModelCallTotals Totals, IReadOnlyList<ModelCallGroup> Groups, ModelCallTotals? Other,
    IReadOnlyList<ModelCallRow> Responses, string? NextCursor);

public sealed record ModelCallDetail(
    string Id, string Card, string TaskId, int Run, int Ordinal, long HappenedAtMs,
    string TimestampBasis, string UsageBasis, string LinkBasis, string Kind, string? Classifier,
    string? Model, string? ReasoningLevel, string? ServiceTier,
    long? PromptTokens, long? FreshInput, long? CachedInput, long? Output, long? Reasoning,
    string PriceStatus, decimal? ListUsd, ModelCallPriceEvidence? Price,
    bool HasResponseAlias, int UsageRevision, string Fingerprint, string? SourceCardUrl);

public sealed record ModelCallPriceEvidence(
    string CatalogRevision, string DateBasis, string Currency, DateTime ValidFrom, string? Source,
    decimal InputPerMillion, decimal CacheReadPerMillion, decimal OutputPerMillion,
    decimal InputUsd, decimal CacheReadUsd, decimal OutputUsd);
