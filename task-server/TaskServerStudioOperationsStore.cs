using System.Text.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Data.Sqlite;

namespace AgentStudio.TaskServer;

/// <summary>
/// The generic ledger behind every P2 "operations and insight" action that
/// must run through a fenced Runner dispatch (analysis, drift, security,
/// design, proposals, publish, deployment, skill readiness, wiki grading,
/// and admin prompt review). Dispatch reuses the existing pull-based task
/// lifecycle: a row here creates a normal task in the ready lane, and a
/// Runner claims it through the existing <c>/runners/{runnerId}/claims</c>,
/// lease, event, and artifact contracts, exactly as a human-started task
/// would. This store never spawns a process itself and never becomes a
/// second workflow engine; it only records that a dispatch happened and
/// reads back the durable task/run/artifact state that already exists.
/// Most operations are project-scoped; a few instance-wide actions (admin
/// prompt review, title/prompt text helpers) pass a null project.
/// </summary>
public sealed partial class TaskServerStore
{
    public async Task<StudioOperationDto> DispatchStudioOperationAsync(
        string projectIdentity,
        string domain,
        string kind,
        string title,
        string promptBody,
        object request,
        string actorId,
        CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        var task = await CreateTaskAsync(
            project.ProjectId,
            new CreateTaskRequest(title, promptBody, StudioTaskLanes.Ready),
            actorId,
            ct);
        return await InsertStudioOperationAsync(
            project.ProjectId, domain, kind, title, task.TaskId, StudioOperationStatuses.Dispatched,
            request, null, actorId, ct);
    }

    /// <summary>
    /// Records an operation whose result is already known because the
    /// computation was pure and deterministic (no checkout or CLI call was
    /// needed), such as the code-pattern-drift rule evaluation, or because it
    /// is instance-wide rather than project-scoped (<paramref name="projectIdentity"/> is null).
    /// </summary>
    public async Task<StudioOperationDto> RecordCompletedStudioOperationAsync(
        string? projectIdentity, string domain, string kind, string title, object request, object result,
        string actorId, CancellationToken ct)
    {
        var projectId = projectIdentity is null ? null : (await RequireProjectAsync(projectIdentity, ct)).ProjectId;
        return await InsertStudioOperationAsync(
            projectId, domain, kind, title, null, StudioOperationStatuses.Completed, request, result, actorId, ct);
    }

