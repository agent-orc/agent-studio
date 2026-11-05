using System.Net.Http.Headers;
using System.Net.Http.Json;
using AgentStudio.Operations.Contracts;

namespace AgentStudio.Security;

/// <summary>Where the kill switch revokes the operator's Operations Server principals (security model SM-2).</summary>
public sealed record OperationsServerRevocationOptions(string? Url, string? CredentialFile)
{
    public const string Section = "Security:OperatorKillSwitch:OperationsServer";

    public bool Configured => !string.IsNullOrWhiteSpace(Url) && !string.IsNullOrWhiteSpace(CredentialFile);

    public static OperationsServerRevocationOptions From(IConfiguration configuration)
        => new(configuration[Section + ":Url"], configuration[Section + ":CredentialFile"]);
}

public sealed record OperatorRevocationOutcome(bool Ok, IReadOnlyList<string> RevokedPrincipalIds, int CancelledOperations, string? Error);

public interface IOperatorPrincipalRevocation
{
    bool Configured { get; }

    /// <summary>Revokes every operator principal (<paramref name="all"/>) or those of the named operator agents.</summary>
    Task<OperatorRevocationOutcome> RevokeAsync(IReadOnlyList<string> operatorAgentIds, bool all, string correlationId, CancellationToken ct);
}

/// <summary>
/// Calls the Operations Server's revocation route with the control plane's
/// own service principal (scope <c>principals.revoke</c>). HTTPS only, no
/// redirects; the credential is read from its file on every call so a rotated
/// file takes effect without a restart.
/// </summary>
public sealed class OperationsServerOperatorRevocation(HttpClient client, OperationsServerRevocationOptions options) : IOperatorPrincipalRevocation
{
    public const string HttpClientName = "operator-kill-switch-operations";

    public bool Configured => options.Configured;

    public async Task<OperatorRevocationOutcome> RevokeAsync(IReadOnlyList<string> operatorAgentIds, bool all, string correlationId,
        CancellationToken ct)
    {
        if (!options.Configured) return new OperatorRevocationOutcome(false, [], 0, "not-configured");
        if (!Uri.TryCreate(options.Url!.TrimEnd('/') + "/", UriKind.Absolute, out var baseUri) || baseUri.Scheme != Uri.UriSchemeHttps
            || !string.IsNullOrEmpty(baseUri.UserInfo))
            return new OperatorRevocationOutcome(false, [], 0, "invalid-url");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, new Uri(baseUri, OperatorPrincipalRevocation.Path));
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", (await File.ReadAllTextAsync(options.CredentialFile!, ct)).Trim());
            request.Headers.Add(OperationsProtocol.Header, OperationsProtocol.Version.ToString());
            request.Content = JsonContent.Create(new OperatorPrincipalRevocationRequest([.. operatorAgentIds], all, correlationId),
                options: OperationsProtocol.Json);
            using var response = await client.SendAsync(request, ct);
            if (!response.IsSuccessStatusCode)
                return new OperatorRevocationOutcome(false, [], 0, $"http-{(int)response.StatusCode}");
            var result = await response.Content.ReadFromJsonAsync<OperatorPrincipalRevocationResponse>(OperationsProtocol.Json, ct);
            return result is null
                ? new OperatorRevocationOutcome(false, [], 0, "empty-response")
                : new OperatorRevocationOutcome(true, result.RevokedPrincipalIds ?? [], result.CancelledAttempts, null);
        }
        catch (Exception exception) when (exception is HttpRequestException or IOException or UnauthorizedAccessException
                                              or System.Text.Json.JsonException
                                          || (exception is TaskCanceledException && !ct.IsCancellationRequested))
        {
            return new OperatorRevocationOutcome(false, [], 0, exception.GetType().Name);
        }
    }
}
