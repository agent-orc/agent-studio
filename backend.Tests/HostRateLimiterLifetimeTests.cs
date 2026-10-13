using System.Net;
using AgentStudio.TaskServer.Contracts;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// AGT-3107: each partitioned rate limiter runs a 100 ms heartbeat timer until
/// it is disposed, and nothing disposed them. In a test process every stopped
/// host stayed reachable from those timers, so timers and heap grew with each
/// boot. The public-demo profile disposes its global limiter when the host
/// stops; profiles without a limit never create the rate limiter.
/// </summary>
public sealed class HostRateLimiterLifetimeTests : IDisposable
{
    private readonly string _workspace = Path.Combine(
        Path.GetTempPath(),
        "host-rate-limiter-lifetime-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_workspace)) Directory.Delete(_workspace, recursive: true);
    }

    [Fact]
    public async Task Public_demo_global_limiter_is_disposed_when_the_host_stops()
    {
        System.Threading.RateLimiting.PartitionedRateLimiter<HttpContext>? limiter;
        using (var factory = BuildFactory(ExecutionAdmissionPolicy.PublicDemoProfile, onRateLimiterOptions: null))
        {
            using var client = factory.CreateClient(new WebApplicationFactoryClientOptions
            {
                BaseAddress = new Uri("https://demo.test"),
                HandleCookies = false,
            });
            Assert.Equal(HttpStatusCode.OK, (await client.GetAsync("/api/environment")).StatusCode);
            limiter = factory.Services.GetRequiredService<IOptions<RateLimiterOptions>>().Value.GlobalLimiter;
            Assert.NotNull(limiter);
            using var lease = limiter.AttemptAcquire(new DefaultHttpContext());
            Assert.True(lease.IsAcquired);
        }

        Assert.Throws<ObjectDisposedException>(() => limiter.AttemptAcquire(new DefaultHttpContext()));
    }

    [Fact]
    public async Task Local_profile_does_not_create_the_rate_limiter()
    {
        var materialized = false;
        using var factory = BuildFactory("local", onRateLimiterOptions: () => materialized = true);
        using var client = factory.CreateClient();

        var response = await client.GetAsync("/api/environment");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.False(materialized, "The local profile built RateLimiterOptions, so its limiter timers would run.");
    }

    private WebApplicationFactory<Program> BuildFactory(string profile, Action? onRateLimiterOptions) =>
        new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseTestSettings(new Dictionary<string, string?>
            {
                ["TaskRepository"] = _workspace,
                ["Security:Profile"] = profile,
            });
            if (onRateLimiterOptions is not null)
            {
                builder.ConfigureTestServices(services =>
                    services.PostConfigure<RateLimiterOptions>(_ => onRateLimiterOptions()));
            }
        });
}
