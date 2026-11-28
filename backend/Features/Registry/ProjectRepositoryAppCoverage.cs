using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.TaskServer.Contracts.GitHubApp;

namespace AgentStudio.Registry;

public interface IProjectRepositoryAppCoverage
{
    Task<GitHubAppProvisioningCoverage> CheckAsync(GitHubRepositoryName repository, bool personalOwner, CancellationToken ct);
}

public sealed class ProjectRepositoryAppCoverage(
    GitHubAppBroker broker, IHttpClientFactory clients, IConfiguration configuration) : IProjectRepositoryAppCoverage
{
    public async Task<GitHubAppProvisioningCoverage> CheckAsync(
        GitHubRepositoryName repository, bool personalOwner, CancellationToken ct)
    {
        if (!TaskServerPlaneProxy.IsConfigured(configuration))
            return await broker.CheckProvisioningCoverageAsync(repository, personalOwner, ct);

        using var client = clients.CreateClient(TaskServerPlaneProxy.ClientName);
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        var query = $"repositoryUrl={Uri.EscapeDataString(repository.CloneUrl)}&personalOwner={personalOwner}";
        return await client.GetFromJsonAsync<GitHubAppProvisioningCoverage>(
            $"/api/v1/management/github-app/provisioning-coverage?{query}", deadline.Token)
            ?? throw new InvalidOperationException("Task Server returned no GitHub App coverage.");
    }
}
