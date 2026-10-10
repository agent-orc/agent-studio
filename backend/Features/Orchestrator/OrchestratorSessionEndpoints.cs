using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestrator;

public static class OrchestratorSessionEndpoints
{
    public static void MapOrchestratorSessionEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/orchestrator/sessions");

        group.MapGet("", async (
            OrchestratorSessionRegistry registry,
            OrchestratorTurnService turns,
            IOrchestratorChatPersistence persistence,
            CancellationToken ct) =>
        {
            if (!persistence.IsCentralTaskServerStore && registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            var statuses = turns.SnapshotStatuses()
                .GroupBy(item => item.ContextKey, StringComparer.Ordinal)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(item => item.Status == OrchestratorTurnService.StatusActive ? 0 : 1).First(),
                    StringComparer.Ordinal);
            // Hidden (task archived) and operator-archived contexts are listed
            // too: the Studio session manager shows them in its Archived group.
            var contexts = await persistence.ListContextsAsync(includeHidden: true, ct);
            var sessions = contexts.Select(session => new
            {
                session.ContextKey,
                session.Kind,
                ProjectId = session.ProjectName,
                session.TaskKey,
                session.CreatedAt,
                session.UpdatedAt,
                session.Model,
                session.CumulativeInputTokens,
                session.CumulativeOutputTokens,
                session.CumulativeCacheReadTokens,
                session.CumulativeCacheCreationTokens,
                Calls = session.TurnCount,
                session.TurnCount,
                LastUsedAt = session.UpdatedAt,
                Summary = session.Summary,
                HiddenAt = session.HiddenAt,
                session.ArchivedAt,
                session.WorkbenchKey,
                RuntimeStatus = statuses.GetValueOrDefault(session.ContextKey)?.Status ?? "idle",
                QueuePosition = statuses.GetValueOrDefault(session.ContextKey)?.QueuePosition ?? 0
            });
            return Results.Ok(new
            {
                sessions
            });
        });

        group.MapGet("/{**contextKey}", (string contextKey, OrchestratorSessionRegistry registry) =>
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out var parsedGet))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            // Always continue with the parsed canonical Value: the forgiving
            // parse accepts once-more-percent-encoded keys (AGT-2165), and the
            // raw form must never become a separate registry identity.
            return Results.Ok(registry.GetOrCreate(parsedGet.Value));
        });

        static IResult PostTurn(string contextKey, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns)
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out var parsed))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            if (string.IsNullOrWhiteSpace(request.Prompt))
                return Results.BadRequest(new { error = "Prompt is required." });

            return Results.Accepted($"/api/orchestrator/sessions/{parsed.Value}", turns.Enqueue(parsed.Value, request));
        }

        static IResult Park(string contextKey, OrchestratorSessionRegistry registry, OrchestratorTurnService turns)
        {
            if (registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            if (!OrchestratorContextKey.TryParse(contextKey, out var parsedPark))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            return Results.Ok(turns.Park(parsedPark.Value));
        }

        static async Task<IResult> SetArchived(
            string contextKey,
            SetOrchestratorContextArchivedRequest request,
            OrchestratorTurnService turns,
            IOrchestratorChatPersistence persistence,
            AgentStudio.Host.OrchestratorContextHubBroadcaster broadcaster,
            CancellationToken ct)
        {
            if (!OrchestratorContextKey.TryParse(contextKey, out var parsed) || parsed.ProjectId is null)
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            var runtimeStatus = turns.SnapshotStatuses()
                .Where(item => string.Equals(item.ContextKey, parsed.Value, StringComparison.Ordinal))
                .Select(item => item.Status)
                .FirstOrDefault();
            if (request.Archived && !OrchestratorContextArchivePolicy.CanArchive(runtimeStatus))
                return Results.Conflict(new { error = "A running or queued chat cannot be archived." });

            try
            {
                var context = await persistence.SetArchivedAsync(parsed, request.Archived, ct);
                await broadcaster.ContextChangedAsync(context.ProjectName, context.ContextKey, context.UpdatedAt, ct);
                return Results.Ok(context);
            }
            catch (KeyNotFoundException exception)
            {
                return Results.NotFound(new { error = exception.Message });
            }
        }

        group.MapPost("/project:{projectId}/archive", (string projectId, SetOrchestratorContextArchivedRequest request, OrchestratorTurnService turns, IOrchestratorChatPersistence persistence, AgentStudio.Host.OrchestratorContextHubBroadcaster broadcaster, CancellationToken ct) =>
            SetArchived($"project:{projectId}", request, turns, persistence, broadcaster, ct));
        group.MapPost("/workbench:{projectId}/{workbenchKey}/archive", (string projectId, string workbenchKey, SetOrchestratorContextArchivedRequest request, OrchestratorTurnService turns, IOrchestratorChatPersistence persistence, AgentStudio.Host.OrchestratorContextHubBroadcaster broadcaster, CancellationToken ct) =>
            SetArchived($"workbench:{projectId}/{workbenchKey}", request, turns, persistence, broadcaster, ct));
        group.MapPost("/task:{projectId}/{taskKey}/archive", (string projectId, string taskKey, SetOrchestratorContextArchivedRequest request, OrchestratorTurnService turns, IOrchestratorChatPersistence persistence, AgentStudio.Host.OrchestratorContextHubBroadcaster broadcaster, CancellationToken ct) =>
            SetArchived($"task:{projectId}/{taskKey}", request, turns, persistence, broadcaster, ct));

        group.MapPost("/global/turns", (OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn("global", request, registry, turns))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);
        group.MapPost("/project:{projectId}/turns", (string projectId, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn($"project:{projectId}", request, registry, turns))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);
        group.MapPost("/workbench:{projectId}/{workbenchKey}/turns", (string projectId, string workbenchKey, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn($"workbench:{projectId}/{workbenchKey}", request, registry, turns))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);
        group.MapPost("/task:{projectId}/{taskKey}/turns", (string projectId, string taskKey, OrchestratorTurnRequest request, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            PostTurn($"task:{projectId}/{taskKey}", request, registry, turns))
            .WithPublicDemoExecutionDenied(ExecutionAdmissionPath.Chat);

        group.MapPost("/global/park", (OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park("global", registry, turns));
        group.MapPost("/project:{projectId}/park", (string projectId, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park($"project:{projectId}", registry, turns));
        group.MapPost("/workbench:{projectId}/{workbenchKey}/park", (string projectId, string workbenchKey, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park($"workbench:{projectId}/{workbenchKey}", registry, turns));
        group.MapPost("/task:{projectId}/{taskKey}/park", (string projectId, string taskKey, OrchestratorSessionRegistry registry, OrchestratorTurnService turns) =>
            Park($"task:{projectId}/{taskKey}", registry, turns));
    }
}
