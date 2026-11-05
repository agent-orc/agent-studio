using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed partial class TaskServerClient
{
    public async Task<OneShotJobWork?> ClaimOneShotJobAsync(string runnerId, CancellationToken ct)
        => await PostJsonAsync<OneShotJobClaimRequest, OneShotJobWork>(
            "/api/v1/one-shot-jobs/claim", new(runnerId, RunnerInstanceId), ct);

    public async Task<bool> CompleteOneShotJobAsync(OneShotJobCompletion completion, CancellationToken ct)
        => await PostJsonAsync<OneShotJobCompletion, bool>(
            "/api/v1/one-shot-jobs/complete", completion, ct);
}
