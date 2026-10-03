using System.Net.Http.Headers;

namespace AgentRunner;

/// <summary>Reads the protected bearer file for each request so active leases keep
/// their client while a staged file is atomically replaced.</summary>
internal sealed class ReloadingPrincipalCredentialHandler : DelegatingHandler
{
    private readonly Func<string> _readCredential;

    internal ReloadingPrincipalCredentialHandler(string path, HttpMessageHandler inner)
        : this(() => RunnerOptions.ReadAuthTokenFile(path), inner)
    {
    }

    internal ReloadingPrincipalCredentialHandler(Func<string> readCredential, HttpMessageHandler inner)
        : base(inner) => _readCredential = readCredential;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _readCredential());
        return base.SendAsync(request, cancellationToken);
    }
}
