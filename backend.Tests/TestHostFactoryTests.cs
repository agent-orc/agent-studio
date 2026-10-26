using System.Net;

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.Routing;
using Microsoft.AspNetCore.Routing.Matching;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Http;
using Microsoft.Extensions.Options;
using Microsoft.Extensions.Time.Testing;

using Xunit;

namespace AgentStudio.Tests;

/// <summary>
/// Pins the host lifetime of <see cref="TestHostFactory"/> (AGT-2859): a
/// request that is still in the pipeline when its host's service provider is
/// disposed failed V1ReviewPlaneDiagnosticsEndpointTests and eight other
/// endpoint suites with <see cref="ObjectDisposedException"/> on
/// <c>IServiceProvider</c> from <c>RequestDelegateFactory.InferMetadata</c>.
/// </summary>
public sealed class InFlightRequestTrackerTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);

    [Fact]
    public async Task Drain_completes_at_once_when_no_request_is_in_flight()
    {
        var tracker = new InFlightRequestTracker(new FakeTimeProvider());

        await tracker.DrainAsync(Timeout, CancellationToken.None);

        Assert.True(tracker.Closed.IsCompleted);
    }

    [Fact]
    public async Task Drain_waits_for_the_last_request_to_leave_the_pipeline()
    {
        var clock = new FakeTimeProvider();
        var tracker = new InFlightRequestTracker(clock);
        var first = tracker.Enter("GET /first");
        var second = tracker.Enter("GET /second");

        var drain = tracker.DrainAsync(Timeout, CancellationToken.None);
        first.Dispose();
        Assert.False(drain.IsCompleted);

        second.Dispose();
        await drain;
        Assert.Equal(0, tracker.InFlightCount);
    }

    [Fact]
    public async Task Drain_times_out_on_the_injected_clock_and_names_the_stuck_request()
    {
        var clock = new FakeTimeProvider();
        var tracker = new InFlightRequestTracker(clock);
        using var stuck = tracker.Enter("POST /api/v1/runners/r/capability-failures");

        var drain = tracker.DrainAsync(Timeout, CancellationToken.None);
        clock.Advance(Timeout - TimeSpan.FromSeconds(1));
        Assert.False(drain.IsCompleted);

        clock.Advance(TimeSpan.FromSeconds(1));
        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => drain);
        Assert.Contains("POST /api/v1/runners/r/capability-failures", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Drain_ends_with_the_host_stop_budget_and_names_the_stuck_request()
    {
        var tracker = new InFlightRequestTracker(new FakeTimeProvider());
        using var stuck = tracker.Enter("GET /stuck");
        using var stopBudget = new CancellationTokenSource();

        var drain = tracker.DrainAsync(Timeout, stopBudget.Token);
        await stopBudget.CancelAsync();

        var timeout = await Assert.ThrowsAsync<TimeoutException>(() => drain);
        Assert.Contains("GET /stuck", timeout.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_request_arriving_after_the_drain_began_is_refused()
    {
        var tracker = new InFlightRequestTracker(new FakeTimeProvider());
        await tracker.DrainAsync(Timeout, CancellationToken.None);

        var refused = Assert.Throws<InvalidOperationException>(() => tracker.Enter("GET /late"));
        Assert.Contains("GET /late", refused.Message, StringComparison.Ordinal);
    }
}

public sealed class TestHostFactoryTests : IDisposable
{
    private const string HoldHeader = "X-Test-Hold-Request";

    private readonly string _workspace;
    private readonly string _watchPath;

    public TestHostFactoryTests()
    {
        _workspace = Path.Combine(Path.GetTempPath(), "atp-test-host-" + Guid.NewGuid().ToString("N"));
        _watchPath = Path.Combine(_workspace, "projects", "test-host");
        foreach (var state in TaskStates.All)
            Directory.CreateDirectory(Path.Combine(_watchPath, state));
    }

    public void Dispose()
    {
        try { Directory.Delete(_workspace, recursive: true); } catch { /* best-effort */ }
    }

    [Fact]
    public void The_host_is_stopped_by_its_test_not_by_a_process_signal()
    {
        using var factory = Configure(new TestHostFactory());
        using var http = factory.CreateClient();

        // ConsoleLifetime turns SIGINT/SIGTERM/SIGQUIT into StopApplication for
        // every live host in the xunit process.
        var lifetime = factory.Services.GetRequiredService<IHostLifetime>();
        Assert.NotEqual("ConsoleLifetime", lifetime.GetType().Name);
    }

    [Fact]
    public async Task Endpoints_are_built_before_the_first_test_request()
    {
        var plainProbe = new MatcherBuildProbe();
        await using (var plain = Configure(new WebApplicationFactory<Program>(), plainProbe))
        {
            using var plainHttp = plain.CreateClient();
            // The plain factory builds them inside the first request, which is
            // where the disposed provider was hit.
            Assert.False(plainProbe.Built);
        }

        var probe = new MatcherBuildProbe();
        await using var factory = Configure(new TestHostFactory(), probe);
        using var http = factory.CreateClient();

        Assert.True(probe.Built);
        var response = await http.GetAsync("/api/v1/protocol");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Sequential_plain_test_hosts_do_not_start_http_handler_expiry_timers()
    {
        for (var host = 0; host < 2; host++)
        {
            await using var factory = Configure(new WebApplicationFactory<Program>());
            var options = factory.Services.GetRequiredService<IOptionsMonitor<HttpClientFactoryOptions>>()
                .Get("secret-external");
            Assert.Equal(Timeout.InfiniteTimeSpan, options.HandlerLifetime);
            using var client = factory.Services.GetRequiredService<IHttpClientFactory>()
                .CreateClient("secret-external");
        }
    }

    [Fact]
    public async Task Disposal_waits_for_a_request_that_is_still_in_the_pipeline()
    {
        var hold = new RequestHold();
        var factory = Configure(new TestHostFactory(), hold: hold);
        // Not factory.CreateClient(): the factory disposes its own clients
        // first, which only cancels the client side of the call. The server
        // side keeps running, and that is the part that needs a live host.
        var http = factory.Server.CreateClient();
        var tracker = factory.Services.GetRequiredService<InFlightRequestTracker>();
        var lifetime = factory.Services.GetRequiredService<IHostApplicationLifetime>();

        using var request = new HttpRequestMessage(HttpMethod.Get, "/api/v1/protocol");
        request.Headers.Add(HoldHeader, "1");
        var inFlight = http.SendAsync(request);
        await hold.Entered;

        var disposal = factory.DisposeAsync().AsTask();
        await tracker.Closed;
        // The host is stopping, but the stop is held while the request runs.
        Assert.True(lifetime.ApplicationStopping.IsCancellationRequested);
        Assert.False(disposal.IsCompleted);
        Assert.False(lifetime.ApplicationStopped.IsCancellationRequested);

        hold.Release();
        using var response = await inFlight;
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        await disposal;
        Assert.Equal(0, tracker.InFlightCount);
        http.Dispose();
    }

    private WebApplicationFactory<Program> Configure(
        WebApplicationFactory<Program> factory,
        MatcherBuildProbe? probe = null,
        RequestHold? hold = null) =>
        factory.WithWebHostBuilder(builder =>
        {
            builder.UseEnvironment("Test");
            builder.UseSetting("TaskRepository", _workspace);
            builder.UseSetting("WatchPaths:0:Name", "test-host");
            builder.UseSetting("WatchPaths:0:Path", _watchPath);
            builder.UseSetting("WatchPaths:0:RootPath", _watchPath);
            builder.UseSetting("WatchPaths:0:RepositoryPath", _watchPath);
            builder.UseSetting("ReviewDecisionOrchestrator:Enabled", "false");
            builder.ConfigureTestServices(services =>
            {
                if (probe is not null) services.AddSingleton<MatcherPolicy>(probe);
                if (hold is not null) services.AddSingleton<IStartupFilter>(hold);
            });
        });

    /// <summary>
    /// Routing asks every matcher policy about the endpoints while it builds the
    /// matcher, so the first call marks the moment the endpoints exist.
    /// </summary>
    private sealed class MatcherBuildProbe : MatcherPolicy, IEndpointSelectorPolicy
    {
        private int _built;

        public bool Built => Volatile.Read(ref _built) == 1;

        public override int Order => 0;

        public bool AppliesToEndpoints(IReadOnlyList<Endpoint> endpoints)
        {
            if (endpoints.Count > 0) Interlocked.Exchange(ref _built, 1);
            return false;
        }

        public Task ApplyAsync(HttpContext httpContext, CandidateSet candidates) => Task.CompletedTask;
    }

    /// <summary>Parks a marked request inside the pipeline until the test releases it.</summary>
    private sealed class RequestHold : IStartupFilter
    {
        private readonly TaskCompletionSource _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly TaskCompletionSource _released = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => _entered.Task;

        public void Release() => _released.TrySetResult();

        public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next) => app =>
        {
            app.Use(async (context, nextMiddleware) =>
            {
                if (context.Request.Headers.ContainsKey(HoldHeader))
                {
                    _entered.TrySetResult();
                    await _released.Task;
                }
                await nextMiddleware(context);
            });
            next(app);
        };
    }
}
