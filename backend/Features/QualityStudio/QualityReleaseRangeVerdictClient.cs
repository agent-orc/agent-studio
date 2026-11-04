using System.Net.Http.Headers;
using System.Text.Json;
using AgentStudio.Secrets;

namespace AgentStudio.QualityStudio;

/// <summary>Reads stored range evidence only. This client never invokes the review route.</summary>
public sealed class QualityReleaseRangeVerdictClient(
    QualityStudioConnectionStore connections, ISecretProvider secrets, IHttpClientFactory clients)
{
    public string? Read(string repositoryId, string baseSha, string headSha)
    {
        var connection = connections.Get();
        if (connection is null) return null;
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            var token = secrets.GetForPlatformAsync(
                SecretReference.Parse(QualityStudioEndpoints.TokenReference),
                "quality-release-range", timeout.Token).GetAwaiter().GetResult();
            var url = connection.InternalBaseUrl + "/api/v1/repos/" + Uri.EscapeDataString(repositoryId)
                + "/change-reviews?base=" + Uri.EscapeDataString(baseSha.ToLowerInvariant())
                + "&head=" + Uri.EscapeDataString(headSha.ToLowerInvariant());
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);
            request.Headers.Add("X-Client-Id", connection.ServiceClientId);
            using var response = clients.CreateClient("quality-studio-integration").Send(request, timeout.Token);
            if (!response.IsSuccessStatusCode) return null;
            using var document = JsonDocument.Parse(response.Content.ReadAsStream(timeout.Token));
            return StoredVerdict(document.RootElement);
        }
        catch (Exception ex) when (ex is HttpRequestException or OperationCanceledException or JsonException
            or SecretNotFoundException or IOException)
        {
            return null;
        }
    }

    public static string? StoredVerdict(JsonElement range)
    {
        if (!range.TryGetProperty("records", out var records) || records.ValueKind != JsonValueKind.Array)
            return null;
        foreach (var record in records.EnumerateArray())
        {
            if (!record.TryGetProperty("decisive", out var decisive) || decisive.ValueKind != JsonValueKind.True
                || !record.TryGetProperty("document", out var document)
                || !document.TryGetProperty("verdict", out var verdict)) continue;
            var value = verdict.GetString();
            if (value is "pass" or "concerns" or "block") return value;
        }
        return null;
    }
}
