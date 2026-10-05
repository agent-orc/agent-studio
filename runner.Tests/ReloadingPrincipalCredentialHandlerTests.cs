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

    [Fact]
    public async Task File_backed_client_sends_only_its_protected_consumer_proof()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "generation-two");
            File.WriteAllText(path + ".consumer-proof", "runner-a\n" + new string('a', 64) + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path + ".consumer-proof", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            using var client = new HttpClient(new ReloadingPrincipalCredentialHandler(path,
                new RecordingHandler(request =>
                {
                    Assert.Equal("runner-a", request.Headers.GetValues("X-Principal-Consumer-Id").Single());
                    Assert.Equal(new string('a', 64), request.Headers.GetValues("X-Principal-Consumer-Proof").Single());
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                })));
            await client.GetAsync("https://task-server.example/workspaces");
        }
        finally
        {
            File.Delete(path + ".consumer-proof");
            File.Delete(path);
        }
    }

    private sealed class RecordingHandler(Func<HttpRequestMessage, HttpResponseMessage> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(respond(request));
    }
}
