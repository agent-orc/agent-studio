using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace AgentStudio.Tests;

public sealed class TaskServerPlaneProxyTests
{
    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("http://task-server.test", true)]
    [InlineData("https://task-server.test/control", true)]
    public void Only_an_explicit_absolute_HTTP_or_HTTPS_URL_selects_remote_mode(
        string? configured,
        bool expectedRemote)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = configured,
            })
            .Build();

        Assert.Equal(expectedRemote, TaskServerPlaneProxy.IsConfigured(configuration));
        Assert.Equal(!expectedRemote, EndpointMapping.MapsLocalV1(configuration));
    }

    [Fact]
    public void Standalone_base_url_disables_every_local_v1_owner()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = "http://127.0.0.1:5071",
            })
            .Build();

        Assert.False(EndpointMapping.MapsLocalV1(configuration));
        Assert.True(TaskServerPlaneProxy.IsConfigured(configuration));
    }

    [Theory]
    [InlineData("task-server")]
    [InlineData("ftp://task-server.test")]
    public void Invalid_standalone_base_url_cannot_fall_back_to_local_task_store(string configured)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["TaskServer:BaseUrl"] = configured,
            })
            .Build();

        Assert.Throws<InvalidOperationException>(() => TaskServerPlaneProxy.IsConfigured(configuration));
        Assert.Throws<InvalidOperationException>(() => EndpointMapping.MapsLocalV1(configuration));
    }

    [Fact]
    public void Proxy_maps_one_catch_all_v1_surface()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["TaskServer:BaseUrl"] = "http://127.0.0.1:5071";
        builder.Services.AddTaskServerPlaneProxy(builder.Configuration);
        var app = builder.Build();

        Assert.True(app.MapTaskServerPlaneProxy());

        var routes = ((IEndpointRouteBuilder)app).DataSources
            .SelectMany(source => source.Endpoints)
            .OfType<RouteEndpoint>()
            .Select(endpoint => endpoint.RoutePattern.RawText)
            .ToList();
        Assert.Equal(["/api/v1/{**path}"], routes);
    }

    [Fact]
    public async Task File_backed_proxy_uses_new_credential_on_next_request()
    {
        var path = Path.GetTempFileName();
        try
        {
            File.WriteAllText(path, "generation-one");
            var configuration = new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["TaskServer:BaseUrl"] = "https://task-server.example",
                    ["TaskServer:AuthTokenFile"] = path,
                }).Build();
            var observed = new List<string?>();
            var observedProof = new List<string?>();
            var services = new ServiceCollection();
            services.AddTaskServerPlaneProxy(configuration);
            services.AddHttpClient(TaskServerPlaneProxy.ClientName)
                .ConfigurePrimaryHttpMessageHandler(() => new RecordingHandler(request =>
                {
                    observed.Add(request.Headers.Authorization?.Parameter);
                    observedProof.Add(request.Headers.TryGetValues("X-Principal-Consumer-Proof", out var values)
                        ? values.Single() : null);
                    return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
                }));
            using var provider = services.BuildServiceProvider();
            using var client = provider.GetRequiredService<IHttpClientFactory>()
                .CreateClient(TaskServerPlaneProxy.ClientName);
            await client.GetAsync("/api/v1/workspaces");
            File.WriteAllText(path, "generation-two");
            File.WriteAllText(path + ".consumer-proof", "studio-edge\n" + new string('c', 64) + "\n");
            if (!OperatingSystem.IsWindows())
                File.SetUnixFileMode(path + ".consumer-proof", UnixFileMode.UserRead | UnixFileMode.UserWrite);
            await client.GetAsync("/api/v1/workspaces");
            Assert.Equal(["generation-one", "generation-two"], observed);
            Assert.Equal([null, new string('c', 64)], observedProof);
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
