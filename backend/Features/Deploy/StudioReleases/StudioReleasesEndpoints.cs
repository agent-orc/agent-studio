using AgentStudio.Registry;
using AgentStudio.Security;

namespace AgentStudio.Deploy;

public static class StudioReleasesEndpoints
{
    public static void MapStudioReleasesEndpoints(this WebApplication app)
    {
        var group = app.MapGroup("/api/projects/{projectName}/studio-releases");
        group.MapGet("", (string projectName, HttpContext context, StudioReleasesService releases, ProjectRegistry projects) =>
        {
            if (!Allowed(context, projectName, projects)) return Results.Forbid();
            if (releases.Source is null) return Results.Ok(new StudioReleasesView(projectName, "unknown", DateTime.UtcNow,
                [], [], new Dictionary<string, StudioCardLiveState>(), "Studio source-project discovery is warming up."));
            if (!releases.IsSource(projectName)) return Results.NotFound();
            return Results.Ok(releases.View ?? new StudioReleasesView(releases.Source!, "unknown", DateTime.UtcNow,
                [], [], new Dictionary<string, StudioCardLiveState>(), "Release facts are warming up."));
        });
        group.MapGet("/cards/{key}", (string projectName, string key, HttpContext context,
            StudioReleasesService releases, ProjectRegistry projects) =>
        {
            if (!Allowed(context, projectName, projects)) return Results.Forbid();
            if (releases.Source is null) return Results.Ok(new StudioCardLiveState("unknown"));
            if (!releases.IsSource(projectName)) return Results.Ok(new StudioCardLiveState("not-applicable"));
            return Results.Ok(releases.View?.CardStates.GetValueOrDefault(key) ?? new StudioCardLiveState("unknown"));
        });
    }

    private static bool Allowed(HttpContext context, string project, ProjectRegistry projects)
        => context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal human
            || ProjectAccessAuthorization.Allows(human.User, project, projects);

}
