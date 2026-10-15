using System.Net;
using System.Text;
using System.Text.Json;
using AgentStudio.Host;
using AgentStudio.TaskServer.Contracts;
using AgentStudio.TestSupport;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class PublicDemoStandaloneProxyProgramTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(), "studio-public-demo-proxy-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task Program_denies_unsafe_standalone_task_server_proxy_requests_before_forwarding()
    {
        using var factory = PublicDemoHostFixture.CreateFactory(
            _workspace,
            new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://127.0.0.1:1",
            });
        using var client = factory.CreateClient();

        var proxyRoute = Assert.Single(factory.Services.GetServices<EndpointDataSource>()
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>(), endpoint =>
                endpoint.Metadata.GetMetadata<TaskServerPlaneProxyEndpoint>() is not null
                && endpoint.Metadata.GetMetadata<ExecutionRouteMetadata>() is not null);
        Assert.Equal(ExecutionAdmissionPath.PostStep,
            proxyRoute.Metadata.GetMetadata<ExecutionRouteMetadata>()!.Path);
        Assert.Contains("POST", proxyRoute.Metadata.GetMetadata<IHttpMethodMetadata>()!.HttpMethods);

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            "/api/v1/runners/forged-runner/claims")
        {
            Content = new StringContent("{}", Encoding.UTF8, "application/json"),
        };
        using var response = await client.SendAsync(request);

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        using var body = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.Equal(
            ExecutionAdmissionPolicy.ExecutionDisabledCode,
            body.RootElement.GetProperty("error").GetString());
        Assert.Equal(
            ExecutionAdmissionPolicy.PublicDemoProfile,
            body.RootElement.GetProperty("profile").GetString());
    }

    public void Dispose() => TestTempRoot.TryDelete(_workspace);
}
