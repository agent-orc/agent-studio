using System.Text.Json;
using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly Regex CriticalProviderFallbackFloor = new(
        @"\b(security|auth(?:entication|orization)?|permission|credential|encryption|data[ -]?loss|race[ -]?condition|concurren|deadlock|distributed|fenc\w*|lease\w*|stale[ -]?write|migration|architecture)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private sealed record ProviderRejectionRecoveryPlan(
        ProviderModelFallback? Fallback,
        ProviderRequestRejection Rejection,
        string Model,
        int RefusalCount,
        string EscalationReason)
    {
        public bool Continue => Fallback is not null;
    }

    private sealed record ProviderRejectionClaimContinuation(
        ProviderModelFallback Fallback,
        string BaseRef,
        string BaseSha);

    public async Task<IReadOnlyList<ProviderRejectionDailyCountDto>> ListProviderRejectionCountsAsync(
        int days,
        CancellationToken ct)
    {
        var boundedDays = Math.Clamp(days, 1, 90);
        var lastDay = UtcNow.ToUniversalTime().Date;
        var firstDay = lastDay.AddDays(-(boundedDays - 1));
        var refusals = new List<(DateTime Day, string Model, string Refusal)>();
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection, """
            SELECT payload_json, occurred_at
              FROM events
             WHERE kind = 'execution.outcome.classified'
               AND occurred_at >= $firstDay;
            """, ("$firstDay", Iso(firstDay)));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var decision = JsonSerializer.Deserialize<ExecutionOutcomeDecision>(reader.GetString(0), OutcomeJson);
            var model = decision?.RawFacts.EffectiveModel?.Trim();
            if (decision?.Outcome != ExecutionOutcomeKind.ProviderRejectedRequest
                || decision.ProviderRejection is not { } rejection
                || string.IsNullOrWhiteSpace(model))
                continue;
            var occurredAt = Parse(reader.GetString(1)).ToUniversalTime();
            if (occurredAt.Date < firstDay || occurredAt.Date > lastDay) continue;
            refusals.Add((occurredAt.Date, model, DescribeProviderRejection(rejection)));
        }

        return refusals
            .GroupBy(item => new { item.Day, item.Model })
            .Select(group => new ProviderRejectionDailyCountDto(
                group.Key.Day.ToString("yyyy-MM-dd", System.Globalization.CultureInfo.InvariantCulture),
                group.Key.Model,
                group.Count(),
                group.Select(item => item.Refusal)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .OrderBy(value => value, StringComparer.OrdinalIgnoreCase)
                    .ToArray()))
            .OrderByDescending(item => item.Day, StringComparer.Ordinal)
            .ThenByDescending(item => item.Count)
            .ThenBy(item => item.Model, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private async Task<ProviderRejectionRecoveryPlan?> PlanProviderRejectionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CompleteRunRequest request,
        CancellationToken ct)
    {
        var decision = request.OutcomeDecision;
        if (decision?.Outcome != ExecutionOutcomeKind.ProviderRejectedRequest
            || decision.ProviderRejection is not { } rejection)
            return null;

        var cli = decision.RawFacts.EffectiveCliType?.Trim() ?? string.Empty;
        var model = decision.RawFacts.EffectiveModel?.Trim() ?? string.Empty;
        var thinking = decision.RawFacts.EffectiveThinkingLevel?.Trim();
        var declared = ModelRoutingPolicyDocument.Value.ProviderRejectionFallbacks.FirstOrDefault(item =>
            string.Equals(item.CliType, cli, StringComparison.OrdinalIgnoreCase)
            && string.Equals(item.From, model, StringComparison.OrdinalIgnoreCase));
        var count = await CountProviderRefusalsAsync(connection, transaction, taskId, model, ct);
        var safeReason = $"Provider refused the request ({DescribeProviderRejection(rejection)}): {rejection.Message}";
        if (string.IsNullOrWhiteSpace(request.SalvageBranch)
            || string.IsNullOrWhiteSpace(request.SalvageCommitSha)
            || decision.RecoveryAction != ExecutionRecoveryAction.StartFreshAttemptFromSalvage)
            return new(null, rejection, model, count, safeReason + " No salvage continuation is available.");
        if (declared is null)
            return new(null, rejection, model, count, safeReason + " No provider-refusal sibling is declared for this model.");

        var taskText = await ReadProviderFallbackTaskTextAsync(connection, transaction, taskId, ct);
        if (!ProviderFallbackMeetsFloor(declared.To, thinking, taskText.TaskType, taskText.Text))
            return new(null, rejection, model, count, safeReason + " The declared sibling is below this card's correctness floor.");

        var fallback = declared with
        {
            ThinkingLevel = thinking,
            CardPinned = count >= 2,
        };
        return new(fallback, rejection, model, count, string.Empty);
    }

    private async Task<ProviderRejectionClaimContinuation?> ReadProviderFallbackForClaimAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        TaskDto task,
        CancellationToken ct)
    {
        string? payload = null;
        string? salvageBranch = null;
        string? salvageCommitSha = null;
        await using (var command = Command(connection, """
            SELECT event.payload_json, completion.salvage_branch, completion.salvage_commit_sha
              FROM events event
              JOIN run_completions completion ON completion.run_id = event.run_id
             WHERE event.task_id = $task
               AND event.kind = 'execution.outcome.classified'
             ORDER BY event.cursor DESC
             LIMIT 1;
            """, transaction, ("$task", task.TaskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
        {
            if (await reader.ReadAsync(ct))
            {
                payload = reader.GetString(0);
                salvageBranch = reader.IsDBNull(1) ? null : reader.GetString(1);
                salvageCommitSha = reader.IsDBNull(2) ? null : reader.GetString(2);
            }
        }
        if (string.IsNullOrWhiteSpace(payload)) return null;

        var decision = JsonSerializer.Deserialize<ExecutionOutcomeDecision>(payload, OutcomeJson);
        if (decision?.Outcome != ExecutionOutcomeKind.ProviderRejectedRequest
            || decision.ProviderRejection is null)
            return null;
        var synthetic = new CompleteRunRequest(
            string.Empty,
            string.Empty,
            string.Empty,
            0,
            decision.Outcome.ToString(),
            OutcomeDecision: decision,
            SalvageBranch: salvageBranch,
            SalvageCommitSha: salvageCommitSha);
        var fallback = (await PlanProviderRejectionAsync(
            connection, transaction, task.TaskId, synthetic, ct))?.Fallback;
        return fallback is null
               || string.IsNullOrWhiteSpace(salvageBranch)
               || string.IsNullOrWhiteSpace(salvageCommitSha)
            ? null
            : new ProviderRejectionClaimContinuation(
                fallback,
                salvageBranch,
                salvageCommitSha);
    }

    private static string DescribeProviderRejection(ProviderRequestRejection rejection)
        => string.IsNullOrWhiteSpace(rejection.Parameter)
            ? rejection.Code ?? "request_rejected"
            : $"{rejection.Code ?? "request_rejected"} {rejection.Parameter}";

    private async Task<int> CountProviderRefusalsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        string model,
        CancellationToken ct)
    {
        var count = 0;
        await using var command = Command(connection, """
            SELECT payload_json FROM events
             WHERE task_id = $task AND kind = 'execution.outcome.classified';
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var decision = JsonSerializer.Deserialize<ExecutionOutcomeDecision>(reader.GetString(0), OutcomeJson);
            if (decision?.Outcome == ExecutionOutcomeKind.ProviderRejectedRequest
                && string.Equals(decision.RawFacts.EffectiveModel, model, StringComparison.OrdinalIgnoreCase))
                count++;
        }
        return count;
    }

    private static async Task<(string TaskType, string Text)> ReadProviderFallbackTaskTextAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        CancellationToken ct)
    {
        await using var command = Command(connection, """
            SELECT COALESCE(fields.task_type, 'chore'), task.title, COALESCE(task.body, '')
              FROM tasks task
              LEFT JOIN task_studio_fields fields ON fields.task_id = task.id
             WHERE task.id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        return await reader.ReadAsync(ct)
            ? (reader.GetString(0), reader.GetString(1) + "\n" + reader.GetString(2))
            : ("chore", string.Empty);
    }

    private static bool ProviderFallbackMeetsFloor(
        string model,
        string? thinking,
        string taskType,
        string taskText)
    {
        var policy = ModelRoutingPolicyDocument.Value;
        var taskDefault = policy.TaskTypeDefaults.FirstOrDefault(item =>
            string.Equals(item.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        var floor = taskDefault?.HardFloorTier is { Length: > 0 } floorId
            ? policy.Tiers.FirstOrDefault(item => item.Id == floorId)?.Rank ?? 0
            : 0;
        if (CriticalProviderFallbackFloor.IsMatch(taskText)) floor = Math.Max(floor, 4);
        var thinkingRank = ThinkingRank(thinking);
        var routeRank = model.ToLowerInvariant() switch
        {
            "gpt-5.6-sol" when thinkingRank >= ThinkingRank("xhigh") => 4,
            "gpt-5.6-sol" when thinkingRank >= ThinkingRank("medium") => 3,
            "gpt-5.6-sol" when thinkingRank >= ThinkingRank("low") => 1,
            "claude-opus-4-8" when thinkingRank >= ThinkingRank("xhigh") => 4,
            "claude-sonnet-4-6" when thinkingRank >= ThinkingRank("medium") => 3,
            "claude-sonnet-4-6" when thinkingRank >= ThinkingRank("low") => 1,
            _ => -1,
        };
        return routeRank >= floor;
    }

    private static int ThinkingRank(string? value) => value?.Trim().ToLowerInvariant() switch
    {
        "minimal" => 0,
        "low" => 1,
        "medium" => 2,
        "high" => 3,
        "xhigh" => 4,
        "max" => 5,
        "ultra" => 6,
        _ => -1,
    };

    private static async Task PinProviderFallbackAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        ProviderModelFallback fallback,
        DateTime now,
        CancellationToken ct)
        => await ExecuteAsync(connection, """
            INSERT INTO task_studio_fields(task_id, cli_type, model, thinking_level, version, updated_at)
            VALUES ($task, $cli, $model, $thinking, 1, $updated)
            ON CONFLICT(task_id) DO UPDATE SET
                cli_type = excluded.cli_type,
                model = excluded.model,
                thinking_level = excluded.thinking_level,
                version = task_studio_fields.version + 1,
                updated_at = excluded.updated_at;
            """, ct, transaction,
            ("$task", taskId), ("$cli", fallback.CliType), ("$model", fallback.To),
            ("$thinking", fallback.ThinkingLevel), ("$updated", Iso(now)));
}
