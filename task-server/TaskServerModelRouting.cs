using System.Globalization;
using AgentStudio.Cli;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

public sealed partial class TaskServerStore
{
    private async Task<SelectionResult?> SelectTaskRouteAsync(SqliteConnection connection, SqliteTransaction transaction,
        string taskId, string runnerId, string provider, string? model, string? thinking, CancellationToken ct,
        string consumer = SelectionConsumers.Coding, string? stepId = null, string? taskMode = null,
        bool codexCheckoutUnavailable = false)
    {
        if (_modelRouteAdmission is null) return null;
        string? project = null;
        string? key = null;
        string? taskType = null;
        string? title = null;
        string? prompt = null;
        long version = 0;
        await using (var command = Command(connection, """
            SELECT p.name, t.task_key, f.task_type, f.thinking_level, t.title, t.body, t.version
              FROM tasks t JOIN projects p ON p.id = t.project_id
              LEFT JOIN task_studio_fields f ON f.task_id = t.id WHERE t.id = $task;
            """, transaction, ("$task", taskId)))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            if (await reader.ReadAsync(ct))
            {
                project = reader.GetString(0);
                key = reader.GetString(1);
                taskType = reader.IsDBNull(2) ? null : reader.GetString(2);
                title = reader.GetString(4);
                prompt = reader.IsDBNull(5) ? null : reader.GetString(5);
                version = reader.GetInt64(6);
                if (consumer == SelectionConsumers.Coding && thinking is null && !reader.IsDBNull(3))
                    thinking = reader.GetString(3);
            }
        var accepted = new List<SelectionRoute>();
        await using (var command = Command(connection, """
            SELECT c.capability_key FROM runner_capabilities c JOIN runners r ON r.id = c.runner_id
             WHERE c.runner_id = $runner AND c.advertised_instance_id = r.instance_id
               AND c.advertised_status = 'ready' AND c.fresh_until > $now
               AND c.capability_key LIKE 'model-acceptance:%' ORDER BY c.capability_key;
            """, transaction, ("$runner", runnerId), ("$now", Iso(UtcNow))))
        await using (var reader = await command.ExecuteReaderAsync(ct))
            while (await reader.ReadAsync(ct))
            {
                var parts = reader.GetString(0).Split(':', 3);
                if (parts.Length == 3 && !await ModelCapacityActiveAsync(connection, transaction, runnerId,
                        parts[1], parts[2], UtcNow, ct))
                    accepted.Add(new(parts[1], parts[2], null));
            }
        var unavailable = new List<string>();
        foreach (var cli in new[] { "claude", "codex" })
        {
            var authReady = Convert.ToInt64(await ScalarAsync(connection, """
                SELECT count(*) FROM runner_capabilities c JOIN runners r ON r.id = c.runner_id
                 WHERE c.runner_id = $runner AND c.advertised_instance_id = r.instance_id
                   AND c.capability_key = $key AND c.advertised_status = 'ready'
                   AND c.fresh_until > $now AND c.health_state NOT IN ('draining', 'held', 'blocked');
                """, ct, transaction, ("$runner", runnerId),
                ("$key", CapabilityProtocol.ProviderAuthentication(cli)), ("$now", Iso(UtcNow))), CultureInfo.InvariantCulture) > 0;
            if ((cli == "codex" && codexCheckoutUnavailable) || !authReady
                || await ProviderUnauthorizedOnHostAsync(connection, transaction, runnerId, cli, ct))
                unavailable.Add(cli);
        }
        var previous = consumer == SelectionConsumers.Coding
            ? await ReadPreviousSessionAsync(connection, transaction, taskId, ct) : null;
        return await _modelRouteAdmission.SelectAsync(new(new SelectionRequest
        {
            Consumer = consumer, Project = project, StepId = stepId, TaskKey = key,
            TaskType = taskType, TaskMode = taskMode, TaskTitle = title, TaskPrompt = prompt, IsExplicitPin = true,
            AttemptId = $"{taskId}:{version}:{stepId}",
            Host = new(runnerId), Pins = new(provider, model, thinking),
            Continuation = previous is { CapturedSessionId.Length: > 0, Model.Length: > 0 }
                ? new(previous.Provider, previous.Model, previous.ThinkingLevel) : null,
            Required = new(SameClassOnly: SelectionConsumers.IsReview(consumer)),
        }, accepted, unavailable), ct);
    }

    private async Task<(ReviewSubjectDto? Subject, string? Reason)> RouteReviewAsync(
        SqliteConnection connection, SqliteTransaction transaction, ReviewSubjectDto subject, string runnerId,
        IReadOnlySet<string> capabilities, CancellationToken ct)
    {
        if (_modelRouteAdmission is null) return (subject, null);
        var commands = new List<ReviewCommandDto>();
        string? waitReason = null;
        foreach (var command in subject.Plan.Commands)
        {
            if (!ReviewCommandKinds.IsAgent(command.ExecutionKind)) { commands.Add(command); continue; }
            var selected = await SelectTaskRouteAsync(connection, transaction, subject.TaskId, runnerId,
                command.CliType ?? command.FileName, command.Model, command.ThinkingLevel, ct,
                command.Aspect == "deep-review" ? SelectionConsumers.ReviewDeep
                    : command.Aspect.Contains("grade", StringComparison.Ordinal) ? SelectionConsumers.ReviewGrade : SelectionConsumers.ReviewAspect,
                command.StepId, subject.Plan.TaskMode,
                codexCheckoutUnavailable: !ReviewCheckoutReadPolicy.Allows(command, "codex", capabilities));
            if (selected is not { ShouldLaunch: true })
            {
                waitReason ??= selected?.Reason ?? "Model route unavailable.";
                continue;
            }
            if (!ReviewCheckoutReadPolicy.Allows(command, selected.Route.CliType, capabilities))
            {
                waitReason ??= $"Host lacks {ReviewCapabilities.CodexCheckoutRead}.";
                continue;
            }
            commands.Add(command with { FileName = selected.Route.CliType, CliType = selected.Route.CliType,
                Model = selected.Route.Model, ThinkingLevel = selected.Route.ThinkingLevel, ModelSelection = selected });
        }
        return waitReason is not null ? (null, waitReason)
            : (subject with { Plan = subject.Plan with { Commands = commands } }, null);
    }
}
