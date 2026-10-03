using System.Net.Http.Headers;

namespace AgentStudio.OrchestratorEngine;

internal sealed class EngineCredentialReloadHandler(string path, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", EngineOptions.ReadCredentialFile(path));
        return base.SendAsync(request, cancellationToken);
    }
}
