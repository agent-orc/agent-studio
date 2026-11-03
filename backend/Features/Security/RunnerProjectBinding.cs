using AgentStudio.Registry;

namespace AgentStudio.Security;

/// <summary>
/// SM-9: the projects a runner credential may claim, read and review. A request
/// without a runner credential is decided by the rest of the access model.
/// </summary>
public static class RunnerProjectBinding
{
    public const string AllProjects = "*";

    public static RunnerPrincipal? Runner(HttpContext context)
        => context.Items[AccessSecurityMiddleware.RunnerPrincipalItem] as RunnerPrincipal;

    public static bool Allows(RunnerPrincipal? runner, string? project, ProjectRegistry? registry = null)
        => runner is null
           || project is not null && runner.Projects.Any(bound =>
               bound == AllProjects || ProjectAccessAuthorization.SameProject(bound, project, registry));

    public static bool Allows(HttpContext context, string? project, ProjectRegistry? registry = null)
        => Allows(Runner(context), project, registry);
}
