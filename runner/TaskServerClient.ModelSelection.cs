using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

public sealed partial class TaskServerClient
{
    /// <summary>Send a host selection receipt to Studio with the runner Events scope.</summary>
    public async Task<bool> PostModelSelectionEventAsync(object entry, CancellationToken ct)
    {
        var baseUrl = _options?.PostMergeStateUrl ?? _options?.ServerUrl;
        var target = baseUrl is null
            ? new Uri(_http.BaseAddress!, "/api/model-selection/events")
            : new Uri(baseUrl.TrimEnd('/') + "/api/model-selection/events");
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                deadline.CancelAfter(TimeSpan.FromSeconds(3));
                using var response = await _http.PostAsJsonAsync(target, entry, deadline.Token);
                response.EnsureSuccessStatusCode();
                return true;
            }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
            {
                if (ct.IsCancellationRequested) throw;
                if (attempt == 2) return false;
                await Task.Delay(TimeSpan.FromMilliseconds(100 * (attempt + 1)), ct);
            }
        }
        return false;
    }

    public static string NewModelSelectionId() => ModelSelectionEventId.New();
}
