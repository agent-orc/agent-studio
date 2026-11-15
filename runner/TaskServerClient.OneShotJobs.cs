using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed partial class TaskServerClient
{
    public async Task<OneShotJobWork?> ClaimOneShotJobAsync(string runnerId, CancellationToken ct)
        => await SendJsonAsync<OneShotJobClaimRequest, OneShotJobWork>(HttpMethod.Post,
            "/api/v1/one-shot-jobs/claim", new(runnerId, RunnerInstanceId), ct,
            allowNoContentResponse: true);

    public async Task<bool> CompleteOneShotJobAsync(OneShotJobCompletion completion, CancellationToken ct)
        => await PostJsonAsync<OneShotJobCompletion, bool>(
            "/api/v1/one-shot-jobs/complete", completion, ct);
}