    private async Task<StudioOperationDto> InsertStudioOperationAsync(
        string? projectId, string domain, string kind, string title, string? taskId, string status,
        object request, object? result, string actorId, CancellationToken ct)
    {
        var id = $"sop_{Guid.NewGuid():N}";
        var now = UtcNow;
        var resultJson = result is null ? null : JsonSerializer.Serialize(result);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection, """
            INSERT INTO studio_operations(
                id, project_id, domain, kind, title, task_id, status, request_json, result_json,
                created_at, updated_at, created_by)
            VALUES ($id, $project, $domain, $kind, $title, $task, $status, $request, $result, $now, $now, $actor);
            """, ct,
            ("$id", id), ("$project", projectId), ("$domain", domain), ("$kind", kind), ("$title", title),
            ("$task", taskId), ("$status", status), ("$request", JsonSerializer.Serialize(request)),
            ("$result", resultJson), ("$now", Iso(now)), ("$actor", actorId));
        return new StudioOperationDto(id, projectId, domain, kind, title, taskId, status, resultJson, now, now, actorId);
    }

    public async Task<StudioOperationListResponse> ListStudioOperationsAsync(
        string? projectIdentity, string domain, string? kind, CancellationToken ct)
    {
        var projectId = projectIdentity is null ? null : (await RequireProjectAsync(projectIdentity, ct)).ProjectId;
        await using var connection = await OpenReadyAsync(ct);
        var sql = "SELECT " + StudioOperationColumns + " FROM studio_operations WHERE domain = $domain" +
                  (projectId is null ? " AND project_id IS NULL" : " AND project_id = $project") +
                  (kind is null ? string.Empty : " AND kind = $kind") + " ORDER BY created_at DESC;";
        await using var command = Command(connection, sql, ("$project", projectId), ("$domain", domain), ("$kind", kind));
        await using var reader = await command.ExecuteReaderAsync(ct);
        var result = new List<StudioOperationDto>();
        while (await reader.ReadAsync(ct)) result.Add(ReadStudioOperation(reader));
        return new StudioOperationListResponse(result);
    }

    public async Task<StudioOperationDto> GetStudioOperationAsync(string? projectIdentity, string domain, string operationId, CancellationToken ct)
    {
        var projectId = projectIdentity is null ? null : (await RequireProjectAsync(projectIdentity, ct)).ProjectId;
        await using var connection = await OpenReadyAsync(ct);
        var sql = "SELECT " + StudioOperationColumns + " FROM studio_operations WHERE domain = $domain AND id = $id" +
                  (projectId is null ? " AND project_id IS NULL" : " AND project_id = $project") + ";";
        await using var command = Command(connection, sql, ("$project", projectId), ("$domain", domain), ("$id", operationId));
        await using var reader = await command.ExecuteReaderAsync(ct);
        if (!await reader.ReadAsync(ct)) throw new KeyNotFoundException("Operation was not found.");
        return ReadStudioOperation(reader);
    }

    public async Task<StudioOperationDto> DecideStudioOperationAsync(
        string? projectIdentity, string domain, string operationId, string decision, string actorId, CancellationToken ct)
    {
        RequireWritable();
        var operation = await GetStudioOperationAsync(projectIdentity, domain, operationId, ct);
        var now = UtcNow;
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection,
            "UPDATE studio_operations SET status = $status, updated_at = $now WHERE id = $id;",
            ct, ("$status", decision), ("$now", Iso(now)), ("$id", operationId));
        return operation with { Status = decision, UpdatedAt = now };
    }

    public async Task DeleteStudioOperationAsync(string projectIdentity, string domain, string operationId, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection,
            "DELETE FROM studio_operations WHERE project_id = $project AND domain = $domain AND id = $id;",
            ct, ("$project", project.ProjectId), ("$domain", domain), ("$id", operationId));
    }

    public async Task DeleteAllStudioOperationsAsync(string projectIdentity, string domain, CancellationToken ct)
    {
        RequireWritable();
        var project = await RequireProjectAsync(projectIdentity, ct);
        await using var connection = await OpenReadyAsync(ct);
        await ExecuteAsync(connection,
            "DELETE FROM studio_operations WHERE project_id = $project AND domain = $domain;",
            ct, ("$project", project.ProjectId), ("$domain", domain));
    }

    /// <summary>
    /// Bounded side effect run after a run's owning mutation
    /// (<see cref="CompleteRunAsync"/>) has already committed: if the
    /// completed run belongs to a dispatched studio operation, fold the
    /// run's terminal outcome into that operation's durable status. This
    /// never re-opens or retries the completion itself.
    /// </summary>
    public async Task TryMaterializeStudioOperationForRunAsync(RunDto run, CancellationToken ct)
    {
        await using var connection = await OpenReadyAsync(ct);
        await using var command = Command(connection,
            "SELECT id FROM studio_operations WHERE task_id = $task AND status = $status;",
            ("$task", run.TaskId), ("$status", StudioOperationStatuses.Dispatched));
        var operationId = Convert.ToString(await command.ExecuteScalarAsync(ct));
        if (string.IsNullOrWhiteSpace(operationId)) return;

        var artifacts = await ListArtifactsAsync(run.RunId, ct);
        var status = string.Equals(run.Status, "succeeded", StringComparison.OrdinalIgnoreCase)
            ? StudioOperationStatuses.Completed
            : StudioOperationStatuses.Failed;
        var resultJson = JsonSerializer.Serialize(new
        {
            runId = run.RunId,
            runStatus = run.Status,
            artifacts = artifacts.Select(artifact => new { artifact.ArtifactId, artifact.Name, artifact.MediaType }),
        });
        await ExecuteAsync(connection, """
            UPDATE studio_operations SET status = $status, result_json = $result, updated_at = $now WHERE id = $id;
            """, ct, ("$status", status), ("$result", resultJson), ("$now", Iso(UtcNow)), ("$id", operationId));
    }

    private const string StudioOperationColumns =
        "id, project_id, domain, kind, title, task_id, status, result_json, created_at, updated_at, created_by";

    private static StudioOperationDto ReadStudioOperation(SqliteDataReader reader) => new(
        reader.GetString(0), reader.IsDBNull(1) ? null : reader.GetString(1), reader.GetString(2), reader.GetString(3),
        reader.GetString(4), reader.IsDBNull(5) ? null : reader.GetString(5), reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        Parse(reader.GetString(8)), Parse(reader.GetString(9)), reader.GetString(10));
}
