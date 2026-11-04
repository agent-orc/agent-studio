using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

/// <summary>Posts Task Server selection receipts to the monolith after a committed transition.</summary>
public sealed class TaskServerModelSelectionPoster(
    IHttpClientFactory clients, IOptions<TaskServerOptions> options, ILogger<TaskServerModelSelectionPoster> logger)
{
    public async Task PostAsync(object entry, CancellationToken ct)
    {
        var baseUrl = options.Value.QuotaAdmissionBaseUrl?.TrimEnd('/');
        var token = options.Value.ModelSelectionBearerToken;
        if (string.IsNullOrWhiteSpace(baseUrl) || string.IsNullOrWhiteSpace(token)) return;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, baseUrl + "/api/model-selection/events")
                {
                    Content = JsonContent.Create(entry),
                };
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                using var response = await clients.CreateClient(nameof(TaskServerModelSelectionPoster)).SendAsync(request, ct);
                response.EnsureSuccessStatusCode();
                return;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException or UriFormatException)
            {
                if (attempt == 2) logger.LogWarning(ex, "model_selection_ingestion_failed after bounded retry");
                else await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
    }

    public static string NewId() => ModelSelectionEventId.New();
}
