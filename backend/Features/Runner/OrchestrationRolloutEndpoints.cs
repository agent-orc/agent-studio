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
        // AGT-3218: the action to effect table the applier canary checks.
        api.MapGet("/applier-contract", () => Results.Ok(new
        {
            effects = EngineApplierContract.All,
            profiles = EngineApplierContract.ProfileActions,
        }));
        api.MapPost("/profiles/{profile}/switch", (HttpContext context, string profile,
            OrchestrationProfileSwitchRequest request, MonolithOrchestrationStore store,
            IConfiguration configuration, ILogger<OrchestrationRolloutStore> logger) =>
        {
            if (SecurityProfiles.IsPublicDemo(configuration)) return Results.StatusCode(403);
            if (SecurityProfiles.IsNetworked(configuration)
                && context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal
                    { User.Role: StudioRoles.Owner or StudioRoles.Operator })
                return Results.StatusCode(403);
            // AGT-3218: forcing the engine without a passed applier canary is an
            // owner's emergency override; the switch receipt records it.
            var forced = string.Equals(request.Target, "force-engine", StringComparison.OrdinalIgnoreCase);
            if (forced && SecurityProfiles.IsNetworked(configuration)
                && context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is not HumanPrincipal
                    { User.Role: StudioRoles.Owner })
                return Results.StatusCode(403);
            var actor = context.Items[AccessSecurityMiddleware.ServiceAccountPrincipalItem] is null
                && context.Items[AccessSecurityMiddleware.HumanPrincipalItem] is HumanPrincipal human
                    ? human.User.Username
                    : AccessSecurityMiddleware.ActorId(context, "local-operator");
            if (forced)
                logger.LogWarning("orchestration-profile-engine-forced profile={Profile} actor={Actor} reason={Reason}",
                    profile, actor, request.Reason);
            try { return Results.Ok(store.SwitchProfile(profile, request.Target, actor, request.Reason)); }
            catch (ArgumentException exception) { return Results.BadRequest(new { error = exception.Message }); }
            catch (InvalidOperationException exception) { return Results.Conflict(new { error = exception.Message }); }
        });
    }
}
