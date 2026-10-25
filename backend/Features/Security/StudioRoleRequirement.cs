using Microsoft.AspNetCore.Routing;

namespace AgentStudio.Security;

/// <summary>
/// The minimum Studio role a mapped route demands from a human session or API
/// token in the networked profile. <see cref="AccessSecurityMiddleware"/>
/// enforces it; the local loopback profile is not affected.
/// </summary>
public sealed record StudioRoleRequirement(string MinimumRole)
{
    public bool IsSatisfiedBy(string role) => Rank(role) >= Rank(MinimumRole);

    private static int Rank(string role) => role switch
    {
        StudioRoles.Owner => 3,
        StudioRoles.Operator => 2,
        StudioRoles.Contributor => 1,
        _ => 0,
    };
}

public static class StudioRoleRequirements
{
    public static TBuilder RequireStudioRole<TBuilder>(this TBuilder builder, string minimumRole)
        where TBuilder : IEndpointConventionBuilder
    {
        var guarded = builder.WithMetadata(new StudioRoleRequirement(minimumRole));
        return minimumRole == StudioRoles.Owner
            ? guarded.WithMetadata(new RequiredPermissionMetadata(IamPermissions.WorkspaceAdminister))
            : guarded;
    }
}

/// <summary>
/// Route families whose every endpoint must declare an Owner requirement. They
/// mirror Task Server routes that require the Owner role, so the monolith may
/// not offer a weaker path to the same installation change.
/// </summary>
public static class AccessSecurityRouteInventory
{
    public static readonly IReadOnlyList<string> OwnerRoutePrefixes =
    [
        // Local first-run setup steps: Git access and repository probes today, later steps too.
        "/api/first-run/",
    ];

    public static bool RequiresOwner(string pattern)
        => OwnerRoutePrefixes.Any(prefix => pattern.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    /// <summary>Route patterns inside an Owner family that lack the Owner requirement.</summary>
    public static IReadOnlyList<string> MissingOwnerRequirement(IEnumerable<Endpoint> endpoints)
        => endpoints.OfType<RouteEndpoint>()
            .Where(route => RequiresOwner(route.RoutePattern.RawText ?? string.Empty)
                && route.Metadata.GetMetadata<StudioRoleRequirement>()?.MinimumRole != StudioRoles.Owner)
            .Select(route => route.RoutePattern.RawText!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();
}
