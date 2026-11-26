namespace AgentStudio.TaskServer.Contracts;

/// <summary>Claim-host facts sent to the installation's model routing authority.</summary>
public sealed record ModelRouteAdmissionRequest(SelectionRequest Request,
    IReadOnlyList<SelectionRoute> AcceptedRoutes, IReadOnlyList<string> UnavailableProviders);

public interface IModelRouteAdmission
{
    IReadOnlyList<ConfiguredModelRouteDto> ConfiguredRoutes => [];
    Task<SelectionResult> SelectAsync(ModelRouteAdmissionRequest request, CancellationToken ct);
}
