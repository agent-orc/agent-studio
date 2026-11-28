using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.TaskServer.Contracts;

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
public sealed class TaskServerWorkCredentialSource : IWorkContainerCredentialSource
{
    private readonly HttpClient _http;
    private readonly string? _bearerFile;
    private readonly string _runnerId;
    private readonly string _instanceId;
    private readonly string _subjectId;

    public TaskServerWorkCredentialSource(
        HttpClient http, string bearerFile, string runnerId, string instanceId, string subjectId)
        : this(http, runnerId, instanceId, subjectId)
        => _bearerFile = bearerFile;

    // The daemon client already supplies the enrolled principal, rotation proof and TLS settings.
    internal TaskServerWorkCredentialSource(HttpClient http, string runnerId, string instanceId, string subjectId)
        => (_http, _runnerId, _instanceId, _subjectId) = (http, runnerId, instanceId, subjectId);

    private object Identity(WorkContainerAttempt attempt)
        => new
        {
            attempt.HostId, attempt.Role, SubjectId = _subjectId, attempt.AttemptId,
            RunnerId = _runnerId, InstanceId = _instanceId, attempt.LeaseId, attempt.RepositorySource,
            attempt.Fence, Epoch = attempt.AuthorityEpoch,
            StorageMode = OperatingSystem.IsWindows() ? "windows-acl-encrypted-volume" : "linux-tmpfs",
        };

    private HttpRequestMessage Request(WorkContainerAttempt attempt, string operation, object payload)
    {
        var request = new HttpRequestMessage(HttpMethod.Post,
            $"api/v1/runs/{Uri.EscapeDataString(attempt.AttemptId)}/credentials/{operation}");
        if (_bearerFile is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", File.ReadAllText(_bearerFile).Trim());
        request.Content = JsonContent.Create(payload);
        return request;
    }

    private async Task<T> SendAsync<T>(WorkContainerAttempt attempt, string operation, object payload, CancellationToken ct)
    {
        using var request = Request(attempt, operation, payload);
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Attempt credential {operation} refused with HTTP {(int)response.StatusCode}.");
        return await response.Content.ReadFromJsonAsync<T>(cancellationToken: ct)
            ?? throw new InvalidDataException("Attempt credential broker returned an empty response.");
    }

    public async Task<WorkCredential> AcquireAsync(WorkContainerAttempt attempt, CancellationToken ct)
    {
        var identity = Identity(attempt);
        var capability = await SendAsync<CapabilityReply>(attempt, "capability", identity, ct);
        try
        {
            var material = await SendAsync<Material>(attempt, "exchange", new
            {
                identity, capability.IssuanceId, capability.Capability,
            }, ct);
            return ReadMaterial(material, capability.IssuanceId);
        }
        catch
        {
            // The exchange response can be lost after the server wrote the root.
            try { await CloseAsync(attempt, capability.IssuanceId, CancellationToken.None); }
            catch (Exception) { /* The completion sweep can close it. */ }
            throw;
        }
    }

    public async Task<WorkCredential?> RenewAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
    {
        using var request = Request(attempt, "renew", new
        {
            identity = Identity(attempt), current.IssuanceId,
        });
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (response.StatusCode == HttpStatusCode.Conflict
            && (await response.Content.ReadFromJsonAsync<ApiError>(cancellationToken: ct))?.Code == "lease-not-renewed")
            return null;
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Attempt credential renewal refused with HTTP {(int)response.StatusCode}.");
        var material = await response.Content.ReadFromJsonAsync<Material>(cancellationToken: ct)
            ?? throw new InvalidDataException("Attempt credential renewal returned an empty response.");
        return ReadMaterial(material, current.IssuanceId);
    }

    public async Task<string> RevokeAsync(WorkContainerAttempt attempt, WorkCredential current, CancellationToken ct)
    {
        var receipt = await CloseAsync(attempt, current.IssuanceId, ct);
        return receipt.RevocationStatus;
    }

    private Task<Receipt> CloseAsync(WorkContainerAttempt attempt, string issuanceId, CancellationToken ct)
        => SendAsync<Receipt>(attempt, "revoke", new { identity = Identity(attempt), issuanceId }, ct);

    private static WorkCredential ReadMaterial(Material material, string issuanceId)
        => material.IssuanceId == issuanceId && !string.IsNullOrWhiteSpace(material.Token)
            ? new WorkCredential(issuanceId, material.Token, material.ExpiresAtUtc)
            : throw new InvalidDataException("Attempt credential material did not match the issuance.");

    public async Task RecordDeletionAsync(WorkContainerAttempt attempt, WorkCredential current,
        string deletionStatus, CancellationToken ct)
    {
        await SendAsync<Receipt>(attempt, "deletion", new
        {
            identity = Identity(attempt), current.IssuanceId, deletionStatus,
        }, ct);
    }

    private sealed record CapabilityReply(string IssuanceId, string Capability, DateTime ExpiresAtUtc);
    private sealed record Material(string IssuanceId, string Token, DateTime ExpiresAtUtc);
    private sealed record Receipt(string IssuanceId, string RevocationStatus);
}
