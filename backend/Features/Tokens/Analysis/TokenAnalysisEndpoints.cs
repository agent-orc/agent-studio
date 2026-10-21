using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Tokens;

/// <summary>
/// Read surface of the card-centric token analysis (AGT-3267):
/// <list type="bullet">
///   <item><c>GET /api/workspace/tokens/analysis?days=&amp;project=&amp;workspaceId=</c>:
///   every visible card with usage in range, its usage cube, project usage
///   outside cards, and the quota windows with tokens per quota point.</item>
///   <item><c>GET /api/projects/{project}/token-usage/analysis/cards/{jobId}</c>:
///   the drill-down timeline of one card.</item>
/// </list>
/// Visibility, not enforcement: nothing here influences scheduling.
/// </summary>
public static class TokenAnalysisEndpoints
{
    public static void MapTokenAnalysisEndpoints(this WebApplication app)
    {
        app.MapGet("/api/workspace/tokens/analysis", (HttpContext context, int? days, string? project,
            string? workspaceId, WorkspaceRegistry workspaces, ProjectRegistry registry,
            TokenAnalysisService analysis) =>
        {
            var workspace = string.IsNullOrWhiteSpace(workspaceId)
                ? workspaces.List().FirstOrDefault(item => item.IsDefault) ?? workspaces.List().FirstOrDefault()
                : workspaces.Find(workspaceId);
            if (workspace is null) return Results.NotFound(new { error = "Unknown workspace." });
            var human = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] as HumanPrincipal;
            var visible = registry.List()
                .Where(record => !record.Archived
                    && string.Equals(record.WorkspaceId, workspace.Id, StringComparison.OrdinalIgnoreCase))
                .Where(record => human is null || ProjectAccessAuthorization.Allows(human.User, record.Id, registry))
                .ToList();
            if (human is not null && !ProjectAccessAuthorization.HasUnrestrictedProjectAccess(human.User)
                && visible.Count == 0)
                return Results.StatusCode(StatusCodes.Status403Forbidden);
            if (!string.IsNullOrWhiteSpace(project))
            {
                visible = visible.Where(record => Names(record, project!)).ToList();
                if (visible.Count == 0) return Results.NotFound(new { error = $"Unknown project '{project}'" });
            }
            var sources = visible
                .Select(record => new TokenAnalysisProjectSource(record.Id, record.DisplayName, record.StorageLocation))
                .ToList();
            return Results.Ok(analysis.Analyze(sources, days));
        });

        app.MapGet("/api/projects/{projectName}/token-usage/analysis/cards/{jobId}", (string projectName,
            string jobId, TaskScannerService scanner, ProjectRegistry registry, TokenAnalysisService analysis) =>
        {
            if (string.IsNullOrWhiteSpace(projectName))
                return Results.BadRequest(new { error = "project required" });
            if (string.IsNullOrWhiteSpace(jobId))
                return Results.BadRequest(new { error = "job id required" });
            var record = registry.List().FirstOrDefault(candidate => !candidate.Archived && Names(candidate, projectName));
            var entry = scanner.GetWatchPaths().FirstOrDefault(candidate =>
                string.Equals(candidate.Name, record?.DisplayName ?? projectName, StringComparison.OrdinalIgnoreCase));
            if (record is null && entry is null)
                return Results.NotFound(new { error = $"Unknown project '{projectName}'" });
            var source = record is not null
                ? new TokenAnalysisProjectSource(record.Id, record.DisplayName, record.StorageLocation)
                : new TokenAnalysisProjectSource(null, entry!.Name, entry.Path);
            var detail = analysis.CardDetail(source, jobId);
            return detail is null
                ? Results.NotFound(new { error = $"No token activity recorded for card '{jobId}'." })
                : Results.Ok(detail);
        });
    }

    private static bool Names(ProjectRecord record, string name)
        => string.Equals(record.Id, name, StringComparison.OrdinalIgnoreCase)
           || string.Equals(record.DisplayName, name, StringComparison.OrdinalIgnoreCase);
}
