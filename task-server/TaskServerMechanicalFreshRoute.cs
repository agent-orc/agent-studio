using System.Text.RegularExpressions;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private static readonly Regex MechanicalProtocolFloor = new(
        @"\b(public[ -]?(?:api|protocol|contract)|protocol|persistent[ -]?state|cross[ -]?subsystem)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static (string? Ref, string? Sha) MechanicalFallbackBase(
        SessionContinuationLedgerEntry? previous)
    {
        if (previous is not { MechanicalResumesUsed: > 0, FallbackReason: { Length: > 0 } }
            || previous.ResultRef is null
            || !previous.ResultRef.StartsWith("refs/heads/", StringComparison.Ordinal)
            || previous.ResultSha is not { Length: 40 or 64 } sha
            || !sha.All(Uri.IsHexDigit))
            return (null, null);
        return (previous.ResultRef, sha);
    }

    private static async Task<MechanicalFreshRunRoute?> ReadMechanicalFreshRouteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string taskId,
        MechanicalRoundDelta? delta,
        SessionContinuationLedgerEntry? previous,
        CancellationToken ct)
    {
        var reason = previous is { MechanicalResumesUsed: > 0, FallbackReason: { Length: > 0 } }
            ? previous.FallbackReason
            : delta is null ? null : "pending-mechanical-continuation";
        if (reason is null) return null;

        await using var command = Command(connection, """
            SELECT COALESCE(fields.cli_type, ''), COALESCE(fields.model, ''),
                   COALESCE(fields.thinking_level, ''), COALESCE(fields.task_type, 'chore'),
                   task.title, COALESCE(task.body, '')
              FROM tasks task
              LEFT JOIN task_studio_fields fields ON fields.task_id = task.id
             WHERE task.id = $task;
            """, transaction, ("$task", taskId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Task was not found.");
        var cli = reader.GetString(0);
        var existingModel = reader.GetString(1);
        var existingThinking = reader.GetString(2);
        var taskType = reader.GetString(3);
        var taskText = reader.GetString(4) + "\n" + reader.GetString(5);

        var policy = ModelRoutingPolicyDocument.Value;
        var taskDefault = policy.TaskTypeDefaults.FirstOrDefault(item =>
            string.Equals(item.TaskType, taskType, StringComparison.OrdinalIgnoreCase));
        var defaultFloor = taskDefault?.HardFloorTier is { Length: > 0 } floorId
            ? policy.Tiers.FirstOrDefault(tier => tier.Id == floorId)?.Rank ?? 0
            : 0;
        var minimum = reason == "semantic-conflict" ? 3 : 2;
        if (MechanicalProtocolFloor.IsMatch(taskText)) minimum = Math.Max(minimum, 3);
        if (CriticalProviderFallbackFloor.IsMatch(taskText)) minimum = 4;
        var floor = Math.Max(minimum, defaultFloor);
        cli = string.IsNullOrWhiteSpace(cli) ? previous?.Provider ?? "codex" : cli;
        if (!string.Equals(cli, "claude", StringComparison.OrdinalIgnoreCase)) cli = "codex";

        // Preserve a configured route only when the versioned policy proves it
        // clears the same floor. An unknown model has no such proof.
        var existingRank = policy.Tiers
            .Where(tier => string.Equals(tier.Model, existingModel, StringComparison.OrdinalIgnoreCase)
                && string.Equals(tier.ThinkingLevel, existingThinking, StringComparison.OrdinalIgnoreCase))
            .Select(tier => tier.Rank)
            .DefaultIfEmpty(-1)
            .Max();
        if (existingRank >= floor && cli == "codex")
            return new MechanicalFreshRunRoute(cli, existingModel, existingThinking, reason);

        var tier = policy.Tiers.First(tier => tier.Rank == floor);
        var route = cli == "claude" && policy.AnthropicRoutes.TryGetValue(tier.Id, out var anthropic)
            ? anthropic
            : (tier.Model, tier.ThinkingLevel);
        return new MechanicalFreshRunRoute(cli, route.Item1, route.Item2, reason);
    }
}
