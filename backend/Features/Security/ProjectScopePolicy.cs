namespace AgentStudio.Security;

/// <summary>How a request addresses projects, derived from its route before any query parameter.</summary>
public enum ProjectRouteScope
{
    /// <summary>The route names no project; collection handlers filter their payloads.</summary>
    Unaddressed,
    /// <summary>A path segment or the addressed task names the project(s).</summary>
    Addressed,
    /// <summary>The target set travels in the body; the handler enforces membership per item.</summary>
    DeferredToHandler,
    /// <summary>A workspace-wide surface that no scoped account may use.</summary>
    WorkspaceWide,
}

/// <summary>
/// The projects a route addresses. For <see cref="ProjectRouteScope.Addressed"/>
/// a blank entry is an unresolved project and fails closed.
/// </summary>
public sealed record ProjectRouteAddress(ProjectRouteScope Scope, IReadOnlyList<string?> Projects)
{
    public static readonly ProjectRouteAddress Unaddressed = new(ProjectRouteScope.Unaddressed, []);
    public static readonly ProjectRouteAddress DeferredToHandler = new(ProjectRouteScope.DeferredToHandler, []);
    public static readonly ProjectRouteAddress WorkspaceWide = new(ProjectRouteScope.WorkspaceWide, []);

    public static ProjectRouteAddress Addressed(params string?[] projects)
        => new(ProjectRouteScope.Addressed, projects);
}

/// <summary>
/// Pure project-scope decision for a scoped (non-unrestricted) account. The
/// route decides first; a <c>?project=</c> query value may only narrow that
/// decision and never replaces a project the route or task already names.
/// </summary>
public static class ProjectScopePolicy
{
    public static bool Allows(ProjectRouteAddress route, string? queryProject, Func<string, bool> isMember)
    {
        switch (route.Scope)
        {
            case ProjectRouteScope.WorkspaceWide:
                return false;
            case ProjectRouteScope.Addressed:
                if (route.Projects.Count == 0) return false;
                foreach (var project in route.Projects)
                    if (string.IsNullOrWhiteSpace(project) || !isMember(project)) return false;
                break;
        }
        return string.IsNullOrWhiteSpace(queryProject) || isMember(queryProject);
    }
}
