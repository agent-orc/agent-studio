using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal static class ModelCallEndpoints
{
    internal static void MapModelCallEndpoints(this WebApplication app)
    {
        var calls = app.MapGroup("/api/v1/projects/{project}/model-calls")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);

        calls.MapGet("/query", async (HttpContext http, string project, long fromMs, long untilMs,
            string? card, int? run, string? cursor, TaskServerStore tasks, ModelCallStore ledger, CancellationToken ct) =>
            await TaskServerEndpoints.InvokeAsync(async () =>
            {
                var scope = await ReadScopeAsync(http, tasks, project, ct);
                ModelCallStore.ValidateWindow(fromMs, untilMs, card, run, cursor);
                return await ledger.QueryAsync(scope, fromMs, untilMs, card, run, cursor, ct);
            }));

        calls.MapGet("/{id}", async (HttpContext http, string project, string id,
            TaskServerStore tasks, ModelCallStore ledger, CancellationToken ct) =>
            await TaskServerEndpoints.InvokeNullableAsync(async () =>
            {
                var scope = await ReadScopeAsync(http, tasks, project, ct);
                var detail = await ledger.DetailAsync(scope, id, ct);
                if (detail is null) return null;
                var task = await tasks.GetTaskAsync(scope.ProjectId, detail.TaskId, ct);
                return task is null ? detail : detail with
                {
                    SourceCardUrl = $"/api/v1/projects/{Uri.EscapeDataString(scope.ProjectId)}/tasks/{Uri.EscapeDataString(task.TaskId)}/artifacts"
                };
            }));

        // The management bearer is issued separately for unattended operator imports.
        // Browser traffic also carries the acting role through the trusted Studio edge.
        app.MapPost("/api/v1/management/model-calls/{project}/historical-import",
            async (HttpContext http, string project, HistoricalModelCallBatch batch, TaskServerStore tasks,
                ModelCallStore ledger, CancellationToken ct) =>
                await TaskServerEndpoints.InvokeAsync(async () =>
                {
                    var principal = http.TaskServerPrincipal();
                    if (principal?.Kind != TaskServerPrincipalKinds.Studio)
                        throw new StudioAuthorizationException("operator-required", "A Task Server management credential is required.");
                    tasks.RequireWritable();
                    var scope = await tasks.RequireProjectAsync(project, ct);
                    ModelCallStore.Validate(batch);
                    var ids = new Dictionary<string, string>(StringComparer.Ordinal);
                    foreach (var card in batch.Calls.Select(item => item.Card).Distinct(StringComparer.Ordinal))
                    {
                        var task = await tasks.GetTaskAsync(scope.ProjectId, card, ct);
                        if (task is null || task.TaskKey != card)
                            throw new ArgumentException($"Card {card} is absent from the import project.");
                        ids.Add(card, task.TaskId);
                    }
                    return await ledger.ImportAsync(scope, batch, ids,
                        http.Items["StudioActorId"] as string ?? principal.PrincipalId, ct);
                }))
            .RequireTaskServerScope(TaskServerScopes.Management)
            .RequireStudioRole(StudioUserRoles.Operator);
    }

    private static async Task<ProjectDto> ReadScopeAsync(
        HttpContext http, TaskServerStore tasks, string project, CancellationToken ct)
    {
        if (http.TaskServerPrincipal()?.Kind != TaskServerPrincipalKinds.Studio)
            throw new StudioAuthorizationException("studio-required", "A Studio principal is required.");
        var scope = await tasks.RequireProjectAsync(project, ct);
        if (StudioEndpoints.StudioSessionToken(http) is { } token)
        {
            var user = (await tasks.GetStudioAuthStatusAsync(token, ct)).User;
            if (user is null || user.Disabled || user.MustChangePassword ||
                user.Role != StudioUserRoles.Owner && !user.ProjectIds.Contains(scope.ProjectId, StringComparer.Ordinal))
                throw new StudioAuthorizationException("project-scope-denied", "Project access is required.");
        }
        return scope;
    }
}
