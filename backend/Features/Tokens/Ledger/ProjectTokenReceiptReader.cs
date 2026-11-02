using System.Collections.Concurrent;
using System.Text.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Tokens;

/// <summary>
/// Reads the durable per-task token receipt attached to <c>task.json</c>.
/// Remote runner completions do not pass through the legacy project bus emit,
/// so this receipt is the current source for their token calls.
/// </summary>
/// <remarks>
/// Receipts written before AGT-3012 can hold the same usage several times
/// (one Claude session repeated its cumulative usage on every result frame).
/// Each task's calls collapse through <see cref="TokenLedgerDuplicates"/>
/// before the totals are derived, so history no longer inflates the
/// project rollups.
/// <para>
/// The ledger re-reads every receipt of a project, archive included, whenever
/// the task index moves. On a workspace with thousands of cards that walk was
/// the largest single allocator of the API (AGT-3369). Each folder's parsed
/// receipt is therefore kept against the length and write time of its
/// <c>task.json</c>, the fingerprint the task scanner uses as well: an
/// unchanged card costs one file stat, a rewritten one is parsed again.
/// </para>
/// </remarks>
public sealed class ProjectTokenReceiptReader
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true,
    };

    private readonly ConcurrentDictionary<string, ParsedReceipt> _parsed = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    /// <summary>
    /// One folder's receipt as read from its <c>task.json</c>. <see cref="JobId"/>
    /// is null when the file names no task; <see cref="Summary"/> is null when the
    /// task has no receipt; <see cref="Failed"/> marks an unreadable file.
    /// </summary>
    private sealed record ParsedReceipt(
        long Length,
        DateTime WriteUtc,
        string? JobId,
        TaskTokenSummary? Summary,
        TokenLedgerCollapseCount Collapsed,
        bool Failed);

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
        var collapsed = TokenLedgerCollapseCount.None;

        var visited = new HashSet<string>(_parsed.Comparer);
        try
        {
            foreach (var folder in EnumerateTaskFolders(watchPath))
            {
                var path = Path.Combine(folder, "task.json");
                var file = new FileInfo(path);
                if (!file.Exists) continue;
                visited.Add(path);

                var receipt = ReadReceipt(path, folder, file);
                if (receipt.Failed)
                {
                    failed++;
                    continue;
                }
                if (!seenTaskIds.Add(receipt.JobId!)) continue;
                if (receipt.Summary is not { } normalized) continue;

                collapsed = collapsed.Add(receipt.Collapsed);
                summaries[receipt.JobId!] = normalized;
                if (normalized.TotalTokens > 0 && normalized.Entries.Count == 0)
                {
                    failed++;
                    continue;
                }
                entries.AddRange(ToEntries(receipt.JobId!, normalized));
            }
        }
        catch (Exception)
        {
            return new ProjectTokenReceiptReadResult(
                entries,
                summaries,
                SourceAvailable: false,
                FailedTaskCount: failed,
                Warning: "The task receipt source could not be enumerated. Token values may be incomplete.",
                CollapsedDuplicates: collapsed);
        }

        // Forget folders of this project that are gone, so the memo cannot grow
        // with history. Other projects' entries live under other prefixes.
        var prefix = Path.TrimEndingDirectorySeparator(watchPath) + Path.DirectorySeparatorChar;
        foreach (var path in _parsed.Keys)
        {
            if (path.StartsWith(prefix, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal)
                && !visited.Contains(path))
                _parsed.TryRemove(path, out _);
        }

        var warning = failed > 0
            ? $"Token receipts for {failed} task{(failed == 1 ? "" : "s")} could not be read. Values may be incomplete."
            : null;
        return new ProjectTokenReceiptReadResult(entries, summaries, true, failed, warning, collapsed);
    }

    private ParsedReceipt ReadReceipt(string path, string folder, FileInfo file)
    {
        if (_parsed.TryGetValue(path, out var known)
            && known.Length == file.Length
            && known.WriteUtc == file.LastWriteTimeUtc)
        {
            return known;
        }

        var parsed = ParseReceipt(folder, file);
        _parsed[path] = parsed;
        return parsed;
    }

    private static ParsedReceipt ParseReceipt(string folder, FileInfo file)
    {
        var length = file.Length;
        var writeUtc = file.LastWriteTimeUtc;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file.FullName));
            var root = document.RootElement;
            var jobId = ReadString(root, "id") ?? Path.GetFileName(folder);
            if (string.IsNullOrWhiteSpace(jobId))
                return new ParsedReceipt(length, writeUtc, null, null, TokenLedgerCollapseCount.None, Failed: true);

            if (!TryGetProperty(root, "tokenSummary", out var tokenSummaryElement)
                || tokenSummaryElement.ValueKind is JsonValueKind.Null or JsonValueKind.Undefined)
            {
                return new ParsedReceipt(length, writeUtc, jobId, null, TokenLedgerCollapseCount.None, Failed: false);
            }

            var summary = tokenSummaryElement.Deserialize<TaskTokenSummary>(Json);
            if (summary is null)
                return new ParsedReceipt(length, writeUtc, jobId, null, TokenLedgerCollapseCount.None, Failed: true);

            var normalized = NormalizeSummary(summary, out var taskCollapsed);
            return new ParsedReceipt(length, writeUtc, jobId, normalized, taskCollapsed, Failed: false);
        }
        catch (Exception)
        {
            // A torn or half-written file: parse it again on the next read
            // instead of keeping the failure against a fingerprint that a
            // finished write may not change.
            return new ParsedReceipt(-1, default, null, null, TokenLedgerCollapseCount.None, Failed: true);
        }
    }

    internal static TaskTokenSummary NormalizeSummary(
        TaskTokenSummary summary,
        out TokenLedgerCollapseCount collapsed)
    {
        summary = CollapseDuplicateCalls(summary, out collapsed);
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

    /// <summary>
    /// Drops repeated calls and subtracts exactly what was dropped from the
    /// receipt totals. Keeping the persisted totals would turn the collapsed
    /// tokens into a residual row and undo the collapse; a genuine residual
    /// (totals not covered by per-call rows) survives unchanged.
    /// </summary>
    internal static TaskTokenSummary CollapseDuplicateCalls(
        TaskTokenSummary summary,
        out TokenLedgerCollapseCount collapsed)
    {
        var calls = summary.Entries ?? [];
        var kept = TokenLedgerDuplicates.CollapseCalls(calls, out collapsed);
        if (collapsed.Entries == 0) return summary;

        long Dropped(Func<TaskTokenCall, long> value) => calls.Sum(value) - kept.Sum(value);
        var droppedCost = calls.Sum(call => call.EstimatedApiCostUsd) - kept.Sum(call => call.EstimatedApiCostUsd);
        return summary with
        {
            Calls = Math.Max(kept.Count, summary.Calls - collapsed.Entries),
            InputTokens = Math.Max(0, summary.InputTokens - Dropped(call => call.InputTokens)),
            OutputTokens = Math.Max(0, summary.OutputTokens - Dropped(call => call.OutputTokens)),
            CacheReadTokens = Math.Max(0, summary.CacheReadTokens - Dropped(call => call.CacheReadTokens)),
            CacheCreationTokens = Math.Max(0, summary.CacheCreationTokens - Dropped(call => call.CacheCreationTokens)),
            TotalTokens = Math.Max(0, summary.TotalTokens - collapsed.Tokens),
            EstimatedApiCostUsd = Math.Max(0m, summary.EstimatedApiCostUsd - droppedCost),
            Entries = kept.ToList(),
        };
    }

    private static IEnumerable<OrchestratorLogEntry> ToEntries(string jobId, TaskTokenSummary summary)
    {
        foreach (var stored in summary.Entries ?? [])
        {
            // A legacy Codex receipt the AGT-2882 repair has not rewritten
            // still counts cached input twice; normalize it before pricing,
            // exactly like immutable bus rows.
            var call = StoredUsageNormalization.Normalize(stored);
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
                        : ModelIdentity.StoredId(call.Model),
                    InputTokens = SafeInt(call.InputTokens),
                    OutputTokens = SafeInt(call.OutputTokens),
                    CacheReadTokens = SafeInt(call.CacheReadTokens),
                    CacheCreationTokens = SafeInt(call.CacheCreationTokens),
                    ReasoningTokens = call.ReasoningTokens is { } reasoning ? SafeInt(reasoning) : null,
                    ThinkingLevel = call.ThinkingLevel,
                    CliType = call.CliType,
                    Host = call.Host,
                    InputIncludesCached = call.InputIncludesCached,
                    UsageNormalization = call.UsageNormalization,
                    PinnedModel = call.PinnedModel,
                    ModelMismatch = call.ModelMismatch,
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
}

public sealed record ProjectTokenReceiptReadResult(
    IReadOnlyList<OrchestratorLogEntry> Entries,
    IReadOnlyDictionary<string, TaskTokenSummary> Summaries,
    bool SourceAvailable,
    int FailedTaskCount,
    string? Warning,
    TokenLedgerCollapseCount CollapsedDuplicates = default);
