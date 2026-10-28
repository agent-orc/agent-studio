using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;

namespace AgentRunner.WorkContainers;

public sealed record WorkCredential(string IssuanceId, string Token, DateTime ExpiresAtUtc);

public interface IWorkContainerCredentialSource
{
    Task<WorkCredential> AcquireAsync(WorkContainerAttempt attempt, CancellationToken ct);
    Task<WorkCredential?> RenewAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct);
    Task<string> RevokeAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct);
    Task RecordDeletionAsync(WorkContainerAttempt attempt, WorkCredential current,
        string deletionStatus, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>Runner-principal client for the canary Task Server credential broker.</summary>
public sealed class TaskServerWorkCredentialSource(
    HttpClient http, string bearerFile, string runnerId, string instanceId, string subjectId)
    : IWorkContainerCredentialSource
{
    private static object Identity(WorkContainerAttempt attempt, string runnerId, string instanceId, string subjectId)
        => new
        {
            attempt.HostId, attempt.Role, SubjectId = subjectId, attempt.AttemptId,
            RunnerId = runnerId, InstanceId = instanceId, attempt.LeaseId,
            attempt.Fence, Epoch = attempt.AuthorityEpoch,
            StorageMode = OperatingSystem.IsWindows() ? "windows-acl-encrypted-volume" : "linux-tmpfs",
        };

    private HttpRequestMessage Request(WorkContainerAttempt attempt, string operation, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/runs/{Uri.EscapeDataString(attempt.AttemptId)}/credentials/{operation}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(bearerFile).Trim());
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task<T> SendAsync<T>(WorkContainerAttempt attempt, string operation, object payload, CancellationToken ct)
    {
        using var request = Request(attempt, operation, payload);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Attempt credential {operation} refused with HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Attempt credential broker returned an empty response.");
    }

    public async Task<WorkCredential> AcquireAsync(WorkContainerAttempt attempt, CancellationToken ct)
    {
        var identity = Identity(attempt, runnerId, instanceId, subjectId);
        var capability = await SendAsync<CapabilityReply>(attempt, "capability", identity, ct);
        var material = await SendAsync<Material>(attempt, "exchange", new
        {
            identity, capability.IssuanceId, capability.Capability,
        }, ct);
        return new WorkCredential(material.IssuanceId, material.Token, material.ExpiresAtUtc);
    }

    public async Task<WorkCredential?> RenewAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
    {
        using var request = Request(attempt, "renew", new
        {
            identity = Identity(attempt, runnerId, instanceId, subjectId), current.IssuanceId,
        });
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Conflict) return null; // The lease has not renewed yet.
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Attempt credential renewal refused with HTTP {(int)response.StatusCode}.");
        var material = await response.Content.ReadFromJsonAsync<Material>(cancellationToken: ct)
            ?? throw new InvalidDataException("Attempt credential renewal returned an empty response.");
        return new WorkCredential(material.IssuanceId, material.Token, material.ExpiresAtUtc);
    }

    public async Task<string> RevokeAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
    {
        var receipt = await SendAsync<Receipt>(attempt, "revoke", new
        {
            identity = Identity(attempt, runnerId, instanceId, subjectId), current.IssuanceId,
        }, ct);
        return receipt.RevocationStatus;
    }

    public async Task RecordDeletionAsync(WorkContainerAttempt attempt, WorkCredential current,
        string deletionStatus, CancellationToken ct)
    {
        await SendAsync<Receipt>(attempt, "deletion", new
        {
            identity = Identity(attempt, runnerId, instanceId, subjectId), current.IssuanceId, deletionStatus,
        }, ct);
    }

    private sealed record CapabilityReply(string IssuanceId, string Capability, DateTime ExpiresAtUtc);
    private sealed record Material(string IssuanceId, string Token, DateTime ExpiresAtUtc);
    private sealed record Receipt(string IssuanceId, string RevocationStatus);
}
