namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration point for the Studio P2 "security review, deployment,
/// publish, and wiki grading" bundle. Every route in this bundle is served
/// directly by <see cref="TaskServerStore"/> (already registered as a
/// singleton by <c>Program.cs</c>) or by the shared fenced studio-operation
/// dispatch it delegates to, so there is no bundle-specific service to
/// register. This stub exists so the integration step has a uniform
/// <c>AddStudioP2*Services()</c> call to make across every P2 bundle, even
/// the ones - like this one - that need no additional DI wiring.
/// </summary>
public static class StudioP2ComplianceServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2ComplianceServices(this IServiceCollection services) => services;
}
