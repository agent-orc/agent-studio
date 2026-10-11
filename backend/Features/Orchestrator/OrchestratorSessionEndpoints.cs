using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Orchestrator;

public static class OrchestratorSessionEndpoints
{
    private static Dictionary<string, OrchestratorContextRuntimeStatus> RuntimeStatuses(OrchestratorTurnService turns)
        => turns.SnapshotStatuses()
            .GroupBy(item => item.ContextKey, StringComparer.Ordinal)
            .ToDictionary(
                group => group.Key,
                group => group.OrderBy(item => item.Status == OrchestratorTurnService.StatusActive ? 0 : 1).First(),
                StringComparer.Ordinal);

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

            var statuses = RuntimeStatuses(turns);
            // Hidden (task in 7-archive) and operator-archived contexts are
            // both listed; the Studio renders them in its Archived group.
            var contexts = await persistence.ListContextsAsync(includeHidden: true, ct);
            var sessions = contexts.Select(session =>
                OrchestratorSessionResponse.From(session, statuses.GetValueOrDefault(session.ContextKey)));
            return Results.Ok(new
            {
                sessions
            });
        });

        group.MapPost("/archive", async (
            SetOrchestratorContextArchivedRequest? request,
            OrchestratorSessionRegistry registry,
            OrchestratorTurnService turns,
            IOrchestratorChatPersistence persistence,
            HttpContext http,
            CancellationToken ct) =>
        {
            if (!persistence.IsCentralTaskServerStore && registry.SessionsRoot == null)
                return Results.Problem("TaskRepository is not configured.", statusCode: StatusCodes.Status500InternalServerError);

            // Boundary validation: a canonical, archivable context key.
            if (request is null
                || !OrchestratorContextKey.TryParse(request.ContextKey, out var parsed)
                || !OrchestratorContextArchivePolicy.IsArchivableKind(parsed.Kind))
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });

            // Pure decision over the observed runtime status.
            var status = RuntimeStatuses(turns).GetValueOrDefault(parsed.Value);
            if (!OrchestratorContextArchivePolicy.CanSetArchived(request.Archived, status?.Status))
                return Results.Conflict(new
                {
                    error = "orchestrator-context-busy",
                    message = "A context with an active or queued turn cannot be archived.",
                    runtimeStatus = status?.Status,
                });

            OrchestratorContextDto? updated;
            try
            {
                updated = await persistence.SetArchivedAsync(parsed.Value, request.Archived, ct);
            }
            catch (ArgumentException)
            {
                return Results.BadRequest(new { error = "Invalid orchestrator context key." });
            }
            if (updated is null)
                return Results.NotFound(new { error = "Orchestrator context was not found." });

            // The central store broadcasts from its persistence boundary,
            // exactly as it does for appended turns.
            if (!persistence.IsCentralTaskServerStore
                && http.RequestServices.GetService<AgentStudio.Host.OrchestratorContextHubBroadcaster>() is { } broadcaster)
            {
                await broadcaster.ContextChangedAsync(updated.ProjectName, updated.ContextKey, updated.UpdatedAt, ct);
            }
            return Results.Ok(OrchestratorSessionResponse.From(updated, status));
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

/// <summary>
/// One row of <c>GET /api/orchestrator/sessions</c> and the body returned by
/// <c>POST /api/orchestrator/sessions/archive</c>. Property names are the
/// established wire contract; <see cref="ArchivedAt"/> and
/// <see cref="TurnCount"/> are additive.
/// </summary>
public sealed record OrchestratorSessionResponse(
    string ContextKey,
    string Kind,
    string ProjectId,
    string? TaskKey,
    DateTime CreatedAt,
    DateTime UpdatedAt,
    string? Model,
    long CumulativeInputTokens,
    long CumulativeOutputTokens,
    long CumulativeCacheReadTokens,
    long CumulativeCacheCreationTokens,
    long Calls,
    DateTime LastUsedAt,
    string Summary,
    DateTime? HiddenAt,
    string RuntimeStatus,
    int QueuePosition,
    DateTime? ArchivedAt,
    long TurnCount)
{
    public static OrchestratorSessionResponse From(
        OrchestratorContextDto session,
        OrchestratorContextRuntimeStatus? status)
        => new(
            session.ContextKey,
            session.Kind,
            session.ProjectName,
            session.TaskKey,
            session.CreatedAt,
            session.UpdatedAt,
            session.Model,
            session.CumulativeInputTokens,
            session.CumulativeOutputTokens,
            session.CumulativeCacheReadTokens,
            session.CumulativeCacheCreationTokens,
            session.TurnCount,
            session.UpdatedAt,
            session.Summary,
            session.HiddenAt,
            status?.Status ?? "idle",
            status?.QueuePosition ?? 0,
            session.ArchivedAt,
            session.TurnCount);
}
