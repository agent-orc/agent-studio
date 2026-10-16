using System.Net;
using System.Net.Http.Json;
using AgentStudio.TaskServer;
using Microsoft.Extensions.Options;
using Xunit;

namespace TaskServer.Tests;

public sealed class WorkspaceQuotaAdmissionTests
{
    [Fact]
    public async Task Remote_refusal_is_returned_to_claim_authority()
    {
        using var client = new HttpClient(new ReplyHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new WorkspaceQuotaVerdict(false, "Workspace quota reached.")),
            }));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost" }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.False(verdict.Allowed);
        Assert.Contains("quota", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_meter_fails_closed()
    {
        using var client = new HttpClient(new ReplyHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost" }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.False(verdict.Allowed);
        Assert.Contains("unavailable", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    private sealed class ClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class ReplyHandler(Func<HttpRequestMessage, HttpResponseMessage> reply) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(reply(request));
    }
}
