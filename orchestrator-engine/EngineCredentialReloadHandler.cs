using System.Net.Http.Headers;
using ProofStore = AgentStudio.TaskServer.Contracts.PrincipalConsumerProofFile;

namespace AgentStudio.OrchestratorEngine;

internal sealed class EngineCredentialReloadHandler(string path, HttpMessageHandler inner) : DelegatingHandler(inner)
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        request.Headers.Authorization = new AuthenticationHeaderValue(
            "Bearer", EngineOptions.ReadCredentialFile(path));
        if (ProofStore.ReadForBearerFile(path) is { } proof)
        {
            request.Headers.Remove("X-Principal-Consumer-Id");
            request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Id", proof.ConsumerId);
            request.Headers.TryAddWithoutValidation("X-Principal-Consumer-Proof", proof.Value);
        }
        return base.SendAsync(request, cancellationToken);
    }
}
