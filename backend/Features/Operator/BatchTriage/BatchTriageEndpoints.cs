using AgentStudio.Security;

namespace AgentStudio.Operator;

/// <summary>
/// OP-4 (AGT-3462): one read for every batch gate flight, member, pending push
/// and queue stall across the projects the caller may read, with the next
/// safe member operation. Operator and viewer tokens may read it; a scoped
/// principal sees only its projects. The middleware defers the project check
/// to this handler, which keeps a project only when <c>project.read</c> holds
/// there.
/// </summary>
public static class BatchTriageEndpoints
{
    public static void MapOperatorBatchTriageEndpoints(this WebApplication app)
    {
        app.MapGet("/api/operator/batch-triage", (HttpContext context, BatchTriageReader reader,
                TaskScannerService scanner, AgentStudio.Registry.ProjectRegistry registry) =>
            Results.Ok(reader.Read(scanner.GetWatchPaths().Select(entry => entry.Name)
                .Where(project => ProjectAccessAuthorization.AllowsTasks(
                    context, [project], IamPermissions.ProjectRead, registry)))))
            .RequirePermission(IamPermissions.ProjectRead);
    }
}
