using Microsoft.Extensions.DependencyInjection;

namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the P2 "analysis and drift" route bundle. All
/// state lives on <see cref="TaskServerStore"/> (already registered by
/// <c>Program.cs</c>) and the static rule/prompt catalogs are plain data, so
/// there is nothing to register here today. Kept as a no-op extension point
/// — rather than skipping registration entirely — so the integration step
/// can call it unconditionally alongside every other P2 group's service
/// registration, and so a future stateful helper (e.g. a dedicated drift
/// projector) has an obvious place to land without touching the
/// integration wiring again.
/// </summary>
public static class StudioP2AnalysisDriftServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2AnalysisDriftServices(this IServiceCollection services) => services;
}
