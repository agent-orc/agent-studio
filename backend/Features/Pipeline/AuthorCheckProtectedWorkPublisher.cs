using System.Net.Http.Json;
using AgentStudio.Host;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Pipeline;

/// <summary>Publishes the bridge coordinator's demand while an author-check pilot is configured.</summary>
internal sealed class AuthorCheckProtectedWorkPublisher(
    IConfiguration configuration, IHttpClientFactory clients, SshGateHostProbe probe,
    GateSlotUsageTracker slots, PullGateStore pullGates, ShardedRemoteGateTransport shards,
    ILogger<AuthorCheckProtectedWorkPublisher> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!TaskServerPlaneProxy.IsConfigured(configuration)
            || configuration["RemoteAuthorCheck:ExecutorHostId"] is not { Length: > 0 } host)
            return;
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(8));
        do
        {
            try
            {
                // The pilot covers one physical bridge host. Unknown or additional routes
                // leave the Task Server without fresh facts.
                if (shards.AdditionalGateHosts.Count != 0) continue;
                var configuredBridge = configuration["RemoteGate:SshHost"];
                var active = 0;
                if (!string.IsNullOrWhiteSpace(configuredBridge))
                {
                    if (probe.Host != configuration["RemoteAuthorCheck:BridgeHost"]) continue;
                    using var budget = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                    budget.CancelAfter(TimeSpan.FromSeconds(6));
                    active = await probe.CountBridgeWorkersAsync(budget.Token);
                }
                var queue = Math.Max(slots.Dispatching, slots.Board().Waiting.Count) + pullGates.QueueObservation().Count;
                using var client = clients.CreateClient(TaskServerPlaneProxy.ClientName);
                using var response = await client.PostAsJsonAsync("/api/v1/author-checks/protected-work",
                    new AuthorCheckProtectedWork(host, Math.Max(active, slots.Snapshot().Busy), queue), stoppingToken);
                response.EnsureSuccessStatusCode();
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                logger.LogWarning("Author-check protected work facts unavailable ({Failure})", ex.GetType().Name);
            }
        } while (await timer.WaitForNextTickAsync(stoppingToken));
    }
}
