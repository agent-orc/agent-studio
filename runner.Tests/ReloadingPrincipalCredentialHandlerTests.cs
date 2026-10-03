using AgentRunner;
using Xunit;

namespace Runner.Tests;

public sealed class ReloadingPrincipalCredentialHandlerTests
{
    [Fact]
    public async Task An_existing_client_uses_the_replaced_generation_on_its_next_request()
    {
        var current = "generation-one";
        var observed = new List<string?>();
        using var client = new HttpClient(new ReloadingPrincipalCredentialHandler(
            () => current,
            new RecordingHandler(request =>
            {
                observed.Add(request.Headers.Authorization?.Parameter);
                return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
            })));

        await client.GetAsync("https://task-server.example/first");
        current = "generation-two";
        await client.GetAsync("https://task-server.example/second");

        Assert.Equal(["generation-one", "generation-two"], observed);
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
