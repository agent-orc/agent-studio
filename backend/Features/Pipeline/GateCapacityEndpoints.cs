using AgentStudio.Security;

namespace AgentStudio.Pipeline;

public static class GateCapacityEndpoints
{
    public static void MapGateCapacityEndpoints(this WebApplication app)
    {
        app.MapGet("/api/system/gate-capacity", (HttpContext context,
            GateCapacitySettings settings, GateSlotUsageTracker slots, PullGateStore pullGates) =>
        {
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new
            {
                capacity = settings.Current(),
                source = settings.HasOverride ? "stored" : "configuration",
                use = Usage(slots, pullGates),
            });
        });

        app.MapPut("/api/system/gate-capacity", (HttpContext context,
            GateCapacity capacity, GateCapacitySettings settings, GateSlotUsageTracker slots,
            PullGateStore pullGates) =>
        {
            if (GateCapacityPolicy.Validate(capacity) is { } error)
                return Results.BadRequest(new { error });
            var actor = context.Items[AccessSecurityMiddleware.HumanPrincipalItem]
                is HumanPrincipal human ? human.User.Id : "local-operator";
            settings.Set(capacity, actor);
            slots.Configure(capacity.Slots);
            context.Response.Headers.CacheControl = "no-store";
            return Results.Ok(new { capacity, source = "stored", use = Usage(slots, pullGates) });
        }).RequireStudioRole(StudioRoles.Owner);
    }

    public static GateSlotUsage Usage(GateSlotUsageTracker slots, PullGateStore pullGates)
    {
        var hosts = pullGates.HealthyHosts();
        return hosts.Count > 0
            ? new GateSlotUsage(hosts.Sum(host => host.Busy), hosts.Sum(host => host.Slots))
            : slots.ReportedOrLocalSnapshot();
    }
}
