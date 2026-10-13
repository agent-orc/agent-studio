using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.TaskServer.Contracts;

namespace AgentStudio.Management;

public interface IProviderRenewalJournal
{
    Task<ProviderRenewalReceiptDto> BeginAsync(string hostId, string method, string actorId,
        string? idempotencyKey, CancellationToken ct);
    Task<ProviderRenewalReceiptDto> AdvanceAsync(string operationId, string step,
        CancellationToken ct);
    Task<ProviderRenewalReceiptDto?> GetAsync(string operationId, CancellationToken ct);
    Task<ProviderRenewalReceiptDto> TryVerifyAsync(ProviderRenewalReceiptDto receipt, CancellationToken ct);
}

/// <summary>The Task Server owns intent and receipts. This client never sends a provider secret.</summary>
public sealed class ProviderRenewalTaskServerJournal(IHttpClientFactory clients) : IProviderRenewalJournal
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public async Task<ProviderRenewalReceiptDto> BeginAsync(string hostId, string method,
        string actorId, string? idempotencyKey, CancellationToken ct)
    {
        var records = await Client().GetFromJsonAsync<List<CredentialRegistryRecordDto>>(
            "/api/v1/management/credentials", Json, ct) ?? [];
        var kind = method switch
        {
            "R1" => "claude_oauth_token",
            "R2" => "claude_native_login",
            "R3" => "codex_chatgpt_login",
            "R8" => "provider_api_key",
            _ => throw new ArgumentException("Unsupported sign-in method.", nameof(method)),
        };
        var matches = records.Where(record => record.HostId == hostId && record.Kind == kind).ToArray();
        if (matches.Length != 1)
            throw new InvalidOperationException("One current host credential binding is required for renewal.");
        var record = matches[0];
        var request = new BeginProviderRenewalRequest(record.InstallationId, hostId,
            record.CredentialId, record.Generation, method,
            idempotencyKey ?? "signin-" + Guid.NewGuid().ToString("N"),
            DateTime.UtcNow.AddMinutes(15));
        using var response = await Client().PostAsJsonAsync(
            "/api/v1/management/provider-renewals", request, Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProviderRenewalReceiptDto>(Json, ct))!;
    }

    public async Task<ProviderRenewalReceiptDto> AdvanceAsync(string operationId, string step,
        CancellationToken ct)
    {
        var request = new AdvanceProviderRenewalRequest(step, null, null, [], false, []);
        using var response = await Client().PostAsJsonAsync(
            $"/api/v1/management/provider-renewals/{Uri.EscapeDataString(operationId)}/steps", request, Json, ct);
        response.EnsureSuccessStatusCode();
        return (await response.Content.ReadFromJsonAsync<ProviderRenewalReceiptDto>(Json, ct))!;
    }

    public async Task<ProviderRenewalReceiptDto?> GetAsync(string operationId, CancellationToken ct)
        => await Client().GetFromJsonAsync<ProviderRenewalReceiptDto>(
            $"/api/v1/management/provider-renewals/{Uri.EscapeDataString(operationId)}", Json, ct);

    public async Task<ProviderRenewalReceiptDto> TryVerifyAsync(
        ProviderRenewalReceiptDto receipt, CancellationToken ct)
    {
        if (receipt.Step is not ("installed" or "recovery-required")) return receipt;
        var snapshots = await Client().GetFromJsonAsync<List<RunnerCapabilitySnapshotDto>>(
            "/api/v1/management/remote-hosts", Json, ct) ?? [];
        var provider = receipt.Method == "R3" ? "codex" : "claude";
        var candidates = snapshots.Where(snapshot => snapshot.HostId == receipt.HostId &&
            snapshot.Status == "active").Select(snapshot => new {
                Snapshot = snapshot,
                Auth = snapshot.Capabilities.FirstOrDefault(capability =>
                    capability.Key == CapabilityProtocol.ProviderAuthentication(provider)),
            }).Where(item => item.Auth is not null && item.Auth.IsFresh &&
                item.Auth.CredentialGeneration is not null &&
                item.Auth.CredentialGeneration != receipt.ExpectedGeneration &&
                item.Auth.LastRealSuccessAt >= receipt.UpdatedAt &&
                item.Auth.HealthOutcome == "healthy").ToArray();
        var coding = candidates.FirstOrDefault(item => item.Snapshot.Capabilities.Any(capability =>
            capability.Key == CapabilityProtocol.CodingExecutor));
        var review = candidates.FirstOrDefault(item => item.Snapshot.Capabilities.Any(capability =>
            capability.Key == CapabilityProtocol.ReviewExecutor));
        if (coding is null || review is null || coding.Snapshot.RunnerId == review.Snapshot.RunnerId ||
            coding.Auth!.CredentialGeneration != review.Auth!.CredentialGeneration ||
            coding.Auth.EffectiveSource != review.Auth.EffectiveSource)
            return receipt;
        var generation = coding.Auth.CredentialGeneration;
        var source = coding.Auth.EffectiveSource;
        if (source != (receipt.Method is "R2" or "R3" ? "native-cli-store" : "environment-file"))
            return receipt;
        using var response = await Client().PostAsJsonAsync(
            $"/api/v1/management/provider-renewals/{Uri.EscapeDataString(receipt.OperationId)}/steps",
            new AdvanceProviderRenewalRequest("verified", generation, source,
                ["agent-runner.service", "agent-runner-review.service"], true, []), Json, ct);
        response.EnsureSuccessStatusCode();
        var verified = (await response.Content.ReadFromJsonAsync<ProviderRenewalReceiptDto>(Json, ct))!;
        var retired = await AdvanceAsync(verified.OperationId, "retired", ct);
        return await AdvanceAsync(retired.OperationId, "complete", ct);
    }

    private HttpClient Client() => clients.CreateClient(TaskServerPlaneProxy.ClientName);
}
