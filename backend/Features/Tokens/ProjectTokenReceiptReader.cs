using System.Text.Json;

namespace AgentStudio.Tokens;

/// <summary>
/// Reads the durable per-task token receipt attached to <c>task.json</c>.
/// Remote runner completions do not pass through the legacy project bus emit,
/// so this receipt is the current source for their token calls.
/// </summary>
public sealed class ProjectTokenReceiptReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    public ProjectTokenReceiptReadResult Read(string watchPath)
    {
        if (string.IsNullOrWhiteSpace(watchPath) || !Directory.Exists(watchPath))
        {
            return new ProjectTokenReceiptReadResult(
                [],
                new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal),
                SourceAvailable: false,
                FailedTaskCount: 0,
                Warning: "The task receipt source is unavailable because the project path cannot be read.");
        }

        var entries = new List<OrchestratorLogEntry>();
        var summaries = new Dictionary<string, TaskTokenSummary>(StringComparer.Ordinal);
        var seenTaskIds = new HashSet<string>(StringComparer.Ordinal);
        var failed = 0;

        try
        {
            foreach (var folder in EnumerateTaskFolders(watchPath))
            {
                var path = Path.Combine(folder, "task.json");
                if (!File.Exists(path)) continue;

                try
                {
                    using var document = JsonDocument.Parse(File.ReadAllText(path));
                    var root = document.RootElement;
                    var jobId = ReadString(root, "id") ?? Path.GetFileName(folder);
                    if (string.IsNullOrWhiteSpace(jobId))
                    {
                        failed++;
                        continue;
                    }
                    if (!seenTaskIds.Add(jobId)) continue;

                    if (!TryGetProperty(root, "tokenSummary", out var tokenSummaryElement)
                        || tokenSummaryElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
                    {
                        continue;
                    }

                    var summary = tokenSummaryElement.Deserialize<TaskTokenSummary>(Json);
                    if (summary is null)
                    {
                        failed++;
                        continue;
                    }

                    var normalized = NormalizeSummary(summary);
                    summaries[jobId] = normalized;
                    if (normalized.TotalTokens > 0 && normalized.Entries.Count == 0)
                    {
                        failed++;
                        continue;
                    }
                    entries.AddRange(ToEntries(jobId, normalized));
                }
                catch (Exception)
                {
                    failed++;
                }
            }
        }
        catch (Exception)
        {
            return new ProjectTokenReceiptReadResult(
                entries,
                summaries,
                SourceAvailable: false,
                FailedTaskCount: failed,
                Warning: "The task receipt source could not be enumerated. Token values may be incomplete.");
        }

        var warning = failed > 0
            ? $"Token receipts for {failed} task{(failed == 1 ? "" : "s")} could not be read. Values may be incomplete."
            : null;
        return new ProjectTokenReceiptReadResult(entries, summaries, true, failed, warning);
    }

    internal static IReadOnlyList<OrchestratorLogEntry> MergeWithoutDuplicates(
        IReadOnlyList<OrchestratorLogEntry> historical,
        IReadOnlyList<OrchestratorLogEntry> receipts)
    {
        var remainingHistoricalKeys = new Dictionary<TokenEntryKey, int>();
        foreach (var entry in historical)
        {
            if (entry.TokenUsage is null) continue;
            var key = TokenEntryKey.From(entry);
            remainingHistoricalKeys.TryGetValue(key, out var count);
            remainingHistoricalKeys[key] = count + 1;
        }

        var merged = new List<OrchestratorLogEntry>(historical.Count + receipts.Count);
        merged.AddRange(historical);
        foreach (var receipt in receipts)
        {
            if (receipt.TokenUsage is not null)
            {
                var key = TokenEntryKey.From(receipt);
                if (remainingHistoricalKeys.TryGetValue(key, out var count) && count > 0)
                {
                    remainingHistoricalKeys[key] = count - 1;
                    continue;
                }
            }
            merged.Add(receipt);
        }
        return merged.OrderBy(entry => entry.Ts).ToList();
    }

    private static TaskTokenSummary NormalizeSummary(TaskTokenSummary summary)
    {
        var entries = (summary.Entries ?? [])
            .Where(call => call.Ts != default)
            .OrderBy(call => call.Ts)
            .ToList();
        var entryInput = entries.Sum(call => call.InputTokens);
        var entryOutput = entries.Sum(call => call.OutputTokens);
        var entryCacheRead = entries.Sum(call => call.CacheReadTokens);
        var entryCacheCreation = entries.Sum(call => call.CacheCreationTokens);

        var componentTotal = summary.InputTokens + summary.OutputTokens
            + summary.CacheReadTokens + summary.CacheCreationTokens;
        var unallocatedTotal = Math.Max(0, summary.TotalTokens - componentTotal);
        var residualInput = Math.Max(0, summary.InputTokens - entryInput) + unallocatedTotal;
        var residualOutput = Math.Max(0, summary.OutputTokens - entryOutput);
        var residualCacheRead = Math.Max(0, summary.CacheReadTokens - entryCacheRead);
        var residualCacheCreation = Math.Max(0, summary.CacheCreationTokens - entryCacheCreation);
        var residualTotal = residualInput + residualOutput + residualCacheRead + residualCacheCreation;
        var residualAt = summary.LastUpdate ?? entries.LastOrDefault()?.Ts;
        if (residualTotal > 0 && residualAt.HasValue)
        {
            entries.Add(new TaskTokenCall
            {
                Ts = residualAt.Value,
                Model = summary.LastModel,
                ParticipantId = "agent:task-receipt",
                InputTokens = residualInput,
                OutputTokens = residualOutput,
                CacheReadTokens = residualCacheRead,
                CacheCreationTokens = residualCacheCreation,
                ModelPriced = summary.AllModelsPriced,
            });
            entries = entries.OrderBy(call => call.Ts).ToList();
        }

        return summary with { Entries = entries };
    }

    private static IEnumerable<OrchestratorLogEntry> ToEntries(string jobId, TaskTokenSummary summary)
    {
        foreach (var call in summary.Entries ?? [])
        {
            var total = call.InputTokens + call.OutputTokens + call.CacheReadTokens + call.CacheCreationTokens;
            if (call.Ts == default || total <= 0) continue;
            yield return new OrchestratorLogEntry
            {
                Ts = call.Ts,
                Kind = OrchestratorLogKinds.Observation,
                Topic = "task-token-receipt",
                Summary = "Token usage recovered from the durable task receipt.",
                JobId = jobId,
                ParticipantId = string.IsNullOrWhiteSpace(call.ParticipantId)
                    ? "agent:task-receipt"
                    : call.ParticipantId,
                TokenUsage = new OrchestratorTokenUsage
                {
                    // Historical receipts persisted the display label (e.g.
                    // "Claude Sonnet 5") instead of the catalog id (AGT-2740);
                    // resolve it back here so pricing never sees a label.
                    Model = string.IsNullOrWhiteSpace(call.Model)
                        ? call.Model
                        : ModelMetadataRegistry.NormalizeId(call.Model),
                    InputTokens = SafeInt(call.InputTokens),
                    OutputTokens = SafeInt(call.OutputTokens),
                    CacheReadTokens = SafeInt(call.CacheReadTokens),
                    CacheCreationTokens = SafeInt(call.CacheCreationTokens),
                },
            };
        }
    }

    private static IEnumerable<string> EnumerateTaskFolders(string watchPath)
    {
        foreach (var folder in TaskStorageLayout.EnumerateJobDirs(watchPath))
            yield return folder;
        foreach (var state in TaskStates.All)
        {
            var stateFolder = Path.Combine(watchPath, state);
            if (!Directory.Exists(stateFolder)) continue;
            foreach (var folder in Directory.EnumerateDirectories(stateFolder)
                         .Where(path => !Path.GetFileName(path).StartsWith('_')))
            {
                yield return folder;
            }
        }
    }

    private static bool TryGetProperty(JsonElement element, string name, out JsonElement value)
    {
        foreach (var property in element.EnumerateObject())
        {
            if (!string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase)) continue;
            value = property.Value;
            return true;
        }
        value = default;
        return false;
    }

    private static string? ReadString(JsonElement element, string name)
        => TryGetProperty(element, name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

    private static int SafeInt(long value)
    {
        if (value <= 0) return 0;
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }

    private readonly record struct TokenEntryKey(
        string JobId,
        long TimestampTicks,
        int Input,
        int Output,
        int CacheRead,
        int CacheCreation)
    {
        public static TokenEntryKey From(OrchestratorLogEntry entry)
        {
            var usage = entry.TokenUsage!;
            return new TokenEntryKey(
                entry.JobId ?? string.Empty,
                entry.Ts.ToUniversalTime().Ticks,
                usage.InputTokens,
                usage.OutputTokens,
                usage.CacheReadTokens,
                usage.CacheCreationTokens);
        }
    }
}

public sealed record ProjectTokenReceiptReadResult(
    IReadOnlyList<OrchestratorLogEntry> Entries,
    IReadOnlyDictionary<string, TaskTokenSummary> Summaries,
    bool SourceAvailable,
    int FailedTaskCount,
    string? Warning);
