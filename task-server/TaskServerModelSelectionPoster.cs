using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
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

    public static string NewId()
    {
        const string alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
        Span<byte> data = stackalloc byte[16];
        var milliseconds = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        for (var i = 5; i >= 0; i--) { data[i] = (byte)milliseconds; milliseconds >>= 8; }
        RandomNumberGenerator.Fill(data[6..]);
        Span<char> chars = stackalloc char[26];
        var bit = 0;
        for (var i = 0; i < chars.Length; i++)
        {
            var value = 0;
            for (var b = 0; b < 5; b++)
            {
                var position = bit++ - 2;
                value = (value << 1) | (position < 0 ? 0 : (data[position / 8] >> (7 - position % 8)) & 1);
            }
            chars[i] = alphabet[value];
        }
        return new string(chars);
    }
}
