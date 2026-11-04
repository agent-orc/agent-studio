using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using AgentStudio.TaskServer;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class TaskServerModelSelectionPosterTests
{
    [Fact]
    public async Task TransientFailureRetriesWithSameIdAndRunnerBearer()
    {
        var calls = 0;
        var token = "runner-test-" + Guid.NewGuid().ToString("N");
        var id = TaskServerModelSelectionPoster.NewId();
        using var client = new HttpClient(new Handler(async request =>
        {
            calls++;
            Assert.Equal("http://localhost/api/model-selection/events", request.RequestUri?.ToString());
            Assert.Equal(token, request.Headers.Authorization?.Parameter);
            var body = await request.Content!.ReadFromJsonAsync<JsonElement>();
            Assert.Equal(id, body.GetProperty("id").GetString());
            return new HttpResponseMessage(calls == 1 ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK);
        }));
        var poster = new TaskServerModelSelectionPoster(new ClientFactory(client),
            Options.Create(new TaskServerOptions
            {
                QuotaAdmissionBaseUrl = "http://localhost", ModelSelectionBearerToken = token,
            }), NullLogger<TaskServerModelSelectionPoster>.Instance);
        await poster.PostAsync(new { id, kind = "switched", consumer = "task-server-recovery" }, CancellationToken.None);
        Assert.Equal(2, calls);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class Handler(Func<HttpRequestMessage, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => send(request);
    }
}
