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
}
