using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.TaskServer;

internal static class ReleaseSuiteVerdictEndpoints
{
    internal static void MapReleaseSuiteVerdictEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/v1/release-suite-verdicts")
            .RequireTaskServerScope(TaskServerScopes.TasksRead);
        group.MapGet("/{project}/{sha}/{digest}", async (string project, string sha, string digest,
            TaskServerStore store, CancellationToken ct) =>
        {
            var verdict = await store.GetReleaseSuiteVerdictAsync(project, sha, digest, ct);
            return verdict is null ? Results.NotFound() : Results.Ok(verdict);
        });
        group.MapPut("/", async (ReleaseSuiteVerdict verdict, TaskServerStore store, CancellationToken ct)
            => await TaskServerEndpoints.InvokeAsync(() => store.PutReleaseSuiteVerdictAsync(verdict, ct)))
            .RequireTaskServerScope(TaskServerScopes.RunsWrite);
    }
}
