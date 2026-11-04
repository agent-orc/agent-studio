using System.Net;
using System.Text.Json;
using AgentRunner;
using Xunit;

namespace AgentRunner.Tests;

public sealed class TaskServerClientModelSelectionTests
{
    [Fact]
    public async Task IngestionRetriesWithTheSameIdAndStopsAfterSuccess()
    {
        var handler = new SelectionHandler();
        using var http = new HttpClient(handler) { BaseAddress = new Uri("https://studio.test/") };
        var token = "runner-test-" + Guid.NewGuid().ToString("N");
        using var client = new TaskServerClient(http, "runner-a", authToken: token);
        var id = TaskServerClient.NewModelSelectionId();

        Assert.True(await client.PostModelSelectionEventAsync(new
        {
            id, kind = "health-marked", consumer = "remote-coding-run", rule = "provider-rejection",
        }, CancellationToken.None));

        Assert.Equal(26, id.Length);
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(handler.Requests, request =>
        {
            Assert.Equal("/api/model-selection/events", request.Path);
            Assert.Equal(id, JsonDocument.Parse(request.Body).RootElement.GetProperty("id").GetString());
            Assert.Equal("Bearer " + token, request.Authorization);
        });
    }

    private sealed class SelectionHandler : HttpMessageHandler
    {
        public List<(string Path, string Body, string? Authorization)> Requests { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests.Add((request.RequestUri!.AbsolutePath,
                await request.Content!.ReadAsStringAsync(cancellationToken),
                request.Headers.Authorization?.ToString()));
            return new HttpResponseMessage(Requests.Count == 1
                ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }
    }
}
