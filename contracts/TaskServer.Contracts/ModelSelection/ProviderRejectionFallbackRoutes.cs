namespace AgentStudio.TaskServer.Contracts;

/// <summary>Ordered routes for a fresh attempt after a host-scoped model refusal.</summary>
public static class ProviderRejectionFallbackRoutes
{
    public static IReadOnlyList<ProviderModelFallback> Candidates(
        string provider, bool providerUnauthorized,
        ProviderModelFallback? sibling, ProviderModelFallback? equivalent,
        ProviderModelFallback? policyEquivalent)
    {
        var routes = new List<ProviderModelFallback>();
        if (!providerUnauthorized && sibling is not null) routes.Add(sibling);
        if (equivalent is not null) routes.Add(equivalent);
        if (policyEquivalent is not null) routes.Add(policyEquivalent);
        return routes.Where(route =>
                !providerUnauthorized || !string.Equals(route.CliType, provider, StringComparison.OrdinalIgnoreCase))
            .DistinctBy(route => (route.CliType.ToLowerInvariant(), route.To.ToLowerInvariant(),
                route.ThinkingLevel?.ToLowerInvariant()))
            .ToArray();
    }
}
