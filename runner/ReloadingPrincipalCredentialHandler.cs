using System.Net.Http.Headers;
using AgentStudio.TaskServer.Contracts;

namespace AgentRunner;

/// <summary>Reads the protected bearer file for each request so active leases keep
/// their client while a staged file is atomically replaced.</summary>
internal sealed class ReloadingPrincipalCredentialHandler : DelegatingHandler
{
    private readonly Func<string> _readCredential;
    private readonly string? _path;

    internal ReloadingPrincipalCredentialHandler(string path, HttpMessageHandler inner)
        : this(() => RunnerOptions.ReadAuthTokenFile(path), inner)
    {
        _path = path;
    }

    internal ReloadingPrincipalCredentialHandler(Func<string> readCredential, HttpMessageHandler inner)
        : base(inner) => _readCredential = readCredential;

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _readCredential());
        if (_path is not null && PrincipalConsumerProofFile.ReadForBearerFile(_path) is { } proof)
        {
            request.Headers.Remove("X-Principal-Consumer-Id");
            request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Id", proof.ConsumerId);
            request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Proof", proof.Value);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
