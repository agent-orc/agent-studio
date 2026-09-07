using System.Text.Json;
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
        string runnerId)
    {
        var path = TaskPaths.CliOutputLog(task.FolderPath);
        var lines = CliOutputLogParser.ParseFile(path);
        var run = _sessions.ReadSessionEvents(task.Id, task.WatchPath)
            .LastOrDefault(entry => string.Equals(
                entry.RunAttemptId,
                runAttemptId,
                StringComparison.OrdinalIgnoreCase));
        var effectiveCli = run?.Cli ?? task.CliType ?? task.Agent;
        var effectiveModel = run?.Model ?? task.Model;
        var parser = _parsers.Get(effectiveCli);
        if (parser is null)
            return new RemoteTokenReceiptResult(false, 0, "No usage parser is registered for the effective run CLI.");
        if (run is not null)
        {
            // One task log can contain several continuation attempts. Restrict
            // the receipt to the fenced session being completed so a later
            // completion cannot count an earlier turn twice.
            var from = run.Ts.AddSeconds(-5);
            var through = (run.FinishedAt ?? DateTime.UtcNow).AddSeconds(5);
            lines = lines
                .Where(line => line.Timestamp >= from && line.Timestamp <= through)
                .ToList();
        }
        var entries = new List<OrchestratorLogEntry>();
        foreach (var line in lines.Where(line =>
                     string.Equals(line.Stream, "stdout", StringComparison.OrdinalIgnoreCase)))
        {
            if (!line.Text.AsSpan().TrimStart().StartsWith("{")) continue;
            try
            {
                using var document = JsonDocument.Parse(line.Text);
                if (!parser.TryParse(document.RootElement, effectiveModel, _models, out var usage)) continue;
                if (usage.Input + usage.Output + usage.CacheRead + usage.CacheWrite <= 0) continue;
                entries.Add(new OrchestratorLogEntry
                {
                    Ts = line.Timestamp == default ? DateTime.UtcNow : line.Timestamp,
                    Kind = OrchestratorLogKinds.Observation,
                    Topic = "remote-task-token-receipt",
                    Summary = "Remote coding-agent token usage.",
                    JobId = task.Id,
                    ParticipantId = $"agent:remote-runner:{runAttemptId}",
                    TokenUsage = new OrchestratorTokenUsage
                    {
                        Model = usage.Model ?? effectiveModel,
                        InputTokens = SafeInt(usage.Input),
                        OutputTokens = SafeInt(usage.Output),
                        CacheReadTokens = SafeInt(usage.CacheRead),
                        CacheCreationTokens = SafeInt(usage.CacheWrite),
                    },
                });
            }
            catch (JsonException ex)
            {
                // Most CLI output is prose or tool traffic. Only provider usage
                // frames are JSON, so a parse miss is expected and silent.
                SilentCatch.Note(ex, "RemoteTokenReceiptService: non-JSON CLI output is not a usage frame.");
            }
        }

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

        return new RemoteTokenReceiptResult(true, entries.Count, null);
    }

    private static int SafeInt(long value)
    {
        if (value <= 0) return 0;
        return value > int.MaxValue ? int.MaxValue : (int)value;
    }
}

public sealed record RemoteTokenReceiptResult(bool Persisted, int Calls, string? Warning);
