using AgentStudio.Security;

namespace AgentStudio.Runner;

public sealed record OrchestrationProfileSwitchRequest(string Target, string Reason);

public static class OrchestrationRolloutEndpoints
{
    public static void MapOrchestrationRolloutEndpoints(this WebApplication app)
    {
        var api = app.MapGroup("/api/orchestration/rollout");
        api.MapGet("/profiles", (OrchestrationRolloutStore store) => Results.Ok(store.GetProfiles()));
        api.MapGet("/profiles/{profile}/mismatches", (string profile, OrchestrationRolloutStore store) =>
            OrchestrationRolloutStore.Profiles.Contains(profile, StringComparer.Ordinal)
                ? Results.Ok(store.Mismatches(profile)) : Results.NotFound());
        api.MapGet("/switch-receipts", (OrchestrationRolloutStore store) =>
            Results.Ok(store.SwitchReceipts()));
        api.MapPost("/profiles/{profile}/switch", (HttpContext context, string profile,
            OrchestrationProfileSwitchRequest request, MonolithOrchestrationStore store,
            IConfiguration configuration) =>
        {
            if (SecurityProfiles.IsPublicDemo(configuration)) return Results.StatusCode(403);
            if (SecurityProfiles.IsNetworked(configuration)
                && context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal
                    { User.Role: StudioRoles.Owner or StudioRoles.Operator })
                return Results.StatusCode(403);
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                ? human.User.Username : "local-operator";
            try { return Results.Ok(store.SwitchProfile(profile, request.Target, actor, request.Reason)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
    }
}
