namespace AgentStudio.TaskServer;

/// <summary>
/// DI registration hook for the Studio P2 "design council, proposals, visual
/// evidence, skill readiness, project snapshot" bundle. Registers
/// <see cref="ProposalsCompletionProjector"/> so the shared studio-operation
/// completion route (<c>StudioOperationsEndpoints.cs</c>) materializes
/// proposal rows once a generate or refine-feedback operation succeeds. This
/// stub exists so the integration step can call it unconditionally for every
/// group.
/// </summary>
public static class StudioP2DesignServiceCollectionExtensions
{
    public static IServiceCollection AddStudioP2DesignServices(this IServiceCollection services)
        => services.AddSingleton<IStudioOperationCompletionProjector, ProposalsCompletionProjector>();
}
