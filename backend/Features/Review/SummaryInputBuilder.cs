using System.Globalization;
using System.Text;
using System.Text.Json;

namespace AgentStudio.Review;

/// <summary>
/// Builds the bounded, task-level evidence package used by Result generation.
/// The order is intentional: task intent always precedes round and log evidence.
/// </summary>
public static class SummaryInputBuilder
{
    public const int TaskTitleBudget = 500;
    public const int TaskPromptBudget = 24_000;
    public const int RoundsBudget = 12_000;
    public const int AgentStatusBudget = 10_000;
    public const int DeliveryBudget = 12_000;
    public const int LastRunLogBudget = 20_000;
    public const int TotalEvidenceBudget = TaskTitleBudget + TaskPromptBudget + RoundsBudget
        + AgentStatusBudget + DeliveryBudget + LastRunLogBudget;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public static SummaryInputs Build(TaskInfo info, string rawLog)
    {
        var pipeline = ReadPipeline(info.FolderPath);
        var events = ReadSessionEvents(info.FolderPath);
        var prompt = ReadFile(Path.Combine(info.FolderPath, "prompt.md"));
        var agentStatus = ReadFile(Path.Combine(info.FolderPath, "results", "status.md"));

        return new SummaryInputs(
            TaskTitle: TruncateMiddleCore(info.Title, TaskTitleBudget, " [title middle truncated] "),
            TaskPrompt: TruncateMiddle(prompt, TaskPromptBudget),
            Rounds: TruncateMiddleCore(BuildRounds(info, events, pipeline), RoundsBudget,
                "\n[round ledger middle truncated]\n"),
            AgentStatus: TruncateTail(string.IsNullOrWhiteSpace(agentStatus) ? "Not provided." : agentStatus, AgentStatusBudget),
            Delivery: TruncateTail(BuildDelivery(info, pipeline), DeliveryBudget),
            Log: TruncateTail(LastRunSlice(rawLog), LastRunLogBudget));
    }

    public static Dictionary<string, string?> ToSlots(
        TaskInfo info,
        SummaryInputs inputs,
        string outcome)
        => new()
        {
            ["taskTitle"] = inputs.TaskTitle,
            ["taskPrompt"] = inputs.TaskPrompt,
            ["rounds"] = inputs.Rounds,
            ["agentStatus"] = inputs.AgentStatus,
            ["delivery"] = inputs.Delivery,
            ["log"] = inputs.Log,
            ["taskType"] = info.TaskType,
            ["mode"] = info.Mode,
            ["outcome"] = outcome,
        };

    public static string TruncateMiddle(string text, int maxChars)
        => TruncateMiddleCore(text, maxChars, "\n\n[task prompt middle truncated]\n\n");

