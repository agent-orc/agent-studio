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
        var serviceToken = "quota-service-" + Guid.NewGuid().ToString("N");
        using var client = new HttpClient(new ReplyHandler(request =>
        {
            Assert.Equal("http://localhost/api/quota/admission/project-1", request.RequestUri!.ToString());
            Assert.Equal(serviceToken, request.Headers.Authorization?.Parameter);
            return
            new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new WorkspaceQuotaVerdict(false, "Workspace quota reached.")),
            };
        }));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost", QuotaAdmissionBearerToken = serviceToken }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.False(verdict.Allowed);
        Assert.Contains("quota", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Unavailable_meter_reports_warning_without_stopping_claims()
    {
        using var client = new HttpClient(new ReplyHandler(_ =>
            new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var admission = new HttpWorkspaceQuotaAdmission(new ClientFactory(client),
            Options.Create(new TaskServerOptions { QuotaAdmissionBaseUrl = "http://localhost" }));
        var verdict = await admission.AdmitAsync("project-1", default);
        Assert.True(verdict.Allowed);
        Assert.True(verdict.MeterUnavailable);
        Assert.Contains("unavailable", verdict.Message!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Default_configuration_never_assumes_the_dev_backend_port()
        => Assert.Null(new TaskServerOptions().QuotaAdmissionBaseUrl);

    [Fact]
    public async Task Missing_backend_configuration_is_visible_and_does_not_stop_claims()
    {
        var verdict = await new UnconfiguredWorkspaceQuotaAdmission().AdmitAsync("project-1", default);
        Assert.True(verdict.Allowed);
        Assert.True(verdict.MeterUnavailable);
        Assert.Contains("not configured", verdict.Message!, StringComparison.OrdinalIgnoreCase);
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
