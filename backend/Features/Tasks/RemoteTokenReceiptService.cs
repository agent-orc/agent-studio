using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using AgentStudio.Cli;
using AgentStudio.Runner;

namespace AgentStudio.Tasks;

/// <summary>
/// Materializes remote CLI usage as the same durable per-task receipt consumed
/// by the board and project token rollups. Remote executions stream provider
/// JSON into <c>cli-output.log</c>, but they do not pass through the in-process
/// message bus that normally creates token events.
/// </summary>
public sealed class RemoteTokenReceiptService
{
    private readonly CliUsageParserRegistry _parsers;
    private readonly ICliModelRegistry _models;
    private readonly TaskMutationService _mutations;
    private readonly TaskSessionLog _sessions;
    private readonly ILogger<RemoteTokenReceiptService> _logger;

    public RemoteTokenReceiptService(
        CliUsageParserRegistry parsers,
        ICliModelRegistry models,
        TaskMutationService mutations,
        TaskSessionLog sessions,
        ILogger<RemoteTokenReceiptService> logger)
    {
        _parsers = parsers;
        _models = models;
        _mutations = mutations;
        _sessions = sessions;
        _logger = logger;
    }

    public RemoteTokenReceiptResult PersistFromLog(
        TaskInfo task,
        string runAttemptId,
        string runnerId,
        string? effectiveModel = null)
    {
        var parser = _parsers.Get(task.CliType ?? task.Agent);
        if (parser is null)
            return new RemoteTokenReceiptResult(false, 0, "No usage parser is registered for the task CLI.");

        var path = TaskPaths.CliOutputLog(task.FolderPath);
        // Keep each frame's original log position through attempt filtering;
        // a re-attach can select a wider window without renumbering a turn.
        var lines = CliOutputLogParser.ParseFile(path)
            .Select((line, index) => (Line: line, Index: index))
            .ToList();
        var run = _sessions.ReadSessionEvents(task.Id, task.WatchPath)
            .LastOrDefault(entry => string.Equals(
                entry.RunAttemptId,
                runAttemptId,
                StringComparison.OrdinalIgnoreCase));
        if (run is not null)
        {
            // One task log can contain several continuation attempts. Restrict
            // the receipt to the fenced session being completed so a later
            // completion cannot count an earlier turn twice.
            var from = run.Ts.AddSeconds(-5);
            var through = (run.FinishedAt ?? DateTime.UtcNow).AddSeconds(5);
            lines = lines
                .Where(item => item.Line.Timestamp >= from && item.Line.Timestamp <= through)
                .ToList();
        }
        var observed = new List<ObservedUsage>();
        foreach (var (line, lineIndex) in lines)
        {
            if (!string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase)) continue;
            if (!line.Text.AsSpan().TrimStart().StartsWith("{")) continue;
            try
            {
                using var document = JsonDocument.Parse(line.Text);
                var modelIndex = 0;
                foreach (var usage in parser.ParseAll(
                             document.RootElement,
                             effectiveModel ?? task.Model,
                             _models))
                {
                    if (usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite <= 0) continue;
                    var identity = usage.CumulativeScope is { } scope
                        ? "scope:" + Convert.ToHexString(SHA256.HashData(
                            Encoding.UTF8.GetBytes(scope + "\n" + usage.Model?.Trim().ToLowerInvariant())))[..24]
                        : $"turn:{lineIndex:D8}:{modelIndex:D2}";
                    observed.Add(new ObservedUsage(
                        line.Timestamp == default ? DateTime.UtcNow : line.Timestamp,
                        usage,
                        identity));
                    modelIndex++;
                }
            }
            catch (JsonException ex)
            {
                // Most CLI output is prose or tool traffic. Only provider usage
                // frames are JSON, so a parse miss is expected and silent.
                SilentCatch.Note(ex, "RemoteTokenReceiptService: non-JSON CLI output is not a usage frame.");
            }
        }

        var entries = LatestCumulativeSnapshots(observed)
            .Select(item => new OrchestratorLogEntry
            {
                Ts = item.Ts,
                Kind = OrchestratorLogKinds.Observation,
                Topic = "remote-task-token-receipt",
                Summary = item.Usage.ModelMismatch
                    ? "Remote coding-agent token usage (model mismatch)."
                    : "Remote coding-agent token usage.",
                JobId = task.Id,
                ParticipantId = $"agent:remote-runner:{runAttemptId}:usage:{item.Identity}",
                TokenUsage = new OrchestratorTokenUsage
                {
                    // Observed usage is authoritative. Never replace it
                    // with the card pin, because pricing follows this id.
                    Model = item.Usage.Model,
                    PinnedModel = item.Usage.PinnedModel,
                    ModelMismatch = item.Usage.ModelMismatch,
                    InputTokens = SafeInt(item.Usage.Input),
                    OutputTokens = SafeInt(item.Usage.Output),
                    CacheReadTokens = SafeInt(item.Usage.CacheRead),
                    CacheCreationTokens = SafeInt(item.Usage.CacheWrite),
                    InputIncludesCached = item.Usage.InputIncludesCached,
                },
            })
            .ToList();

        if (entries.Count == 0)
            return new RemoteTokenReceiptResult(false, 0, "The remote CLI log contains no token usage frames.");

        var summary = TokenSummaryService.SummarizePerJob(entries).GetValueOrDefault(task.Id);
        if (summary is null)
            return new RemoteTokenReceiptResult(false, 0, "Token usage frames could not be summarized.");

        var written = _mutations.SetRemoteTokenSummaryOnFolder(
            task.FolderPath,
            runAttemptId,
            summary);
        if (!written)
        {
            _logger.LogWarning(
                "remote-token-receipt-write-failed task={TaskKey} runner={Runner} attempt={Attempt}",
                task.Key ?? task.Id,
                runnerId,
                runAttemptId);
            return new RemoteTokenReceiptResult(false, entries.Count, "The token receipt could not be persisted.");
        }

        return new RemoteTokenReceiptResult(true, entries.Count, null, summary.TotalTokens);
    }

    /// <summary>
    /// Per-turn usages pass through in log order. Session-cumulative usages
    /// (<see cref="ParsedTurnUsage.CumulativeScope"/>) restate the running
    /// total on every frame, so only the last snapshot per scope and model is
    /// a call; summing them counted one Claude session 22 times (AGT-3004).
    /// </summary>
    internal static IReadOnlyList<ObservedUsage> LatestCumulativeSnapshots(
        IReadOnlyList<ObservedUsage> observed)
    {
        var lastIndex = new Dictionary<(string Scope, string Model), int>();
        for (var index = 0; index < observed.Count; index++)
        {
            var usage = observed[index].Usage;
            if (usage.CumulativeScope is null) continue;
            lastIndex[(usage.CumulativeScope, (usage.Model ?? string.Empty).ToLowerInvariant())] = index;
        }
        if (lastIndex.Count == 0) return observed;

        var keep = lastIndex.Values.ToHashSet();
        return observed
            .Where((item, index) => item.Usage.CumulativeScope is null || keep.Contains(index))
            .ToList();
    }

    internal sealed record ObservedUsage(DateTime Ts, ParsedTurnUsage Usage, string Identity);

    private static int SafeInt(long value)
    {
        if (value <= 0) return 0;
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}

public sealed record RemoteTokenReceiptResult(bool Persisted, int Calls, string? Warning, long? TotalTokens = null);
