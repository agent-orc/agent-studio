using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace AgentStudio.Tests;

/// <summary>
/// A <see cref="WebApplicationFactory{TEntryPoint}"/> whose host lives exactly as
/// long as the test that owns it (AGT-2859).
///
/// <para>The plain factory leaves three gaps that let a request outlive its
/// host and die with <see cref="ObjectDisposedException"/> on
/// <c>IServiceProvider</c> inside <c>RequestDelegateFactory.InferMetadata</c>:</para>
/// <list type="number">
///   <item>The host keeps the default <c>ConsoleLifetime</c>. It stops the
///   application on SIGINT, SIGTERM or SIGQUIT and cancels the signal so the
///   process survives. One signal to the xunit process therefore stops every
///   live test host at once while their tests are still sending requests. A
///   test host is stopped by its test, never by the process.</item>
///   <item>Minimal-API endpoints are built lazily by the first request that
///   reaches routing, and that build is the slowest step of the first request.
///   They are built here while the host is starting, so no test request ever
///   runs <c>InferMetadata</c>.</item>
///   <item>Nothing makes the host stop wait for requests still in the
///   pipeline. <see cref="InFlightRequestDrain"/> is the first hosted service
///   to stop and holds the stop until <see cref="InFlightRequestTracker"/> is
///   empty. Disposal waits for that whether the factory or the entry point's
///   own <c>app.Run()</c> stops the host.</item>
/// </list>
/// Derived factories from <c>WithWebHostBuilder</c> inherit all three, because
/// they call back into this instance's <see cref="ConfigureWebHost"/> and
/// <see cref="CreateHost"/>.
/// </summary>
public class TestHostFactory : WebApplicationFactory<Program>
{
    /// <summary>Unrouted path the start-up request uses to build the endpoints.</summary>
    public const string EndpointWarmUpPath = "/__test-host/endpoint-warm-up";

    /// <summary>Longest the host stop waits for in-flight requests, on the host's clock.</summary>
    public static readonly TimeSpan DrainTimeout = TimeSpan.FromSeconds(30);

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<IHostLifetime, TestOwnedHostLifetime>();
            services.AddSingleton(provider => new InFlightRequestTracker(provider.GetRequiredService<TimeProvider>()));
            services.AddSingleton<IStartupFilter, InFlightRequestStartupFilter>();
            // Registered last, so it is the first hosted service to stop.
            services.AddHostedService<InFlightRequestDrain>();
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        var host = base.CreateHost(builder);
        BuildEndpoints(host);
        return host;
    }

    /// <summary>
    /// Sends one request through routing while the host is known to be alive, so
    /// the matcher and every endpoint's metadata exist before the test's first
    /// request. The answer does not matter (404 in the local profile, 401 in the
    /// networked one); a failure to build the endpoints propagates and fails
    /// the host start.
    /// </summary>
    private static void BuildEndpoints(IHost host)
    {
        var server = (TestServer)host.Services.GetRequiredService<IServer>();
        using var client = server.CreateClient();
        using var response = client.GetAsync(EndpointWarmUpPath).GetAwaiter().GetResult();
    }

    /// <summary>Host lifetime with no process-signal or console hooks.</summary>
    private sealed class TestOwnedHostLifetime : IHostLifetime
    {
        public Task WaitForStartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    }

    private sealed class InFlightRequestStartupFilter(InFlightRequestTracker tracker) : IStartupFilter
    {
        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                using var _ = tracker.Enter($"{context.Request.Method} {context.Request.Path}");
                await nextMiddleware(context);
            });
            next(app);
        };
    }

    private sealed class InFlightRequestDrain(InFlightRequestTracker tracker) : IHostedService
    {
        public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task StopAsync(CancellationToken cancellationToken) =>
            tracker.DrainAsync(DrainTimeout, cancellationToken);
    }
}
