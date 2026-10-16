using System.Net.Http.Headers;
using System.Net.Http.Json;
using Microsoft.Extensions.Options;

namespace AgentStudio.TaskServer;

public sealed record WorkspaceQuotaVerdict(bool Allowed, string? Message);

public interface IWorkspaceQuotaAdmission
{
    Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct);
}

/// <summary>The monolith owns quota meters; the Task Server asks it before assigning new work.</summary>
public sealed class HttpWorkspaceQuotaAdmission(
    IHttpClientFactory clients, IOptions<TaskServerOptions> options) : IWorkspaceQuotaAdmission
{
    public async Task<WorkspaceQuotaVerdict> AdmitAsync(string projectId, CancellationToken ct)
    {
        try
        {
            var baseUrl = options.Value.QuotaAdmissionBaseUrl.TrimEnd('/');
            using var request = new HttpRequestMessage(HttpMethod.Get,
                $"{baseUrl}/api/quota/admission/{Uri.EscapeDataString(projectId)}");
            if (!string.IsNullOrWhiteSpace(options.Value.QuotaAdmissionBearerToken))
                request.Headers.Authorization = new AuthenticationHeaderValue(
                    "Bearer", options.Value.QuotaAdmissionBearerToken);
            using var response = await clients.CreateClient(nameof(HttpWorkspaceQuotaAdmission))
                .SendAsync(request, ct);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadFromJsonAsync<WorkspaceQuotaVerdict>(cancellationToken: ct)
                ?? new(false, "Quota admission returned no verdict.");
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or InvalidOperationException)
        {
            return new(false, "Quota admission is temporarily unavailable. New work waits for the meter.");
        }
    }
}
