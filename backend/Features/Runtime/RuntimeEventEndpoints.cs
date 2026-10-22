

namespace AgentStudio.Runtime;

/// <summary>
/// Read API for the project-screen Product Runtime Observability panel.
/// Backed by <see cref="ProductRuntimeEventStore"/>: disk is the source of
/// truth; the store keeps an in-memory projection per (workspace, project)
/// pair so the UI's poll never triggers a full disk rescan.
/// </summary>
public static class RuntimeEventEndpoints
{
    public static void MapRuntimeEventEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/runtime");

        group.MapGet("/task-detail-latency", (AgentStudio.Tasks.TaskDetailLatencyMetrics metrics) =>
            Results.Ok(metrics.Snapshot()));

        group.MapGet("/{project}/events", (
            string project,
            IConfiguration config,
            ProductRuntimeEventStore store,
            bool? refresh,
            CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(project))
                return Results.BadRequest(new { error = "project required" });
            var workspace = config["TaskRepository"];
            if (string.IsNullOrWhiteSpace(workspace))
                return Results.Ok(EmptyResponse(project));

            if (refresh == true)
                store.InvalidateProjection(workspace!, project);

            var snapshot = store.GetSnapshot(workspace!, project, ct);
            return Results.Ok(new RuntimeEventListResponse(
                Project: project,
                Events: snapshot.Events,
                Warnings: snapshot.Warnings.Select(w => new RuntimeEventParseWarningDto(
                    w.SourcePath, w.LineNumber, w.Reason, w.RawLine)).ToArray()));
        });
    }

    private static RuntimeEventListResponse EmptyResponse(string project) => new(
        project,
        Array.Empty<ProductRuntimeEvent>(),
        Array.Empty<RuntimeEventParseWarningDto>());
}

/// <summary>
/// Wire response for <c>GET /api/runtime/{project}/events</c>.
/// </summary>
public sealed record RuntimeEventListResponse(
    string Project,
    IReadOnlyList<ProductRuntimeEvent> Events,
    IReadOnlyList<RuntimeEventParseWarningDto> Warnings);

/// <summary>
/// Surface-able parse-warning DTO. Mirrors <see cref="RuntimeEventParseWarning"/>
/// but lives in the endpoint layer so the model record stays a pure type.
/// </summary>
public sealed record RuntimeEventParseWarningDto(
    string SourcePath,
    int LineNumber,
    string Reason,
    string RawLine);