    private static string TruncateMiddleCore(string text, int maxChars, string marker)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        var available = Math.Max(0, maxChars - marker.Length);
        var head = Math.Max(1, available * 3 / 4);
        var tail = available - head;
        return text[..head] + marker + (tail > 0 ? text[^tail..] : string.Empty);
    }

    private static string BuildRounds(
        TaskInfo info,
        IReadOnlyList<SessionEvent> events,
        PipelineExecutionRecord? pipeline)
    {
        var attempts = pipeline is null
            ? new List<PipelineExecutionRecord>()
            : pipeline.PreviousAttempts.AsEnumerable().Reverse().Append(pipeline).ToList();
        var count = Math.Max(events.Count, attempts.Count);
        if (count == 0) return "Run 1, initial: no structured round ledger was recorded.";

        var continuationPrompts = Directory.Exists(info.FolderPath)
            ? Directory.EnumerateFiles(info.FolderPath, "prompt-*.md", SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .Select(ReadFirstLines)
                .ToList()
            : [];
        var lines = new List<string>(count);
        for (var i = 0; i < count; i++)
        {
            var evt = i < events.Count ? events[i] : null;
            var attempt = i < attempts.Count ? attempts[i] : null;
            var trigger = ClassifyTrigger(i, evt, continuationPrompts);
            var duration = evt?.DurationSeconds
                ?? (attempt?.CompletedAt - attempt?.StartedAt)?.TotalSeconds;
            var outcome = evt?.Result ?? evt?.Status
                ?? (attempt?.IsComplete == true ? "completed" : "unknown");
            var usage = SumUsage(attempt);
            var durationText = duration.HasValue
                ? FormatDuration(duration.Value)
                : "duration unknown";
            var tokenText = usage.Total > 0
                ? $"{usage.Total.ToString("N0", CultureInfo.InvariantCulture)} tokens"
                : "tokens unknown";
            var costText = usage.Total > 0 && usage.AllPriced
                ? $"{usage.Cost.ToString("0.000", CultureInfo.InvariantCulture)} USD"
                : "cost unknown";
            lines.Add($"Run {i + 1}, {trigger}, {durationText}: outcome {outcome}; {tokenText}; {costText}.");
        }
        return string.Join('\n', lines);
    }

    private static string BuildDelivery(TaskInfo info, PipelineExecutionRecord? pipeline)
    {
        var sb = new StringBuilder();
        var activeCommits = info.Commits
            .Where(commit => string.IsNullOrWhiteSpace(commit.SupersededBySha)
                && string.IsNullOrWhiteSpace(commit.SupersededByAttempt))
            .ToList();
        if (activeCommits.Count == 0)
        {
            sb.AppendLine("Attributed commits: none recorded.");
        }
        else
        {
            sb.AppendLine($"Attributed commits ({activeCommits.Count}):");
            foreach (var commit in activeCommits)
                sb.AppendLine($"- {commit.ShortSha}: {commit.Message}");

            var files = activeCommits.SelectMany(commit => commit.Files)
                .Where(file => !string.IsNullOrWhiteSpace(file))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(file => file, StringComparer.OrdinalIgnoreCase)
                .ToList();
            var reportedCount = files.Count > 0 ? files.Count : activeCommits.Sum(commit => commit.FilesChanged);
            sb.AppendLine($"git diff --stat against merge base: {reportedCount} files changed (from attributed delivery metadata).");
            foreach (var file in files) sb.AppendLine($"- `{file}`");
        }

        if (pipeline is not null)
        {
            foreach (var stepId in new[]
            {
                PipelineCatalogue.CodeReviewGradeStepId,
                PipelineCatalogue.OrchestratorDecisionStepId,
                PipelineCatalogue.BuildTestGateStepId,
            })
            {
                var step = pipeline.Steps.FirstOrDefault(candidate =>
                    string.Equals(candidate.StepId, stepId, StringComparison.OrdinalIgnoreCase));
                if (step is null) continue;
                var name = PipelineCatalogue.FindStep(stepId)?.DisplayName ?? stepId;
                sb.Append($"{name}: {step.Status}");
                if (!string.IsNullOrWhiteSpace(step.Verdict)) sb.Append($", verdict {step.Verdict}");
                if (!string.IsNullOrWhiteSpace(step.Reason)) sb.Append($", {step.Reason}");
                if (!string.IsNullOrWhiteSpace(step.EvidenceRef)) sb.Append($", evidence `{step.EvidenceRef}`");
                sb.AppendLine(".");
            }
        }

        return sb.Length == 0 ? "No structured delivery facts were recorded." : sb.ToString().TrimEnd();
    }

    private static string ClassifyTrigger(
        int index,
        SessionEvent? evt,
        IReadOnlyList<string> continuationPrompts)
    {
        if (index == 0) return "initial";
        var continuation = index - 1 < continuationPrompts.Count
            ? continuationPrompts[index - 1]
            : string.Empty;
        var evidence = string.Join(' ', new[] { evt?.Kind, evt?.Reason, continuation }).ToLowerInvariant();
        if (evidence.Contains("integration") || evidence.Contains("conflict") || evidence.Contains("rebase"))
            return "integration recovery";
        if (evidence.Contains("review") || evidence.Contains("finding")) return "review finding";
        if (evidence.Contains("timeout")) return "timeout continuation";
        if (!string.IsNullOrWhiteSpace(continuation))
            return $"operator continuation: {continuation}";
        if (string.Equals(evt?.Kind, "recovery", StringComparison.OrdinalIgnoreCase)) return "recovery continuation";
        return "operator continuation";
    }

    private static string LastRunSlice(string rawLog)
    {
        if (string.IsNullOrWhiteSpace(rawLog)) return "No last-run log was recorded.";
        var normalized = rawLog.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        var marker = normalized.LastIndexOf("[taskboard] Started ", StringComparison.OrdinalIgnoreCase);
        return marker >= 0 ? normalized[marker..] : normalized;
    }

    private static string ReadFirstLines(string path)
    {
        var lines = ReadFile(path).Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n')
            .Select(line => line.Trim())
            .Where(line => line.Length > 0)
            .Take(2);
        return string.Join(" / ", lines);
    }

    private static IReadOnlyList<SessionEvent> ReadSessionEvents(string folder)
    {
        var path = TaskPaths.SessionEventsLog(folder);
        if (!File.Exists(path)) return [];
        var events = new List<SessionEvent>();
        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            try
            {
                var parsed = JsonSerializer.Deserialize<SessionEvent>(line, JsonOptions);
                if (parsed is not null) events.Add(parsed);
            }
            catch (JsonException ex)
            {
                SilentCatch.Note(ex, "SummaryInputBuilder: ignored malformed session-events line.");
                // A torn trailing line must not hide earlier round evidence.
            }
        }
        return events;
    }

    private static PipelineExecutionRecord? ReadPipeline(string folder)
    {
        var path = Path.Combine(folder, PipelineExecutionLog.FileName);
        if (!File.Exists(path)) return null;
        try
        {
            return JsonSerializer.Deserialize<PipelineExecutionRecord>(File.ReadAllText(path), JsonOptions);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static (long Input, long Output, long CacheRead, long CacheCreate, long Total, decimal Cost, bool AllPriced)
        SumUsage(PipelineExecutionRecord? attempt)
    {
        if (attempt is null) return (0, 0, 0, 0, 0, 0, false);
        var input = attempt.Steps.Sum(step => step.InputTokens);
        var output = attempt.Steps.Sum(step => step.OutputTokens);
        var cacheRead = attempt.Steps.Sum(step => step.CacheReadTokens);
        var cacheCreate = attempt.Steps.Sum(step => step.CacheCreationTokens);
        var priced = attempt.Steps
            .Where(step => step.InputTokens + step.OutputTokens + step.CacheReadTokens + step.CacheCreationTokens > 0)
            .Select(step => TokenPricing.Estimate(step.Model,
                step.InputTokens, step.OutputTokens, step.CacheReadTokens, step.CacheCreationTokens,
                attempt.CompletedAt ?? attempt.StartedAt))
            .ToList();
        return (input, output, cacheRead, cacheCreate,
            input + output + cacheRead + cacheCreate,
            priced.Sum(cost => cost.ModelKnown ? cost.Total : 0),
            priced.Count > 0 && priced.All(cost => cost.ModelKnown));
    }

    private static string FormatDuration(double seconds)
    {
        if (seconds < 60) return $"{Math.Max(0, Math.Round(seconds)).ToString(CultureInfo.InvariantCulture)} sec";
        return $"{Math.Round(seconds / 60, 1).ToString("0.#", CultureInfo.InvariantCulture)} min";
    }

    private static string ReadFile(string path)
    {
        try { return File.Exists(path) ? File.ReadAllText(path) : string.Empty; }
        catch (IOException) { return string.Empty; }
        catch (UnauthorizedAccessException) { return string.Empty; }
    }

    private static string TruncateTail(string text, int maxChars)
    {
        if (string.IsNullOrEmpty(text) || text.Length <= maxChars) return text;
        const string marker = "[earlier content truncated]\n";
        return marker + text[^(maxChars - marker.Length)..];
    }
}

public sealed record SummaryInputs(
    string TaskTitle,
    string TaskPrompt,
    string Rounds,
    string AgentStatus,
    string Delivery,
    string Log);
