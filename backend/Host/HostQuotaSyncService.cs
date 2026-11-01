using System.Net.Http.Json;
using AgentStudio.Cli;
using AgentStudio.Security;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Host;

/// <summary>Copies parsed host quota from the standalone Task Server into Studio's quota cache.</summary>
public sealed class HostQuotaSyncService(
    IConfiguration configuration, IHttpClientFactory clients, QuotaService quota,
    ILogger<HostQuotaSyncService> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (SecurityProfiles.IsLocal(configuration) || !TaskServerPlaneProxy.IsConfigured(configuration)) return;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var client = clients.CreateClient(TaskServerPlaneProxy.ClientName);
                var readings = await client.GetFromJsonAsync<HostQuotaReadingDto[]>(
                    "/api/v1/quota/readings", stoppingToken);
                foreach (var reading in readings ?? []) quota.IngestHostReading(reading);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception exception)
            {
                logger.LogWarning("Host quota sync failed: {ErrorType}", exception.GetType().Name);
            }
            try { await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
        }
    }
}
